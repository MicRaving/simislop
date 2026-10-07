/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The rules that decide whether a facade is built at all this frame, and what one costs.
    ///
    /// <para><b>The bug these pin.</b> The neighbourhood froze for eleven seconds: the log showed
    /// two facades arriving with nothing in between, and forty-one lots in the neighbourhood. The
    /// pass already had a per-frame budget - two meshes, ten milliseconds - and it did not help,
    /// because one build costs seconds and the budget was checked <em>before</em> a build rather
    /// than around it. So the three things that actually decide the freeze are the cap, the
    /// cooldown, and the ceiling, and all three are arithmetic over numbers with no graphics
    /// device in them. That is what is tested here.</para>
    ///
    /// <para><b>What cannot be tested here, and is not.</b> Whether a build is worth starting at
    /// all is decided by how long it takes, and the only way to know that is to run the lot view.
    /// What is tested is the consequence: given a build that cost N, the pass does not start
    /// another one for the next F frames, and gives up entirely if N was over the ceiling.</para>
    /// </summary>
    public class NeighbourhoodFacadeScheduleTests
    {
        private static NeighbourhoodLot Lot(short number, int worldX, int worldY,
            uint hash = 0xABCD, int size = 32, int rotation = 0, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                Rotation = rotation,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = hash,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(1, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, 512, lots);
        }

        /// <summary>A layout of <paramref name="count"/> houses on a grid, all with distinct hashes.</summary>
        private static NeighbourhoodLayout Grid(int count)
        {
            var lots = new List<NeighbourhoodLot>(count);
            for (int i = 0; i < count; i++)
            {
                lots.Add(Lot((short)(10 + i), (i % 8) * 64, (i / 8) * 64, (uint)(0x1000 + i)));
            }
            return LayoutOf(lots.ToArray());
        }

        // ---- the cap ------------------------------------------------------------------------

        [Fact]
        public void AFrameBuildsOneFacadeAndNotTwo()
        {
            // The one that matters most and is a single constant. It was two, on the reasoning
            // that a fifty-lot neighbourhood would take four seconds to fill in - a reasoning made
            // before anything had measured a build, when a build takes about five seconds. "Two
            // per frame" was not a fill-in rate; it was a ten-second freeze with a log line at
            // each end of it.
            Assert.Equal(1, NeighbourhoodFacades.DefaultBuildsPerFrame);
        }

        [Fact]
        public void TheBuildListIsNeverLongerThanTheCap()
        {
            // The cap has to hold for every cap, not just the default: a caller that raises
            // BuildsPerFrame must still be told no more than it asked for, and a cap of zero must
            // build nothing at all rather than everything.
            var layout = Grid(41);
            var cache = new Dictionary<short, uint>();
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 0));
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, -1));
            Assert.Single(NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 1));
            Assert.Equal(3, NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 3).Count);
            Assert.Equal(41, NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 41).Count);
            //and never more than there is, which is the other half of "at most the cap"
            Assert.Equal(4, NeighbourhoodFacadePlan.ToBuild(
                LayoutOf(Lot(1, 0, 0), Lot(2, 64, 0), Lot(3, 0, 64), Lot(4, 64, 64)),
                new Dictionary<short, uint>(), null, Vector2.Zero, 100).Count);
        }

        [Fact]
        public void TheFrameAllowanceCountsDownAndNeverGoesNegative()
        {
            // The count budget is the other half of the cap, and it has to reach zero rather than
            // go past it: a negative allowance is a different code path, and a caller that treats
            // it as "how many left" would index off the front of the list.
            const int cap = 1;
            Assert.Equal(1, NeighbourhoodFacadePlan.FrameAllowance(cap, 0d, 0, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(cap, 0d, 1, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(cap, 0d, 2, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(cap, 0d, 99, 0d));
        }

        // ---- only what the camera can see ---------------------------------------------------

        [Fact]
        public void ALotTooFarFromTheCameraIsNotBuiltThisFrame()
        {
            // The freeze was 41 builds, and the camera can see perhaps six of them at a time. A
            // lot 900 tiles away is not a house the player will look at; building it is a stall
            // paid for something invisible.
            var near = Lot(11, 0, 0);
            var far = Lot(12, 4096, 4096);
            var layout = LayoutOf(near, far);
            var cache = new Dictionary<short, uint>();

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null,
                NeighbourhoodWorldMapping.PackedCentre(near), 10, 420f);

            Assert.Single(wanted);
            Assert.Equal(11, wanted[0].Number);
        }

        [Fact]
        public void ALotTooFarAwayIsDeferredRatherThanFailed()
        {
            // The distinction the radius must not blur. A far lot is not a failure: it keeps no
            // cache entry, so the next call asks for it again, and the moment the camera pans over
            // it is built. Recording it as "done" would leave a permanent hole in the map, and
            // recording it as "failed" would put a warning in the log for a lot that was never
            // wrong - just not near enough yet.
            var layout = LayoutOf(Lot(11, 0, 0), Lot(12, 4096, 4096));
            var cache = new Dictionary<short, uint>();

            //ask with a radius that excludes the far lot, then ask again with the camera on it
            var first = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 10, 420f);
            Assert.Single(first);
            cache[first[0].Number] = first[0].HouseHash;

            var second = NeighbourhoodFacadePlan.ToBuild(layout, cache, null,
                NeighbourhoodWorldMapping.PackedCentre(layout.FindLot(12)), 10, 420f);

            Assert.Single(second);
            Assert.Equal(12, second[0].Number);
        }

        [Fact]
        public void ARadiusOfZeroMeansNoDistanceLimitRatherThanNothingInRange()
        {
            // The unbounded overload has to be the same rule with the radius turned off, not a
            // different rule - otherwise "no limit" and "limit of nothing" differ by a whole
            // neighbourhood, and one of them silently builds nothing forever.
            Assert.True(NeighbourhoodFacadePlan.WithinRadius(new Vector2(99999, 0), 0f));
            Assert.True(NeighbourhoodFacadePlan.WithinRadius(new Vector2(99999, 0), -1f));

            var layout = LayoutOf(Lot(11, 0, 0), Lot(12, 4000, 4000));
            var cache = new Dictionary<short, uint>();
            Assert.Equal(2, NeighbourhoodFacadePlan.ToBuild(layout, cache, null, Vector2.Zero, 10).Count);
        }

        [Fact]
        public void ARadiusIsMeasuredToTheLotNotToItsEdge()
        {
            // The centre is what the plan has always ordered by, and ordering by one thing while
            // gating on another would let a lot the list calls nearest be the one the radius drops.
            var lot = Lot(11, 100, 0, size: 32);
            var centre = NeighbourhoodWorldMapping.PackedCentre(lot);
            var reach = centre.Length();

            //exactly at the boundary is inside, because a lot on the edge of the radius is a lot
            //the player can see; a hair further out is not
            Assert.True(NeighbourhoodFacadePlan.WithinRadius(centre, reach));
            Assert.False(NeighbourhoodFacadePlan.WithinRadius(centre, reach - 0.5f));
            //and the far corner of the lot is not what the radius reads: the lot is 32 tiles wide,
            //so its corner is sixteen tiles further out and must not push it past the limit
            Assert.True(NeighbourhoodFacadePlan.WithinRadius(centre, reach));
            Assert.False(NeighbourhoodFacadePlan.WithinRadius(
                new Vector2(centre.X + 16f, centre.Y + 16f), reach));
        }

        [Fact]
        public void ACameraThatHasNotBeenPlacedBuildsNothing()
        {
            // The focus is a Vector2 and a placement that failed leaves it as NaN. "Build
            // everything" and "build nothing" are both defensible answers to a NaN; only one of
            // them is safe, because the other is a forty-one lot stall on the first frame after a
            // failure. A NaN offset compares false against every radius, including the unlimited
            // one, so the rule is stated once rather than per caller.
            var layout = Grid(41);
            var cache = new Dictionary<short, uint>();
            var notPlaced = new Vector2(float.NaN, float.NaN);

            Assert.False(NeighbourhoodFacadePlan.WithinRadius(notPlaced, 420f));
            Assert.Empty(NeighbourhoodFacadePlan.ToBuild(layout, cache, null, notPlaced, 10, 420f));
        }

        // ---- the cooldown: one expensive build must not be followed by another --------------

        [Fact]
        public void ABuildThatOverranItsFrameIsFollowedByQuietFrames()
        {
            // The whole of the freeze fix, as arithmetic. A build cannot be abandoned half way -
            // a half-built facade is exactly the partial mesh the view must not show - so the
            // only lever is when the next one is allowed to start. A five second build in a ten
            // millisecond frame is followed by five hundred quiet ones.
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(0d, 10d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(10d, 10d));
            Assert.Equal(9, NeighbourhoodFacadePlan.FramesAfterBuild(100d, 10d));
            Assert.Equal(499, NeighbourhoodFacadePlan.FramesAfterBuild(5000d, 10d));
        }

        [Fact]
        public void TheCooldownIsSkippedWhenThereIsNoFrameBudgetToAverageTowards()
        {
            // A zero budget means "do not pace me", and it must not be read as "pace me to zero
            // frames" - which is division by zero and would either throw or turn every build into
            // an infinite wait.
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(5000d, 0d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(5000d, -1d));
        }

        [Fact]
        public void ACooldownFromAnUnmeasurableCostIsNoCooldown()
        {
            // A stopwatch that has not started, or a division that produced an infinity, must not
            // turn into a wait of two billion frames: that is a queue that never drains, which
            // looks exactly like a queue that is broken.
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(double.NaN, 10d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(double.PositiveInfinity, 10d));
            Assert.Equal(0, NeighbourhoodFacadePlan.FramesAfterBuild(-1d, 10d));
        }

        [Fact]
        public void AFrameAlwaysGetsAtLeastOneBuildSoTheQueueCannotWedge()
        {
            // The rule that keeps a slow neighbourhood from becoming a broken one. A frame that
            // begins over budget must still be allowed one build, or nothing is ever built again
            // and the symptom is "no houses and no error".
            Assert.True(NeighbourhoodFacadePlan.FrameAllowance(1, 10d, 0, 5000d) >= 1);
            Assert.True(NeighbourhoodFacadePlan.FrameAllowance(1, 10d, 0, double.PositiveInfinity) >= 1);
        }

        // ---- the ceiling: one build over the line and the pass stops ------------------------

        [Fact]
        public void ABuildUnderTheCeilingLeavesThePassWillingToTryTheNextOne()
        {
            // One-sided on purpose. The cost is the lot's, not ours: the next lot may be an
            // apartment rather than a detached house, so a cheap build says nothing about the
            // next one and the pass keeps going.
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(0d, 250d));
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(10d, 250d));
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(250d, 250d));
        }

        [Fact]
        public void ABuildOverTheCeilingIsFlaggedRatherThanAllowedQuietly()
        {
            // What a build over the line does now. It used to stop the pass, which is the bug this
            // file is named after: a 2.18 second build blew the 250ms ceiling, the pass stopped,
            // and a forty-one lot neighbourhood got one house in it. The predicate below is
            //unchanged - it still says "this build was over the line" - and everything it now
            //gates is reporting. What it must never gate again is the queue.
            Assert.False(NeighbourhoodFacadePlan.WithinBuildCeiling(251d, 250d));
            Assert.False(NeighbourhoodFacadePlan.WithinBuildCeiling(5000d, 250d));
            Assert.True(NeighbourhoodFacadePlan.DecideAfterBuild(2180d, 10d, 250d, 2).OverCeiling);
            //and it costs the pass nothing but the marking: the wait is bounded, so the next lot
            //is still coming
            Assert.True(NeighbourhoodFacadePlan.DecideAfterBuild(2180d, 10d, 250d, 2).Frames <= 2);
        }

        [Fact]
        public void ACeilingOfZeroIsNoCeiling()
        {
            // Zero has to mean "off" rather than "nothing may take even a millisecond", because
            // zero is what a caller that never set the property has, and a pass that stopped on
            // its first build would look like a pass that cannot build anything.
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(5000d, 0d));
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(5000d, -1d));
        }

        [Fact]
        public void TheCeilingDefaultsToSomethingAFrameCanSurvive()
        {
            // A quarter of a second is about fifteen frames: long enough that a slow lot still
            // gets built, short enough that one of them is a hitch and not a freeze.
            Assert.Equal(250d, NeighbourhoodFacadePlan.DefaultMaxBuildMs);
            //and it is a real ceiling rather than a formality: it has to be below the cost the
            //player's log actually showed, or the rule would have passed this neighbourhood
            Assert.True(NeighbourhoodFacadePlan.DefaultMaxBuildMs < 5000d,
                "the ceiling is above the cost that froze the game, so it would not have stopped it");
        }

        [Fact]
        public void AFrameBudgetOfNothingIsNeverAWaitOfEverything()
        {
            // The two ceilings are not the same rule and must not be allowed to become one: the
            // frame budget is a target the pass averages towards, the build ceiling is a line it
            // stops at. A build that cost 100ms is over a 10ms frame budget and must still be
            // allowed, because the frame budget is checked before a build it cannot bound.
            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(100d, 250d));
            Assert.Equal(9, NeighbourhoodFacadePlan.FramesAfterBuild(100d, 10d));
        }

        // ---- what the freeze looked like, as a simulation -----------------------------------

        /// <summary>
        /// Runs the pass's real rules over a whole neighbourhood, the way <see cref="Step"/> does,
        /// with a build of a fixed cost, and reports what it built and how long it took.
        /// </summary>
        /// <remarks>
        /// The one piece of <see cref="NeighbourhoodFacades"/> that has a graphics device in it is
        /// the build itself, so the policy around it - the per-frame cap, the delay between
        /// builds, the ceiling flag and the decision to keep going - is reproduced here over the
        /// same pure functions the class calls. Every "the neighbourhood fills in" claim below is
        /// a claim about <c>DecideAfterBuild</c> and <c>FrameAllowance</c>, which is the part that
        /// can actually stop a queue.
        /// </remarks>
        private sealed class PacedRun
        {
            public int Built;
            public int Frames;
            public int MarkedOverCeiling;
            public int LongestWait;
            public int BuildsInOneFrame;
        }

        private static PacedRun RunSchedule(
            NeighbourhoodLayout layout, Vector2 focus, double costOfOneHouse,
            int cap = NeighbourhoodFacades.DefaultBuildsPerFrame,
            double frameBudget = NeighbourhoodFacades.DefaultBuildTimeBudgetMs,
            double ceiling = NeighbourhoodFacadePlan.DefaultMaxBuildMs,
            int maxCooldown = NeighbourhoodFacadePlan.DefaultMaxCooldownFrames,
            int frameCap = 20000)
        {
            var cache = new Dictionary<short, uint>();
            var run = new PacedRun();
            var cooldown = 0;
            while (run.Frames < frameCap && run.Built < layout.LotCount)
            {
                run.Frames++;
                if (cooldown > 0) { cooldown--; continue; }
                var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, focus, cap,
                    NeighbourhoodFacadePlan.DefaultBuildRadiusTiles);
                if (wanted.Count == 0) break;
                var inThisFrame = 0;
                foreach (var lot in wanted) { cache[lot.Number] = lot.HouseHash; run.Built++; inThisFrame++; }
                run.BuildsInOneFrame = Math.Max(run.BuildsInOneFrame, inThisFrame);
                var pace = NeighbourhoodFacadePlan.DecideAfterBuild(
                    costOfOneHouse, frameBudget, ceiling, maxCooldown);
                cooldown = pace.Frames;
                run.LongestWait = Math.Max(run.LongestWait, pace.Frames);
                if (pace.OverCeiling) run.MarkedOverCeiling++;
            }
            return run;
        }

        [Fact]
        public void AFortyOneLotNeighbourhoodOfTwoSecondHousesFillsInRatherThanStoppingAtOne()
        {
            // The regression test for the reported bug, in the exact numbers from the log: every
            // build cost 2.18s against a 250ms per-frame stall ceiling, and the neighbourhood came
            // up with one house out of forty-one.
            //
            // It stopped because a build over the ceiling set a flag that ended building for the
            // rest of the visit. The flag is now a report and not an instruction, so the pass walks
            // the whole queue - and every one of those forty-one frames was held for over the
            // ceiling, which is why each of their log lines carries the over-ceiling mark and the
            // first one carries the warning that explains the policy. Silence was never an option;
            // a stop was the worst one.
            var layout = Grid(41);
            var focus = new Vector2(240f, 240f);

            var run = RunSchedule(layout, focus, costOfOneHouse: 2180d);

            Assert.Equal(41, run.Built);
            //every one of them was over the ceiling, and every one of them was logged as such
            Assert.Equal(41, run.MarkedOverCeiling);
            //one lot a frame, every time: the cap is the only thing bounding a frame
            Assert.Equal(1, run.BuildsInOneFrame);
            //and the wait between them is bounded by a constant, so the queue is never throttled
            //to a crawl by a machine that happens to be slow
            Assert.True(run.LongestWait <= NeighbourhoodFacadePlan.DefaultMaxCooldownFrames,
                "waited " + run.LongestWait + " frames between builds");
        }

        [Fact]
        public void NoBuildCostCanStopTheQueue()
        {
            // The property, over a range rather than over one reported number. A cost of zero, a
            // cost under the frame budget, a cost over the ceiling, a cost of an hour, and a cost
            // that could not be measured at all: every one of them has to reach the end of the
            // queue, because the pass has no rule that can return "stop".
            foreach (var cost in new[] { 0d, 1d, 4d, 200d, 250d, 251d, 2180d, 60000d, double.NaN })
            {
                var layout = Grid(41);
                var run = RunSchedule(layout, new Vector2(240f, 240f), cost);
                Assert.Equal(41, run.Built);
                Assert.Equal(1, run.BuildsInOneFrame);
                Assert.True(run.LongestWait <= NeighbourhoodFacadePlan.DefaultMaxCooldownFrames,
                    "a build of " + cost + "ms waited " + run.LongestWait + " frames");
            }
        }

        [Fact]
        public void TheDelayBetweenBuildsIsBoundedByAConstantHoweverExpensiveTheBuild()
        {
            // The rule as arithmetic, and the reason the cooldown is capped rather than left
            // proportional to the cost. A two second build at a ten millisecond budget is two hundred
            // quiet frames if the cooldown is left alone - and forty-one of those is a hundred and
            // twenty seconds during which the player has one house and a reason to believe the game
            // has hung. The stall has already happened when the cost is known, so a longer wait buys
            // nothing and costs the rest of the street.
            var ceiling = NeighbourhoodFacadePlan.DefaultMaxBuildMs;
            const double budget = NeighbourhoodFacades.DefaultBuildTimeBudgetMs;
            const int cap = NeighbourhoodFacadePlan.DefaultMaxCooldownFrames;

            //unclamped, this is where the pass used to end up
            Assert.Equal(217, NeighbourhoodFacadePlan.FramesAfterBuild(2180d, budget));
            //clamped, this is what it does
            Assert.Equal(cap, NeighbourhoodFacadePlan.DecideAfterBuild(2180d, budget, ceiling, cap).Frames);
            //an hour, a day: the same bounded wait
            Assert.Equal(cap, NeighbourhoodFacadePlan.DecideAfterBuild(3600000d, budget, ceiling, cap).Frames);
            //a cost that could not be measured at all is not a cost to pace against, so it is not
            //paced against - the same answer a zero build gets, and the reason the wait is never
            //a number of frames large enough to matter
            Assert.Equal(0, NeighbourhoodFacadePlan.DecideAfterBuild(
                double.PositiveInfinity, budget, ceiling, cap).Frames);
            //and a build that was measured as nothing is not delayed at all
            Assert.Equal(0, NeighbourhoodFacadePlan.DecideAfterBuild(0d, budget, ceiling, cap).Frames);
        }

        [Fact]
        public void ABuildThatFitsTheFrameBudgetIsNotDelayedAtAll()
        {
            // The other half of the clamp, and the reason it is a clamp rather than a replacement.
            // A four millisecond build is under the frame budget, so the next frame builds the next
            // house and a cheap neighbourhood still fills in at the rate the old constant of two per
            // frame was trying to buy.
            var pace = NeighbourhoodFacadePlan.DecideAfterBuild(4d,
                NeighbourhoodFacades.DefaultBuildTimeBudgetMs,
                NeighbourhoodFacadePlan.DefaultMaxBuildMs,
                NeighbourhoodFacadePlan.DefaultMaxCooldownFrames);
            Assert.Equal(0, pace.Frames);
            Assert.False(pace.OverCeiling);
        }

        [Fact]
        public void ACoolingCapOfZeroMeansEveryFrameAndNeverAMore()
        {
            // A caller that wants no pacing at all gets exactly one build a frame - the count cap
            // is still there - and a negative cap is the same answer rather than a negative wait,
            // which would sit the pass out for ever.
            Assert.Equal(0, NeighbourhoodFacadePlan.DecideAfterBuild(2180d, 10d, 250d, 0).Frames);
            Assert.Equal(0, NeighbourhoodFacadePlan.DecideAfterBuild(2180d, 10d, 250d, -5).Frames);
            //and a cap of one still leaves the pass advancing every other frame
            Assert.Equal(1, NeighbourhoodFacadePlan.DecideAfterBuild(2180d, 10d, 250d, 1).Frames);
        }

        [Fact]
        public void TheCeilingIsStillReportedOnEveryBuildThatBrokeIt()
        {
            // The other requirement, and the one that makes the first safe: a frame may not be held
            // for longer than the ceiling without the player being told. So the flag is per build,
            // and the mark that carries it is a single string the log line and the warning both
            // use - a log that marks the stalls one way and describes them another is a log whose
            // stalls have to be found by reading.
            var mark = NeighbourhoodFacades.OverCeilingMark(
                NeighbourhoodFacadePlan.DefaultMaxBuildMs);
            Assert.Contains("250ms", mark);
            Assert.Contains("stalled", mark);
            //and a build under the ceiling carries no mark, so the mark on a line is a fact
            Assert.False(NeighbourhoodFacadePlan.DecideAfterBuild(10d, 10d, 250d, 2).OverCeiling);
            Assert.True(NeighbourhoodFacadePlan.DecideAfterBuild(300d, 10d, 250d, 2).OverCeiling);
            //exactly at the line is not over it
            Assert.False(NeighbourhoodFacadePlan.DecideAfterBuild(250d, 10d, 250d, 2).OverCeiling);
        }

        [Fact]
        public void TheDefaultCoolingCapIsShortEnoughToBeARateAndLongEnoughToBeOne()
        {
            // Two frames, chosen rather than guessed. Zero would put a two second build in every
            // frame with no breathing room at all; anything past a couple of frames starts trading
            // the rest of the neighbourhood against a stall that has already happened. The test
            // that matters is the one below it: whatever this is, the pass still finishes.
            Assert.Equal(2, NeighbourhoodFacadePlan.DefaultMaxCooldownFrames);
            Assert.True(NeighbourhoodFacadePlan.DefaultMaxCooldownFrames >= 0);
        }

        [Fact]
        public void ThePerFrameCapIsIndependentOfTheCooling()
        {
            // They are two different rules and must not be confused for one. The cap bounds how
            // much of a frame is building; the cooling bounds how often a build starts. A frame
            // that has already built its one lot is refused by the cap whatever the clock says,
            // and a frame that has not built anything is allowed one build whatever the cooling -
            // which is the whole of the guarantee that a slow build cannot wedge the queue.
            Assert.Equal(0, NeighbourhoodFacadePlan.FrameAllowance(
                NeighbourhoodFacades.DefaultBuildsPerFrame, 10d, 1, 0d));
            Assert.Equal(1, NeighbourhoodFacadePlan.FrameAllowance(
                NeighbourhoodFacades.DefaultBuildsPerFrame, 10d, 0, 5000d));
            Assert.Equal(2, NeighbourhoodFacadePlan.FrameAllowance(2, 10d, 0, 0d));
            //and the pass does not advertise a cap of two
            Assert.Equal(1, NeighbourhoodFacades.DefaultBuildsPerFrame);
        }

        [Fact]
        public void ACheapNeighbourhoodStillFillsInQuickly()
        {
            // The other half of the same rule, and the reason the ceiling is a ceiling rather than
            // a blanket "never build anything". A four millisecond build is over a frame budget of
            // nothing much, is nowhere near the ceiling, and must still fill forty-one lots in.
            var layout = Grid(41);
            var cache = new Dictionary<short, uint>();
            //the middle of the grid, not one corner of it: this is about the rate, and a camera
            //sitting in a corner would defer the far lots for reasons the radius owns
            var focus = new Vector2(240f, 240f);
            const double frameBudget = NeighbourhoodFacades.DefaultBuildTimeBudgetMs;
            const double ceiling = NeighbourhoodFacadePlan.DefaultMaxBuildMs;
            const double costOfOneHouse = 4d;

            var built = 0;
            var frames = 0;
            var cooldown = 0;
            while (frames < 10000 && built < layout.LotCount)
            {
                frames++;
                if (cooldown > 0) { cooldown--; continue; }
                var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, null, focus,
                    NeighbourhoodFacades.DefaultBuildsPerFrame,
                    NeighbourhoodFacadePlan.DefaultBuildRadiusTiles);
                if (wanted.Count == 0) break;
                foreach (var lot in wanted) { cache[lot.Number] = lot.HouseHash; built++; }
                cooldown = NeighbourhoodFacadePlan.FramesAfterBuild(costOfOneHouse, frameBudget);
                Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(costOfOneHouse, ceiling));
            }

            Assert.Equal(41, built);
            //a four millisecond build is under the frame budget, so the very next frame builds the
            //next one: forty-one frames, which is the fill-in the old constant of two per frame was
            //trying to buy and could not
            Assert.True(frames <= 60, "took " + frames + " frames to fill in a cheap neighbourhood");
        }

        [Fact]
        public void AnExpensiveButNotHopelessNeighbourhoodIsSpacedRatherThanStopped()
        {
            // Between "cheap" and "hopeless" there is the band the cooldown exists for. A 200ms
            // build is over the frame budget and over four fifths of the way to the ceiling; it is
            // under the ceiling, so it is not marked, and the spacing it asks for is under the cap
            // so the pass reaches the end of the queue. This is what the ceiling must not swallow
            // and what the cap must not swallow either.
            var layout = Grid(41);
            //the middle of the grid rather than one corner: this is about the rate, and a camera
            //sitting in a corner would defer the far lots for reasons the radius owns
            var focus = new Vector2(240f, 240f);
            const double costOfOneHouse = 200d;

            Assert.True(NeighbourhoodFacadePlan.WithinBuildCeiling(
                costOfOneHouse, NeighbourhoodFacadePlan.DefaultMaxBuildMs));
            Assert.Equal(19, NeighbourhoodFacadePlan.FramesAfterBuild(
                costOfOneHouse, NeighbourhoodFacades.DefaultBuildTimeBudgetMs));
            var pace = NeighbourhoodFacadePlan.DecideAfterBuild(costOfOneHouse,
                NeighbourhoodFacades.DefaultBuildTimeBudgetMs,
                NeighbourhoodFacadePlan.DefaultMaxBuildMs,
                NeighbourhoodFacadePlan.DefaultMaxCooldownFrames);
            Assert.False(pace.OverCeiling);
            //what it would have asked for on its own, and what it actually gets: the cap is two,
            //so nineteen becomes two. A lot that costs a fifth of a second is not a lot that
            //should be spaced out over a fifth of a second of silence.
            Assert.Equal(19, NeighbourhoodFacadePlan.FramesAfterBuild(
                costOfOneHouse, NeighbourhoodFacades.DefaultBuildTimeBudgetMs));
            Assert.Equal(NeighbourhoodFacadePlan.DefaultMaxCooldownFrames, pace.Frames);
            //and the whole neighbourhood still gets built
            Assert.Equal(41, RunSchedule(layout, focus, costOfOneHouse).Built);
        }

        // ---- what a build cost, and the line that says it ------------------------------------

        private static NeighbourhoodFacadeTimings Timings(
            double total, double house = 0, double world = 0, double load = 0, double lighting = 0,
            double walls = 0, double floor = 0, double roof = 0, double assemble = 0)
        {
            return new NeighbourhoodFacadeTimings(new[]
            {
                house, world, load, lighting, walls, floor, roof, assemble
            }, total);
        }

        [Fact]
        public void TheTimingLineNamesEveryStageAndTheTotal()
        {
            // The eleven seconds of silence this replaces had no attribution at all. A line that
            // listed only the stages that cost something would send the next reader looking for
            // why the others were free, so all eight are there whatever they say.
            var line = Timings(4990d, walls: 4510d, load: 380d, house: 6d, world: 4d, floor: 90d)
                .Describe(12);

            Assert.Contains("lot 12", line);
            Assert.Contains("4.99s total", line);
            Assert.Contains("walls 4.51s", line);
            Assert.Contains("load house into VM 380ms", line);
            Assert.Contains("roof 0ms", line);
            foreach (NeighbourhoodFacadeStage stage in (NeighbourhoodFacadeStage[])Enum.GetValues(
                typeof(NeighbourhoodFacadeStage)))
            {
                if (stage == NeighbourhoodFacadeStage.None) continue;
                Assert.Contains(NeighbourhoodFacadeTimings.DescribeStage(stage), line);
            }
        }

        [Fact]
        public void TheDominantStageIsTheOneThatCostTheMost()
        {
            // What the ceiling message says the cost was "in", so it has to be the stage that
            // actually cost it. Getting this wrong would send the next person to the wrong pass.
            var walls = Timings(5000d, walls: 4900d, load: 80d);
            Assert.Equal(NeighbourhoodFacadeStage.Walls, walls.Dominant);

            var load = Timings(5000d, walls: 80d, load: 4700d, floor: 100d);
            Assert.Equal(NeighbourhoodFacadeStage.Load, load.Dominant);

            var roof = Timings(5000d, walls: 100d, roof: 4800d);
            Assert.Equal(NeighbourhoodFacadeStage.Roof, roof.Dominant);

            //nothing measured is nothing to name
            Assert.Equal(NeighbourhoodFacadeStage.None, NeighbourhoodFacadeTimings.None.Dominant);
            Assert.Equal(NeighbourhoodFacadeStage.None, Timings(0d).Dominant);
        }

        [Fact]
        public void ABuildThatNeverRanIsNotMeasured()
        {
            // A lot refused before the world existed costs a few milliseconds, and a line saying
            // "0ms total" for it would be a lie in the other direction. Measured is the question
            // the pass asks before it logs anything at all.
            Assert.False(NeighbourhoodFacadeTimings.None.Measured);
            Assert.False(Timings(0d).Measured);
            Assert.True(Timings(0.5d).Measured);
        }

        [Fact]
        public void AStallIsWrittenInSecondsSoThatItIsLegible()
        {
            // 4900ms on a log line is read as a number and skipped. 4.9s is read as a freeze.
            Assert.Equal("0ms", NeighbourhoodFacadeTimings.Milliseconds(0d));
            Assert.Equal("0ms", NeighbourhoodFacadeTimings.Milliseconds(-5d));
            Assert.Equal("4ms", NeighbourhoodFacadeTimings.Milliseconds(4.2d));
            Assert.Equal("99ms", NeighbourhoodFacadeTimings.Milliseconds(99.4d));
            Assert.Equal("250ms", NeighbourhoodFacadeTimings.Milliseconds(250d));
            Assert.Equal("1.00s", NeighbourhoodFacadeTimings.Milliseconds(1000d));
            Assert.Equal("4.90s", NeighbourhoodFacadeTimings.Milliseconds(4900d));
            Assert.Equal("12.3s", NeighbourhoodFacadeTimings.Milliseconds(12345d));
            //a stopwatch that produced something impossible says so rather than formatting it
            Assert.Equal("?ms", NeighbourhoodFacadeTimings.Milliseconds(double.NaN));
        }

        [Fact]
        public void TheTotalIsNotTheSumOfTheStages()
        {
            // The throwaway world is stood up and torn down outside every stage, and a stage that
            // threw is only charged for the part that ran. So the total is measured rather than
            // added up, and it is the total the game thread actually lost.
            var timings = Timings(5000d, walls: 1000d, load: 1000d, house: 10d);
            double sum = 0d;
            foreach (NeighbourhoodFacadeStage stage in (NeighbourhoodFacadeStage[])Enum.GetValues(
                typeof(NeighbourhoodFacadeStage)))
            {
                sum += timings[stage];
            }
            Assert.Equal(2010d, sum, 1);
            Assert.Equal(5000d, timings.TotalMs, 1);
            Assert.NotEqual(sum, timings.TotalMs);
        }

        [Fact]
        public void ATimingWithNoStagesIsReadableRatherThanThrowing()
        {
            // A caller that hands over a short array - a deserialised record, a struct that was
            // default-constructed - must get a line that says nothing rather than an
            // IndexOutOfRangeException from inside the log call.
            var malformed = new NeighbourhoodFacadeTimings(new double[3], 12d);
            var none = new NeighbourhoodFacadeTimings(null, 12d);

            Assert.Equal(0d, malformed[NeighbourhoodFacadeStage.Walls]);
            Assert.False(string.IsNullOrWhiteSpace(malformed.Describe(3)));
            Assert.False(string.IsNullOrWhiteSpace(none.Describe(3)));
            Assert.Equal(12d, none.TotalMs);
        }

        [Fact]
        public void ThereIsOneStagePerNameAndNoNameIsBlank()
        {
            // The stage list and the naming have to agree, or a stage is timed under a name the
            // line does not print - which is a cost that is measured and never read.
            var stages = NeighbourhoodFacadeTimings.None.Stages;
            Assert.Equal(NeighbourhoodFacadeTimings.StageCount, stages.Count);
            var seen = new HashSet<string>();
            foreach (NeighbourhoodFacadeStage stage in stages)
            {
                var name = NeighbourhoodFacadeTimings.DescribeStage(stage);
                Assert.False(string.IsNullOrWhiteSpace(name), stage + " has no name");
                Assert.True(seen.Add(name), stage + " is named the same as another stage");
            }
        }

            // ---- where a stage's milliseconds go ------------------------------------------------
            // This is the bug that stopped every facade in the neighbourhood being placed.

        [Fact]
        public void EveryStageHasASlotAndTheLastOneFits()
        {
            // A lot's facade is built, placed and uploaded, and then the pass charges the time to
            // the stage it was assembled in - the last of the eight - and that charge indexed an
            // eight-slot array with the stage's own enum value. Seven fitted; the eighth did not.
            // The exception was thrown after the mesh was on the GPU, was caught by the pipeline's
            // catch-all, and was reported as "while placing the lot's facade into the generated
            // world": a lot whose facade had in fact been placed, thrown away by its own stopwatch.
            var stages = NeighbourhoodFacadeTimings.None.Stages;
            var slots = new HashSet<int>();
            foreach (NeighbourhoodFacadeStage stage in stages)
            {
                var slot = NeighbourhoodFacadeTimings.SlotFor(stage);
                Assert.InRange(slot, 0, NeighbourhoodFacadeTimings.StageCount - 1);
                Assert.True(slots.Add(slot), stage + " shares a slot with another stage");
            }
            Assert.Equal(NeighbourhoodFacadeTimings.StageCount, slots.Count);
            //the stage that used to be the one past the end
            Assert.Equal(NeighbourhoodFacadeTimings.StageCount - 1,
                NeighbourhoodFacadeTimings.SlotFor(NeighbourhoodFacadeStage.Assemble));
            //and a stage that is not timed has no slot at all, rather than slot -1 of the first
            Assert.Equal(-1, NeighbourhoodFacadeTimings.SlotFor(NeighbourhoodFacadeStage.None));
            Assert.Equal(-1, NeighbourhoodFacadeTimings.SlotFor((NeighbourhoodFacadeStage)99));
        }

        [Fact]
        public void ChargingTheLastStageLandsInTheArray()
        {
            // The regression itself: charge every stage the way the builder does, in pipeline
            // order, and nothing may throw and nothing may be lost.
            var stages = NeighbourhoodFacadeTimings.NewStages();
            foreach (NeighbourhoodFacadeStage stage in NeighbourhoodFacadeTimings.None.Stages)
            {
                Assert.True(NeighbourhoodFacadeTimings.TryCharge(stages, stage, 10d), stage + " was not charged");
            }

            var timings = new NeighbourhoodFacadeTimings(stages, 80d);
            foreach (NeighbourhoodFacadeStage stage in NeighbourhoodFacadeTimings.None.Stages)
            {
                Assert.Equal(10d, timings[stage], 3);
            }
            //and the un-timed stage is not one of the eight, whatever is in slot zero
            Assert.Equal(0d, timings[NeighbourhoodFacadeStage.None], 3);
        }

        [Fact]
        public void ChargingAnUntimedStageIsRefusedRatherThanThrown()
        {
            // A stopwatch that cannot record one stage is a log line that is one stage short. A
            // stopwatch that throws takes the whole facade with it, which is the bug above.
            var stages = NeighbourhoodFacadeTimings.NewStages();
            Assert.False(NeighbourhoodFacadeTimings.TryCharge(
                stages, NeighbourhoodFacadeStage.None, 5d));
            Assert.False(NeighbourhoodFacadeTimings.TryCharge(
                stages, (NeighbourhoodFacadeStage)99, 5d));
            Assert.False(NeighbourhoodFacadeTimings.TryCharge(null, NeighbourhoodFacadeStage.Walls, 5d));
            //a short array is somebody else's mistake, and must not be this one's exception
            Assert.False(NeighbourhoodFacadeTimings.TryCharge(
                new double[2], NeighbourhoodFacadeStage.Assemble, 5d));
            Assert.All(stages, s => Assert.Equal(0d, s));
        }

        [Fact]
        public void AStageIsPrintedWithItsOwnCost()
        {
            // The other half of the same off-by-one, and the reason this bug was read twice: the
            // line labelled every stage with the cost of the stage before it. A 1.58 second wall
            // pass came out of this as "floor 1.58s", the stage that actually cost it was never
            // printed, and the search for the cost started in the floor pass. Each label now has
            // to be able to find its own number in the line.
            var line = Timings(600d, house: 1d, world: 2d, load: 3d, lighting: 4d,
                walls: 500d, floor: 50d, roof: 20d, assemble: 6d).Describe(64);

            var costs = new NeighbourhoodFacadeTimings(
                new double[] { 1d, 2d, 3d, 4d, 500d, 50d, 20d, 6d }, 600d);
            foreach (NeighbourhoodFacadeStage stage in NeighbourhoodFacadeTimings.None.Stages)
            {
                var labelled = NeighbourhoodFacadeTimings.DescribeStage(stage)
                    + " " + NeighbourhoodFacadeTimings.Milliseconds(costs[stage]);
                Assert.Contains(labelled, line);
            }
            //the wall pass is named as the wall pass this time
            Assert.Contains("walls 500ms", line);
            Assert.Contains("load house into VM 3ms", line);
            Assert.Contains("assemble 6ms", line);
            Assert.DoesNotContain("floor 500ms", line);
        }

        [Fact]
        public void ATimingsWithNoStagesAtAllStillPrintsALine()
        {
            // The log line is written after a build has already gone wrong, from a struct that may
            // have been default-constructed. A line that throws is a line nobody reads.
            var line = default(NeighbourhoodFacadeTimings).Describe(64);
            Assert.Contains("lot 64", line);
            foreach (NeighbourhoodFacadeStage stage in NeighbourhoodFacadeTimings.None.Stages)
            {
                Assert.Contains(NeighbourhoodFacadeTimings.DescribeStage(stage), line);
            }
            Assert.Equal(NeighbourhoodFacadeStage.None, default(NeighbourhoodFacadeTimings).Dominant);
        }
    }
}
