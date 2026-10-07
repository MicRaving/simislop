using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Marshals.Threads;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards what a lot keeps when a family is evicted from it.
    ///
    /// <para>
    /// The bug these exist for: an eviction refunded the money and released the house in the
    /// neighbourhood, and then stopped. The lot's own <c>House{NN}.iff</c> still held the family's
    /// furniture, their objects value, and their lot price, so the next family to pick the lot
    /// moved straight into it. The filter that fixes that is pure - it takes the facts about an
    /// object and returns a decision - so it is pinned here rather than in a playtest.
    /// </para>
    ///
    /// <para>
    /// Synthetic data only: no TS1 install, no <c>Content</c>, no VM, no lot on disk. The GUIDs are
    /// the real ones, because the whole point of the essential-object list is that it is the exact
    /// set <c>VerifyFamily</c> needs.
    /// </para>
    /// </summary>
    public sealed class LotEvictionTests
    {
        /// <summary>The mailbox GUID <c>VMTS1LotState.VerifyFamily</c> looks for.</summary>
        private const uint Mailbox = 0xEF121974u;

        /// <summary>The alternate mailbox GUID <c>VerifyFamily</c> also accepts.</summary>
        private const uint AltMailbox = 0x1D95C9B0u;

        /// <summary>The two-tile mailbox - a different object that looks the same.</summary>
        private const uint TwoTileMailbox = 0x39CCF441u;

        /// <summary>A bin.</summary>
        private const uint TrashBin = 0xA4258067u;

        /// <summary>A phone.</summary>
        private const uint Phone = 0x313D2F9Au;

        /// <summary>The community lot phone.</summary>
        private const uint CommunityPhone = 0x303CD603u;

        /// <summary>A sofa, or any other thing with a price in the buy catalog.</summary>
        private const uint Sofa = 0x12345678u;

        /// <summary>A door: build mode, so part of the lot's fabric.</summary>
        private const uint Door = 0x0BADF00Du;

        /// <summary>A controller - the mailman and friends.</summary>
        private const uint Controller = 0x0BADBEEFu;

        // ------------------------------------------------------------ essential objects

        [Theory]
        [InlineData(Mailbox)]
        [InlineData(AltMailbox)]
        [InlineData(TwoTileMailbox)]
        [InlineData(TrashBin)]
        [InlineData(Phone)]
        [InlineData(CommunityPhone)]
        public void LotInfrastructureIsEssential(uint guid)
        {
            Assert.True(LotEviction.IsEssentialObject(guid), "0x" + guid.ToString("X8"));
        }

        [Theory]
        [InlineData(Sofa)]
        [InlineData(Door)]
        [InlineData(Controller)]
        [InlineData(0u)]
        public void OrdinaryObjectsAreNotEssential(uint guid)
        {
            Assert.False(LotEviction.IsEssentialObject(guid), "0x" + guid.ToString("X8"));
        }

        [Fact]
        public void TheMailboxSurvivesEvenThoughItIsACatalogObject()
        {
            //the mailbox is a buy-catalog item, so the "is it furniture" test would remove it. If it
            //went, VerifyFamily would spawn the next family at OUT_OF_WORLD, i.e. nowhere.
            var disposition = LotEviction.Classify(
                Mailbox, false, false, false, true, buildModeType: 0, functionFlags: 1);

            Assert.Equal(LotEviction.Disposition.Preserve, disposition);
        }

        [Fact]
        public void BothMailboxVariantsSurvive()
        {
            //VerifyFamily accepts either GUID, so the filter has to keep either one. Keeping only
            //the primary would break every lot that ships the alternate.
            foreach (uint mailbox in new[] { Mailbox, AltMailbox })
            {
                Assert.Equal(LotEviction.Disposition.Preserve,
                    LotEviction.Classify(mailbox, false, false, false, true, 0, 1));
            }
        }

        // ------------------------------------------------------------ the three dispositions

        [Fact]
        public void SimsGoEvenWhenTheyAreInsideAFurniturePiece()
        {
            //an avatar that was sitting in a chair, or parked in the pool, is still a member of the
            //family that just left.
            Assert.Equal(LotEviction.Disposition.Remove,
                LotEviction.Classify(Sofa, true, false, false, true, 0, 1));
        }

        [Fact]
        public void ControllersAreKeptButWiped()
        {
            Assert.Equal(LotEviction.Disposition.PreserveAndReset,
                LotEviction.Classify(Controller, false, true, false, true, 0, 1));
        }

        [Fact]
        public void AControllerIsNotRemovedEvenThoughItIsACatalogObject()
        {
            //the "is it furniture" test is what strips a lot bare; a controller that falls through
            //it would be re-created with EP0, which is how the last family gets handed back.
            var disposition = LotEviction.Classify(
                Controller, false, isController: true, false, true, buildModeType: 0, functionFlags: 4);

            Assert.Equal(LotEviction.Disposition.PreserveAndReset, disposition);
        }

        [Fact]
        public void BoughtFurnitureGoes()
        {
            Assert.Equal(LotEviction.Disposition.Remove,
                LotEviction.Classify(Sofa, false, false, false, true, buildModeType: 0, functionFlags: 1));
        }

        [Fact]
        public void BuildModePiecesStay()
        {
            //doors, windows and stairs are catalog objects but they are the lot, not the furniture.
            Assert.Equal(LotEviction.Disposition.Preserve,
                LotEviction.Classify(Door, false, false, false, true, buildModeType: 1, functionFlags: 1));
        }

        [Fact]
        public void NonCatalogObjectsStay()
        {
            //portals, spawn points, the lot tile marker: FunctionFlags == 0, so not in the catalog
            //and not something a family bought.
            Assert.Equal(LotEviction.Disposition.Preserve,
                LotEviction.Classify(Sofa, false, false, false, true, buildModeType: 0, functionFlags: 0));
        }

        [Fact]
        public void ObjectsParkedOutOfBoundsStay()
        {
            //a thing sitting at OUT_OF_WORLD is inventory or a hidden system object, not something
            //the family put on the lot - and it is what controllers and pooled objects look like.
            Assert.Equal(LotEviction.Disposition.Preserve,
                LotEviction.Classify(Sofa, false, false, isOutOfWorld: true, true, 0, 1));
        }

        [Fact]
        public void AnObjectContentCannotResolveIsRemoved()
        {
            //there is no way to tell a missing buy-mode object from a missing door, and keeping it
            //would leave the previous family's furniture on the lot with no way to price it.
            Assert.Equal(LotEviction.Disposition.Remove,
                LotEviction.Classify(Sofa, false, false, false, objectIsKnown: false, 0, 0));
        }

        [Fact]
        public void AnAvatarIsRemovedBeforeAnythingElseIsConsidered()
        {
            //a Sim is not kept just because it happens to be standing where the catalog filter
            //would have kept it. Stated as an ordering contract, not one example of it.
            Assert.Equal(LotEviction.Disposition.Remove,
                LotEviction.Classify(Controller, isAvatar: true, isController: true,
                    isOutOfWorld: true, objectIsKnown: true, buildModeType: 2, functionFlags: 0));
        }

        [Theory]
        [InlineData(Mailbox, LotEviction.Disposition.Preserve)]
        [InlineData(Door, LotEviction.Disposition.Preserve)]
        [InlineData(Sofa, LotEviction.Disposition.Remove)]
        [InlineData(Controller, LotEviction.Disposition.PreserveAndReset)]
        public void EveryObjectSortsIntoExactlyOneOutcome(uint guid, LotEviction.Disposition expected)
        {
            //one representative of each class, classified the way the eviction classifies it
            var disposition = LotEviction.Classify(
                guid,
                isAvatar: false,
                isController: guid == Controller,
                isOutOfWorld: false,
                objectIsKnown: true,
                buildModeType: guid == Door ? 1 : 0,
                functionFlags: guid == Controller ? 4 : (guid == Mailbox ? 1 : (guid == Sofa ? 1 : 0)));

            Assert.Equal(expected, disposition);
        }

        // ------------------------------------------------------------ multitile groups

        [Fact]
        public void AGroupGoesIfAnyOfItsTilesGoes()
        {
            //a group whose lead tile is gone but whose other tiles are not is a group the VM will
            //try to restore into an object that no longer exists.
            var kept = new HashSet<short> { 1, 3 };

            Assert.False(LotEviction.KeepsGroup(new short[] { 1, 2, 3 }, kept));
            Assert.False(LotEviction.KeepsGroup(new short[] { 1, 99 }, kept));
        }

        [Fact]
        public void AGroupStaysWhenEveryTileStays()
        {
            var kept = new HashSet<short> { 1, 2, 3 };

            Assert.True(LotEviction.KeepsGroup(new short[] { 1, 2, 3 }, kept));
            Assert.True(LotEviction.KeepsGroup(new short[] { 3 }, kept));
        }

        [Fact]
        public void AnEmptyGroupIsKeptAndAGroupWithNothingKeptIsNot()
        {
            //nothing in an empty group can dangle, so there is no reason to drop it; a group with
            //nothing left to keep is dropped rather than left referring to the void.
            var kept = new HashSet<short> { 1 };

            Assert.True(LotEviction.KeepsGroup(null, kept));
            Assert.True(LotEviction.KeepsGroup(new short[0], kept));
            Assert.False(LotEviction.KeepsGroup(new short[] { 7 }, kept));
            Assert.False(LotEviction.KeepsGroup(new short[] { 7 }, null));
        }

        [Fact]
        public void GroupFilteringIsIndependentOfGroupOrder()
        {
            var kept = new HashSet<short> { 4, 5, 6 };

            Assert.True(LotEviction.KeepsGroup(new short[] { 4, 5, 6 }, kept));
            Assert.True(LotEviction.KeepsGroup(new short[] { 6, 4, 5 }, kept));
            Assert.False(LotEviction.KeepsGroup(new short[] { 4, 5, 7 }, kept));
            Assert.False(LotEviction.KeepsGroup(new short[] { 7, 4, 5 }, kept));
        }

        // ------------------------------------------------------------ the object id counter

        [Fact]
        public void TheFreeObjectIdIsOnePastTheHighestSurvivor()
        {
            //the old family's ids are gone; the counter has to come from what is actually left or
            //the lot hands out ids that were in use a moment ago.
            Assert.Equal(6, LotEviction.NextObjectId(new short[] { 1, 5, 2, 4 }));
        }

        [Fact]
        public void AnEmptiedLotStartsCountingAgainAtOne()
        {
            Assert.Equal(1, LotEviction.NextObjectId(new short[0]));
            Assert.Equal(1, LotEviction.NextObjectId(null));
        }

        [Fact]
        public void ASingleSurvivorSetsTheCounter()
        {
            Assert.Equal(2, LotEviction.NextObjectId(new short[] { 1 }));
        }

        [Fact]
        public void TheObjectIdCounterNeverWraps()
        {
            //max + 1 in a short would come out negative and hand the next created object an id that
            //collides with a live one, so the counter sticks at the top of the range instead.
            Assert.Equal(short.MaxValue, LotEviction.NextObjectId(new short[] { short.MaxValue }));
            Assert.Equal(short.MaxValue, LotEviction.NextObjectId(new short[] { 1, short.MaxValue }));
        }

        [Fact]
        public void TheObjectIdCounterIsNeverBelowOne()
        {
            //only object id 0 means "no object", and a counter of 0 or less is not an id the VM can
            //hand out.
            foreach (var result in new[]
            {
                LotEviction.NextObjectId(new short[0]),
                LotEviction.NextObjectId(new short[] { 0 }),
                LotEviction.NextObjectId(new short[] { 0, 0, 0 })
            })
            {
                Assert.InRange(result, 1, short.MaxValue);
            }
        }

        // ------------------------------------------------------------ the lot's value

        [Fact]
        public void TheBulldozedArchitectureValueIsTheArchitectureItIsLeftWith()
        {
            //an eviction empties a house, it does not flatten it: the walls and floors stay, and
            //nothing that is counted into the architecture value is ever removed - the build-mode
            //objects that make up the rest of it (doors, windows, stairs) are all preserved.
            Assert.Equal(4321, LotEviction.ArchitectureValueAfterEvict(4321, 0));
        }

        [Fact]
        public void BuildModeObjectsThatGoAreStillDeducted()
        {
            //the second term is only ever zero because the filter keeps every build-mode object. If
            //that ever changes, this is the arithmetic that has to keep working.
            Assert.Equal(3200, LotEviction.ArchitectureValueAfterEvict(4000, -800));
            Assert.Equal(4800, LotEviction.ArchitectureValueAfterEvict(4000, 800));
        }

        [Fact]
        public void TheArchitectureValueSaturatesRatherThanWrapping()
        {
            Assert.Equal(int.MaxValue, LotEviction.ArchitectureValueAfterEvict(int.MaxValue, 1));
            Assert.Equal(int.MinValue, LotEviction.ArchitectureValueAfterEvict(int.MinValue, -1));
        }

        [Fact]
        public void TheBulldozedLotIsNotChargedForTheLastFamilysFurniture()
        {
            //This is the end the whole change is for. A lot's price is SIMI.PurchaseValue, and that
            //is LotValue + ObjectsValue + 70% of ArchitectureValue - so an eviction that leaves the
            //old ObjectsValue behind is charging the next family for the last family's sofa. The
            //formula is the real one, on a real SIMI chunk, rather than a restatement of it.
            var simi = new SIMI
            {
                LotValue = 1000,
                ObjectsValue = 8500,                                        // the furniture being bulldozed
                ArchitectureValue = LotEviction.ArchitectureValueAfterEvict(4000, 0),
            };
            var priceBeforeEviction = simi.PurchaseValue;

            //what the eviction writes back
            simi.ObjectsValue = 0;
            simi.ArchitectureValue = LotEviction.ArchitectureValueAfterEvict(4321, 0);
            simi.Version = LotEviction.NormalizedSimiVersion;

            var priceAfterEviction = simi.PurchaseValue;

            Assert.Equal(12300, priceBeforeEviction);
            Assert.Equal(1000 + (4321 * 7) / 10, priceAfterEviction);
            Assert.True(priceAfterEviction < priceBeforeEviction / 2,
                "the bulldozed lot still costs " + priceAfterEviction);
        }

        [Fact]
        public void TheBulldozedLotStillHasAFabricWorthSomething()
        {
            //an eviction is not a demolition: the walls and floors the lot was built with are still
            //there, and the lot is still worth something to the next family.
            var simi = new SIMI
            {
                LotValue = 1000,
                ObjectsValue = 0,
                ArchitectureValue = LotEviction.ArchitectureValueAfterEvict(4321, 0),
            };

            Assert.Equal(4321, simi.ArchitectureValue);
            Assert.True(simi.PurchaseValue > 1000, "the lot came out worth its bare fabric only");
        }

        [Fact]
        public void TheValuesChunkIsNormalisedToTheVersionItsOwnWriterStamps()
        {
            // The contract the comment used to describe but not test. The old assertion here
            // compared a constant with a literal - it failed only if someone edited the constant,
            // exercised no code, and its own comment correctly diagnosed F-02 and then tested
            // nothing about it.
            //
            // What it should have said, and now does: an eviction stamps a chunk whose version
            // agrees with the number of globals the writer will put behind it, so a lot that is
            // bulldozed produces a file the lot loader can read back. The writer's own stamp is
            // SIMI.CURRENT_VERSION, and the chunk is asked for that - so this fails if the two
            // ever drift apart, which is the thing that would corrupt an eviction.
            Assert.Equal(SIMI.CURRENT_VERSION, (uint)LotEviction.NormalizedSimiVersion);
        }

        [Theory]
        [InlineData(0x3D)]
        [InlineData(0x3E)]
        [InlineData(0x3F)]
        [InlineData(0x40)]
        public void AnEvictedLotProducesAChunkWhoseVersionDescribesItsOwnLayout(int version)
        {
            // The real half of F-02, at the eviction end of it. Whatever version the caller's
            // chunk happens to be carrying, writing it has to produce a payload whose header says
            // how many globals are in it - because the reader believes the header, and a chunk
            // that put 64 globals behind a 32-global header has its lot values read out of the
            // tail of the globals. That is silent, permanent, and happens on every bulldoze.
            var simi = ValuesChunk((uint)version);

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                Assert.True(simi.Write(null, stream));
                payload = stream.ToArray();
            }

            //the chunk was restamped to what the writer emits, and the writer emits a layout that
            //agrees with that stamp
            Assert.Equal(SIMI.CURRENT_VERSION, simi.Version);
            Assert.Equal(SIMI.GlobalsOnWire(simi.Version),
                SIMI.GlobalsOnWire(LotEviction.NormalizedSimiVersion));

            //and the lot's own values are read back out of the globals rather than out of one
            var back = ReadSimi(payload);
            Assert.Equal(12000, back.LotValue);
            Assert.Equal(3400, back.ObjectsValue);
            Assert.Equal(4321, back.ArchitectureValue);
        }

        /// <summary>A values chunk at a given version, with every field distinct and non-zero.</summary>
        private static SIMI ValuesChunk(uint version)
        {
            return new SIMI
            {
                ChunkID = 1,
                ChunkType = "SIMI",
                ChunkLabel = "Lot Information",
                ChunkProcessed = true,
                Version = version,
                LotValue = 12000,
                ObjectsValue = 3400,
                ArchitectureValue = 4321,
                GlobalData = Enumerable.Range(0, 38).Select(i => (short)(1000 + i)).ToArray(),
                BudgetDays = Enumerable.Range(0, 6).Select(_ => new SIMI.SIMIBudgetDay()).ToArray(),
            };
        }

        private static SIMI ReadSimi(byte[] payload)
        {
            var back = new SIMI();
            using (var stream = new MemoryStream(payload))
            {
                back.Read(null, stream);
            }
            return back;
        }

        [Fact]
        public void AChunkThatAsksForTheCurrentVersionAndOneThatAsksForNothingBothRoundTrip()
        {
            // the compatibility requirement behind the normalisation: a chunk already at 0x3E -
            // which is every save on a player's disk - comes out of the writer unchanged, so the
            // eviction path cannot shift a byte of an existing file
            var simi = ValuesChunk(LotEviction.NormalizedSimiVersion);

            byte[] payload;
            using (var stream = new MemoryStream())
            {
                simi.Write(null, stream);
                payload = stream.ToArray();
            }

            var back = ReadSimi(payload);

            Assert.Equal(12000, back.LotValue);
            Assert.Equal(3400, back.ObjectsValue);
            Assert.Equal(4321, back.ArchitectureValue);
            Assert.Equal(32, SIMI.GlobalsOnWire(back.Version));
            Assert.Equal(simi.GlobalData.Take(32).ToArray(), back.GlobalData.Take(32).ToArray());
        }

        // ------------------------------------------------------------ the lot globals

        [Fact]
        public void TheLotIsToldEveryExpansionPackIsInstalled()
        {
            //global 20 is the game edition. A lot left at whatever the last session wrote behaves as
            //though no expansion packs are running, which is what breaks controller behaviour.
            var globals = LotEviction.ResetLotGlobals(new short[38]);

            Assert.Equal(255, globals[20]);
        }

        [Fact]
        public void TheRuntimeCodeVersionIsReset()
        {
            //global 17 is read while controller trees start up, and a stale value takes the magic
            //man and the new neighbours with it.
            var globals = LotEviction.ResetLotGlobals(new short[38]);

            Assert.Equal(4, globals[17]);
        }

        [Fact]
        public void TheIdleInteractionGlobalIsReset()
        {
            Assert.Equal(4, LotEviction.ResetLotGlobals(new short[38])[25]);
        }

        [Fact]
        public void TheSelectedSimIsCleared()
        {
            //the selection would otherwise point at a Sim that is no longer on the lot.
            Assert.Equal(0, LotEviction.ResetLotGlobals(new short[38])[3]);
        }

        [Fact]
        public void TheRestOfTheLotGlobalsAreLeftAlone()
        {
            //the lot clock, the family id and the house number are not ours to touch here - the
            //next session sets those up for itself.
            var original = new short[38];
            for (int i = 0; i < original.Length; i++) original[i] = (short)(i + 1);

            var result = LotEviction.ResetLotGlobals(original);

            for (int i = 0; i < original.Length; i++)
            {
                if (i == 3 || i == 17 || i == 20 || i == 25) continue;
                Assert.Equal(original[i], result[i]);
            }
        }

        [Fact]
        public void ResettingTheGlobalsDoesNotMutateTheLotItWasGiven()
        {
            //the caller keeps the deserialized marshal; quietly overwriting its globals would make
            //it impossible to fall back to the values as they were.
            var original = new short[38];

            LotEviction.ResetLotGlobals(original);

            Assert.All(original, value => Assert.Equal(0, value));
        }

        [Fact]
        public void ShortGlobalArraysAreToleratedRatherThanThrownAt()
        {
            //a corrupt save with fewer globals than the normal layout still has to be evictable.
            var result = LotEviction.ResetLotGlobals(new short[2]);

            Assert.Equal(2, result.Length);
            Assert.Equal(0, result[0]);
            Assert.Equal(0, result[1]);
        }

        [Fact]
        public void AGlobalsArrayThatIsTooShortForAResetStillGetsWhatItCan()
        {
            //globals 17/20/25 are past the end, so only the selection is set - and nothing throws.
            var result = LotEviction.ResetLotGlobals(new short[4]);

            Assert.Equal(0, result[3]);
            Assert.Equal(4, result.Length);
        }

        [Fact]
        public void NoGlobalsIsNoGlobals()
        {
            Assert.Null(LotEviction.ResetLotGlobals(null));
        }

        // ------------------------------------------------------------ blank threads

        [Fact]
        public void ASurvivingObjectsThreadIsEmpty()
        {
            //nothing the evicted family set in motion should still be queued when the next family
            //walks in.
            var thread = LotEviction.CreateBlankThread();

            Assert.Empty(thread.Stack);
            Assert.Empty(thread.Queue);
            Assert.Equal(-1, thread.ActiveQueueBlock);
        }

        [Fact]
        public void ASurvivingObjectsThreadIsShapedLikeALoadedOne()
        {
            //20 temp registers and 2 extended locals, because that is what the VM expects to find;
            //a zero-length array is not what a thread that came out of a load looks like.
            var thread = LotEviction.CreateBlankThread();

            Assert.Equal(20, thread.TempRegisters.Length);
            Assert.Equal(2, thread.TempXL.Length);
            Assert.All(thread.TempRegisters, value => Assert.Equal(0, value));
            Assert.All(thread.TempXL, value => Assert.Equal(0, value));
        }

        [Fact]
        public void EachSurvivingObjectGetsItsOwnThread()
        {
            //the threads array is index-aligned with the entity array, so handing the same instance
            //to two objects would leave one of them sharing the other's state.
            var first = LotEviction.CreateBlankThread();
            var second = LotEviction.CreateBlankThread();

            Assert.NotSame(first, second);
            Assert.NotSame(first.TempRegisters, second.TempRegisters);
            Assert.IsType<VMThreadMarshal>(first);
        }

        // ------------------------------------------------------------ the filter as a whole

        [Fact]
        public void FilteringALotLeavesTheLotStandingAndTheFamilyGone()
        {
            //the shape of the whole change, stated over a list of objects the way the eviction sees
            //them. Each entry is (guid, isAvatar, isController, buildModeType, functionFlags).
            var lot = new[]
            {
                (Guid: Sofa,       Avatar: false, Controller: false, Build: 0, Flags: 1),  // a sofa the family bought
                (Guid: Mailbox,    Avatar: false, Controller: false, Build: 0, Flags: 1),  // a catalog object that is the lot
                (Guid: Controller, Avatar: false, Controller: true,  Build: 0, Flags: 4),  // the mailman
                (Guid: Door,       Avatar: false, Controller: false, Build: 1, Flags: 1),  // the front door
                (Guid: TrashBin,   Avatar: false, Controller: false, Build: 0, Flags: 1),  // the bin
                (Guid: Sofa + 1,   Avatar: true,  Controller: false, Build: 0, Flags: 1),  // a Sim
            };

            var result = lot
                .Select(o => LotEviction.Classify(o.Guid, o.Avatar, o.Controller, false, true, o.Build, o.Flags))
                .ToArray();

            Assert.Equal(
                new[]
                {
                    LotEviction.Disposition.Remove,         // the sofa
                    LotEviction.Disposition.Preserve,       // the mailbox
                    LotEviction.Disposition.PreserveAndReset, // the mailman
                    LotEviction.Disposition.Preserve,       // the door
                    LotEviction.Disposition.Preserve,       // the bin
                    LotEviction.Disposition.Remove,         // the Sim
                },
                result);

            //two objects gone, four left, and exactly one of the four is wiped
            Assert.Equal(2, result.Count(d => d == LotEviction.Disposition.Remove));
            Assert.Equal(1, result.Count(d => d == LotEviction.Disposition.PreserveAndReset));
            Assert.Equal(3, result.Count(d => d == LotEviction.Disposition.Preserve));
        }

        [Fact]
        public void OnlyTheControllersAreWiped()
        {
            //wiping everything would take the doors' and the mailbox's state with it; wiping nothing
            //is the original bug in a different shape.
            var door = LotEviction.Classify(Door, false, false, false, true, 1, 1);
            var mailbox = LotEviction.Classify(Mailbox, false, false, false, true, 0, 1);
            var controller = LotEviction.Classify(Controller, false, true, false, true, 0, 4);

            Assert.Equal(LotEviction.Disposition.Preserve, door);
            Assert.Equal(LotEviction.Disposition.Preserve, mailbox);
            Assert.Equal(LotEviction.Disposition.PreserveAndReset, controller);
        }

        [Fact]
        public void TheFilterIsDeterministic()
        {
            //it runs once per object against a lot file that is about to be overwritten, so two
            //different answers for one object would mean one of them was accidental.
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(LotEviction.Disposition.Remove,
                    LotEviction.Classify(Sofa, false, false, false, true, 0, 1));
                Assert.Equal(LotEviction.Disposition.Preserve,
                    LotEviction.Classify(Mailbox, false, false, false, true, 0, 1));
            }
        }
    }
}
