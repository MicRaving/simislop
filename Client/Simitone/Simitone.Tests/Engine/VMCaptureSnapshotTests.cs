using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.Platform;
using FSO.SimAntics.Model.TS1Platform;
using FSO.SimAntics.Model.TSOPlatform;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// What a capture owns. <c>VM.Save</c> is not a snapshot on the paths that keep its result: the
    /// server driver serializes a state sync on a background task while the lot thread carries on,
    /// and the sandbox and blueprint paths hold a marshal for as long as they like. So every
    /// structure a capture hands over has to be detached from the live one, and every structure it
    /// deliberately shares has to say so.
    ///
    /// <para>
    /// These are the tests for that decision. The rules themselves are plain data copies, so they
    /// run without a VM, a lot, content or a graphics device - a test process has none of them.
    /// </para>
    /// </summary>
    public sealed class VMCaptureSnapshotTests
    {
        // ---- a motive rate change (E-22) ----------------------------------------------------

        [Fact]
        public void AMotiveRateChangeIsCopiedDownToTheElement()
        {
            // The array clone alone aliases sixteen live objects, because they are classes. The
            // element is the mutable part: VMAvatar.Tick writes PerHourChange, MaxValue, Ticked and
            // the private fractional accumulator on all sixteen, every frame.
            var live = new VMMotiveChange
            {
                PerHourChange = -50,
                MaxValue = 30,
                Motive = VMMotive.Hunger,
                Ticked = true,
            };

            var copy = live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.Equal(live.PerHourChange, copy.PerHourChange);
            Assert.Equal(live.MaxValue, copy.MaxValue);
            Assert.Equal(live.Motive, copy.Motive);
            Assert.Equal(live.Ticked, copy.Ticked);
        }

        [Fact]
        public void AMotiveRateTableIsCopiedElementForElement()
        {
            var live = Enumerable.Range(0, 16)
                .Select(i => new VMMotiveChange { PerHourChange = (short)(-i), MaxValue = (short)i })
                .ToArray();

            var copy = VMAvatar.SnapshotMotiveChanges(live);

            Assert.NotSame(live, copy);
            Assert.Equal(16, copy.Length);
            for (int i = 0; i < 16; i++)
            {
                Assert.NotSame(live[i], copy[i]);
                Assert.Equal(live[i].PerHourChange, copy[i].PerHourChange);
                Assert.Equal(live[i].MaxValue, copy[i].MaxValue);
            }
        }

        [Fact]
        public void AMotiveRateTableWithAHoleInItCopiesTheHoleAsHole()
        {
            // A save can carry a null entry; VMMotiveChange.ClearOne already treats null as "no
            // change here". The copy must not invent one.
            var live = new VMMotiveChange[3];
            live[1] = new VMMotiveChange { PerHourChange = 5 };

            var copy = VMAvatar.SnapshotMotiveChanges(live);

            Assert.Null(copy[0]);
            Assert.NotSame(live[1], copy[1]);
            Assert.Equal(5, copy[1].PerHourChange);
            Assert.Null(VMAvatar.SnapshotMotiveChanges(null));
        }

        // ---- motive decay (E-22) ------------------------------------------------------------

        [Fact]
        public void AMotiveDecaysFractionTableIsCopied()
        {
            var live = new VMTS1MotiveDecay { LastMinute = 12 };
            live.MotiveFractions[3] = 250;

            var copy = live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.NotSame(live.MotiveFractions, ((VMTS1MotiveDecay)copy).MotiveFractions);
            Assert.Equal(12, ((VMTS1MotiveDecay)copy).LastMinute);
            Assert.Equal(250, ((VMTS1MotiveDecay)copy).MotiveFractions[3]);
        }

        [Fact]
        public void DecayingTheSnapshotDoesNotDecayTheAvatar()
        {
            // The shape of the bug: the capture went quiet while the lot kept running, because the
            // marshal held the very object VMAvatar.Tick writes to.
            var live = new VMTS1MotiveDecay { LastMinute = 1 };
            var copy = (VMTS1MotiveDecay)VMAvatar.SnapshotMotiveDecay(live);

            live.MotiveFractions[0] = 999;
            live.LastMinute = 77;

            Assert.Equal(0, copy.MotiveFractions[0]);
            Assert.Equal(1, copy.LastMinute);
        }

        [Fact]
        public void ThereIsNoMotiveDecayToCopyIsNotAnError()
        {
            Assert.Null(VMAvatar.SnapshotMotiveDecay(null));
        }

        // ---- outfits and decorations (E-22) -------------------------------------------------

        [Fact]
        public void AnOutfitReferenceIsCopiedByValueAndKeepsItsContent()
        {
            var live = new VMOutfitReference(0x24C0000000D);

            var copy = live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.Equal(live.ID, copy.ID);

            // Shared on purpose: outfit content is content-database state, is immutable, and is
            // not what the save format writes. Stated here so the choice is asserted, not implied.
            Assert.Same(live.OftData, copy.OftData);
        }

        [Fact]
        public void AnOutfitReferenceThatIsNamedRatherThanNumberedKeepsItsName()
        {
            // ID == uint.MaxValue means the name is the identity, and the format writes the name
            // in that case. A copy that lost it would serialize as an outfit with no id at all.
            var live = new VMOutfitReference("sim_1234_abcdefg.oft");

            var copy = live.Snapshot();

            Assert.Equal(uint.MaxValue, copy.ID);
            Assert.Equal("sim_1234_abcdefg.oft", copy.Name);
            Assert.NotSame(live, copy);
        }

        [Fact]
        public void TheDefaultSuitsAreThreeIndependentReferences()
        {
            var live = new VMAvatarDefaultSuits(false);

            var copy = live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.NotSame(live.Daywear, copy.Daywear);
            Assert.NotSame(live.Swimwear, copy.Swimwear);
            Assert.NotSame(live.Sleepwear, copy.Sleepwear);
            Assert.Equal(live.Daywear.ID, copy.Daywear.ID);
            Assert.Equal(live.Swimwear.ID, copy.Swimwear.ID);
            Assert.Equal(live.Sleepwear.ID, copy.Sleepwear.ID);
        }

        [Fact]
        public void AChangeSuitInteractionDoesNotRewriteACapture()
        {
            // VMChangeSuitOrAccessory assigns DefaultSuits.Daywear, and VMNetSetOutfitCmd assigns
            // every field of DynamicSuits and Decoration. Both are ordinary mid-capture writes.
            var suits = new VMAvatarDefaultSuits(false);
            var dynamic = new VMAvatarDynamicSuits(false);
            var decoration = new VMAvatarDecoration();
            decoration.Head = 7;

            var suitsCopy = suits.Snapshot();
            var dynamicCopy = dynamic.Snapshot();
            var decorationCopy = decoration.Snapshot();

            var daywearBefore = suits.Daywear.ID;
            var dynamicDaywearBefore = dynamic.Daywear;

            suits.Daywear = new VMOutfitReference(0x1234);
            dynamic.Daywear = 0x99;
            decoration.Head = 0x88;

            Assert.Equal(daywearBefore, suitsCopy.Daywear.ID);
            Assert.Equal(dynamicDaywearBefore, dynamicCopy.Daywear);
            Assert.Equal(7UL, decorationCopy.Head);
        }

        // ---- the motive tables an avatar loads (E-36) ---------------------------------------

        [Fact]
        public void ASaveWithAShortMotiveTableIsFlooredToTheSixteenSlotsAnAvatarHas()
        {
            // The bug this closes: VMAvatarMarshal sizes both motive arrays from a count in the file
            // and does not floor them, three lines after flooring person data to 101. A save with
            // twelve motives gave an avatar with twelve motive slots.
            var table = VMAvatar.FloorMotiveTable(new short[12]);

            Assert.Equal(16, table.Length);
            Assert.All(table, x => Assert.Equal((short)0, x));
        }

        [Fact]
        public void TheValuesASaveDidCarrySurviveTheFloor()
        {
            var source = Enumerable.Range(0, 12).Select(i => (short)(i * 5)).ToArray();

            var table = VMAvatar.FloorMotiveTable(source);

            Assert.Equal(16, table.Length);
            for (int i = 0; i < 12; i++) Assert.Equal(source[i], table[i]);
        }

        [Fact]
        public void AMotiveTableOfTheRightLengthIsNotCopiedForNothing()
        {
            var source = new short[16];
            source[0] = 42;

            var table = VMAvatar.FloorMotiveTable(source);

            Assert.Equal(16, table.Length);
            Assert.Equal(42, table[0]);
        }

        [Fact]
        public void AnAbsentMotiveTableBecomesTheNeutralOne()
        {
            Assert.Equal(16, VMAvatar.FloorMotiveTable(null).Length);
        }

        [Fact]
        public void AMotiveRateTableIsFlooredToTheSameSixteenSlots()
        {
            var source = new[] { new VMMotiveChange { PerHourChange = 3 } };

            var table = VMAvatar.FloorMotiveChanges(source);

            Assert.Equal(16, table.Length);
            Assert.Equal(3, table[0].PerHourChange);
            // the padding is made of real rate changes, not holes: ClearOne and VMAvatar.Tick both
            // walk this array
            Assert.All(table.Skip(1), x => Assert.NotNull(x));
        }

        [Fact]
        public void AHoleInTheSavesRateTableIsFilledWithARealRateChange()
        {
            var source = new VMMotiveChange[3];
            source[1] = new VMMotiveChange { PerHourChange = 5 };

            var table = VMAvatar.FloorMotiveChanges(source);

            Assert.Equal(16, table.Length);
            Assert.NotNull(table[0]);
            Assert.Equal(5, table[1].PerHourChange);
            Assert.NotNull(table[15]);
        }

        // ---- the over-limit rule and its dead twin (E-37) ----------------------------------

        [Fact]
        public void ARateChangeStopsAtAMotiveThatIsAlreadyPastItsLimit()
        {
            // The single predicate, now named. A positive rate can only push a motive up, so it
            // stops at one that is already over its maximum; a negative one stops at one already
            // under. Past that the change does nothing - it does NOT clamp.
            Assert.True(VMMotiveChange.IsPastLimit(31, 30, 0.01));
            Assert.True(VMMotiveChange.IsPastLimit(-31, -30, -0.01));
        }

        [Fact]
        public void ARateChangeInsideItsRangeStillRuns()
        {
            Assert.False(VMMotiveChange.IsPastLimit(30, 30, 0.01));
            Assert.False(VMMotiveChange.IsPastLimit(0, 100, 0.01));
            Assert.False(VMMotiveChange.IsPastLimit(100, 0, -0.01));
        }

        [Fact]
        public void ARateChangeOnlyStopsWhenItIsAlreadyGoingThatWay()
        {
            // The mirror image is the point: a positive rate does not stop at a motive that is
            // *under* the limit, or every overfill would be frozen.
            Assert.False(VMMotiveChange.IsPastLimit(0, 30, 0.01));
            Assert.False(VMMotiveChange.IsPastLimit(0, -30, -0.01));
        }

        [Fact]
        public void ARateChangeForAMotiveThatDoesNotExistIsRefusedRatherThanThrownOn()
        {
            // Motive is deserialised from a raw byte, so 16..255 is reachable from a save, and
            // GetMotiveData throws on it. That throw happens inside VMAvatar.Tick, which takes the
            // sim's whole thread with it.
            Assert.True(VMMotiveChange.AppliesTo(VMMotive.HappyLife));
            Assert.True(VMMotiveChange.AppliesTo(VMMotive.Fun));

            Assert.False(VMMotiveChange.AppliesTo((VMMotive)16));
            Assert.False(VMMotiveChange.AppliesTo((VMMotive)255));
        }

        // ---- the lot state half of a capture (E-39) ------------------------------------------

        [Fact]
        public void ACapturedLotStateIsNotTheLotsOwnObject()
        {
            var live = new VMTSOLotState(VMMarshal.LATEST_VERSION)
            {
                Name = "Test Lot",
                LotID = 4242,
                OwnerID = 99,
                NhoodID = 7,
                PropertyCategory = 10,
            };
            live.Roommates.Add(11);
            live.ChatChannels.Add(new VMTSOChatChannel { ID = 3, Name = "local" });

            var copy = (VMTSOLotState)live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.Equal("Test Lot", copy.Name);
            Assert.Equal(4242u, copy.LotID);
            Assert.Equal(99u, copy.OwnerID);
            Assert.Equal(7u, copy.NhoodID);
            Assert.Equal(10, copy.PropertyCategory);
            Assert.Equal(new uint[] { 11 }, copy.Roommates);
            Assert.Equal("local", Assert.Single(copy.ChatChannels).Name);
        }

        [Fact]
        public void ACapturedLotStateDoesNotMoveWithTheLot()
        {
            // This is the whole point. VMServerDriver.PrepareSync captures on the lot thread and
            // serializes the state sync under Task.Run, so between the two the lot thread keeps
            // running and keeps writing to this object.
            var live = new VMTSOLotState(VMMarshal.LATEST_VERSION) { OwnerID = 1, Name = "before" };
            live.ChatChannels.Add(new VMTSOChatChannel { ID = 1, Name = "channel" });
            var copy = (VMTSOLotState)live.Snapshot();

            live.OwnerID = 2;
            live.Name = "after";
            live.Roommates.Add(55);
            live.ChatChannels.Clear();

            Assert.Equal(1u, copy.OwnerID);
            Assert.Equal("before", copy.Name);
            Assert.Empty(copy.Roommates);
            Assert.Equal("channel", Assert.Single(copy.ChatChannels).Name);
        }

        [Fact]
        public void ACapturedLotStateSerializesToTheBytesItWasCapturedFrom()
        {
            // The copy has to be a copy of the *state*, not a lossy projection of it, or loading it
            // back quietly changes the lot. Round-tripping through the format is how that is
            // guaranteed rather than hoped for.
            var live = new VMTSOLotState(VMMarshal.LATEST_VERSION)
            {
                Name = "Round Trip",
                LotID = 12,
                OwnerID = 34,
                Size = 0x00010103,
                SkillMode = 2,
                ObjectLimit = 250,
                DonateLimit = 500,
            };
            live.Roommates.Add(1);
            live.Roommates.Add(2);
            live.BuildRoommates.Add(9);

            var copy = (VMTSOLotState)live.Snapshot();

            Assert.Equal(Bytes(live), Bytes(copy));
        }

        [Fact]
        public void ACapturedTs1LotStateCarriesItsChunks()
        {
            var live = new VMTS1LotState(VMMarshal.LATEST_VERSION)
            {
                SimulationInfo = new SIMI
                {
                    ChunkID = 1,
                    ChunkType = "SIMI",
                    ChunkLabel = "Lot Information",
                    ChunkProcessed = true,
                    LotValue = 1000,
                    ObjectsValue = 8500,
                    ArchitectureValue = 4000,
                    GlobalData = new short[38],
                    BudgetDays = Enumerable.Range(0, 6).Select(_ => new SIMI.SIMIBudgetDay()).ToArray(),
                },
            };
            live.SimulationInfo.GlobalData[25] = 4;

            var copy = (VMTS1LotState)live.Snapshot();

            Assert.NotSame(live, copy);
            Assert.NotNull(copy.SimulationInfo);
            Assert.Equal(live.SimulationInfo.LotValue, copy.SimulationInfo.LotValue);
            Assert.Equal(live.SimulationInfo.ObjectsValue, copy.SimulationInfo.ObjectsValue);
            Assert.Equal(4, copy.SimulationInfo.GlobalData[25]);
        }

        [Fact]
        public void ACapturedTs1LotStateDoesNotAliasTheSimisGlobalData()
        {
            // VMTS1LotState.UpdateSIMI points SimulationInfo.GlobalData at the VM's live
            // GlobalState array. A capture that shares that array is a capture of the whole game
            // state, sixteen times over, for every lot that gets saved.
            var liveGlobal = new short[38];
            var live = new VMTS1LotState(VMMarshal.LATEST_VERSION)
            {
                SimulationInfo = new SIMI
                {
                    ChunkID = 1,
                    ChunkType = "SIMI",
                    ChunkLabel = "Lot Information",
                    ChunkProcessed = true,
                    GlobalData = liveGlobal,
                    BudgetDays = Enumerable.Range(0, 6).Select(_ => new SIMI.SIMIBudgetDay()).ToArray(),
                },
            };

            var copy = (VMTS1LotState)live.Snapshot();

            liveGlobal[25] = 4;
            live.SimulationInfo.LotValue = 999;

            Assert.NotSame(liveGlobal, copy.SimulationInfo.GlobalData);
            Assert.Equal(0, copy.SimulationInfo.GlobalData[25]);
            Assert.NotEqual(999, copy.SimulationInfo.LotValue);
        }

        [Fact]
        public void ThereIsNoLotStateToCopyAtAll()
        {
            // VM.Save calls this through `?.`, which is the only way it can be reached: an instance
            // method on a null reference is a NullReferenceException before the body runs.
            VMAbstractLotState none = null;
            Assert.Null(none?.Snapshot());
        }

        // ---- the architecture half of a capture (E-39) --------------------------------------

        [Fact]
        public void TheArchitecturesSnapshotIsTakenAtSaveTimeAndNotAfterwards()
        {
            // VMArchitecture.Save hands Walls, Floors, Terrain and FineBuildableArea over by
            // reference and says so - and it is still a real snapshot, because Preserialize writes
            // the whole lot to Preserialized on the lot thread and SerializeInto then returns those
            // bytes without touching a tile. This is the test that says so, because the comment
            // alone is how the audit read it the other way.
            var marshal = Architecture();

            marshal.Preserialize();
            var captured = (byte[])marshal.Preserialized.Clone();

            //the lot is edited after the capture, on the lot thread, as it always is
            marshal.Walls[0][0].Segments = (WallSegments)0xF;
            marshal.Floors[0][1].Pattern = 3;
            marshal.FineBuildableArea[2] = true;

            Assert.Equal(captured, marshal.Preserialized);

            var written = new MemoryStream();
            using (var writer = new BinaryWriter(written)) marshal.SerializeInto(writer);
            Assert.Equal(captured, written.ToArray());
        }

        [Fact]
        public void TheArchitecturesSnapshotIsWhatALoadActuallyReads()
        {
            var marshal = Architecture();
            marshal.Preserialize();
            var capturedSegments = marshal.Walls[0][0].Segments;

            marshal.Walls[0][0].Segments = (WallSegments)0xF; //edited after the capture

            var read = new VMArchitectureMarshal(VMMarshal.LATEST_VERSION);
            using (var reader = new BinaryReader(new MemoryStream(marshal.Preserialized)))
            {
                read.Deserialize(reader);
            }

            //the state as captured, not as edited
            Assert.Equal(capturedSegments, read.Walls[0][0].Segments);
            Assert.NotEqual(marshal.Walls[0][0].Segments, read.Walls[0][0].Segments);
        }

        // ---- helpers -----------------------------------------------------------------------

        /// <summary>A four-by-four, one-storey lot with a wall, a floor and a fine buildable area.</summary>
        private static VMArchitectureMarshal Architecture()
        {
            var size = 4 * 4;
            var marshal = new VMArchitectureMarshal(VMMarshal.LATEST_VERSION)
            {
                Width = 4,
                Height = 4,
                Stories = 1,
                Terrain = new VMArchitectureTerrain(4, 4),
                Walls = new[] { new WallTile[size] },
                Floors = new[] { new FloorTile[size] },
                FineBuildableArea = new bool[size],
            };
            marshal.Walls[0][0].Segments = (WallSegments)0x3;
            marshal.Floors[0][1].Pattern = 1;
            marshal.FineBuildableArea[1] = true;
            return marshal;
        }

        private static byte[] Bytes(VMTSOLotState state)
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = new BinaryWriter(buffer)) state.SerializeInto(writer);
                return buffer.ToArray();
            }
        }
    }
}
