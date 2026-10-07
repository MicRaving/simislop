using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The chunks that make up <c>Neighborhood.iff</c> - the most valuable file in a save -
    /// read and written on their own.
    ///
    /// <para>
    /// Every one of these chunks was a genuine gap. <c>SaveCompatTests</c> and
    /// <c>NeighborhoodServiceTests</c> both hand <c>IffFile</c> a file that <c>IffFile</c> just
    /// wrote, so the <em>reader</em> was only ever exercised against three hand-made shapes: an
    /// empty file, a four-byte non-IFF, and a missing file. Not one of the chunk parsers below had
    /// a round-trip test, let alone a malformed-input one.
    /// </para>
    ///
    /// <para>
    /// They matter more than the average chunk because of what each one's counts are: a count in
    /// <c>Neighborhood.iff</c> is four bytes that a truncated write, a half-copied first-run
    /// template or a bit flip turns into anything, and every one of them went straight into an
    /// array bound. A negative was an <see cref="OverflowException"/> and a large one was a
    /// multi-gigabyte allocation, both raised from the middle of opening a player's
    /// neighbourhood. The tests below are the shape that catches that class: a legal file still
    /// round trips field for field, and three shapes of damage are each refused with a message
    /// that names the count.
    /// </para>
    ///
    /// <para>
    /// Each chunk reads through a bare <see cref="MemoryStream"/> and never touches its
    /// <see cref="IffFile"/>, so no file, no VM and no graphics device is needed.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodChunkFormatTests
    {
        #region helpers

        /// <summary>
        /// A payload of <paramref name="count"/> written as the little-endian int32 the chunks
        /// read their counts from, followed by <paramref name="trailing"/> filler bytes.
        /// </summary>
        private static byte[] CountThen(int count, int trailing)
        {
            var bytes = new byte[4 + trailing];
            bytes[0] = (byte)count;
            bytes[1] = (byte)(count >> 8);
            bytes[2] = (byte)(count >> 16);
            bytes[3] = (byte)(count >> 24);
            for (int i = 4; i < bytes.Length; i++) bytes[i] = 0x5A;
            return bytes;
        }

        /// <summary>
        /// The NGBH header: a pad word, the version, the magic, and sixteen shorts of
        /// neighbourhood data - 40 bytes before the inventory count.
        /// </summary>
        private static List<byte> NgbhHeader(uint version = 0x49)
        {
            var bytes = new List<byte>();
            void Int32(int v)
            {
                bytes.Add((byte)v);
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)(v >> 16));
                bytes.Add((byte)(v >> 24));
            }
            Int32(0);
            Int32((int)version);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("HBGN"));
            for (int i = 0; i < 16; i++)
            {
                //sixteen *shorts*, not sixteen ints: the header is 44 bytes, and a test that
                //builds 76 puts every count test in the wrong place.
                bytes.Add(0);
                bytes.Add(0);
            }
            return bytes;
        }

        /// <summary>One neighbourhood's inventory entry: version, id, item count, then items.</summary>
        private static void NgbhEntry(List<byte> bytes, short id, params int[] types)
        {
            void Int32(int v)
            {
                bytes.Add((byte)v);
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)(v >> 16));
                bytes.Add((byte)(v >> 24));
            }
            Int32(1);
            bytes.Add((byte)id);
            bytes.Add((byte)(id >> 8));
            Int32(types.Length);
            foreach (var type in types)
            {
                //an item is a type, a guid and a count: ten bytes, not four. A hand-built entry
                //that writes only the type puts every later count test in the wrong place.
                Int32(type);
                Int32(0);
                bytes.Add(0);
                bytes.Add(0);
            }
        }

        private static NGBH ReadNgbh(byte[] payload)
        {
            var chunk = new NGBH();
            chunk.Read(null, new MemoryStream(payload));
            return chunk;
        }

        private static FAMI ReadFami(byte[] payload)
        {
            var chunk = new FAMI();
            chunk.Read(null, new MemoryStream(payload));
            return chunk;
        }

        private static NBRS ReadNbrs(byte[] payload)
        {
            var chunk = new NBRS();
            chunk.Read(null, new MemoryStream(payload));
            return chunk;
        }

        private static byte[] BuildFami(int memberCount, int trailing)
        {
            //pad, version, magic, then six ints, then the member count
            var header = new List<byte> { 0, 0, 0, 0, 9, 0, 0, 0 };
            header.AddRange(System.Text.Encoding.ASCII.GetBytes("IMAF"));
            for (int i = 0; i < 6; i++) header.AddRange(new byte[] { 0, 0, 0, 0 });
            header.Add((byte)memberCount);
            header.Add((byte)(memberCount >> 8));
            header.Add((byte)(memberCount >> 16));
            header.Add((byte)(memberCount >> 24));
            var bytes = header.ToArray();
            //the member area is only there so a legal count has something behind it. A count of
            //int.MaxValue must not be *built* - the test is about what the reader does with it.
            long members = Math.Max(0, Math.Min(memberCount, 4096)) * 4L;
            var result = new byte[bytes.Length + members + trailing];
            Array.Copy(bytes, result, bytes.Length);
            //fill the member area and the trailing area with recognisable values
            for (int i = bytes.Length; i < result.Length; i++) result[i] = 0x11;
            return result;
        }

        /// <summary>Offset of the member count inside a written FAMI payload.</summary>
        private const int FamiMemberCountOffset = 36;

        /// <summary>
        /// Where a chunk's payload starts inside the file: the sixty-byte magic plus the four-byte
        /// resource-map offset, then the chunk's own seventy-six-byte header.
        /// </summary>
        private const int IffPayloadStart = 60 + 4 + 76;

        #endregion

        #region NGBH

        [Fact]
        public void ANgbhWithNoInventoryAtAllIsAnNgbhWithNoInventory()
        {
            //the 40-byte header and nothing after it. `if (!io.HasMore) return;` is a real shape:
            //an early game had no magicoins at all, and it must still open.
            var chunk = ReadNgbh(NgbhHeader().ToArray());

            Assert.Equal(0x49u, chunk.Version);
            Assert.Empty(chunk.InventoryByID);
        }

        [Fact]
        public void ANgbhRoundTripsEveryNeighbourhoodsInventory()
        {
            //The write path, because NGBH.Write is what puts the magicoins back after a purchase.
            //If the reader and the writer disagree about the layout, a family's money silently
            //resets - which is the same class of bug as the SIMI budget-day shift, on a different
            //file.
            var original = new NGBH { Version = 0x49 };
            for (short i = 0; i < 16; i++) original.NeighborhoodData[i] = (short)(100 + i);
            original.InventoryByID[7] = new List<InventoryItem>
            {
                new InventoryItem { Type = 0, GUID = 0xABCD1234, Count = 3 },
                new InventoryItem { Type = 1, GUID = 0x0BADF00D, Count = 12 },
            };
            original.InventoryByID[9] = new List<InventoryItem>
            {
                new InventoryItem { Type = 2, GUID = 0xDEADBEEF, Count = 1 },
            };

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                Assert.True(original.Write(null, stream));
                payload = stream.ToArray();
            }

            var back = ReadNgbh(payload);

            Assert.Equal(original.NeighborhoodData, back.NeighborhoodData);
            Assert.Equal(2, back.InventoryByID.Count);
            var family = back.InventoryByID[7];
            Assert.Equal(2, family.Count);
            Assert.Equal(0, family[0].Type);
            Assert.Equal(0xABCD1234u, family[0].GUID);
            Assert.Equal((ushort)3, family[0].Count);
            Assert.Equal(0x0BADF00Du, family[1].GUID);
            Assert.Equal((ushort)12, family[1].Count);
            Assert.Single(back.InventoryByID[9]);
        }

        [Fact]
        public void ANgbhWithNoNeighbourhoodsOfItsOwnIsStillWrittenAndRead()
        {
            var original = new NGBH { Version = 0x49 };

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                Assert.True(original.Write(null, stream));
                payload = stream.ToArray();
            }

            Assert.Empty(ReadNgbh(payload).InventoryByID);
        }

        [Fact]
        public void ANgbhNamingTheSameNeighbourhoodTwiceKeepsTheLaterOne()
        {
            //The dictionary is keyed on the neighbourhood id, and the format has no room for two
            //entries with the same one. `Add` was a KeyNotFoundException out of the read of a
            //player's own neighbourhood; "the later one wins" is what the Sims did.
            var bytes = NgbhHeader();
            bytes.AddRange(CountThen(2, 0));
            NgbhEntry(bytes, 5, 111);
            NgbhEntry(bytes, 5, 222);

            var chunk = ReadNgbh(bytes.ToArray());

            Assert.Single(chunk.InventoryByID);
            Assert.Equal(222, chunk.InventoryByID[5][0].Type);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        [InlineData(0x40000000)]
        public void ANgbhNeighbourhoodCountTheFileCannotHoldIsRefused(int count)
        {
            //Before the fix these were an OverflowException (negative) and a four-billion-element
            //loop (large), both from four bytes in the middle of a neighbourhood file.
            var bytes = NgbhHeader();
            bytes.AddRange(CountThen(count, 64));

            var ex = Assert.Throws<IoFormatException>(() => ReadNgbh(bytes.ToArray()));
            Assert.Contains("neighbourhood count", ex.Message);
        }

        [Fact]
        public void ANgbhInventoryCountTheFileCannotHoldIsRefusedRatherThanLooping()
        {
            //A plausible count: 40000 items, in a chunk with a few hundred bytes behind it. The
            //remaining-bytes test is the one that catches it, and it fires before the List exists.
            var bytes = NgbhHeader();
            bytes.AddRange(CountThen(1, 0));
            //one entry header: version, id, then a count of 40000
            bytes.AddRange(new byte[] { 1, 0, 0, 0, 5, 0 });
            bytes.AddRange(CountThen(40000, 32));

            var ex = Assert.Throws<IoFormatException>(() => ReadNgbh(bytes.ToArray()));
            Assert.Contains("inventory item count", ex.Message);
        }

        [Fact]
        public void ANgbhWithAnEmptyInventoryIsNotAnError()
        {
            //A family that has never spent anything: count 1, entry, item count 0. The boundary
            //the bounds test above has to respect, so it is pinned explicitly.
            var bytes = NgbhHeader();
            bytes.AddRange(CountThen(1, 0));
            NgbhEntry(bytes, 3);

            var chunk = ReadNgbh(bytes.ToArray());

            Assert.Single(chunk.InventoryByID);
            Assert.Empty(chunk.InventoryByID[3]);
        }

        #endregion

        #region FAMI

        [Fact]
        public void AFamiRoundTripsEveryMembersGuid()
        {
            var original = new FAMI
            {
                Version = 9,
                HouseNumber = 42,
                FamilyNumber = 17,
                Budget = 15000,
                ValueInArch = 25000,
                FamilyFriends = 3,
                Unknown = 24,
                FamilyGUIDs = new uint[] { 0x11111111, 0x22222222, 0x33333333, 0x44444444 },
            };

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                Assert.True(original.Write(null, stream));
                payload = stream.ToArray();
            }

            var back = ReadFami(payload);

            Assert.Equal(42, back.HouseNumber);
            Assert.Equal(17, back.FamilyNumber);
            Assert.Equal(15000, back.Budget);
            Assert.Equal(25000, back.ValueInArch);
            Assert.Equal(3, back.FamilyFriends);
            Assert.Equal(24, back.Unknown);
            Assert.Equal(original.FamilyGUIDs, back.FamilyGUIDs);
        }

        [Fact]
        public void AFamiWithNoMembersIsNotAnError()
        {
            //A townie family. The boundary the bounds test below has to respect.
            var back = ReadFami(BuildFami(0, 16));

            Assert.Empty(back.FamilyGUIDs);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void AFamiMemberCountTheFileCannotHoldIsRefused(int count)
        {
            //`new uint[io.ReadInt32()]` used to be the whole of it: a negative was an
            //OverflowException and 0x40000000 was a four-billion-element array on a file of a few
            //hundred bytes. FAMI is one family in Neighborhood.iff.
            var ex = Assert.Throws<IoFormatException>(() => ReadFami(BuildFami(count, 64)));
            Assert.Contains("family member count", ex.Message);
        }

        [Fact]
        public void AFamiWithATruncatedMemberListIsRefusedRatherThanReadShort()
        {
            //A file cut short after the count: the count says 8, only two GUIDs are behind it.
            //BinaryReader.ReadBytes would have returned the two and called it a family of two.
            var header = BuildFami(8, 0);
            var truncated = new byte[header.Length - 2];
            Array.Copy(header, truncated, truncated.Length);

            var ex = Assert.Throws<IoFormatException>(() => ReadFami(truncated));
            Assert.Contains("family member count", ex.Message);
        }

        #endregion

        #region NBRS

        /// <summary>One neighbour record, as the writer lays it out.</summary>
        private static void NbrsNeighbour(List<byte> bytes, short id, uint guid, int version = 0xA)
        {
            void Int32(int v)
            {
                bytes.Add((byte)v);
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)(v >> 16));
                bytes.Add((byte)(v >> 24));
            }
            Int32(1);                    // Unknown1
            Int32(version);              // Version
            if (version == 0xA) Int32(9); // Unknown3
            foreach (var ch in System.Text.Encoding.ASCII.GetBytes("Neighbour" + id)) bytes.Add(ch);
            bytes.Add(0);                 // terminator
            //and the pad the reader consumes after an even-length name. The Sims' writer emits
            //it and the reader expects it, so a hand-built record has to as well.
            if (("Neighbour" + id).Length % 2 == 0) bytes.Add(0);
            Int32(0);                    // MysteryZero
            Int32(0);                    // PersonMode - no person data
            bytes.Add((byte)id);          // NeighbourID
            bytes.Add((byte)(id >> 8));
            bytes.Add((byte)guid);
            bytes.Add((byte)(guid >> 8));
            bytes.Add((byte)(guid >> 16));
            bytes.Add((byte)(guid >> 24));
            Int32(-1);                   // UnknownNegOne
            Int32(1);                    // one relationship
            Int32(1);                    // key count
            Int32(1000 + id);            // the key
            Int32(2);                    // two values
            Int32(10 + id);
            Int32(20 + id);
        }

        private static byte[] NbrsPayload(int declaredCount, Action<List<byte>> body)
        {
            var bytes = new List<byte> { 0, 0, 0, 0, 0x49, 0, 0, 0 };
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("SRBN"));
            bytes.Add((byte)declaredCount);
            bytes.Add((byte)(declaredCount >> 8));
            bytes.Add((byte)(declaredCount >> 16));
            bytes.Add((byte)(declaredCount >> 24));
            body(bytes);
            return bytes.ToArray();
        }

        [Fact]
        public void ANbrsRoundTripsEveryNeighbourAndItsRelationships()
        {
            var original = new NBRS { Version = 0x49 };
            for (short id = 1; id <= 3; id++)
            {
                original.AddNeighbor(new Neighbour
                {
                    Unknown1 = 1,
                    Version = 0xA,
                    Unknown3 = 9,
                    Name = "Neighbour" + id,
                    NeighbourID = id,
                    GUID = (uint)(0x1000 + id),
                    UnknownNegOne = -1,
                    PersonMode = 0,
                    Relationships = new Dictionary<int, List<short>>
                    {
                        { 1000 + id, new List<short> { (short)(10 + id), (short)(20 + id) } },
                    },
                });
            }

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                Assert.True(original.Write(null, stream));
                payload = stream.ToArray();
            }

            var back = ReadNbrs(payload);

            Assert.Equal(3, back.Entries.Count);
            Assert.Equal(original.Entries.Select(x => x.Name), back.Entries.Select(x => x.Name));
            foreach (var neighbour in original.Entries)
            {
                var read = back.NeighbourByID[neighbour.NeighbourID];
                Assert.Equal(neighbour.GUID, read.GUID);
                Assert.Equal(neighbour.Relationships[1000 + neighbour.NeighbourID],
                    read.Relationships[1000 + neighbour.NeighbourID]);
            }
        }

        [Fact]
        public void AnNbrsWithNoNeighboursIsNotAnError()
        {
            var back = ReadNbrs(NbrsPayload(0, _ => { }));

            Assert.Empty(back.Entries);
        }

        [Fact]
        public void AnNbrsNeighbourCountTheFileCannotHoldIsRefused()
        {
            //Count 0xFFFFFFFF read as an int is -1, which is why the count reader has an unsigned
            //overload rather than a cast: "not negative" is the test that rejects it. The outer
            //count is checked against the ceiling rather than against the bytes left, so this
            //test is about "not a possible claim", not about the file being short.
            var ex = Assert.Throws<IoFormatException>(
                () => ReadNbrs(NbrsPayload(-1, _ => { })));
            Assert.Contains("neighbour count", ex.Message);
        }

        [Fact]
        public void AnNbrsNeighbourCountAboveAnythingRealIsRefused()
        {
            var ex = Assert.Throws<IoFormatException>(
                () => ReadNbrs(NbrsPayload(0x40000000, _ => { })));
            Assert.Contains("neighbour count", ex.Message);
        }

        [Fact]
        public void AnNbrsDeclaringMoreNeighboursThanItHoldsKeepsTheOnesItHas()
        {
            //A real save cut short. The loop's `if (!io.HasMore) break;` is deliberate - a
            //truncated neighbourhood should still open with the families it does have, rather
            //than refusing to open at all.
            var back = ReadNbrs(NbrsPayload(9, bytes => NbrsNeighbour(bytes, 1, 0xAA)));

            Assert.Single(back.Entries);
            Assert.Equal("Neighbour1", back.Entries[0].Name);
        }

        [Fact]
        public void AnNbrsNeighbourWithTheSameIdTwiceKeepsTheLaterOne()
        {
            //The id is the key of a dictionary. `Add` was a KeyNotFoundException out of the read
            //of the file that holds every family in the game; "later wins" is what it should do.
            var back = ReadNbrs(NbrsPayload(2, bytes =>
            {
                NbrsNeighbour(bytes, 4, 0xAA);
                NbrsNeighbour(bytes, 4, 0xBB);
            }));

            Assert.Single(back.NeighbourByID);
            Assert.Equal(0xBBu, back.NeighbourByID[4].GUID);
        }

        [Fact]
        public void AnNbrsRelationshipCountTheFileCannotHoldIsRefused()
        {
            //Inside one neighbour: the relationship count is four bytes and used to bound a loop
            //that then read a key, a key count and a value count per iteration.
            var bytes = new List<byte> { 0, 0, 0, 0, 0x49, 0, 0, 0 };
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("SRBN"));
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });   // Unknown1
            bytes.AddRange(new byte[] { 4, 0, 0, 0 });   // Version 0x4: no Unknown3
            foreach (var ch in System.Text.Encoding.ASCII.GetBytes("Neighbour1")) bytes.Add(ch);
            bytes.Add(0);
            bytes.Add(0);   // the pad after an even-length name, which the reader consumes
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });   // MysteryZero
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });   // PersonMode
            bytes.AddRange(new byte[] { 1, 0 });         // NeighbourID
            bytes.AddRange(new byte[] { 0xAA, 0, 0, 0 });// GUID
            bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // UnknownNegOne
            bytes.AddRange(CountThen(20000, 8));        // 20000 relationships, 8 bytes behind

            var ex = Assert.Throws<IoFormatException>(() => ReadNbrs(bytes.ToArray()));
            Assert.Contains("relationship count", ex.Message);
        }

        [Fact]
        public void AnNbrsRelationshipValueCountTheFileCannotHoldIsRefused()
        {
            var bytes = new List<byte> { 0, 0, 0, 0, 0x49, 0, 0, 0 };
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("SRBN"));
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });
            bytes.AddRange(new byte[] { 4, 0, 0, 0 });
            foreach (var ch in System.Text.Encoding.ASCII.GetBytes("Neighbour1")) bytes.Add(ch);
            bytes.Add(0);
            bytes.Add(0);   // the pad after an even-length name, which the reader consumes
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
            bytes.AddRange(new byte[] { 1, 0 });
            bytes.AddRange(new byte[] { 0xAA, 0, 0, 0 });
            bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            bytes.AddRange(CountThen(1, 0));           // one relationship
            bytes.AddRange(new byte[] { 1, 0, 0, 0 }); // key count
            bytes.AddRange(new byte[] { 0xE8, 0x03, 0, 0 }); // key 1000
            bytes.AddRange(CountThen(40000, 8));       // 40000 values, 8 bytes behind

            var ex = Assert.Throws<IoFormatException>(() => ReadNbrs(bytes.ToArray()));
            Assert.Contains("relationship value count", ex.Message);
        }

        [Fact]
        public void AnNbrsNeighbourWithTheSameRelationshipKeyTwiceKeepsTheLaterOne()
        {
            //Same reasoning as the id: the dictionary is keyed on the relationship and the format
            //has no room for two of them.
            var bytes = new List<byte> { 0, 0, 0, 0, 0x49, 0, 0, 0 };
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("SRBN"));
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });
            bytes.AddRange(new byte[] { 1, 0, 0, 0 });
            bytes.AddRange(new byte[] { 4, 0, 0, 0 });
            foreach (var ch in System.Text.Encoding.ASCII.GetBytes("Neighbour1")) bytes.Add(ch);
            bytes.Add(0);
            bytes.Add(0);   // the pad after an even-length name, which the reader consumes
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
            bytes.AddRange(new byte[] { 1, 0 });
            bytes.AddRange(new byte[] { 0xAA, 0, 0, 0 });
            bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            bytes.AddRange(CountThen(2, 0));
            foreach (var value in new[] { 1, 2 })
            {
                bytes.AddRange(new byte[] { 1, 0, 0, 0 });
                bytes.AddRange(new byte[] { 0xE8, 0x03, 0, 0 });
                bytes.AddRange(CountThen(value, 0));
                //and `value` actual values, or the second relationship is short by one
                for (int v = 0; v < value; v++) bytes.AddRange(CountThen(value * 10 + v, 0));
            }

            var back = ReadNbrs(bytes.ToArray());

            Assert.Single(back.Entries);
            var relationships = back.Entries[0].Relationships;
            Assert.Single(relationships);
            Assert.Equal(new List<short> { 20, 21 }, relationships[1000]);
        }

        #endregion

        #region the whole file

        [Fact]
        public void ANeighbourhoodOfEveryChunkSurvivesAWriteAndAReadThroughIffFile()
        {
            //The framing, not just the chunk bodies: a real Neighborhood.iff is written by
            //IffFile and read by IffFile, and that is the path a player's save actually takes.
            var file = new IffFile();

            var fami = new FAMI
            {
                ChunkLabel = "family",
                HouseNumber = 3,
                FamilyNumber = 9,
                Budget = 20000,
                ValueInArch = 30000,
                FamilyFriends = 1,
                Unknown = 1,
                FamilyGUIDs = new uint[] { 1, 2, 3 },
            };
            var nbrs = new NBRS { Version = 0x49, ChunkLabel = "neighbours" };
            nbrs.AddNeighbor(new Neighbour
            {
                Unknown1 = 1,
                Version = 0x4,
                Name = "Tester",
                NeighbourID = 1,
                GUID = 0xFEED,
                UnknownNegOne = -1,
                PersonMode = 0,
                Relationships = new Dictionary<int, List<short>> { { 7, new List<short> { 1 } } },
            });
            var ngbh = new NGBH { Version = 0x49, ChunkLabel = "neighbourhood" };
            ngbh.InventoryByID[3] = new List<InventoryItem> { new InventoryItem { GUID = 99, Count = 2 } };

            file.AddChunk(fami);
            file.AddChunk(nbrs);
            file.AddChunk(ngbh);

            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                file.Write(stream);
                bytes = stream.ToArray();
            }

            var back = new IffFile();
            back.Read(new MemoryStream(bytes));

            var readFami = back.Get<FAMI>(0);
            Assert.Equal(3, readFami.HouseNumber);
            Assert.Equal(new uint[] { 1, 2, 3 }, readFami.FamilyGUIDs);

            var readNbrs = back.List<NBRS>().Single();
            Assert.Single(readNbrs.Entries);
            Assert.Equal("Tester", readNbrs.Entries[0].Name);

            var readNgbh = back.List<NGBH>().Single();
            Assert.Single(readNgbh.InventoryByID[3]);
            Assert.Equal((ushort)2, readNgbh.InventoryByID[3][0].Count);
        }

        [Fact]
        public void AChunkBuiltInMemoryIsAlreadyItsOwnParsedSelf()
        {
            //AddChunk marks a chunk that has neither raw data nor original data as processed,
            //because there is nothing for the reader to parse. Without it, `Write` -> `ListAll`
            //-> `prepare` asked `new MemoryStream(null)` for it, and the failure was an
            //ArgumentNullException on a parameter named "buffer" from the middle of a save.
            var file = new IffFile();
            var fami = new FAMI { HouseNumber = 1, FamilyGUIDs = new uint[] { 1 } };
            file.AddChunk(fami);

            Assert.True(fami.ChunkProcessed);
            Assert.Null(fami.ChunkData);

            //and if something does get here with neither, the message says which chunk and why
            var unprepared = new FAMI { HouseNumber = 1, FamilyGUIDs = new uint[0] };
            unprepared.ChunkProcessed = false;
            var other = new IffFile();
            other.AddChunk(unprepared);
            unprepared.ChunkProcessed = false;

            var ex = Assert.Throws<IffFormatException>(() => other.Write(new MemoryStream()));
            Assert.Contains("ChunkProcessed", ex.Message);
        }

        [Fact]
        public void ANeighbourhoodChunkCountingMoreThanItHoldsIsRefusedByTheChunkNotTheFile()
        {
            //The bounds are the chunk's, not the file's: IffFile has already handed the chunk
            //exactly its declared payload, so a count the payload cannot hold is a corrupt
            //payload and the chunk is the only place that can say so.
            var file = new IffFile();
            file.AddChunk(new FAMI { FamilyGUIDs = new uint[0] });
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                file.Write(stream);
                bytes = stream.ToArray();
            }

            //the member count is 36 bytes into the FAMI payload, and the payload starts after the
            //file header and the chunk header
            var at = IffPayloadStart + FamiMemberCountOffset;
            bytes[at] = 0xFF;
            bytes[at + 1] = 0xFF;
            bytes[at + 2] = 0xFF;
            bytes[at + 3] = 0x7F;

            var back = new IffFile();
            back.Read(new MemoryStream(bytes));

            var ex = Assert.ThrowsAny<Exception>(() => back.Get<FAMI>(0));
            Assert.Contains("family member count", ex.Message);
        }

        #endregion
    }
}
