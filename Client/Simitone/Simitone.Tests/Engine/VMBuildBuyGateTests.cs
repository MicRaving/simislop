using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// Lot build/buy gating, which is TS1 global call 10 (<c>DisableBuildBuy</c>) and 11
    /// (<c>EnableBuildBuy</c>).
    ///
    /// <para>
    /// The bug: both calls only ever flipped a bare <c>bool</c> on the lot, and the callers pair
    /// them across instructions of one bhav. <c>ExpansionPack5/NPC_Unleased_AnimalControl.iff</c>
    /// ("NPC - Unleashed - Animal Control") disables at <c>PersonGlobals</c> bhav 4096
    /// <c>Main</c> <c>[12]</c> and enables at <c>[13]</c>, and its <c>Main</c> tree is
    /// <c>idle</c> / <c>Wait For Notify</c> / <c>Leave</c> - so if the NPC leaves, is removed, or
    /// is still waiting when the lot unloads, the <c>[13]</c> never runs, the bool stays false,
    /// and because the bool was marshalled, every later load of that lot restored the false again
    /// with nothing left able to clear it. Playtest symptom: build/buy permanently greyed out
    /// after the animal control service was called. The same shape is in <c>Fire.iff</c>,
    /// <c>RepoMan</c>, the thief, the <c>DTController</c> "Sanitation Engineering" and the
    /// <c>VacationController</c>.
    /// </para>
    ///
    /// <para>
    /// The fix makes the flag a derivation over the set of objects that hold a disable
    /// (<see cref="VMBuildBuyGate"/>): a hold is taken by the object that called 10, given up by
    /// that object calling 11, or dropped by the lot when the object is removed. Build/buy is on
    /// exactly when nothing holds it. These tests drive the decision core directly, with strings
    /// standing in for objects, so they need no lot, no content and no graphics device - which is
    /// also why the core was written as a separate pure type rather than left inline in
    /// <c>VMArchitecture</c>. What is <em>not</em> covered here, because it needs a running
    /// lot, is the engine-side half: that <c>VMEntity.Dead</c>'s setter drops the hold. That is
    /// covered structurally by <c>BuildBuyGateTests</c> in the UI suite.
    /// </para>
    /// </summary>
    public sealed class VMBuildBuyGateTests
    {
        // ---- the rule -------------------------------------------------------------------

        [Fact]
        public void NoHolderMeansBuildBuyIsOn()
        {
            Assert.True(VMBuildBuyGate.IsEnabled(0));
        }

        [Fact]
        public void OneLiveHolderKeepsBuildBuyOff()
        {
            // the retail half, and the half that must not regress: while a disabling event really
            // is live on the lot, build/buy stays unavailable.
            Assert.False(VMBuildBuyGate.IsEnabled(1));
            Assert.False(VMBuildBuyGate.IsEnabled(2));
        }

        [Fact]
        public void OnlyRemovalMakesAHolderStopCounting()
        {
            // A holder is live until the engine says it is gone. There is deliberately nothing else
            // in the test - notably not its position, because a TS1 lot controller sits at
            // OUT_OF_WORLD for its whole life, and treating "moved off the lot" as gone would
            // throw away the hold of the very objects that legitimately take one.
            Assert.True(VMBuildBuyGate.IsLiveHolder(dead: false));
            Assert.False(VMBuildBuyGate.IsLiveHolder(dead: true));
        }

        // ---- the recompute ---------------------------------------------------------------

        [Fact]
        public void AnEmptyLotRecomputesToBuildBuyOn()
        {
            var survivors = new List<string>();

            Assert.True(VMBuildBuyGate.Recompute(new string[0], survivors, AlwaysLive));
            Assert.Empty(survivors);
        }

        [Fact]
        public void TheReportedBugTheDisablerWentAwayAndBuildBuyComesBack()
        {
            // Animal control arrives and disables. It is still on the lot.
            var holders = new HashSet<string> { "animal control" };
            var dead = new HashSet<string>();

            Assert.False(VMBuildBuyGate.Recompute(holders, new List<string>(), Live(dead)));
            Assert.Single(holders);

            // The service finishes, the NPC is taken off the lot, and its script never reaches
            // the instruction that would have re-enabled build/buy. Before the fix this is where
            // the lot stayed blocked for good - and stayed blocked after a save and reload.
            dead.Add("animal control");

            var survivors = new List<string>();
            Assert.True(VMBuildBuyGate.Recompute(holders, survivors, Live(dead)));
            Assert.Empty(survivors);
        }

        [Fact]
        public void ADisablerStillOnTheLotKeepsBuildBuyOff()
        {
            // The same recompute with the fire still burning: retail semantics preserved.
            var holders = new HashSet<string> { "fire" };
            var dead = new HashSet<string>();

            var survivors = new List<string>();

            Assert.False(VMBuildBuyGate.Recompute(holders, survivors, Live(dead)));
            Assert.Equal(new[] { "fire" }, survivors);
        }

        [Fact]
        public void OneDisablerLeavingDoesNotFreeAnother()
        {
            // The repo man takes his hold back while a fire is still on the lot. Build/buy has to
            // stay off: the survivor set, not the last caller to speak, decides.
            var holders = new HashSet<string> { "fire", "repo man" };
            var dead = new HashSet<string> { "repo man" };

            var survivors = new List<string>();

            Assert.False(VMBuildBuyGate.Recompute(holders, survivors, Live(dead)));
            Assert.Equal(new[] { "fire" }, survivors);
        }

        [Fact]
        public void RecomputeRebuildsTheSurvivorSetFromScratch()
        {
            // The fold has to be a rebuild, not a merge: a holder dropped by an earlier recompute
            // must not be able to come back, or a lot would latch off the second time round.
            var survivors = new List<string> { "stale" };
            var holders = new List<string> { "fire" };

            Assert.False(VMBuildBuyGate.Recompute(holders, survivors, AlwaysLive));
            Assert.Equal(new[] { "fire" }, survivors);
        }

        [Fact]
        public void RecomputeIsStableWhenNothingChanged()
        {
            // A lot with a fire on it recomputes every tick. It has to be a no-op each time, or
            // the derived flag would be rebuilt 60 times a second for nothing.
            var holders = new HashSet<string> { "fire" };
            var dead = new HashSet<string>();

            for (var i = 0; i < 3; i++)
            {
                var survivors = new List<string>();
                Assert.False(VMBuildBuyGate.Recompute(holders, survivors, Live(dead)));
                Assert.Equal(new[] { "fire" }, survivors);
            }
        }

        [Fact]
        public void ANullHolderIsDroppedRatherThanCounted()
        {
            // A torn-down object graph can leave a null in the set. It can never hold anything, and
            // counting it would wedge the lot in exactly the way this fixes.
            var holders = new List<string> { null, "fire" };
            var survivors = new List<string>();

            Assert.False(VMBuildBuyGate.Recompute(holders, survivors, AlwaysLive));
            Assert.Equal(new[] { "fire" }, survivors);

            Assert.True(VMBuildBuyGate.Recompute(new string[] { null }, new List<string>(), AlwaysLive));
        }

        [Fact]
        public void RecomputeRejectsTheArgumentsItCannotHonour()
        {
            var holders = new List<string> { "fire" };

            Assert.Throws<ArgumentNullException>(() => VMBuildBuyGate.Recompute(holders, null, AlwaysLive));
            Assert.Throws<ArgumentNullException>(() => VMBuildBuyGate.Recompute(holders, new List<string>(), null));
        }

        [Fact]
        public void ANullCandidateSetRecomputesToBuildBuyOn()
        {
            // Defensive: a lot with no recorded holders at all (which is what every freshly
            // loaded lot has) must read as enabled, not throw.
            Assert.True(VMBuildBuyGate.Recompute<string>(null, new List<string>(), AlwaysLive));
        }

        // ---- save compatibility ----------------------------------------------------------

        [Fact]
        public void TheLotSaveFormatIsUnchanged()
        {
            // The build/buy fix deliberately does not extend the save format. The flag is still
            // one bool, at the same offset, read and written by the same version gate as before
            // - it is simply not read back into a loaded lot, because that is what let a lot be
            // saved already wedged. Extending VMArchitectureMarshal means bumping this number, and
            // until somebody does, the two sides have to agree.
            Assert.Equal(38, VMMarshal.LATEST_VERSION);
        }

        [Fact]
        public void TheMarshalledBuildBuyBitStillRoundTrips()
        {
            // The field is still in the format, still written from the lot's state, and still
            // readable by anything that looks for it (an older build, a diagnostic tool). Only
            // VMArchitecture.Load's decision to re-derive changed, so prove the field itself is
            // intact in both directions.
            foreach (var enabled in new[] { true, false })
            {
                var written = LotMarshal(enabled);

                using var stream = new MemoryStream(written);
                using var reader = new BinaryReader(stream);
                var read = new VMArchitectureMarshal(VMMarshal.LATEST_VERSION);
                read.Deserialize(reader);

                Assert.Equal(enabled, read.BuildBuyEnabled);
            }
        }

        [Fact]
        public void TheMarshalledBuildBuyBitDefaultsToOn()
        {
            // A marshal built without reading a save - the TS1 lot activator builds one straight
            // from a HOUS chunk - has to come out enabled.
            Assert.True(new VMArchitectureMarshal().BuildBuyEnabled);
        }

        // ---- helpers ---------------------------------------------------------------------

        private static bool AlwaysLive(string holder) => true;

        /// <summary>The liveness rule the lot uses: an object holds its disable until removed.</summary>
        private static Predicate<string> Live(HashSet<string> dead) => holder => VMBuildBuyGate.IsLiveHolder(dead.Contains(holder));

        /// <summary>
        /// The smallest architecture marshal that will serialize, with its build/buy bit set.
        /// </summary>
        private static byte[] LotMarshal(bool buildBuyEnabled)
        {
            var marshal = new VMArchitectureMarshal(VMMarshal.LATEST_VERSION)
            {
                Width = 1,
                Height = 1,
                Stories = 1,
                Terrain = new VMArchitectureTerrain(1, 1),
                Walls = new[] { new WallTile[1] },
                Floors = new[] { new FloorTile[1] },
                BuildBuyEnabled = buildBuyEnabled,
            };
            marshal.Terrain.Version = VMMarshal.LATEST_VERSION;
            marshal.Preserialize();
            return marshal.Preserialized;
        }
    }
}
