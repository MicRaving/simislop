/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// M5's rule, asserted rather than described: what has to be regenerated when a neighbourhood
    /// changes, and what has to keep standing.
    ///
    /// <para>
    /// §4.2 states the invalidation contract in one line and §12's M5 acceptance is that "editing a
    /// lot is reflected on return". Between those two sentences sit three claims that are easy to
    /// believe and easy to get wrong:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     A lot whose <c>HouseHash</c> moved regenerates <em>that lot</em>. Not the
    ///     neighbourhood: re-packing is a relaxation over every rectangle, so a whole-street
    ///     reshuffle would satisfy the letter of §4.2 and break the spirit of it, and the player
    ///     would come back from bulldozing one house to find the map rearranged.
    ///   </description></item>
    ///   <item><description>
    ///     The raster is regenerated when the <em>layout</em> changed and not otherwise. It is a
    ///     pure function of (layout, seed), so regenerating an unchanged one is a quarter of a
    ///     million tiles of work to produce the same bytes.
    ///   </description></item>
    ///   <item><description>
    ///     Every failure is the 2D bitmap, with a reason. A neighbourhood that cannot be generated
    ///     still opens, and a player who asked for 3D and got 2D can find out why from the log.
    ///   </description></item>
    /// </list>
    /// <para>
    /// All of it is pure: layouts, lot records, plans and sidecars in a temp directory. No
    /// graphics device, no provider, no <c>UserData</c>.
    /// </para>
    ///
    /// <para>
    /// In the "generated world cache" collection, like the other class that asserts on
    /// <see cref="NeighbourhoodWorldCache"/>: that cache is process-wide and cumulative, and these
    /// tests assert on its exact contents and counters, so two of them running at once would be
    /// asserting on each other.
    /// </para>
    /// </summary>
    [Collection("generated world cache")]
    public sealed class NeighbourhoodInvalidationTests : IDisposable
    {
        private const int Slot = 4;
        private const int Mode = 4;
        private const int MapSize = 128;
        private const int Seed = 424242;

        private readonly string _root;

        public NeighbourhoodInvalidationTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-invalidation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            NeighbourhoodWorldCache.Clear();
        }

        public void Dispose()
        {
            NeighbourhoodWorldCache.Clear();
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

        // ---- fixtures -------------------------------------------------------------------------

        /// <summary>
        /// Four lots spread across the whole 800x600 bitmap, well clear of one another, so the
        /// layout builder has nothing to adjust and every position in the layout is the transform's.
        /// A change to one of them therefore has somewhere to go without touching the others, which
        /// is the case §4.2's cheap path is about.
        /// </summary>
        /// <remarks>
        /// The bitmap coordinates are calibrated for <see cref="MapSize"/> rather than written as
        /// "small numbers": a lot's tile position is its bitmap position through
        /// <see cref="NeighbourhoodPixelTransform"/>, so on a 128-tile map a fixture using
        /// coordinates in the twenties puts all four lots in the same corner. These put them on the
        /// four quadrants of the bitmap - 260 pixels apart across and 330 down, which on 128 tiles
        /// is a 24- and a 27-tile pitch: an eight-tile street corridor between neighbours, which is
        /// what the patch's cheap path insists on keeping clear.
        ///
        /// <para>CHANGED by the Part 9 TSO-alignment rework. This was 200 pixels across and 300
        /// down, written when the transform was an axis-aligned square mapping and a bitmap pixel
        /// was a third of a tile. Under the diamond-aligned projection a pixel is 0.74 of a tile
        /// along the difference diagonal, so 200 pixels is nineteen and the lots overlapped.</para>
        /// </remarks>
        private static NeighbourhoodLayout CachedLayout()
        {
            return NeighbourhoodLayoutBuilder.PackOnGrid(Neighbourhood(), Slot, Seed, MapSize);
        }

        private static List<NeighbourhoodLotRead> Neighbourhood()
        {
            return new List<NeighbourhoodLotRead>
            {
                Read(21, 150, 120, 16, 0x1000u),
                Read(22, 410, 120, 16, 0x2000u),
                Read(23, 150, 450, 16, 0x3000u),
                Read(24, 410, 450, 16, 0x4000u),
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

        // ---- the decision ----------------------------------------------------------------------

        [Fact]
        public void AHouseHashThatMovedRegeneratesThatLotAndNothingElse()
        {
            // The whole of §4.2's cheap case, and M5's headline acceptance. The player bought a
            // swimming pool in lot 22: its HouseNN.iff has a new length and a new write time, so
            // its hash moved and no other lot's did.
            var plan = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, CachedLayout(), Seed,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, new short[] { 22 }, false);

            Assert.Equal(NeighbourhoodRefreshKind.LotsChanged, plan.Kind);
            Assert.True(plan.BuildWorld);
            Assert.True(plan.ReuseSidecarLayout);
            Assert.True(plan.PatchOnlyChangedLots);
            Assert.Equal(new short[] { 22 }, plan.LotsToRegenerate);
            Assert.True(plan.RegenerateMap);
            //The facades follow the same list, and that list has one member. Lot 21's walls are
            //exactly as they were and must not be rebuilt.
            Assert.True(plan.RegenerateFacades);
            Assert.Single(plan.LotsToRegenerate);
        }

        [Fact]
        public void TheMapIsOnlyRegeneratedWhenTheLayoutActuallyChanged()
        {
            var layout = CachedLayout();
            var version = NeighbourhoodLayoutBuilder.CurrentGeneratorVersion;

            //Nothing changed and the raster is in memory: the whole refresh is one file read.
            var hit = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, layout, Seed, version, null, true);
            Assert.Equal(NeighbourhoodRefreshKind.LayoutUnchanged, hit.Kind);
            Assert.True(hit.IsCacheHit);
            Assert.False(hit.RegenerateMap);
            Assert.False(hit.RegenerateFacades);
            Assert.Empty(hit.LotsToRegenerate);

            //Same layout, but the raster is not in memory - a fresh launch, or a different
            //neighbourhood evicted it. The layout is still reused; only the raster is rebuilt.
            var cold = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, layout, Seed, version, null, false);
            Assert.Equal(NeighbourhoodRefreshKind.LayoutUnchanged, cold.Kind);
            Assert.False(cold.IsCacheHit);
            Assert.True(cold.RegenerateMap);
            Assert.False(cold.RegenerateFacades);
            Assert.True(cold.ReuseSidecarLayout);

            //And one moved hash does regenerate it, because the lots it rasterises have moved.
            var changed = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, layout, Seed, version, new short[] { 23 }, false);
            Assert.True(changed.RegenerateMap);
        }

        [Fact]
        public void TheSettingOffDoesNothingAtAll()
        {
            //§0 decision 1 and §10 in one assertion: a player who has not opted in reads no sidecar,
            //stats no lot and generates no tile. Every "do this" must be false, and the reason has
            //to name the setting rather than some failure that happened behind the player's back.
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var plan = NeighbourhoodRefreshPlan.Decide(
                    view, CachedLayout(), Seed, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion,
                    new short[] { 22 }, true);

                Assert.Equal(NeighbourhoodRefreshKind.SettingIsNotWorld3D, plan.Kind);
                Assert.False(plan.BuildWorld);
                Assert.False(plan.ReuseSidecarLayout);
                Assert.False(plan.PatchOnlyChangedLots);
                Assert.False(plan.RegenerateMap);
                Assert.False(plan.RegenerateFacades);
                Assert.Empty(plan.LotsToRegenerate);
                Assert.True(plan.Selection.UseBitmap2D);
                Assert.Contains("3D neighbourhood is " + NeighbourhoodViewSetting.Describe(view), plan.Reason);
            }
        }

        [Fact]
        public void AFailedGenerationIsThe2DBitmapWithItsReason()
        {
            // The plan's non-negotiable. Not "the neighbourhood did not open" - it opens, as the map
            // it has always opened as - and the reason survives so a player who asked for 3D can
            // find out why from the log.
            var attempted = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, CachedLayout(), Seed,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false);

            var failed = NeighbourhoodRefreshPlan.ForFailure(
                attempted, "the road graph could not be made connected");

            Assert.False(failed.BuildWorld);
            Assert.False(failed.RegenerateMap);
            Assert.False(failed.RegenerateFacades);
            Assert.True(failed.Selection.UseBitmap2D);
            Assert.False(failed.Selection.Use3D);
            Assert.Equal(NeighbourhoodView.World3D, failed.Selection.Requested);
            Assert.Contains("road graph", failed.Selection.Reason);
            Assert.Contains("road graph", failed.Reason);
        }

        [Fact]
        public void AFailedGenerationWithNoReasonStillDrawsTheBitmap()
        {
            // "Generation failed" with nothing to say why is still a neighbourhood that has to open.
            var attempted = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, null, Seed, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion,
                null, false);

            var failed = NeighbourhoodRefreshPlan.ForFailure(attempted, null);

            Assert.True(failed.Selection.UseBitmap2D);
            Assert.Contains("no map was produced", failed.Selection.Reason);
        }

        [Fact]
        public void AMissingOrUnusableSidecarIsARepackAndSaysSo()
        {
            var version = NeighbourhoodLayoutBuilder.CurrentGeneratorVersion;

            var missing = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, null, Seed, version, null, false);
            Assert.Equal(NeighbourhoodRefreshKind.NoSidecar, missing.Kind);
            Assert.False(missing.ReuseSidecarLayout);
            Assert.True(missing.RegenerateMap);
            Assert.True(missing.RegenerateFacades);
            //A full pack replaces every lot, so the honest answer for "which lots changed" is that
            //there is no short list - not a list of all of them.
            Assert.Empty(missing.LotsToRegenerate);
            Assert.Contains("no cached layout", missing.Reason);

            //A layout from another seed is a different world, not a patchable one.
            var stale = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, CachedLayout(), Seed + 1, version, null, false);
            Assert.Equal(NeighbourhoodRefreshKind.SidecarStale, stale.Kind);
            Assert.False(stale.ReuseSidecarLayout);
            Assert.Contains("seed", stale.Reason);

            //And so is one packed by a different arrangement rule.
            var old = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, CachedLayout(), Seed, version + 1, null, false);
            Assert.Equal(NeighbourhoodRefreshKind.SidecarStale, old.Kind);
            Assert.Contains("layout generator", old.Reason);
        }

        [Fact]
        public void ASchemaThisBuildDoesNotUnderstandIsNeverHalfRead()
        {
            // The store already refuses to load one; this is the plan's answer when it arrives
            // anyway, and it has to be the same answer as no file at all.
            var cached = CachedLayout();
            var future = new NeighbourhoodLayout(cached.Slot, cached.Seed, cached.GeneratorVersion,
                NeighbourhoodLayout.CurrentSchemaVersion + 5, cached.MapSize, cached.Lots);

            var plan = NeighbourhoodRefreshPlan.Decide(
                NeighbourhoodView.World3D, future, Seed,
                NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false);

            Assert.Equal(NeighbourhoodRefreshKind.SidecarRejected, plan.Kind);
            Assert.False(plan.ReuseSidecarLayout);
            Assert.True(plan.RegenerateMap);
            Assert.Contains("schema", plan.Reason);
        }

        [Fact]
        public void EveryPlanExplainsItself()
        {
            // A refresh whose log line is blank is the one worth chasing, so there is no path through
            // this type that produces one - including the failure path, and including a plan that
            // has not been decided on at all.
            var version = NeighbourhoodLayoutBuilder.CurrentGeneratorVersion;
            var layout = CachedLayout();
            var plans = new[]
            {
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.Off, layout, Seed, version, null, false),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, null, Seed, version, null, false),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, Seed, version, null, true),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, Seed, version, new short[] { 21 }, false),
                NeighbourhoodRefreshPlan.ForFailure(
                    NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, Seed, version, null, false),
                    "the mesh was refused"),
            };

            foreach (var plan in plans)
            {
                Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
                Assert.False(string.IsNullOrWhiteSpace(plan.ToString()));
                Assert.False(string.IsNullOrWhiteSpace(plan.Selection.Reason));
            }
        }

        // ---- the cheap staleness check -----------------------------------------------------------

        [Fact]
        public void AFileStatAloneIsEnoughToTellThatNothingChanged()
        {
            // The perf claim, as a test. Twenty HouseNN.iff parses is the expensive part of a layout
            // build, and on the common return - nothing was edited - a length and a timestamp per
            // lot is the whole answer. A neighbourhood whose files have all moved on is reported as
            // clean, and one whose lot 23 has been rewritten is reported as exactly that lot.
            var layout = CachedLayout();

            var clean = NeighbourhoodLifecycle.StaleByFileStat(layout, number => layout.FindLot(number).HouseHash);
            Assert.Empty(clean);

            var edited = NeighbourhoodLifecycle.StaleByFileStat(layout,
                number => number == 23 ? 0xdeadbeefu : layout.FindLot(number).HouseHash);
            Assert.Equal(new short[] { 23 }, edited);
        }

        [Fact]
        public void ALotThatCannotBeStattedIsReportedRatherThanWavedThrough()
        {
            // A lot whose file cannot be stat'd is a lot that needs looking at. Assuming it is fine
            // because reading it threw would be the worst possible reading of an IO error.
            var layout = CachedLayout();

            var stale = NeighbourhoodLifecycle.StaleByFileStat(layout, number =>
            {
                if (number == 22) throw new IOException("the disk said no");
                return layout.FindLot(number).HouseHash;
            });

            Assert.Equal(new short[] { 22 }, stale);
            Assert.Empty(NeighbourhoodLifecycle.StaleByFileStat(null, number => 0u));
            Assert.Empty(NeighbourhoodLifecycle.StaleByFileStat(layout, null));
        }

        [Fact]
        public void AnAbsentHouseFileIsAHashOfZero()
        {
            //Which is how an empty plot reads on both sides of the comparison: the sidecar records
            //hash 0 with hasHouse false, and a missing file has to produce exactly that rather than
            //something that looks like a changed house.
            Assert.Equal(0u, NeighbourhoodLifecycle.HouseHashOfFile(null));
            Assert.Equal(0u, NeighbourhoodLifecycle.HouseHashOfFile(""));
            Assert.Equal(0u, NeighbourhoodLifecycle.HouseHashOfFile(
                Path.Combine(_root, "there-is-no-such-house.iff")));

            var path = Path.Combine(_root, "House21.iff");
            File.WriteAllBytes(path, new byte[128]);
            var hash = NeighbourhoodLifecycle.HouseHashOfFile(path);
            Assert.NotEqual(0u, hash);
            Assert.Equal(hash, NeighbourhoodLayoutBuilder.HouseHashFor(
                new FileInfo(path).Length, new FileInfo(path).LastWriteTimeUtc));
        }

        [Fact]
        public void ComparingTwoSetsOfLotRecordsFindsTheSameLotsTheStoreDoes()
        {
            // The same rule has to give the same answer whether it is asked from here or from the
            // store, or one of them is wrong and nothing would notice.
            var layout = CachedLayout();
            var reads = Neighbourhood();
            reads.Find(r => r.Number == 22).HouseHash = 0x9999u;
            reads.Add(Read(25, 90, 90, 16, 0x5000u));

            var mine = NeighbourhoodLifecycle.StaleByHouseHash(layout, reads);
            var theirs = NeighbourhoodLayoutStore.ChangedLots(layout, new SourceOver(reads), Mode);

            Assert.Equal(new short[] { 22, 25 }, mine);
            Assert.Equal(theirs.OrderBy(x => x), mine.OrderBy(x => x));
        }

        // ---- the cache ----------------------------------------------------------------------------

        [Fact]
        public void TheCacheRemembersAWorldAndCountsTheReuse()
        {
            var world = NeighbourhoodGeneratedWorld.Built(Slot, CachedLayout(),
                NeighbourhoodMap.Create(32),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, CachedLayout(), Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false));

            Assert.True(world.HasWorld);
            NeighbourhoodWorldCache.Store(world);
            Assert.Equal(1, NeighbourhoodWorldCache.Count);
            Assert.Same(world, NeighbourhoodWorldCache.Peek(Slot));

            //§0 decision 4: one world per mode, so a different slot is a different entry and the
            //nine slots do not fight over one cache line.
            Assert.Null(NeighbourhoodWorldCache.Peek(5));
            NeighbourhoodWorldCache.Store(NeighbourhoodGeneratedWorld.Built(5, CachedLayout(),
                NeighbourhoodMap.Create(32),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, null, Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false)));
            Assert.Equal(2, NeighbourhoodWorldCache.Count);

            //A failed world is not cached: an entry that only ever says "failed" would make the
            //next refresh look like a hit.
            NeighbourhoodWorldCache.Store(NeighbourhoodGeneratedWorld.Failed(5, CachedLayout(),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, CachedLayout(), Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false),
                "the road graph could not be made connected"));
            Assert.Null(NeighbourhoodWorldCache.Peek(5));
            Assert.Equal(1, NeighbourhoodWorldCache.Count);
        }

        [Fact]
        public void InvalidatingALotDropsTheCachedWorldAndSaysWhetherThereWasOne()
        {
            var world = NeighbourhoodGeneratedWorld.Built(Slot, CachedLayout(),
                NeighbourhoodMap.Create(32),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, CachedLayout(), Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false));
            NeighbourhoodWorldCache.Store(world);

            Assert.True(NeighbourhoodWorldCache.InvalidateLot(Slot, 22));
            Assert.Null(NeighbourhoodWorldCache.Peek(Slot));
            //idempotent, because an eviction invalidates twice (once in ResetHouse, once in
            //EvictLot) and the second one must not throw or pretend to have done something
            Assert.False(NeighbourhoodWorldCache.InvalidateLot(Slot, 22));
            Assert.Equal(2, NeighbourhoodWorldCache.Stats.Invalidations);
        }

        [Fact]
        public void AWorldDescribesItselfForTheLog()
        {
            var layout = CachedLayout();
            var world = NeighbourhoodGeneratedWorld.Built(Slot, layout, NeighbourhoodMap.Create(64),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, new short[] { 22 }, false));

            var text = world.Describe();
            Assert.Contains("neighbourhood 4", text);
            Assert.Contains("64x64", text);
            Assert.Contains("lot 22", text);
            Assert.Contains("4 lots", text);
            Assert.Equal(text, world.ToString());

            //and a world that failed says the bitmap and why, with no map half to claim
            var failed = NeighbourhoodGeneratedWorld.Failed(Slot, layout,
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, layout, Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false),
                "the mesh was refused");
            Assert.False(failed.HasWorld);
            Assert.Contains("2D", failed.Describe());
            Assert.Contains("mesh was refused", failed.Describe());
        }

        [Fact]
        public void TheCacheCountersExplainThemselves()
        {
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodWorldCache.Invalidate(Slot);
            var stats = NeighbourhoodWorldCache.Stats;
            Assert.Contains("1 invalidation", stats.Describe());
            Assert.Equal(stats.Describe(), stats.ToString());
        }

        [Fact]
        public void ARefreshJobThatThrowsIsARefreshThatDidNotHappen()
        {
            // The whole of the failure contract at its lowest level: whatever goes wrong in the
            // worker, the caller gets a job that completed and no world, and the neighbourhood
            // falls back to the bitmap. A throw across the thread boundary would take the frame
            // with it.
            var applied = 0;
            var job = NeighbourhoodRefreshJob.RunNow(Slot, _ => throw new IOException("the disk said no"),
                _ => applied++);

            Assert.True(job.IsComplete);
            Assert.Null(job.World);
            Assert.Equal(0, applied);
            //the wait still returns, so a caller that is about to build the panel is not left
            //waiting on a job that is never going to finish
            Assert.True(job.WaitForLayout(TimeSpan.Zero));
            Assert.Null(NeighbourhoodWorldCache.Peek(Slot));
        }

        [Fact]
        public void ARefreshJobSignalsTheLayoutBoundaryAndNotTheWholeGeneration()
        {
            // The perf claim, as a test: the sidecar is current before the expensive half starts,
            // so a caller that only needs the layout does not wait for a quarter of a million
            // tiles. The build is held open here to show the wait is bounded and does expire,
            // rather than being a wait that always succeeds because the work is instant.
            NeighbourhoodRefreshJob job;
            using (var release = new ManualResetEventSlim(false))
            {
                var layoutSignalled = false;
                job = NeighbourhoodRefreshJob.Start(Slot, onLayoutWritten =>
                {
                    onLayoutWritten();
                    layoutSignalled = true;
                    //now the expensive half, which the layout wait must not care about
                    release.Wait(TimeSpan.FromSeconds(5));
                    return null;
                }, null);

                try
                {
                    Assert.True(job.WaitForLayout(TimeSpan.FromSeconds(5)));
                    Assert.True(layoutSignalled);
                }
                finally
                {
                    release.Set();
                }
            }

            Assert.True(job.WaitForLayout(TimeSpan.FromSeconds(5)));
            Assert.Null(job.World);
        }

        [Fact]
        public void ARefreshJobPublishesWhatItBuiltAndSignalsWhenTheLayoutIsCurrent()
        {
            var world = NeighbourhoodGeneratedWorld.Built(Slot, CachedLayout(), NeighbourhoodMap.Create(32),
                NeighbourhoodRefreshPlan.Decide(NeighbourhoodView.World3D, null, Seed,
                    NeighbourhoodLayoutBuilder.CurrentGeneratorVersion, null, false));

            NeighbourhoodWorldCache.Clear();
            NeighbourhoodGeneratedWorld applied = null;
            var signalled = false;

            var job = NeighbourhoodRefreshJob.RunNow(Slot, onLayoutWritten =>
            {
                onLayoutWritten();
                signalled = true;
                return world;
            }, w => applied = w);

            Assert.True(signalled);
            Assert.True(job.IsComplete);
            Assert.Same(world, job.World);
            Assert.Same(world, applied);
            //and the cache is filled by the apply step, so a second refresh is a hit
            Assert.Same(world, NeighbourhoodWorldCache.Peek(Slot));
        }

        [Fact]
        public void TheLayoutWaitIsBoundedAndNeverThrows()
        {
            var job = NeighbourhoodRefreshJob.RunNow(Slot, onLayoutWritten => null, null);
            //a negative timeout is clamped rather than thrown at: a caller that has already given
            //up must not get an exception out of the give-up path
            job.WaitForLayout(TimeSpan.FromMilliseconds(-1));
            Assert.True(job.IsComplete);
            Assert.Null(job.World);
        }

        // ---- the sidecar round trip -------------------------------------------------------------

        [Fact]
        public void NothingDerivedIsWrittenIntoATs1NeighbourhoodFolder()
        {
            // The one boundary that is not negotiable, restated for the M5 path: the repair writes
            // a sidecar, and a sidecar inside UserData would be a save-file change.
            var userData = Path.Combine(_root, "UserData");
            Directory.CreateDirectory(Path.Combine(userData, "Houses"));
            var layout = CachedLayout();

            Assert.True(NeighbourhoodLayoutStore.Save(_root, layout));
            Assert.False(NeighbourhoodLayoutStore.IsInsideUserData(_root,
                NeighbourhoodLayoutStore.PathFor(_root, Slot)));
            Assert.True(NeighbourhoodLayoutStore.IsInsideUserData(_root,
                Path.Combine(userData, "Houses", "House21.iff")));
        }

        // ---- helpers -----------------------------------------------------------------------------

        /// <summary>
        /// A content source over a fixed list of lot records, so the store's own
        /// <c>ChangedLots</c> can be asked the same question this file asks it.
        /// </summary>
        private sealed class SourceOver : ILotContentSource
        {
            private readonly List<NeighbourhoodLotRead> _reads;

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
                lot = _reads.FirstOrDefault(r => r.Number == number);
                return lot != null;
            }
        }
    }
}
