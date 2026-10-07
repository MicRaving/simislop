using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Marshals.Threads;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TS1Platform;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.Model;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The counts that live <em>inside</em> a serialised lot, as opposed to the five
    /// <see cref="VMMarshal"/> itself reads.
    /// </summary>
    ///
    /// <para>
    /// <b>Why this is a separate file from <see cref="VMMarshalFormatTests"/>.</b> That one covers
    /// the counts the lot carries at its top level - the compressed length, the entities, the
    /// threads, the multitile groups and the globals. These are the rest: an entity carries seven,
    /// an avatar five more, a thread two, a stack frame two, a queued action one, a multitile
    /// group one, the wall/floor pattern map two, and the hollow and standalone-object blobs two
    /// each. Every one of them was four bytes off the wire going straight into <c>new T[n]</c>,
    /// and every one needed its own <c>bytesPerItem</c> to be bounded - two bytes for a short,
    /// three for a pattern name, one for a type tag - so a test that damaged "some count" would
    /// prove nothing about which.
    /// </para>
    ///
    /// <para>
    /// <b>How the damage is aimed.</b> Not by hard-coding a byte offset, which breaks the moment
    /// the format moves, and not by searching for a value, which finds whichever count happens to
    /// match. Each case serialises its subject twice - once with a collection one element long and
    /// once with it two long - and takes the first byte that differs. A count is always written
    /// before the elements it counts, so that byte is the count whatever the format happens to be.
    /// <see cref="CountOffset"/> does it. If a mutation stops reaching a count it throws, so a
    /// test cannot quietly start aiming at the wrong field.
    /// </para>
    ///
    /// <para>
    /// <b>What each case asserts.</b> That a legal count still reads - the bounds must not be
    /// tight enough to refuse a real save - and that a negative value, a value at
    /// <see cref="int.MaxValue"/>, a value too large for the bytes that remain, and a stream that
    /// ends at the count are each reported as <see cref="MarshalFormatException"/> naming the
    /// field.
    /// </para>
    public sealed class NestedMarshalCountTests
    {
        #region the aiming mechanism

        private static int CountOffset(Func<int, byte[]> serialise, int shortLength, int longLength)
        {
            var small = serialise(shortLength);
            var large = serialise(longLength);
            for (int i = 0; i < Math.Min(small.Length, large.Length); i++)
            {
                if (small[i] != large[i]) return i;
            }
            throw new InvalidOperationException(
                "the two serialisations are identical, so the mutation did not reach a count");
        }

        private static byte[] WithCount(byte[] good, int offset, int value)
        {
            var copy = (byte[])good.Clone();
            Array.Copy(BitConverter.GetBytes(value), 0, copy, offset, 4);
            return copy;
        }

        private static byte[] Serialise(VMSerializable thing)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                thing.SerializeInto(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void Read(byte[] bytes, Action<BinaryReader> read)
        {
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                read(reader);
            }
        }

        private static void ReadDamaged(byte[] bytes, Action<BinaryReader> read)
        {
            Read(bytes, read);
        }

        #endregion

        #region subjects

        private static T[] Filled<T>(int count, int value) where T : struct
        {
            var array = new T[count];
            for (int i = 0; i < count; i++) array[i] = (T)Convert.ChangeType(value, typeof(T));
            return array;
        }

        private static VMGameObjectMarshal Entity(Action<VMGameObjectMarshal> tweak = null)
        {
            var entity = new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true)
            {
                ObjectID = 1,
                PersistID = 1,
                GUID = 0x1000,
                ObjectData = Filled<short>(2, 3),
                MyList = new short[0],
                Contained = new short[0],
                Attributes = new short[0],
                MeToObject = new VMEntityRelationshipMarshal[0],
                MeToPersist = new VMEntityPersistRelationshipMarshal[0],
                Position = new LotTilePos(1, 2, 0),
                PlatformState = new VMTS1ObjectState(),
            };
            tweak?.Invoke(entity);
            return entity;
        }

        private static VMGameObjectMarshal TsoEntity()
        {
            // The standalone object blob is the one format whose entities are read as *TSO* - the
            // reader hard-codes `ts1: false` on the game objects it builds - so a fixture that
            // serialises a TS1 platform state for it produces a stream the reader desynchronises
            // from, and every count after the first one is read from the wrong place. Worth
            // spelling out, because it is invisible until a count is added.
            return new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, false)
            {
                ObjectID = 1,
                PersistID = 1,
                GUID = 0x1000,
                ObjectData = Filled<short>(2, 3),
                MyList = new short[0],
                Contained = new short[0],
                Attributes = new short[0],
                MeToObject = new VMEntityRelationshipMarshal[0],
                MeToPersist = new VMEntityPersistRelationshipMarshal[0],
                Position = new LotTilePos(1, 2, 0),
                PlatformState = new VMTSOObjectState(),
            };
        }

        private static VMStackFrameMarshal Frame()
        {
            return new VMStackFrameMarshal(VMMarshal.LATEST_VERSION)
            {
                Locals = Filled<short>(2, 37),
                Args = Filled<short>(2, 41),
            };
        }

        private static VMQueuedActionMarshal Queued(int argCount)
        {
            return new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION)
            {
                Args = argCount < 0 ? null : Filled<short>(argCount, 47),
            };
        }

        private static VMThreadMarshal Thread(Action<VMThreadMarshal> tweak)
        {
            var thread = new VMThreadMarshal(VMMarshal.LATEST_VERSION)
            {
                Stack = new VMStackFrameMarshal[0],
                Queue = new VMQueuedActionMarshal[0],
                ActiveQueueBlock = -1,
                TempRegisters = Filled<short>(20, 53),
                TempXL = new int[2],
            };
            tweak(thread);
            return thread;
        }

        private static VMAnimationStateMarshal Animation()
        {
            return new VMAnimationStateMarshal
            {
                Anim = "a0_00",
                CurrentFrame = 1f,
                EventQueue = Filled<short>(3, 5),
                EventsRun = 0,
                EndReached = false,
                PlayingBackwards = false,
                Speed = 1f,
                Weight = 1f,
                Loop = true,
            };
        }

        private static VMAvatarMarshal Avatar(Action<VMAvatarMarshal> tweak = null)
        {
            var avatar = new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true)
            {
                ObjectID = 1,
                PersistID = 1,
                GUID = 0x1000,
                ObjectData = new short[4],
                MyList = new short[0],
                Contained = new short[0],
                Attributes = new short[0],
                MeToObject = new VMEntityRelationshipMarshal[0],
                MeToPersist = new VMEntityPersistRelationshipMarshal[0],
                Position = new LotTilePos(1, 2, 0),
                PlatformState = new VMTS1AvatarState(),

                Animations = new[] { Animation() },
                CarryAnimationState = null,
                Message = "",
                MessageTimeout = 0,
                MotiveChanges = Enumerable.Range(0, 16).Select(_ => new VMMotiveChange()).ToArray(),
                MotiveDecay = new VMAvatarMotiveDecay(),
                PersonData = Filled<short>(101, 7),
                MotiveData = Filled<short>(16, 11),
                HandObjectOld = 0,
                RadianDirection = 0f,
                KillTimeout = -1,
                DefaultSuits = new VMAvatarDefaultSuits(false),
                DynamicSuits = new VMAvatarDynamicSuits(false),
                Decoration = new VMAvatarDecoration(),
                BoundAppearances = new[] { "look0" },
                BodyOutfit = null,
                HeadOutfit = null,
            };
            tweak?.Invoke(avatar);
            return avatar;
        }

        private static VMMultitileGroupMarshal Group(int objectCount)
        {
            return new VMMultitileGroupMarshal(VMMarshal.LATEST_VERSION)
            {
                MultiTile = false,
                Name = "group",
                Price = 100,
                SalePrice = -1,
                Objects = Filled<short>(objectCount, 31),
                Offsets = Enumerable.Range(0, objectCount).Select(_ => new LotTilePos()).ToArray(),
            };
        }

        private static VMResourceIDMarshal Patterns(int wallCount, int floorCount = 0)
        {
            var map = new VMResourceIDMarshal { Roof = "roof" };
            for (int i = 0; i < wallCount; i++) map.WallNamesByID[(ushort)(i + 1)] = "wall" + i;
            for (int i = 0; i < floorCount; i++) map.FloorNamesByID[(ushort)(i + 1)] = "floor" + i;
            return map;
        }

        #endregion

        #region the cases

        /// <summary>
        /// Every nested count, as the arguments the theory below takes.
        /// </summary>
        /// <remarks>
        /// One element and two, always. One is the boundary a real save sits on and the point where
        /// a count of zero stops being legal; two is what gives the differential two encodings to
        /// compare. Both have to write real elements, or "one element" and "no elements" would
        /// serialise identically and the offset could not be found - which throws rather than
        /// aiming at the wrong field.
        /// </remarks>
        public static IEnumerable<object[]> Cases()
        {
            // ---- entity: seven counts
            yield return Case("entity data count", n => Serialise(Entity(e => e.ObjectData = Filled<short>(n, 3))),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("entity list count", n => Serialise(Entity(e => e.MyList = Filled<short>(n, 7))),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("contained count", n => Serialise(Entity(e => e.Contained = Filled<short>(n, 11))),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("attribute count", n => Serialise(Entity(e => e.Attributes = Filled<short>(n, 13))),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("relationship count",
                n => Serialise(Entity(e => e.MeToObject = Enumerable.Range(0, n)
                    .Select(_ => new VMEntityRelationshipMarshal
                    {
                        Target = 1,
                        Values = Filled<short>(2, 17),
                    }).ToArray())),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("persistent relationship count",
                n => Serialise(Entity(e => e.MeToPersist = Enumerable.Range(0, n)
                    .Select(_ => new VMEntityPersistRelationshipMarshal
                    {
                        Target = 1,
                        Values = Filled<short>(2, 19),
                    }).ToArray())),
                r => new VMGameObjectMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));

            // ---- the two relationship records, one level further down
            yield return Case("relationship value count",
                n => Serialise(new VMEntityRelationshipMarshal
                {
                    Target = 1,
                    Values = Filled<short>(n, 23),
                }),
                r => new VMEntityRelationshipMarshal().Deserialize(r));
            yield return Case("persistent relationship value count",
                n => Serialise(new VMEntityPersistRelationshipMarshal
                {
                    Target = 1,
                    Values = Filled<short>(n, 29),
                }),
                r => new VMEntityPersistRelationshipMarshal().Deserialize(r));

            // ---- avatar: five counts of its own
            yield return Case("animation count",
                n => Serialise(Avatar(a => a.Animations = Enumerable.Range(0, n).Select(_ => Animation()).ToArray())),
                r => new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("motive change count",
                n => Serialise(Avatar(a => a.MotiveChanges = Enumerable.Range(0, n).Select(_ => new VMMotiveChange()).ToArray())),
                r => new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            //the array is grown to 101 for a save that carried fewer, so the two lengths have to
            //straddle that floor for the count itself to be the thing that differs
            yield return Case("person data count",
                n => Serialise(Avatar(a => a.PersonData = Filled<short>(100 + n, 7 + n))),
                r => new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("motive data count",
                n => Serialise(Avatar(a => a.MotiveData = Filled<short>(n, 11))),
                r => new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));
            yield return Case("bound appearance count",
                n => Serialise(Avatar(a => a.BoundAppearances = Enumerable.Range(0, n).Select(_ => "look" + _).ToArray())),
                r => new VMAvatarMarshal(VMMarshal.LATEST_VERSION, true).Deserialize(r));

            // ---- multitile group
            yield return Case("multitile object count", n => Serialise(Group(n)),
                r => new VMMultitileGroupMarshal(VMMarshal.LATEST_VERSION).Deserialize(r));

            // ---- thread
            yield return Case("stack frame count",
                n => Serialise(Thread(t => t.Stack = Enumerable.Range(0, n).Select(_ => Frame()).ToArray())),
                r => new VMThreadMarshal(VMMarshal.LATEST_VERSION).Deserialize(r));
            yield return Case("queued action count",
                n => Serialise(Thread(t => t.Queue = Enumerable.Range(0, n).Select(_ => Queued(2)).ToArray())),
                r => new VMThreadMarshal(VMMarshal.LATEST_VERSION).Deserialize(r));

            // ---- the queued action's own argument list, whose sentinel is -1
            yield return Case("queued action argument count", n => Serialise(Queued(n)),
                r => new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION).Deserialize(r),
                hasSentinel: true);

            // ---- the wall and floor pattern map
            yield return Case("wall pattern count", n => Serialise(Patterns(n)),
                r => new VMResourceIDMarshal().Deserialize(r));
            yield return Case("floor pattern count", n => Serialise(Patterns(0, n)),
                r => new VMResourceIDMarshal().Deserialize(r));

            // ---- the hollow lot the CAS editor reads, and the standalone object blob
            yield return Case("entity count", n => HollowLot(n, 1),
                r => new VMHollowMarshal().Deserialize(r));
            yield return Case("multitile group count", n => HollowLot(1, n),
                r => new VMHollowMarshal().Deserialize(r));
            yield return Case("entity count", n => StandaloneLot(n),
                r => new VMStandaloneObjectMarshal().Deserialize(r));
        }

        private static object[] Case(string field, Func<int, byte[]> serialise, Action<BinaryReader> read,
            bool hasSentinel = false)
        {
            return new object[] { field, serialise, read, hasSentinel };
        }

        /// <summary>
        /// A hollow lot - "FSOh", uncompressed - with the given number of entities and groups.
        /// </summary>
        /// <remarks>
        /// This is the blob the CAS editor writes and the content layer reads, and it is a
        /// <em>different format</em> from the full lot: a different four-character preamble, no
        /// "is this TS1" flag, and hollow entities with seven fields instead of a platform state
        /// and a relationship list. Its two counts were unguarded in exactly the same way, which
        /// is why they are here rather than left to the lot fixture.
        /// </remarks>
        private static byte[] HollowLot(int entityCount, int groupCount)
        {
            var context = VMMarshalFixture.SyntheticLot(1, 1, 38).Context;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'F', 'S', 'O', 'h' });
                writer.Write(VMMarshal.LATEST_VERSION);
                writer.Write(false);                     //not compressed
                context.SerializeInto(writer);

                writer.Write(entityCount);
                for (int i = 0; i < entityCount; i++) HollowEntity().SerializeInto(writer);

                writer.Write(groupCount);
                for (int i = 0; i < groupCount; i++) Group(1).SerializeInto(writer);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static VMHollowGameObjectMarshal HollowEntity()
        {
            return new VMHollowGameObjectMarshal(VMMarshal.LATEST_VERSION)
            {
                ObjectID = 1,
                GUID = 0x1000,
                MasterGUID = 0x1000,
                Position = new LotTilePos(1, 2, 0),
                Direction = Direction.NORTH,
                Graphic = 0x1234,
                Contained = new short[0],
            };
        }

        /// <summary>
        /// A standalone object blob - "FSOo", uncompressed - with the given number of entities.
        /// </summary>
        /// <remarks>
        /// The third of the three framed formats, and the one with the fewest fields: a preamble,
        /// the version, the compressed flag, an entity count and then always exactly one multitile
        /// group with no count of its own. Its length field was the same unchecked
        /// <c>ReadBytes</c>, which is why it is here.
        /// </remarks>
        private static byte[] StandaloneLot(int entityCount)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'F', 'S', 'O', 'o' });
                writer.Write(VMMarshal.LATEST_VERSION);
                writer.Write(false);                     //not compressed

                writer.Write(entityCount);
                for (int i = 0; i < entityCount; i++) TsoEntity().SerializeInto(writer);

                Group(1).SerializeInto(writer);

                writer.Flush();
                return stream.ToArray();
            }
        }

        #endregion

        #region the assertions

        [Theory]
        [MemberData(nameof(Cases))]
        public void ALegalCountStillReads(string field, Func<int, byte[]> serialise,
            Action<BinaryReader> read, bool hasSentinel)
        {
            // the compatibility requirement, stated first: the bounds must not be tight enough to
            // refuse a real save. One and two elements have to read; so, separately below, does zero
            Read(serialise(1), read);
            Read(serialise(2), read);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ANegativeCountIsReportedWithItsOwnName(string field, Func<int, byte[]> serialise,
            Action<BinaryReader> read, bool hasSentinel)
        {
            // new T[-1] is an OverflowException from the middle of a parse, saying nothing about
            // the file. This is the sign bit flipping.
            //
            // -1 is skipped for the one field where the format uses it as a sentinel - a queued
            // action with no arguments - and -2 stands in for it, because the boundary that
            // matters there is "the sentinel and nothing else". See MinusTwoIsStillRefused below.
            var offset = CountOffset(serialise, 1, 2);
            var bytes = serialise(1);
            var badValues = hasSentinel ? new[] { -2, int.MinValue } : new[] { -1, int.MinValue };

            foreach (var bad in badValues)
            {
                var thrown = Assert.Throws<MarshalFormatException>(
                    () => Read(WithCount(bytes, offset, bad), read));
                Assert.Contains(field, thrown.Message);
                Assert.Contains("damaged", thrown.Message);
            }
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ACountAtIntMaxValueIsReportedRatherThanAllocated(string field,
            Func<int, byte[]> serialise, Action<BinaryReader> read, bool hasSentinel)
        {
            // a two-gigabyte allocation for a file of a few hundred bytes, and an
            // OutOfMemoryException the player sees as a crash
            var offset = CountOffset(serialise, 1, 2);

            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(WithCount(serialise(1), offset, int.MaxValue), read));
            Assert.Contains(field, thrown.Message);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ACountLargerThanTheDataIsRefusedEvenWhenItIsSmall(string field,
            Func<int, byte[]> serialise, Action<BinaryReader> read, bool hasSentinel)
        {
            // The case a maximum alone cannot catch, and the one bit rot actually produces: a
            // few hundred, in a stream with a few hundred bytes left.
            //
            // One more than the bytes remaining, not a per-element fraction of them: the floor
            // differs per field (two for a short, one for a type tag, eight for a path point) and
            // a value chosen for one of them can slip past the check for another and then run off
            // the end of the data - which is the failure this is here to prevent, not to replace
            // with a different one.
            var bytes = serialise(1);
            var offset = CountOffset(serialise, 1, 2);
            var remaining = bytes.Length - (offset + 4);
            var tooMany = remaining + 1;

            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(WithCount(bytes, offset, tooMany), read));
            Assert.Contains(field, thrown.Message);
            Assert.Contains("bytes left", thrown.Message);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void AStreamThatEndsAtTheCountIsReportedAsTruncated(string field,
            Func<int, byte[]> serialise, Action<BinaryReader> read, bool hasSentinel)
        {
            // a file cut short, which is what a full disk or an interrupted write leaves. Before
            // the bounds this was an EndOfStreamException from wherever the reader happened to be
            var bytes = serialise(1);
            var offset = CountOffset(serialise, 1, 2);

            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(bytes.Take(offset).ToArray(), read));
            Assert.Contains("truncated", thrown.Message);
            Assert.Contains(field, thrown.Message);
        }

        #endregion

        #region the sentinel counts, which are a different shape

        [Fact]
        public void MinusOneIsStillTheFormatSaysThereIsNothingYet()
        {
            // A queued action with no arguments is the normal state of half the queue and the
            // format says so with -1. ReadCount rejects every negative value, which is right for
            // every other field and wrong for this one - so the sentinel has to survive, or a
            // legitimate save is refused.
            var read = new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION);
            Read(Serialise(Queued(-1)), r => read.Deserialize(r));

            Assert.Null(read.Args);
        }

        [Fact]
        public void MinusTwoIsStillRefusedBecauseItIsNotTheSentinel()
        {
            // the boundary: -1 is the format, everything else negative is damage
            var offset = CountOffset(n => Serialise(Queued(n)), 1, 2);

            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(WithCount(Serialise(Queued(1)), offset, -2),
                    r => new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION).Deserialize(r)));
            Assert.Contains("queued action argument count", thrown.Message);
        }

        [Fact]
        public void AQueuedActionWithNoArgumentsAndOneWithSomeAreBothLegal()
        {
            // the two states of the same field, side by side, because a bound that rejected the
            // second would also have to be checked against the first
            var none = new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION);
            Read(Serialise(Queued(-1)), r => none.Deserialize(r));
            Assert.Null(none.Args);

            var some = new VMQueuedActionMarshal(VMMarshal.LATEST_VERSION);
            Read(Serialise(Queued(3)), r => some.Deserialize(r));
            Assert.Equal(3, some.Args.Length);
        }

        #endregion

        #region the shared array reader every fixed-width collection goes through

        [Fact]
        public void TheSharedArrayReaderRefusesANegativeCount()
        {
            // VMSerializableUtils.ReadArray is the last checkpoint before the allocation for every
            // fixed-width collection in the format: an entity's data and its list, the objects
            // inside it, its attributes, a multitile group's members, a stack frame's locals and
            // arguments, an avatar's motive data. Its size arrives from a caller that has already
            // read it off the wire, so it is the one reader that cannot read the count for itself
            // - and therefore the one that has to check what it is handed.
            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(new byte[4], r => VMSerializableUtils.ReadArray<short>(r, -1)));
            Assert.Contains("not a possible number", thrown.Message);
        }

        [Fact]
        public void TheSharedArrayReaderRefusesACountTheStreamCannotHold()
        {
            // forty shorts is eighty bytes and the stream has four. Small, plausible-looking, and
            // a guaranteed walk off the end of the data. The message has to carry both numbers,
            // because a player looking at "this save is damaged" has to be able to see what was
            // asked for and what was there.
            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(new byte[4], r => VMSerializableUtils.ReadArray<short>(r, 40)));
            Assert.Contains("80 bytes", thrown.Message);
            Assert.Contains("only 4 left", thrown.Message);
            Assert.Contains("damaged", thrown.Message);
        }

        [Fact]
        public void TheSharedArrayReaderRefusesACountThatWouldBeGigabytes()
        {
            // A hundred million shorts is two hundred megabytes. On any realistically sized stream
            // the *remaining-bytes* test is the one that fires, and it is the stronger of the two
            // - so this asserts that the count is refused and that the refusal says how much was
            // asked for, rather than which of the two bounds caught it.
            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(new byte[0], r => VMSerializableUtils.ReadArray<short>(r, 100_000_000)));
            Assert.Contains("100000000", thrown.Message);
            Assert.Contains("200000000 bytes", thrown.Message);
            Assert.Contains("only 0 left", thrown.Message);
            Assert.Contains("damaged", thrown.Message);
        }

        [Fact]
        public void TheSharedArrayReaderMeasuresTheElementNotTheArray()
        {
            // a wider element is a tighter bound, and getting that wrong the other way - assuming
            // one byte per element - would let a count of a hundred four-byte values through on a
            // stream with four hundred bytes left
            var thrown = Assert.Throws<MarshalFormatException>(
                () => Read(new byte[64], r => VMSerializableUtils.ReadArray<int>(r, 100)));
            Assert.Contains("Int32", thrown.Message);
            Assert.Contains("400", thrown.Message);
        }

        [Fact]
        public void TheSharedArrayReaderStillReadsACountThatFits()
        {
            // the compatibility requirement again, and the reason it matters beyond the bounds: a
            // collection that fits is read, and the tail the stream does not cover is zero
            short[] read = null;
            var bytes = new byte[8];
            Array.Copy(BitConverter.GetBytes((short)7), 0, bytes, 0, 2);
            Array.Copy(BitConverter.GetBytes((short)9), 0, bytes, 2, 2);
            Read(bytes, r => read = VMSerializableUtils.ReadArray<short>(r, 4));

            Assert.Equal(new short[] { 7, 9, 0, 0 }, read);
        }

        [Fact]
        public void TheSharedArrayReaderAcceptsAnEmptyCollection()
        {
            // a count of zero is legal and is what an object with nothing inside it carries
            short[] read = null;
            Read(new byte[0], r => read = VMSerializableUtils.ReadArray<short>(r, 0));

            Assert.NotNull(read);
            Assert.Empty(read);
        }

        #endregion

        #region the whole lot, end to end

        [Fact]
        public void TheFixtureLotWithEveryNestedCollectionInItStillReads()
        {
            // the compatibility requirement for the nested bounds as a whole: the fixture lot
            // carries entities with data, threads with frames, and multitile groups with members,
            // and none of it may have become unreadable
            var marshal = VMMarshalFixture.Read(VMMarshalFixture.Build(out _));

            Assert.Equal(3, marshal.Entities.Length);
            Assert.Equal(3, marshal.Threads.Length);
            Assert.Equal(2, marshal.MultitileGroups.Length);
        }

        [Fact]
        public void AnAvatarRoundTripsThroughTheLot()
        {
            // the avatar reader is the deepest one - five counts of its own on top of the entity's
            // seven - and it is the reader a player's own family comes through, so its bounds are
            // the ones that must not be tight
            var lot = LotWith(Avatar(a =>
            {
                a.Animations = new[] { Animation(), Animation(), Animation() };
                a.BoundAppearances = new[] { "look0", "look1" };
                a.MotiveData = Filled<short>(16, 11);
            }));

            var marshal = VMMarshalFixture.Read(lot);
            var avatar = Assert.IsType<VMAvatarMarshal>(Assert.Single(marshal.Entities));

            Assert.Equal(3, avatar.Animations.Length);
            Assert.Equal("a0_00", avatar.Animations[0].Anim);
            Assert.Equal(2, avatar.BoundAppearances.Length);
            Assert.Equal(16, avatar.MotiveChanges.Length);
            Assert.Equal(16, avatar.MotiveData.Length);
            Assert.Equal(101, avatar.PersonData.Length);
        }

        [Fact]
        public void AGameObjectStillRoundTripsThroughTheLot()
        {
            var lot = LotWith(Entity(e => e.ObjectData = Filled<short>(5, 42)));

            var marshal = VMMarshalFixture.Read(lot);
            var entity = Assert.IsType<VMGameObjectMarshal>(Assert.Single(marshal.Entities));

            Assert.Equal(5, entity.ObjectData.Length);
            Assert.Equal(Filled<short>(5, 42), entity.ObjectData);
        }

        /// <summary>
        /// A complete lot carrying exactly one entity, built field by field.
        /// </summary>
        /// <remarks>
        /// Written out rather than reused from <see cref="VMMarshalFixture"/> because this needs an
        /// entity the fixture does not have - an avatar, or one with a particular collection - and
        /// because the fixture's <c>Build</c> deliberately records offsets rather than exposing a
        /// way to substitute the entities. Every test that deserialises a whole lot reads it back
        /// through <c>VMMarshal.Deserialize</c>, so a layout that had drifted from the format
        /// fails here rather than making a bound look effective.
        /// </remarks>
        private static byte[] LotWith(VMEntityMarshal entity)
        {
            var template = VMMarshalFixture.SyntheticLot(1, 1, 38);
            template.Entities = new[] { entity };

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new char[] { 'F', 'S', 'O', 'v' });
                writer.Write(VMMarshal.LATEST_VERSION);
                writer.Write(false);                 //not compressed
                writer.Write(true);                  //TS1, because LATEST_VERSION > 26
                template.Context.SerializeInto(writer);

                writer.Write(template.Entities.Length);
                foreach (var ent in template.Entities)
                {
                    writer.Write((byte)((ent is VMAvatarMarshal) ? 1 : 0));
                    ent.SerializeInto(writer);
                }
                writer.Write(template.Threads.Length);
                foreach (var thr in template.Threads) thr.SerializeInto(writer);
                writer.Write(template.MultitileGroups.Length);
                foreach (var grp in template.MultitileGroups) grp.SerializeInto(writer);
                writer.Write(template.GlobalState.Length);
                foreach (var val in template.GlobalState) writer.Write(val);
                template.PlatformState.SerializeInto(writer);
                writer.Write(template.ObjectId);
                writer.Write(template.Tuning != null);
                writer.Flush();
                return stream.ToArray();
            }
        }

        #endregion
    }
}
