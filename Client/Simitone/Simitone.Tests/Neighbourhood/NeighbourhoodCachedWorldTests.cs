/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// <see cref="NeighbourhoodWorldCache"/> is process-wide and its counters are cumulative, so
    /// this class's assertions about hits and misses are only true while nothing else is
    /// refreshing a world. Disabling parallelisation for the collection is what buys that, and it
    /// is scoped here rather than turned on assembly-wide: the rest of the suite is unaffected.
    ///
    /// <para>
    /// The distinct <c>Slot</c> below is the second half of the same answer. A shared slot would
    /// let another class's world be served out of the cache in the middle of one of these tests,
    /// which is a much worse failure than a flaky counter - it would make a cache hit that never
    /// happened look like a pass.
    /// </para>
    /// </summary>
    [CollectionDefinition("generated world cache", DisableParallelization = true)]
    public sealed class GeneratedWorldCacheCollection
    {
    }

    /// <summary>
    /// The neighbourhood panel asking <see cref="NeighbourhoodLifecycle"/> for its world, and
    /// what the panel does with what comes back.
    ///
    /// <para>
    /// <see cref="UINeighborhoodSelectionPanel.BuildSceneSource(int, out string)"/> used to read
    /// the layout sidecar and then call <see cref="NeighbourhoodMapGenerator.Generate"/> itself,
    /// on every attach, so the 512x512 raster - 262 144 tiles - was rebuilt even when the
    /// neighbourhood had not changed by a single lot hash. It now goes through
    /// <see cref="NeighbourhoodLifecycle.RefreshNow"/>, which is M5's cache, and this file pins
    /// the two claims that change rests on:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>A second attach is a cache hit.</b> Not "the same map" - the same <em>instance</em>,
    ///     and a hit on the counter, which is the only statement that can be made about work that
    ///     did not happen. A fresh attach to an unedited neighbourhood has to cost one small file
    ///     read, or the swap bought nothing.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Every failure is still the 2D bitmap, with its reason.</b> A refresh that returned
    ///     nothing, one that failed, and one that worked all have to come out of the panel's
    ///     hand-over as a <see cref="NeighbourhoodSceneSource"/> the existing
    ///     <c>AttachWorld</c> can act on unchanged - because the panel's contract is that a
    ///     neighbourhood always opens.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Pure with respect to the game: a temp directory, a temp user directory, canned lot records
    /// and no graphics device, provider or screen. The panel itself cannot be constructed here, so
    /// the part of the swap that has behaviour in it -
    /// <see cref="UINeighborhoodSelectionPanel.SceneSourceFor"/> - is a static rule over a value
    /// precisely so that it can be.
    /// </para>
    ///
    /// <para>
    /// <b>What the route looks like now.</b> The panel's call is
    /// <see cref="NeighbourhoodLifecycle.RefreshNow"/>, which does not wait for the world: on a
    /// cold cache it hands back a world that says it is being built and puts the work on a worker.
    /// So <see cref="Refresh"/> here is the panel's call <em>plus the one thing a test cannot
    /// borrow from a frame</em> - a wait for the refresh to finish, standing in for the game-thread
    /// apply the panel would be handed. Waiting is not what the panel does; the panel draws the
    /// painted map meanwhile, which is the guarantee
    /// <see cref="UINeighborhoodSelectionPanel.SceneSourceFor"/> carries in both cases.
    /// </para>
    /// </summary>
    [Collection("generated world cache")]
    public sealed class NeighbourhoodCachedWorldTests : IDisposable
    {
        private const int Slot = 7;
        private const int Mode = 4;

        /// <summary>How long a test waits for a background refresh that is doing real work.</summary>
        private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

        private readonly string _root;
        private readonly int _seed;

        public NeighbourhoodCachedWorldTests()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "simitone-neigh-cached-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _seed = NeighbourhoodSceneSource.SeedForMode(Slot);
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
        }

        public void Dispose()
        {
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // ---- the cache -------------------------------------------------------------------------

        [Fact]
        public void TheFirstAttachGeneratesAndTheSecondOneReusesTheSameWorld()
        {
            // The whole of the change, as an assertion. Two attaches, nothing edited in between:
            // the second must hand back the very object the first produced, because a raster is a
            // pure function of (layout, seed) and regenerating it would be a quarter of a million
            // tiles of work to produce the same bytes.
            var source = new SourceOver(FourLots());

            var first = Refresh(source);
            Assert.NotNull(first);
            Assert.True(first.HasWorld);
            Assert.Equal(NeighbourhoodRefreshKind.NoSidecar, first.Plan.Kind);
            Assert.False(first.Plan.IsCacheHit);
            Assert.Equal(MapSize(), first.Map.Width);
            Assert.Same(first, NeighbourhoodWorldCache.Peek(Slot));

            var second = Refresh(source);
            //identity, and not a plan flag: a world handed back out of the cache still carries the
            //plan from the visit that built it, so "is this plan a cache hit" is the wrong question
            //about it - and so is "does this plan want to regenerate". Being the same instance is
            //the whole of what a reuse means.
            Assert.Same(first, second);
            Assert.Same(second, NeighbourhoodWorldCache.Peek(Slot));

            //and the counters agree, which is the only thing a test can say about work that did
            //not happen. The two hits are the panel's second attach and the one that arrives after
            //the first refresh finished; the one miss and the one refresh are the generation itself,
            //however many times it was asked for.
            var stats = NeighbourhoodWorldCache.Stats;
            Assert.Equal(1, stats.Misses);
            Assert.Equal(2, stats.Hits);
            Assert.Equal(0, stats.Fallbacks);
            Assert.Equal(1, stats.Refreshes);
            Assert.Equal(0, stats.Joined);
        }

        [Fact]
        public void AColdAttachIsAnsweredAtOnceAndTheWorldArrivesAfterwards()
        {
            // The freeze, as an assertion. A cold cache must answer the panel's call immediately -
            // with no world, and with a reason that says to look again - and the generated world
            // must still be produced, on a worker, and cached for the next attach. The panel's
            // answer to "not yet" is the painted map, which it is already drawing.
            var world = Panel(new SourceOver(FourLots()));

            Assert.True(world.Pending);
            Assert.False(world.HasWorld);
            Assert.Null(world.Map);
            Assert.Equal(NeighbourhoodRefreshKind.Refreshing, world.Plan.Kind);
            //not a failure: nothing failed, and a log line must not say it did
            Assert.False(string.IsNullOrWhiteSpace(world.Failure));
            Assert.Contains("still generating", world.Describe());

            var job = NeighbourhoodLifecycle.RefreshInFlight(Slot);
            Assert.NotNull(job);
            Assert.True(job.WaitForFinish(RefreshTimeout));

            //and the next attach - the one the game thread would make when the apply runs - is a
            //hit, so the player never waits twice for the same neighbourhood
            var settled = Panel(new SourceOver(FourLots()));
            Assert.True(settled.HasWorld);
            Assert.Same(settled, NeighbourhoodWorldCache.Peek(Slot));
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
        }

        [Fact]
        public void AColdCacheWithACurrentSidecarRegeneratesTheRasterButNotTheLayout()
        {
            // The other half of "returning to a neighbourhood is free", and the case a fresh launch
            // is: the sidecar is on disk and current, but the raster is not in memory. The layout
            // must be reused verbatim - the lots must come out byte-identical - and only the
            // 512x512 regeneration is paid for.
            var reads = FourLots();
            //The shipped revert packs synthetically, so the sidecar this test plants is a shelf
            //layout - the one the refresh will arrive at. See NeighbourhoodConnected.
            var packed = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, _seed);
            Assert.True(NeighbourhoodLayoutStore.Save(_root, packed));
            var serializedBefore = NeighbourhoodLayoutStore.Serialize(packed);

            var world = Refresh(new SourceOver(reads));

            Assert.True(world.HasWorld);
            Assert.Equal(NeighbourhoodRefreshKind.LayoutUnchanged, world.Plan.Kind);
            Assert.False(world.Plan.IsCacheHit);
            Assert.True(world.Plan.RegenerateMap);
            //the crucial one: the arrangement is the one the player has been looking at
            Assert.False(world.Plan.PatchOnlyChangedLots);
            Assert.Equal(serializedBefore, NeighbourhoodLayoutStore.Serialize(world.Layout));
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Misses);
        }

        [Fact]
        public void ALotThatChangedIsRepairedAndOnlyTheRasterIsRebuilt()
        {
            // M5's cheap path, driven on the worker that does it in the game. The neighbourhood is
            // packed, one lot's house file changes underneath it, and the refresh has to reflect
            // that - without the other three lots moving, and without the whole neighbourhood
            // being packed again.
            //
            // RefreshBlocking rather than the panel's call, and that is the point of the split: the
            // hash comparison is twenty IFF reads, and it now happens where those reads cost the
            // frame nothing. A cache hit re-reads nothing at all, because a world in memory is the
            // answer until something invalidates it - and whatever invalidates it knows which lot
            // changed, which is more than a hash sweep could say on its own.
            var reads = FourLots();
            //The shipped revert packs synthetically - see the test above and NeighbourhoodConnected.
            var before = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, _seed);
            NeighbourhoodWorldCache.Store(NeighbourhoodGeneratedWorld.Built(Slot, before,
                NeighbourhoodMap.Create(MapSize()),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, null, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false)));
            Assert.True(NeighbourhoodLayoutStore.Save(_root, before));

            reads.Find(r => r.Number == 22).HouseHash = 0x9999u;
            var world = Rebuild(new SourceOver(reads));

            Assert.Equal(NeighbourhoodRefreshKind.LotsChanged, world.Plan.Kind);
            Assert.True(world.Plan.PatchOnlyChangedLots);
            Assert.Equal(new short[] { 22 }, world.Plan.LotsToRegenerate);
            Assert.Equal(0x9999u, world.Layout.FindLot(22).HouseHash);
            //the other three rectangles came out of the repair exactly where they were, which is
            //what "only that lot was regenerated" actually means
            foreach (var number in new short[] { 21, 23, 24 })
            {
                Assert.Equal(before.FindLot(number).WorldX, world.Layout.FindLot(number).WorldX);
                Assert.Equal(before.FindLot(number).WorldY, world.Layout.FindLot(number).WorldY);
            }
            Assert.Equal(0,
                NeighbourhoodLayoutBuilder.CountOverlaps(world.Layout.Lots,
                    NeighbourhoodLayoutBuilder.SidewalkMargin));
        }

        [Fact]
        public void ADroppedCacheMakesTheNextAttachARebuildRatherThanAHalfOne()
        {
            // The panel's own InvalidateGeneratedWorld does not drop the shared cache - the
            // service does, and then calls it - so the sequence a bulldoze actually produces is
            // invalidate-then-attach. This is that sequence, and it has to come out as a full
            // rebuild of the raster with the repaired layout rather than as a world with no raster
            // in it.
            var reads = FourLots();
            Refresh(new SourceOver(reads));
            var cached = NeighbourhoodWorldCache.Peek(Slot);
            Assert.NotNull(cached);

            reads.Find(r => r.Number == 24).HouseHash = 0xbeefu;
            //the cheap staleness rule has to name that lot, or the eviction below is guessing
            Assert.Equal(new short[] { 24 },
                NeighbourhoodLifecycle.StaleByFileStat(cached.Layout,
                    number => reads.Find(r => r.Number == number).HouseHash));
            Assert.True(NeighbourhoodWorldCache.InvalidateLot(Slot, 24));
            Assert.Null(NeighbourhoodWorldCache.Peek(Slot));

            var world = Refresh(new SourceOver(reads));
            Assert.True(world.HasWorld);
            Assert.Equal(0xbeefu, world.Layout.FindLot(24).HouseHash);
            Assert.Same(world, NeighbourhoodWorldCache.Peek(Slot));
            //one eviction, and it is the only one: the panel stores a world, it never drops one
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Invalidations);
        }

        [Fact]
        public void TheSettingOffGeneratesNothingAtAll()
        {
            // §0 decision 1 through the panel's new call. Before the swap the panel only reached
            // the lifecycle at all after it had checked the setting itself; now the check is
            // inside the lifecycle, so this is the assertion that the two checks agree - a player
            // who has not opted in must not pay for a world, whichever one answers.
            var world = UINeighborhoodSelectionPanel.RefreshCachedWorld(_root, Slot, Mode,
                NeighbourhoodView.Bitmap2D, new SourceOver(FourLots()));
            Assert.False(world.HasWorld);
            Assert.False(world.Pending);
            Assert.Null(world.Map);
            Assert.Equal(NeighbourhoodRefreshKind.SettingIsNotWorld3D, world.Plan.Kind);
            Assert.True(world.Plan.Selection.UseBitmap2D);
            Assert.Null(NeighbourhoodLayoutStore.Load(_root, Slot));
            Assert.Equal(0, NeighbourhoodWorldCache.Count);
            //and the strongest form of the claim: not only did it generate nothing, it did not
            //even start a worker to generate something later
            Assert.Equal(0, NeighbourhoodWorldCache.Stats.Refreshes);
            Assert.Null(NeighbourhoodLifecycle.RefreshInFlight(Slot));
        }

        [Fact]
        public void AFailedGenerationReachesThePanelAsTheBitmapWithItsReason()
        {
            // A neighbourhood whose lots cannot be read at all: the source has no location table,
            // so the layout is empty, the generator produces an empty but legal raster, and the
            // panel still gets a world. What it must not get is a source with no map and no
            // reason - that is the failure mode the 2D fallback exists to prevent.
            var world = UINeighborhoodSelectionPanel.RefreshCachedWorld(_root, Slot, Mode,
                NeighbourhoodView.World3D, new SourceOver(new List<NeighbourhoodLotRead>()));
            var job = NeighbourhoodLifecycle.RefreshInFlight(Slot);
            Assert.NotNull(job);
            Assert.True(job.WaitForFinish(RefreshTimeout));
            world = UINeighborhoodSelectionPanel.RefreshCachedWorld(_root, Slot, Mode,
                NeighbourhoodView.World3D, new SourceOver(new List<NeighbourhoodLotRead>()));

            Assert.False(world.Pending);
            var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out var failure);
            Assert.Null(failure);
            Assert.True(source.HasWorld);
        }

        [Fact]
        public void AWorldSupersededByABulldozeIsNotHandedToTheNextAttach()
        {
            // The race the background refresh introduced and the epoch exists for. A refresh reads
            // the lot files, then takes seconds to pack and generate; a bulldoze can land in the
            // middle. The world that comes out describes the house files as they were before it,
            // so storing it would put a world with a house back on the lot that was just emptied
            // and hand it to the next attach as a hit.
            var reads = FourLots();
            Refresh(new SourceOver(reads));
            var before = NeighbourhoodWorldCache.Peek(Slot);
            Assert.NotNull(before);

            //a refresh that starts, and a bulldoze that lands while it is running
            var stale = NeighbourhoodGeneratedWorld.Built(Slot, before.Layout,
                NeighbourhoodMap.Create(MapSize()),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, before.Layout, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, true));
            var epoch = NeighbourhoodWorldCache.EpochOf(Slot);
            Assert.True(NeighbourhoodWorldCache.InvalidateLot(Slot, 24));

            Assert.False(NeighbourhoodWorldCache.TryStore(stale, epoch));
            Assert.Null(NeighbourhoodWorldCache.Peek(Slot));
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Superseded);

            //and the next attach starts a fresh one rather than being answered with it
            var world = Refresh(new SourceOver(reads));
            Assert.True(world.HasWorld);
            Assert.NotSame(stale, world);
            Assert.Equal(2, NeighbourhoodWorldCache.Stats.Refreshes);
        }

        // ---- what the panel does with what comes back --------------------------------------------

        [Fact]
        public void AWorldBecomesASceneSourceThePanelCanDraw()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var world = NeighbourhoodGeneratedWorld.Built(Slot, layout,
                NeighbourhoodMap.Create(MapSize()),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false));

            var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out var failure);

            Assert.Null(failure);
            Assert.True(source.HasWorld);
            Assert.Same(layout, source.Layout);
            Assert.Equal(MapSize(), source.Map.Width);
            //the neighbourhood screen is never inside a lot, so the source must not claim to be
            Assert.Null(source.ActiveLot);
        }

        [Fact]
        public void TheSceneSourceDoesNotHandOutTheCachedMapItself()
        {
            // The panel's own world-lifetime rule, and the reason the clone exists. The map handed
            // back may be the instance sitting in the shared cache that M4's in-lot surroundings
            // and the next visit both read; the panel holds its source for as long as the view is
            // up, and the renderer writes nothing but must not be able to reach the shared one.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var map = NeighbourhoodMap.Create(MapSize());
            var world = NeighbourhoodGeneratedWorld.Built(Slot, layout, map,
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false));

            var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out _);

            Assert.NotSame(map, source.Map);
            //equal, though: a copy, not a regeneration
            Assert.True(map.ContentEquals(source.Map));
        }

        [Fact]
        public void AFailedRefreshBecomesTheBitmapAndSaysWhy()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var world = NeighbourhoodGeneratedWorld.Failed(Slot, layout,
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false),
                "the road graph could not be made connected");

            var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out var failure);

            Assert.False(source.HasWorld);
            Assert.Null(source.Map);
            Assert.NotNull(failure);
            Assert.Contains("road graph", failure);
            //the layout is still handed over, because the camera and the log are worth having even
            //when there is no raster to draw
            Assert.Same(layout, source.Layout);
        }

        [Fact]
        public void ARefreshThatProducedNothingIsTheBitmapRatherThanACrash()
        {
            // RefreshNow does not return null today, so this is the answer to a broken invariant
            // rather than to a case that happens - which is exactly why it is pinned: a null must
            // mean the painted map with a reason, never a NullReferenceException inside
            // AttachWorld, and never a source that claims to have a world.
            var source = UINeighborhoodSelectionPanel.SceneSourceFor(null, out var failure);

            Assert.False(source.HasWorld);
            Assert.Null(source.Map);
            Assert.Null(source.Layout);
            Assert.False(string.IsNullOrWhiteSpace(failure));
        }

        [Fact]
        public void AFailedRefreshWithNoReasonStillSaysSomething()
        {
            // "Generation failed" with nothing to say why is still a neighbourhood that has to
            // open, and still a log line somebody can act on. The plan's own selection reason is
            // the fallback.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var world = NeighbourhoodGeneratedWorld.Failed(Slot, layout,
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false), null);

            var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out var failure);

            Assert.False(source.HasWorld);
            Assert.False(string.IsNullOrWhiteSpace(failure));
            Assert.Equal(world.Plan.Selection.Reason, failure);
        }

        [Fact]
        public void EveryHandOverExplainsItself()
        {
            // The panel's log is the only place a player who asked for 3D and got 2D can find out
            // why, so no path through the hand-over may produce an empty sentence.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(FourLots(), Slot, _seed);
            var worlds = new[]
            {
                null,
                NeighbourhoodGeneratedWorld.Built(Slot, layout, NeighbourhoodMap.Create(MapSize()),
                    NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                        NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false)),
                NeighbourhoodGeneratedWorld.Failed(Slot, layout,
                    NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, _seed,
                        NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false),
                    "the mesh was refused"),
            };

            foreach (var world in worlds)
            {
                var source = UINeighborhoodSelectionPanel.SceneSourceFor(world, out var failure);
                Assert.NotNull(source);
                if (source.HasWorld) Assert.Null(failure);
                else Assert.False(string.IsNullOrWhiteSpace(failure));
            }
        }

        // ---- helpers -----------------------------------------------------------------------------

        /// <summary>
        /// The panel's route, verbatim: same slot, same mode, 3D requested, and the store that
        /// makes the second call cheap. Driving this rather than
        /// <see cref="NeighbourhoodLifecycle.RefreshNow"/> directly is the point - the store lives
        /// in the panel, so a test that called the lifecycle would be asserting about a route the
        /// panel does not take.
        ///
        /// <para>
        /// One thing is added, and it is the thing the panel does not do: when the refresh is still
        /// building, wait for it. The panel's answer to that state is the painted map, which needs
        /// no game to be correct, so a test that wants to look at the finished world has to stand
        /// in for the game-thread apply the panel would be handed.
        /// </para>
        /// </summary>
        private NeighbourhoodGeneratedWorld Refresh(ILotContentSource source)
        {
            var world = Panel(source);
            if (!world.Pending) return world;

            var job = NeighbourhoodLifecycle.RefreshInFlight(Slot);
            Assert.NotNull(job);
            Assert.True(job.WaitForFinish(RefreshTimeout),
                "the background refresh for slot " + Slot + " did not finish in time");
            return Panel(source);
        }

        /// <summary>
        /// The panel's call, and nothing else: what a frame's worth of attaching actually does.
        /// </summary>
        private NeighbourhoodGeneratedWorld Panel(ILotContentSource source)
        {
            return UINeighborhoodSelectionPanel.RefreshCachedWorld(
                _root, Slot, Mode, NeighbourhoodView.World3D, source);
        }

        /// <summary>
        /// The worker's own pipeline, for the tests that are about what it does rather than about
        /// what the panel does with the answer. Stores the world the way the job would, so a later
        /// call sees the same cache the game would.
        /// </summary>
        private NeighbourhoodGeneratedWorld Rebuild(ILotContentSource source)
        {
            var world = NeighbourhoodLifecycle.RefreshBlocking(
                _root, Slot, Mode, NeighbourhoodView.World3D, source);
            NeighbourhoodWorldCache.Store(world);
            return world;
        }

        private static int MapSize()
        {
            return NeighbourhoodLayout.DefaultMapSize;
        }

        /// <summary>
        /// Four lots a hundred pixels apart on the backdrop, so the sixteen-tile rectangles
        /// <see cref="NeighbourhoodPixelTransform"/> puts them at clear one another with a street
        /// corridor between every pair - which is what the road graph needs to lay one connected
        /// network rather than four islands, and what a change to one of them needs somewhere to go
        /// without touching the others.
        /// </summary>
        private static List<NeighbourhoodLotRead> FourLots()
        {
            return new List<NeighbourhoodLotRead>
            {
                Read(21, 20, 20, 16, 0x1000u),
                Read(22, 120, 20, 16, 0x2000u),
                Read(23, 20, 120, 16, 0x3000u),
                Read(24, 120, 120, 16, 0x4000u),
            };
        }

        private static NeighbourhoodLotRead Read(short number, int mapX, int mapY, int size, uint hash)
        {
            return new NeighbourhoodLotRead
            {
                Number = number,
                MapX = mapX,
                MapY = mapY,
                Size = size,
                HasHouse = true,
                Rotation = 0,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = hash,
                Zone = LotZoning.ZoneResidential
            };
        }

        /// <summary>
        /// A content source over a fixed list of lot records, so the lifecycle can be driven with
        /// no neighbourhood folder anywhere - and counting what it was asked for, because "which
        /// lots did this refresh read" is the question the cheap path is about.
        /// </summary>
        private sealed class SourceOver : ILotContentSource
        {
            private readonly List<NeighbourhoodLotRead> _reads;

            public int Reads { get; private set; }

            public SourceOver(List<NeighbourhoodLotRead> reads)
            {
                _reads = reads;
            }

            public IReadOnlyList<string> LotLocationLines(int mode)
            {
                return _reads.Select(r => r.Number + "," + r.MapX + "," + r.MapY).ToList();
            }

            public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
            {
                Reads++;
                lot = _reads.FirstOrDefault(r => r.Number == number);
                return lot != null;
            }
        }
    }
}
