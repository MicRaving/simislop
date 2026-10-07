/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The decisions behind reading a neighbour's real contents out of its own
    /// <c>HouseNN.iff</c>: which lot is worth a build, whether the answer is already in memory,
    /// whether this lot load may spend the build, and what happens to a lot that cannot be read.
    ///
    /// <para>
    /// <b>What is here is the whole of what can be here.</b> <see cref="NeighbourHollowLoader"/>
    /// is the only file in this pass that needs a graphics device, and everything it decides is
    /// separated from everything it does precisely so that this suite exists: the refusals are two
    /// field reads, the cache is two dictionaries, the budget is a comparison, and the store's
    /// own rule about what may be written over it is a state lookup. A fixture of a real packed
    /// neighbourhood would add nothing to any of them.</para>
    ///
    /// <para>
    /// <b>What is deliberately not tested, and why it cannot be.</b> The build itself - a throwaway
    /// <c>World</c> added to <c>GameFacade.Scenes</c>, a <c>VM</c> initialised over it,
    /// <c>VMTS1Activator.LoadFromIff</c>, <c>VM.HollowSave()</c>, and the teardown that unsubscribes
    /// the VM from the static <c>VM.OnBHAVChange</c> event and disposes the world. Every one of
    /// those needs a <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice"/>, a live lot
    /// view, an initialised content manager and a real <c>HouseNN.iff</c>; a unit test host has
    /// none of the four and would have to fake all four to test the fakes. The facade pass's own
    /// suite draws the same line in the same place (see
    /// <c>NeighbourhoodFacadeFailFastTests</c>), and for the same reason: the pipeline is proven
    /// by running the game, and what a test can hold it to is which lots it refuses and why.</para>
    ///
    /// <para>
    /// <b>One thing that is only half device-bound and is asserted anyway.</b> The cache-hit path
    /// must never reach for a device, and that is provable here: a loader built with
    /// <c>null</c> as its device answers a hit and refuses a miss, and the refusal it gives is the
    /// graphics-device sentence from the facade pre-flight. So a passing test on the hit path
    /// says the device was genuinely not needed, and a passing test on the miss path says the
    /// miss really did go as far as standing a world up.</para>
    /// </summary>
    public sealed class NeighbourHollowLoaderTests
    {
        /// <summary>The fixture's grid: one street and two rows of lots.</summary>
        private const int Size = 64;

        /// <summary>The row the fixture's single street runs along.</summary>
        private const int StreetY = 36;

        private const int LotSize = 16;

        /// <summary>A house-file fingerprint of the kind the packer writes.</summary>
        private const uint Hash = 0xABCD1234u;

        // ==================================================================== the fixture

        private static NeighbourhoodMap NeighbourMap()
        {
            var map = NeighbourhoodMap.Create(Size);
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, StreetY)] = RoadMask.All;
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            return map;
        }

        private static NeighbourhoodLot Lot(
            short number, int x, int y, uint houseHash = Hash, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = LotSize,
                WorldX = x,
                WorldY = y,
                Rotation = (int)NeighbourhoodLotSide.North,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                DoorSide = NeighbourhoodLotSide.North,
                HouseHash = houseHash,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                Size, lots);
        }

        private static SimitoneSurroundingsPlacement PlacementFor(
            NeighbourhoodLayout layout, NeighbourhoodMap map, short lotNumber)
        {
            SimitoneSurroundingsPlacement placement;
            string reason;
            Assert.True(SimitoneSurroundingsPlacement.TryFor(
                layout, map, lotNumber, out placement, out reason), reason);
            return placement;
        }

        /// <summary>
        /// Two rows of lots either side of the fixture's street, spaced the way a packer spaces
        /// them, all with their doors on it so all are mutually reachable: 11 is the lot a test
        /// stands on, 12 and 14 are its two nearest neighbours and 13 is across the street.
        /// </summary>
        private static NeighbourhoodLayout StreetLayout()
        {
            return LayoutOf(
                Lot(11, 4, 4),
                Lot(12, 4, 26),
                Lot(13, 40, 4),
                Lot(14, 40, 26));
        }

        private static NeighbourhoodQuery StreetQuery(
            NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            return new NeighbourhoodQuery(layout, map, new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(13, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(14, 48, StreetY, 0, -1)
            });
        }

        /// <summary>
        /// The plan over the fixture with a store behind it, which is the shape the bridge is asked
        /// about: three neighbours, none of them hollow, because nobody has visited this
        /// neighbourhood.
        /// </summary>
        private static SimitoneSurroundingsPlan Plan(
            NeighbourhoodMap map, NeighbourhoodLayout layout, LotStore store = null)
        {
            return SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map, StreetQuery(layout, map),
                layout, store, 64);
        }

        private static NeighbourHollowLoader Loader(int maxBuilds = 4)
        {
            //No device on purpose: every test here is about a decision, and a loader with a device
            //in hand would quietly be able to do the one thing these tests must not depend on.
            return new NeighbourHollowLoader(null, maxBuilds);
        }

        // ====================================================== which lot is worth a build

        [Fact]
        public void ALotWithNoHouseIsRefusedBeforeAnythingIsTouched()
        {
            // The two refusals that cost nothing: a field read each, and both of them have to run
            // before a world is stood up, because a bulldozed plot is the common case in a
            // generated neighbourhood and must not cost a device.
            Assert.Null(NeighbourHollowLoader.BuildRefusalFor(Lot(12, 4, 26)));

            var noHouse = NeighbourHollowLoader.BuildRefusalFor(Lot(12, 4, 26, Hash, false));
            Assert.NotNull(noHouse);
            Assert.Contains("no house file", noHouse);
            Assert.Contains("12", noHouse);

            // No fingerprint is not a build either: a hash of zero is what a lot the packer could
            // not read reports, and it is also not a usable cache key - anything cached under it
            // would be served forever.
            var noHash = NeighbourHollowLoader.BuildRefusalFor(Lot(12, 4, 26, 0u));
            Assert.NotNull(noHash);
            Assert.Contains("fingerprint", noHash);

            Assert.NotNull(NeighbourHollowLoader.BuildRefusalFor(null));
            Assert.False(NeighbourHollowLoader.NeedsBuild(null, null));
        }

        [Fact]
        public void AnEmptyPlanNeedsNoBuildsAndAFilledOneNeverNeedsItsOwnBack()
        {
            var lot = Lot(12, 4, 26);

            // No cache at all is the ordinary case - the first time this street is walked - and
            // every lot with a house file in it needs one.
            Assert.True(NeighbourHollowLoader.NeedsBuild(lot, null));
            Assert.True(NeighbourHollowLoader.NeedsBuild(lot, new Dictionary<short, uint>()));

            // Remembered from this file: nothing to do.
            var hashes = new Dictionary<short, uint> { { 12, Hash } };
            Assert.False(NeighbourHollowLoader.NeedsBuild(lot, hashes));

            // The file moved: the contents in memory are the contents of a file that no longer
            // exists, so this is the case the hash is there for.
            hashes[12] = Hash + 1u;
            Assert.True(NeighbourHollowLoader.NeedsBuild(lot, hashes));

            // A lot with no house is not "needs a build", it is "has nothing to build". The two
            // rules cannot disagree about a bulldozed plot.
            Assert.False(NeighbourHollowLoader.NeedsBuild(Lot(12, 4, 26, 0u), null));
            Assert.False(NeighbourHollowLoader.NeedsBuild(Lot(12, 4, 26, Hash, false), null));
        }

        // ==================================================================== the cache

        [Fact]
        public void ACachedMarshalIsServedWithoutAskingForADevice()
        {
            // The whole point of the cache, and the only way to show it: a loader holding no
            // device at all answers this lot, because a hit is a dictionary lookup and never
            // reaches the build path. If this test ever needs a device the cache has stopped
            // being one.
            var loader = Loader();
            var marshal = new VMHollowMarshal();
            loader.Remember(12, Hash, marshal);

            VMHollowMarshal cached;
            Assert.True(loader.TryGetCached(12, Hash, out cached));
            Assert.Same(marshal, cached);

            var lot = Lot(12, 4, 26);
            VMHollowMarshal loaded;
            string reason;
            Assert.True(loader.TryLoad(lot, out loaded, out reason));
            Assert.Same(marshal, loaded);
            Assert.Null(reason);
            Assert.Equal(1, loader.CachedCount);
            Assert.Equal(0, loader.BuiltCount);
            Assert.Equal(0, loader.Attempts);
        }

        [Fact]
        public void AHouseFileThatChangedIsAMissRatherThanAStaleHit()
        {
            var loader = Loader();
            loader.Remember(12, Hash, new VMHollowMarshal());
            var rewritten = Lot(12, 4, 26, Hash + 1u);

            Assert.False(loader.TryGetCached(12, Hash + 1u, out _));
            Assert.True(NeighbourHollowLoader.NeedsBuild(rewritten, loader.CachedHashes));

            // And the miss really does go as far as standing a world up, which is observable here
            // without a device: the refusal it comes back with is the facade pre-flight's own
            // graphics-device sentence rather than a cache answer.
            VMHollowMarshal marshal;
            string reason;
            Assert.False(loader.TryLoad(rewritten, out marshal, out reason));
            Assert.Null(marshal);
            Assert.Contains("graphics device", reason);
            Assert.Equal(0, loader.CachedCount);
            Assert.Equal(1, loader.Attempts);
            Assert.Equal(1, loader.BlankCount);
        }

        [Fact]
        public void AHashWithNoMarshalBehindItIsAMiss()
        {
            // The two halves of the cache are written together, and a hit that handed back null
            // would be a lot that reported success and then built blank.
            var hashes = new Dictionary<short, uint> { { 12, Hash } };
            var marshals = new Dictionary<short, VMHollowMarshal>();

            Assert.False(NeighbourHollowLoader.TryGetCached(12, Hash, hashes, marshals, out _));
            Assert.False(NeighbourHollowLoader.TryGetCached(12, Hash, null, marshals, out _));
            Assert.False(NeighbourHollowLoader.TryGetCached(12, Hash, hashes, null, out _));
            Assert.False(NeighbourHollowLoader.TryGetCached(99, Hash, hashes, marshals, out _));

            marshals[12] = new VMHollowMarshal();
            Assert.True(NeighbourHollowLoader.TryGetCached(12, Hash, hashes, marshals, out var hit));
            Assert.NotNull(hit);
        }

        [Fact]
        public void ForgettingALotForgetsBothHalvesOfItsCacheEntry()
        {
            // A null marshal is "forget this lot", and it has to take the hash with it: a hash
            // left behind with nothing behind it is a lot that is permanently "not stale" and
            // permanently unanswerable.
            var loader = Loader();
            loader.Remember(12, Hash, new VMHollowMarshal());
            Assert.Equal(1, loader.CachedLots);

            loader.Remember(12, Hash, null);
            Assert.Equal(0, loader.CachedLots);
            Assert.False(loader.TryGetCached(12, Hash, out _));
            Assert.True(NeighbourHollowLoader.NeedsBuild(Lot(12, 4, 26), loader.CachedHashes));

            loader.Remember(12, Hash, new VMHollowMarshal());
            loader.Clear();
            Assert.Equal(0, loader.CachedLots);
            Assert.Empty(loader.CachedHashes);
        }

        // ==================================================================== the budget

        [Fact]
        public void TheFirstBuildOfAPassIsUnconditionalAndTheRestAreCounted()
        {
            // The facade pass's own rule (NeighbourhoodFacadePlan.FrameAllowance), and the reason
            // for it is the same: a queue that can never advance is indistinguishable from a queue
            // that is broken. A clock that has already run out must not refuse the first build.
            Assert.True(NeighbourHollowLoader.AllowsBuild(4, 0, 1000d, 5000d));
            Assert.False(NeighbourHollowLoader.AllowsBuild(4, 1, 1000d, 5000d));
            Assert.True(NeighbourHollowLoader.AllowsBuild(4, 3, 1000d, 999d));
            Assert.False(NeighbourHollowLoader.AllowsBuild(4, 4, 1000d, 0d));
            Assert.False(NeighbourHollowLoader.AllowsBuild(0, 0, 0d, 0d));
            //no clock means no clock
            Assert.True(NeighbourHollowLoader.AllowsBuild(1, 0, 0d, double.MaxValue));

            var loader = Loader(1);
            Assert.True(loader.AllowsBuild());

            VMHollowMarshal marshal;
            string reason;
            Assert.False(loader.TryLoad(Lot(12, 4, 26), out marshal, out reason));
            Assert.Equal(1, loader.Attempts);

            // The budget is spent, and the refusal has to say what was spent and what it costs, or
            // a street of blank neighbours is a mystery again.
            Assert.False(loader.AllowsBuild());
            Assert.False(loader.TryLoad(Lot(13, 40, 4), out marshal, out reason));
            Assert.Contains("budget", reason);
            Assert.Contains("blank", reason);
            Assert.Contains("13", reason);
            Assert.Equal(1, loader.Attempts);
            Assert.Equal(2, loader.BlankCount);
        }

        [Fact]
        public void APassStartsWithAnEmptyBudgetAndAnUntouchedCache()
        {
            // A loader a caller keeps for the whole session would otherwise answer "no" for ever
            // after one pass's worth of milliseconds and silently blank every lot after the first.
            var loader = Loader();
            loader.Remember(12, Hash, new VMHollowMarshal());

            VMHollowMarshal marshal;
            string reason;
            loader.TryLoad(Lot(13, 40, 4), out marshal, out reason);
            Assert.Equal(1, loader.Attempts);

            loader.BeginPass();
            Assert.Equal(0, loader.Attempts);
            Assert.Equal(0, loader.BuiltCount);
            Assert.Equal(0, loader.BlankCount);
            Assert.Equal(1, loader.CachedLots);
            Assert.True(loader.AllowsBuild());
        }

        [Fact]
        public void TheSummarySaysWhatThePassSpentAndWhatItAnswered()
        {
            // One line, because a neighbourhood of blank neighbours has to be distinguishable from
            // a neighbourhood whose neighbours were built - that is the whole of the log line this
            // pass was written for.
            var loader = Loader();
            loader.Remember(12, Hash, new VMHollowMarshal());

            VMHollowMarshal marshal;
            string reason;
            loader.TryLoad(Lot(12, 4, 26), out marshal, out reason);
            loader.TryLoad(Lot(13, 40, 4), out marshal, out reason);

            var described = loader.Describe();
            Assert.Contains("0 neighbour lots loaded", described);
            Assert.Contains("1 cache hit", described);
            Assert.Contains("1 blank", described);
            Assert.Contains("budget remaining", described);
        }

        // ==================================================================== the store

        [Fact]
        public void OnlyALotTheStoreHoldsNothingForMayBeWrittenOver()
        {
            // LotStore is a three-way union per lot and every writer clears the other two payloads,
            // so writing over an Active entry would throw away the resident marshal of the lot the
            // player is standing in. That lot is never a neighbour - the plan excludes it - but
            // this is asked of a store rather than of a plan, so it asks the store.
            var store = new LotStore();
            Assert.True(NeighbourHollowLoader.ShouldPublish(store, 12));
            Assert.False(NeighbourHollowLoader.ShouldPublish(null, 12));

            store.SetActive(12, new VMMarshal());
            Assert.False(NeighbourHollowLoader.ShouldPublish(store, 12));

            store.SetHollow(13, new VMHollowMarshal());
            Assert.False(NeighbourHollowLoader.ShouldPublish(store, 13));

            var loader = Loader();
            Assert.False(loader.Publish(store, 12, new VMHollowMarshal()));
            Assert.False(loader.Publish(store, 13, new VMHollowMarshal()));
            Assert.False(loader.Publish(store, 14, null));
            Assert.True(loader.Publish(store, 14, new VMHollowMarshal()));

            VMHollowMarshal read;
            Assert.True(store.TryGetHollow(14, out read));
            Assert.NotNull(read);
            //and the resident lot's own state is untouched
            VMMarshal active;
            Assert.True(store.TryGetActive(12, out active));
        }

        // ============================================================= the bridge to HollowLoad

        [Fact]
        public void APlanWithNoHollowNeighbourIsTheOnlyOneThatSkipsTheLoader()
        {
            // The one question the restore asks before it reaches for a loader at all, and the whole
            // of why this pass costs nothing in the ordinary case: a plan whose neighbours the store
            // already holds marshals for never constructs one.
            var map = NeighbourMap();
            var layout = StreetLayout();

            var noStore = Plan(map, layout);
            Assert.Equal(0, noStore.HollowCount);
            Assert.True(noStore.NeedsHollows());

            var store = new LotStore();
            foreach (var number in noStore.NeighbourNumbers) store.SetHollow(number, new VMHollowMarshal());
            var held = Plan(map, layout, store);
            Assert.Equal(noStore.Neighbours.Count, held.HollowCount);
            Assert.False(held.NeedsHollows());
        }

        [Fact]
        public void FillingInAContentsChangesTheMarshalAndNothingElse()
        {
            // The bridge, asserted from the pure side: the marshal reaches VM.HollowLoad because
            // WithHollows puts it on the neighbour and the plan's own Describe and Neighbours then
            // count it as hollow - with every other field of the plan untouched, which is what
            // makes it safe for the restore to build from the resolved plan rather than the one it
            // was handed.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout);

            var marshal = new VMHollowMarshal();
            var resolved = plan.WithHollows(new Dictionary<short, VMHollowMarshal> { { 12, marshal } },
                new[] { "neighbour 13 was built blank: its house file could not be read" });

            Assert.Equal(1, resolved.HollowCount);
            Assert.Same(marshal, resolved.Neighbours[0].Hollow);
            Assert.True(resolved.NeighbourFor(12).IsHollow);
            Assert.False(resolved.NeighbourFor(13).IsHollow);
            Assert.True(resolved.Describe().Contains("1 hollow, 2 blank"));

            // everything else is the same object or the same number
            Assert.Same(plan.ActiveLot, resolved.ActiveLot);
            Assert.Equal(plan.ActiveBaseLevel, resolved.ActiveBaseLevel);
            Assert.Equal(plan.RadiusTiles, resolved.RadiusTiles);
            Assert.Equal(plan.RoadReachability, resolved.RoadReachability);
            Assert.Equal(plan.Failure, resolved.Failure);
            Assert.Equal(plan.NeighbourNumbers, resolved.NeighbourNumbers);
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                Assert.Equal(plan.Neighbours[i].Terrain, resolved.Neighbours[i].Terrain);
                Assert.Equal(plan.Neighbours[i].SubWorldPosition, resolved.Neighbours[i].SubWorldPosition);
                Assert.Equal(plan.Neighbours[i].SubWorldSize, resolved.Neighbours[i].SubWorldSize);
                Assert.Equal(plan.Neighbours[i].LotDirection, resolved.Neighbours[i].LotDirection);
            }

            // the plan it came from is untouched, which is the rule a plan is a value because of
            Assert.Equal(0, plan.HollowCount);
            Assert.Null(plan.Neighbours[0].Hollow);
            Assert.Empty(plan.Warnings);

            // and the warnings are appended, so the plan's own reasons survive the fill-in
            Assert.Single(resolved.Warnings);
            Assert.Contains("neighbour 13", resolved.Warnings[0]);
        }

        [Fact]
        public void FillingInNothingReturnsTheVerySamePlan()
        {
            // The flag-off and store-holding-everything case, and the reason the bridge can decide
            // to stand a loader up at all: with nothing to read, the plan that comes back is the
            // plan that went in, by reference rather than by equality.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout);

            Assert.Same(plan, plan.WithHollows(null));
            Assert.Same(plan, plan.WithHollows(new Dictionary<short, VMHollowMarshal>()));
            Assert.Same(plan, plan.WithHollows(new Dictionary<short, VMHollowMarshal>(), null));

            // A marshal for a lot this plan does not build has nowhere to go, so it changes
            // nothing - but the reason it was not built does belong in the log.
            var warned = plan.WithHollows(new Dictionary<short, VMHollowMarshal> { { 99, new VMHollowMarshal() } },
                new[] { "neighbour 12 was built blank: no house file" });
            Assert.Equal(0, warned.HollowCount);
            Assert.NotSame(plan, warned);
            Assert.Single(warned.Warnings);
        }

        [Fact]
        public void TheResolvedPlanIsWhatTheNextLotLoadReadsOutOfTheStore()
        {
            // End to end without a VM: what the loader builds is published into the store, so the
            // next plan over the same street is hollow out of memory - which is the second visit
            // being free, and the reason the store is where this answer goes rather than a field
            // on the loader.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var store = new LotStore();
            var loader = Loader();

            var first = Plan(map, layout, store);
            Assert.True(first.NeedsHollows());

            foreach (var number in first.NeighbourNumbers)
            {
                VMHollowMarshal marshal;
                string reason;
                //seeded rather than built: the build itself is the device-bound half, and this is
                //the part around it
                marshal = new VMHollowMarshal();
                loader.Remember(number, Hash, marshal);
                loader.TryLoad(layout.FindLot(number), out marshal, out reason);
                Assert.True(loader.Publish(store, number, marshal), reason);
            }

            var second = Plan(map, layout, store);
            Assert.False(second.NeedsHollows());
            Assert.Equal(first.Neighbours.Count, second.HollowCount);
            Assert.Same(store, second.Store);
            Assert.Same(first.Store, second.WithHollows(null).Store);
        }
    }
}