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
using System.Threading.Tasks;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Entering a neighbourhood for the first time used to stop the game: the panel asked
    /// <see cref="NeighbourhoodLifecycle.RefreshNow"/> for a world, and that call read twenty
    /// <c>HouseNN.iff</c> files, packed a layout, generated 512x512 tiles of streets, ground, water
    /// and elevation and handed the result to the mesh builder - all on the thread that draws the
    /// frame. Eleven seconds of nothing, with the map the player is looking at not yet drawn.
    ///
    /// <para>
    /// These are the claims that fix rests on, and each one is a claim about something that did not
    /// happen, which is the only kind of performance claim a test can actually make:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>A cold request returns before any of the work has run, on another thread.</b> The
    ///     source is held open by a gate, so a lifecycle that generated on the calling thread could
    ///     not return at all - the test would time out rather than merely be slow - and one that
    ///     read the lots inline would be caught by the thread the read is recorded against.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A second request joins the first rather than starting a second one.</b> The panel
    ///     asks once when it is built and then, because nothing is attached yet, once per frame;
    ///     the service asks once before the panel exists. Every one of them is entitled to a world
    ///     and none is entitled to its own 262 144 tiles.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The finished world still arrives, and the next ask is a hit on the same instance.</b>
    ///     Non-blocking is only worth anything if the work still happens and the answer is still
    ///     there for whoever asks next.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The schedule is a rule over values</b> - <see cref="NeighbourhoodSchedule"/> - so
    ///     "the panel never waits" and "a cold neighbourhood is never generated twice" are checked
    ///     without a thread, a disk or a clock anywhere in sight.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Every stage of a generation is timed</b>, and the stages nothing measured are named
    ///     as unmeasured rather than quietly left out of the log line.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// No graphics device, no provider, no <c>UserData</c> and no game thread: the refreshes are
    /// real, on real workers, but everything they read is a canned lot list and everything they
    /// write is a sidecar in a temp directory. The one thing that is deliberately faked is time -
    /// a lot read held open on a gate - because that is the only way to say "returned before the
    /// work" rather than "returned in less time than this machine is slow".
    /// </para>
    /// </summary>
    [Collection("generated world cache")]
    public sealed class NeighbourhoodAsyncGenerationTests : IDisposable
    {
        private const int Slot = 5;
        private const int Mode = 5;

        private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

        private readonly string _root;

        public NeighbourhoodAsyncGenerationTests()
        {
            _root = Path.Combine(Path.GetTempPath(),
                "simitone-neigh-async-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            NeighbourhoodWorldCache.Clear();
            NeighbourhoodLifecycle.Forget();
        }

        public void Dispose()
        {
            //anything this class left running has to be finished before the temp directory it is
            //writing into disappears, and before the next test in the collection sees a warm cache
            Settle();
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

        // ---- the freeze -------------------------------------------------------------------------

        [Fact]
        public void AColdRequestAnswersWhileTheLotFilesAreStillLocked()
        {
            // The freeze, stated as the only thing that can be stated about work that did not
            // happen. The gate is inside the source, so the refresh cannot get past its first read
            // until this test opens it: a lifecycle that did any of this on the calling thread would
            // time out here rather than merely be slow.
            var gate = new Gate();
            try
            {
                var world = RefreshNow(new GatedSource(FourLots(), gate));

                Assert.True(world.Pending);
                Assert.False(world.HasWorld);
                Assert.Equal(NeighbourhoodRefreshKind.Refreshing, world.Plan.Kind);
                Assert.False(string.IsNullOrWhiteSpace(world.Failure));

                //and the read that does happen happens somewhere else entirely
                Assert.True(gate.WaitForEntry(RefreshTimeout),
                    "the refresh never started reading the neighbourhood's lots");
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, gate.EntryThread);

                //nothing is cached yet, and exactly one refresh was started for it
                Assert.Null(NeighbourhoodWorldCache.Peek(Slot));
                Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);

                gate.Open();
                Settle();
            }
            finally
            {
                //only once the worker is out of the gate: a handle disposed under a thread that is
                //inside Wait is how a test stops testing the thing it means to
                gate.Dispose();
            }

            Assert.NotNull(NeighbourhoodWorldCache.Peek(Slot));
        }

        [Fact]
        public void ASurroundAskedForOnALotLoadIsNeverBuiltOnTheLotLoadThread()
        {
            // The same shape, asked for by the lot rather than by the neighbourhood screen, because
            // that is the case that was reported: entering a lot stalled for twenty seconds in the
            // final loading stage. The gate is inside the source, so a lot load that generated here
            // could not return at all until the test opened it - which is the stall, expressed as a
            // test rather than as a timer.
            var gate = new Gate();
            try
            {
                var world = RefreshNow(new GatedSource(FourLots(), gate));

                //nothing to attach, and the reason is "still building" rather than "failed", so the
                //lot comes up with no surroundings and the horizon fills in behind the player
                Assert.True(world.Pending);
                Assert.False(world.HasWorld);
                Assert.Null(world.Map);
                Assert.Equal(NeighbourhoodRefreshKind.Refreshing, world.Plan.Kind);
                Assert.True(gate.WaitForEntry(RefreshTimeout),
                    "the surround's generation never started reading the neighbourhood's lots");
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, gate.EntryThread);

                gate.Open();
                Settle();
            }
            finally
            {
                gate.Dispose();
            }

            //and the world it produced is there for the lot, and for the next lot in the same
            //neighbourhood, with no second generation
            var landed = RefreshNow(new SourceOver(FourLots()));
            Assert.True(landed.HasWorld);
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
        }

        [Fact]
        public void ASecondRequestJoinsTheRefreshAlreadyRunningRatherThanStartingASecond()
        {
            var gate = new Gate();
            try
            {
                var first = RefreshNow(new GatedSource(FourLots(), gate));
                var second = RefreshNow(new GatedSource(FourLots(), gate));
                var third = RefreshNow(new GatedSource(FourLots(), gate));

                Assert.True(first.Pending);
                Assert.True(second.Pending);
                Assert.True(third.Pending);

                var job = NeighbourhoodLifecycle.RefreshInFlight(Slot);
                Assert.NotNull(job);

                //one generation, two requests that joined it. The counter is the observation; the
                //alternative is three workers packing the same neighbourhood and racing to put
                //three different worlds in one cache slot.
                var stats = NeighbourhoodWorldCache.Stats;
                Assert.Equal(1, stats.Refreshes);
                Assert.Equal(2, stats.Joined);

                gate.Open();
                Assert.True(job.WaitForFinish(RefreshTimeout));
            }
            finally
            {
                gate.Dispose();
            }

            Assert.NotNull(NeighbourhoodWorldCache.Peek(Slot));
            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
        }

        [Fact]
        public void AskingRepeatedlyWhileARefreshRunsStaysAsynchronous()
        {
            // The per-frame case. The panel's reconcile asks again every frame while nothing is
            // attached, which for the several seconds a generation takes is a few hundred asks. Each
            // one has to come back as "still building" and none of them may start a second
            // generation, or the fix would have traded one freeze for a thread leak.
            var gate = new Gate();
            try
            {
                var first = RefreshNow(new GatedSource(FourLots(), gate));
                Assert.True(first.Pending);

                for (int i = 0; i < 50; i++)
                {
                    Assert.True(RefreshNow(new GatedSource(FourLots(), gate)).Pending);
                }

                Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
                Assert.Equal(50, NeighbourhoodWorldCache.Stats.Joined);

                gate.Open();
                Settle();
            }
            finally
            {
                gate.Dispose();
            }
        }

        [Fact]
        public void TheFinishedWorldIsCachedAndTheNextAskIsTheSameInstance()
        {
            // The other half of the fix: nothing may be lost by not waiting. The worker finishes,
            // the world is in the cache, and the next attach - the one the game thread makes when
            // the apply runs - is a hit on the very object, with no second generation.
            var source = new SourceOver(FourLots());
            Assert.True(RefreshNow(source).Pending);
            Settle();

            var settled = RefreshNow(source);
            Assert.False(settled.Pending);
            Assert.True(settled.HasWorld);
            Assert.Equal(NeighbourhoodLayout.DefaultMapSize, settled.Map.Width);

            var again = RefreshNow(source);
            Assert.Same(settled, again);
            Assert.Same(settled, NeighbourhoodWorldCache.Peek(Slot));

            var stats = NeighbourhoodWorldCache.Stats;
            Assert.Equal(1, stats.Refreshes);
            Assert.Equal(1, stats.Misses);
            //two hits: the attach that found the finished world, and the one after it
            Assert.Equal(2, stats.Hits);
            Assert.Equal(0, stats.Fallbacks);
        }

        [Fact]
        public void ASettingThatIsNot3DStartsNothingAndWaitsForNothing()
        {
            // §0 decision 1 with the new shape. A player who has not opted in must not pay for a
            // world in any currency, and "paid" now includes starting a thread.
            var world = NeighbourhoodLifecycle.RefreshNow(_root, Slot, Mode,
                NeighbourhoodView.Bitmap2D, new SourceOver(FourLots()));

            Assert.False(world.Pending);
            Assert.False(world.HasWorld);
            Assert.Equal(NeighbourhoodRefreshKind.SettingIsNotWorld3D, world.Plan.Kind);
            Assert.Null(NeighbourhoodLifecycle.RefreshInFlight(Slot));
            Assert.Equal(0, NeighbourhoodWorldCache.Stats.Refreshes);
            Assert.Null(NeighbourhoodLayoutStore.Load(_root, Slot));
        }

        [Fact]
        public void ACallerArrivingAfterTheRefreshStartedStillGetsTheAnswer()
        {
            // The shape the neighbourhood panel needs. The service starts the refresh a moment
            // before the panel exists, so the panel is always the late caller: it has to be able to
            // subscribe to a job that is already running rather than start one of its own, and it
            // has to end up with its apply on that job rather than on one that never happens.
            var gate = new Gate();
            try
            {
                var service = NeighbourhoodLifecycle.Ensure(_root, Slot, Mode,
                    NeighbourhoodView.World3D, () => new GatedSource(FourLots(), gate), null);
                Assert.NotNull(service);
                Assert.True(gate.WaitForEntry(RefreshTimeout));

                NeighbourhoodGeneratedWorld handed = null;
                var panel = NeighbourhoodLifecycle.Ensure(_root, Slot, Mode,
                    NeighbourhoodView.World3D, () => new GatedSource(FourLots(), gate),
                    world => handed = world);
                //the callback cannot have run yet: the refresh is still behind the gate
                Assert.Null(handed);

                //the same job, and the panel's apply is queued behind it
                Assert.Same(service, panel);
                Assert.Equal(1, panel.SubscriberCount);
                Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
                Assert.Equal(1, NeighbourhoodWorldCache.Stats.Joined);

                gate.Open();
                Assert.True(panel.WaitForFinish(RefreshTimeout));

                //the apply itself runs on the game thread, which nothing in a test pumps, so what is
                //asserted is everything around it: the world was cached, and the list of callers
                //waiting for it was taken rather than left for a second delivery
                Assert.NotNull(NeighbourhoodWorldCache.Peek(Slot));
                Assert.Equal(0, panel.SubscriberCount);
            }
            finally
            {
                gate.Dispose();
            }

            Assert.Null(NeighbourhoodLifecycle.RefreshInFlight(Slot));
        }

        [Fact]
        public void ARefreshThatProducedNoWorldIsNotRetriedOnEveryFrame()
        {
            // The one thing about failure that the asynchronous shape makes worse, and therefore has
            // to be answered. A world with no raster cannot be cached, so before this rule the next
            // frame started another refresh, and the frame after that, for as long as the player
            // stayed in a neighbourhood the generator cannot satisfy - a worker per frame.
            //
            // A source that throws stands in for a road graph that will not connect: the refresh
            // completes, produces nothing, and says why.
            var refused = RefreshNow(new ThrowingSource());
            Assert.True(refused.Pending);
            Settle();

            //and the next twenty asks all get the same answer, from the same finished attempt
            for (int i = 0; i < 20; i++)
            {
                var again = RefreshNow(new ThrowingSource());
                Assert.False(again.Pending);
                Assert.False(again.HasWorld);
                Assert.False(string.IsNullOrWhiteSpace(again.Failure));
            }

            Assert.Equal(1, NeighbourhoodWorldCache.Stats.Refreshes);
            Assert.Null(NeighbourhoodWorldCache.Peek(Slot));
        }

        [Fact]
        public void AnInvalidationIsWhatMakesAFailedRefreshWorthTryingAgain()
        {
            // The other half of the rule, and the reason it can exist: whatever changed the
            // neighbourhood is what can change the answer, and the only thing that knows that is
            // the thing that reports the change.
            RefreshNow(new ThrowingSource());
            Settle();
            Assert.False(RefreshNow(new ThrowingSource()).Pending);

            //a bulldoze, or a rezone, or a neighbourhood switch. The cache's own eviction rather
            //than the lifecycle's wrapper, because that wrapper is gated on the player's setting -
            //which is read out of the graphics preferences, and a test has no preferences file to
            //read. The epoch is bumped either way, and the epoch is what this rule turns on.
            NeighbourhoodWorldCache.InvalidateLot(Slot, 24);

            var retry = RefreshNow(new ThrowingSource());
            Assert.True(retry.Pending);
            Settle();
            Assert.Equal(2, NeighbourhoodWorldCache.Stats.Refreshes);
        }

        // ---- the schedule, as a rule ------------------------------------------------------------
        [Fact]
        public void ASettingThatIsNot3DIsNothingToDo()
        {
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var schedule = NeighbourhoodSchedule.Decide(view, true, true);
                Assert.Equal(NeighbourhoodScheduleAction.Nothing, schedule.Action);
                Assert.False(schedule.WorldReady);
                Assert.False(schedule.IsAsynchronous);
                Assert.Contains("no world was read, generated or waited for", schedule.Reason);
            }
        }

        [Fact]
        public void ACachedWorldWinsOverARefreshThatIsStillRunning()
        {
            // The order is the contract: a world that landed a frame ago is handed back rather than
            // shadowed by a refresh that is about to finish anyway, which would be another frame
            //of painted map for nothing.
            var schedule = NeighbourhoodSchedule.Decide(NeighbourhoodView.World3D, true, true);

            Assert.Equal(NeighbourhoodScheduleAction.UseCache, schedule.Action);
            Assert.True(schedule.WorldReady);
            Assert.False(schedule.IsAsynchronous);
        }

        [Fact]
        public void ARefreshInFlightIsJoinedAndNotStarted()
        {
            var schedule = NeighbourhoodSchedule.Decide(NeighbourhoodView.World3D, false, true);

            Assert.Equal(NeighbourhoodScheduleAction.JoinRefresh, schedule.Action);
            Assert.False(schedule.WorldReady);
            Assert.True(schedule.IsAsynchronous);
            //the reason is what a log line shows, so it has to say the work is already under way
            Assert.Contains("already being generated", schedule.Reason);
        }

        [Fact]
        public void NeitherCachedNorRunningMeansStart()
        {
            var schedule = NeighbourhoodSchedule.Decide(NeighbourhoodView.World3D, false, false);

            Assert.Equal(NeighbourhoodScheduleAction.StartRefresh, schedule.Action);
            Assert.False(schedule.WorldReady);
            Assert.True(schedule.IsAsynchronous);
            Assert.Contains("off the game thread", schedule.Reason);
        }

        [Fact]
        public void EveryScheduleExplainsItself()
        {
            // A log line that says "join refresh" and nothing else is a log line nobody can act on.
            for (var cached = 0; cached < 2; cached++)
            {
                for (var running = 0; running < 2; running++)
                {
                    var schedule = NeighbourhoodSchedule.Decide(NeighbourhoodView.World3D,
                        cached == 1, running == 1);
                    Assert.False(string.IsNullOrWhiteSpace(schedule.Reason));
                    Assert.Contains(schedule.Reason, schedule.ToString());
                }
            }
        }

        [Fact]
        public void APendingWorldIsNotAFailureAndSaysWhatItIsInstead()
        {
            // The distinction a log reader needs. "Still being built" and "could not be built" want
            // opposite things, and a world that cannot say which one it is turns a ten-second wait
            // into an apparent failure.
            var schedule = NeighbourhoodSchedule.Decide(NeighbourhoodView.World3D, false, true);
            var world = NeighbourhoodGeneratedWorld.Refreshing(Slot,
                NeighbourhoodRefreshPlan.Refreshing(NeighbourhoodView.World3D, schedule.Reason),
                schedule.Reason);

            Assert.True(world.Pending);
            Assert.False(world.HasWorld);
            Assert.Null(world.Map);
            Assert.Equal(NeighbourhoodRefreshKind.Refreshing, world.Plan.Kind);
            //3D is still what this is being built for; the painted map is what covers for it
            Assert.True(world.Plan.BuildWorld);
            Assert.True(world.Plan.Selection.Use3D);
            Assert.Contains("still generating", world.Describe());
            Assert.Equal(world.Describe(), world.ToString());
        }

        [Fact]
        public void ARefreshingPlanIsNeitherACacheHitNorAFailure()
        {
            // The three states a refresh can report in are three states, and a fourth that could be
            // mistaken for any of them is a bug waiting for a log reader to find it.
            var refreshing = NeighbourhoodRefreshPlan.Refreshing(NeighbourhoodView.World3D, "wait");
            Assert.Equal(NeighbourhoodRefreshKind.Refreshing, refreshing.Kind);
            Assert.False(refreshing.IsCacheHit);
            Assert.True(refreshing.BuildWorld);
            Assert.True(refreshing.Reason.Length > 0);

            //and the same plan, once generation has had its say and lost, is a failure
            var failure = NeighbourhoodRefreshPlan.ForFailure(refreshing, "the disk said no");
            Assert.False(failure.BuildWorld);
            Assert.Contains("could not be built", failure.Reason);
        }

        // ---- the stage timings ------------------------------------------------------------------

        [Fact]
        public void AGenerationReportsEveryStageItMeasuredAndTheTotal()
        {
            // The breakdown, driven end to end over a real neighbourhood. Every stage this file can
            // measure has to be measured, and the stages that belong to other files are named as
            // unmeasured rather than dropped - a log line with a silent gap in it is worse than no
            // log line at all.
            var before = NeighbourhoodGenerationTimings.LastPublished;
            var world = NeighbourhoodLifecycle.RefreshBlocking(_root, Slot, Mode,
                NeighbourhoodView.World3D, new SourceOver(FourLots()));
            Assert.True(world.HasWorld);

            var timings = NeighbourhoodGenerationTimings.LastPublished;
            Assert.NotNull(timings);
            //it is this run's, not a previous test's
            Assert.NotSame(before, timings);

            foreach (var phase in new[]
            {
                NeighbourhoodGenerationPhase.SidecarRead,
                NeighbourhoodGenerationPhase.LotReads,
                NeighbourhoodGenerationPhase.LayoutBuild,
                NeighbourhoodGenerationPhase.LayoutWrite,
                NeighbourhoodGenerationPhase.MapInput,
                NeighbourhoodGenerationPhase.MapGenerate,
                NeighbourhoodGenerationPhase.Total
            })
            {
                Assert.True(timings.WasMeasured(phase),
                    phase + " was not measured by a cold generation");
            }

            var text = timings.Describe();
            foreach (var label in new[] { "sidecar read", "lot reads", "layout", "generation", "total" })
            {
                Assert.Contains(label, text);
            }

            //the total is measured around the whole refresh, so it cannot be less than what is
            //inside it. That is the arithmetic that says the phases are nested in the refresh
            //rather than timed beside it.
            var inside = timings.Elapsed(NeighbourhoodGenerationPhase.SidecarRead)
                + timings.Elapsed(NeighbourhoodGenerationPhase.LotReads)
                + timings.Elapsed(NeighbourhoodGenerationPhase.LayoutBuild)
                + timings.Elapsed(NeighbourhoodGenerationPhase.LayoutWrite)
                + timings.Elapsed(NeighbourhoodGenerationPhase.MapInput)
                + timings.Elapsed(NeighbourhoodGenerationPhase.MapGenerate);
            Assert.True(timings.Elapsed(NeighbourhoodGenerationPhase.Total) >= inside,
                "the total (" + timings.Elapsed(NeighbourhoodGenerationPhase.Total)
                + ") is less than the stages inside it (" + inside + ")");

            var missing = timings.UnmeasuredLabels();
            Assert.Contains("roads", missing);
            Assert.Contains("terrain fill", missing);
            Assert.Contains("seam solve", missing);
            Assert.Contains("mesh build", missing);
            Assert.Contains("mesh upload", missing);
        }

        [Fact]
        public void AStageThatThrowsStillReportsHowLongItTook()
        {
            // A generation that fails halfway is the run whose log line is worth reading, and the
            //stage that threw is a stage that took time.
            var timings = new NeighbourhoodGenerationTimings();
            Assert.Throws<IOException>(() => timings.Measure(NeighbourhoodGenerationPhase.TerrainFill,
                () => { throw new IOException("the disk said no"); }));

            Assert.True(timings.WasMeasured(NeighbourhoodGenerationPhase.TerrainFill));
            Assert.Contains("terrain fill", timings.Describe());
        }

        [Fact]
        public void ATimingIsTheSumOfEveryReportAndUnmeasuredStagesReadAsNothing()
        {
            var timings = new NeighbourhoodGenerationTimings();
            timings.Record(NeighbourhoodGenerationPhase.MeshUpload, TimeSpan.FromMilliseconds(10));
            timings.Record(NeighbourhoodGenerationPhase.MeshUpload, TimeSpan.FromMilliseconds(15));
            //a stage nobody reported must read as nothing rather than as whatever was in the array
            Assert.Equal(TimeSpan.Zero, timings.Elapsed(NeighbourhoodGenerationPhase.Roads));
            Assert.False(timings.WasMeasured(NeighbourhoodGenerationPhase.Roads));

            Assert.Equal(TimeSpan.FromMilliseconds(25),
                timings.Elapsed(NeighbourhoodGenerationPhase.MeshUpload));
            Assert.Contains("mesh upload", timings.Describe());
            Assert.Equal("mesh upload",
                NeighbourhoodGenerationTimings.Label(NeighbourhoodGenerationPhase.MeshUpload));
            Assert.Contains("roads", timings.UnmeasuredLabels());
        }

        [Fact]
        public async Task ATimingsScopeBelongsToTheWorkerThatStartedItAndNotToTheCaller()
        {
            // Two neighbourhoods generating at once must not write into each other's numbers. The
            // scope is published by the refresh, inside its own worker, and that is what keeps it
            // out of the caller's reach - and the caller's is the one a stage running on the game
            // thread would otherwise find and append to.
            Assert.Null(NeighbourhoodGenerationTimings.Current);

            NeighbourhoodGenerationTimings theirs = null;
            await Task.Run(() =>
            {
                theirs = NeighbourhoodGenerationTimings.Begin();
                theirs.Record(NeighbourhoodGenerationPhase.MeshBuild, TimeSpan.FromMilliseconds(3));
                NeighbourhoodGenerationTimings.End();
            });

            Assert.NotNull(theirs);
            Assert.True(theirs.WasMeasured(NeighbourhoodGenerationPhase.MeshBuild));
            //the worker published it and took it away again; the game thread is left with nothing
            Assert.Null(NeighbourhoodGenerationTimings.Current);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>
        /// The panel's own call - the one the neighbourhood screen makes on the game thread.
        /// </summary>
        private NeighbourhoodGeneratedWorld RefreshNow(ILotContentSource source)
        {
            return NeighbourhoodLifecycle.RefreshNow(_root, Slot, Mode,
                NeighbourhoodView.World3D, source);
        }

        /// <summary>
        /// Waits for whatever refresh this slot has in flight, so a test never leaves one running
        /// into the next one.
        /// </summary>
        private static void Settle()
        {
            var job = NeighbourhoodLifecycle.RefreshInFlight(Slot);
            if (job == null) return;
            Assert.True(job.WaitForFinish(RefreshTimeout),
                "the refresh for slot " + Slot + " did not finish in time");
        }

        /// <summary>
        /// A gate a lot read waits on. The only fake in this file: it turns "returned before the
        /// work" into something a test can state, and it records which thread got there.
        /// </summary>
        private sealed class Gate : IDisposable
        {
            private readonly ManualResetEventSlim _open = new ManualResetEventSlim(false);
            private readonly ManualResetEventSlim _entered = new ManualResetEventSlim(false);
            private int _entryThread;

            /// <summary>The thread that reached the gate, or 0 if nobody has.</summary>
            public int EntryThread
            {
                get { return _entryThread; }
            }

            /// <summary>
            /// Records that a read arrived, on whichever thread that was, and blocks there until
            /// the test opens the gate.
            /// </summary>
            public void Arrive(TimeSpan timeout)
            {
                _entryThread = Thread.CurrentThread.ManagedThreadId;
                _entered.Set();
                if (!_open.Wait(timeout))
                {
                    throw new TimeoutException("a gated lot read was never opened");
                }
            }

            /// <summary>Waits for a read to arrive at the gate.</summary>
            public bool WaitForEntry(TimeSpan timeout)
            {
                return _entered.Wait(timeout);
            }

            public void Open()
            {
                _open.Set();
            }

            public void Dispose()
            {
                _open.Set();
                _open.Dispose();
                _entered.Dispose();
            }
        }

        /// <summary>
        /// A content source over a fixed list of lot records whose first read waits on a gate.
        /// </summary>
        private sealed class GatedSource : ILotContentSource
        {
            private readonly List<NeighbourhoodLotRead> _reads;
            private readonly Gate _gate;

            public GatedSource(List<NeighbourhoodLotRead> reads, Gate gate)
            {
                _reads = reads;
                _gate = gate;
            }

            public IReadOnlyList<string> LotLocationLines(int mode)
            {
                _gate.Arrive(TimeSpan.FromSeconds(30));
                return _reads.Select(r => r.Number + "," + r.MapX + "," + r.MapY).ToList();
            }

            public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
            {
                lot = _reads.FirstOrDefault(r => r.Number == number);
                return lot != null;
            }
        }

        /// <summary>
        /// A content source that cannot read the neighbourhood at all - the case the 2D fallback
        /// exists for, and here the case that makes a refresh finish with no world.
        /// </summary>
        private sealed class ThrowingSource : ILotContentSource
        {
            public IReadOnlyList<string> LotLocationLines(int mode)
            {
                throw new IOException("LotLocations.iff would not read");
            }

            public bool TryReadLot(int number, out NeighbourhoodLotRead lot)
            {
                lot = null;
                return false;
            }
        }

        /// <summary>
        /// A content source over a fixed list of lot records, and nothing else: no neighbourhood
        /// folder, no provider, no game.
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
                //residential, the default zone; the packer only ever moves lots around, it does not
                //read this to decide anything
                Zone = 0
            };
        }
    }
}
