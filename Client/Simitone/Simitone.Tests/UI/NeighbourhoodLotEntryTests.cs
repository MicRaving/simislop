/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Entering a lot removes the neighbourhood view; it does not refresh it.
    ///
    /// <para>
    /// The report this pins is a screenshot, not a stack trace: a player clicked a house and found
    /// the neighbourhood's 3D map drawn over the lot, immovable, on top of a lot they were
    /// standing in. The world lives in <c>GameFacade.Scenes</c>, the lot view draws in the same
    /// layer, and the two things that put it there - dropping the panel's world as stale and
    /// rebuilding it - ran <em>after</em> the panel had been removed from the screen, so nothing
    /// left in the session could ever take it down again.
    /// </para>
    ///
    /// <para>
    /// The rule is a value with no screen, no panel and no scene layer in it
    /// (<see cref="NeighbourhoodLotEntry"/>), so "a lot load detaches the view", "nothing is
    /// regenerated while a lot is up" and "the neighbourhood screen still brings its world up to
    /// date" are assertions here rather than something a screenshot has to demonstrate.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLotEntryTests
    {
        [Fact]
        public void ALotLoadTakesTheViewDown()
        {
            var decision = NeighbourhoodLotEntry.For(lotLoaded: true);

            Assert.True(decision.DetachesPanel, "the panel and its switcher leave the screen");
            Assert.True(decision.RemovesScene,
                "the 3D world and its proxy leave GameFacade.Scenes, which is the part a player sees");
        }

        [Fact]
        public void ALotLoadNeverRegeneratesOrInvalidatesTheGeneratedWorld()
        {
            var decision = NeighbourhoodLotEntry.For(lotLoaded: true);

            //the world being refreshed is what drew the overlay, and the world being dropped is
            //what made the lot load generate 512x512 tiles of a neighbourhood nobody was looking at
            Assert.False(decision.RefreshesGeneratedWorld);
            Assert.False(decision.InvalidatesGeneratedWorld);
        }

        [Fact]
        public void TheNeighbourhoodScreenStillRefreshesItsOwnWorld()
        {
            var decision = NeighbourhoodLotEntry.For(lotLoaded: false);

            Assert.False(decision.DetachesPanel, "there is nothing to detach with no lot up");
            Assert.False(decision.RemovesScene);
            Assert.True(decision.RefreshesGeneratedWorld,
                "a bulldoze or a rezone on the neighbourhood screen still repairs the world");
        }

        [Fact]
        public void EveryAnswerSaysWhy()
        {
            foreach (var loaded in new[] { true, false })
            {
                var decision = NeighbourhoodLotEntry.For(loaded);
                Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                Assert.Contains(loaded ? "removes the neighbourhood view" : "up to date", decision.Reason);
            }
        }
    }

    /// <summary>
    /// The lot load itself never waits for a generated neighbourhood: the surround is attached now
    /// when a world is already in memory, and after a worker has made one when it is not.
    ///
    /// <para>
    /// The report was "the game is stuck 20s in the final loading stage, then very slowly fades into
    /// the static 2d background". Twenty IFF reads, a relaxation pass and 262 144 tiles of
    /// generation were running inside <c>TS1GameScreen.InitializeLot</c>, on the thread bringing the
    /// lot up, before its first frame. This is the decision that came out of that, as a value over
    /// two booleans so that "it never generates on this thread" is checkable at all.
    /// </para>
    /// </summary>
    public sealed class SimitoneSurroundingsAttachTests
    {
        /// <summary>FreeSO's surrounding-lot detail as a Simitone install ships it: the lowest step
        /// that is still on, which is the only thing the gate asks about.</summary>
        private const int On = SurroundingLotModeRow.Default;

        [Fact]
        public void AWarmNeighbourhoodIsAttachedOnTheLotLoadFrame()
        {
            // The second lot of a session in one neighbourhood: the world is in memory, so attaching
            // it costs a placement and a mesh and nothing else.
            var enabled = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, On);
            var plan = SimitoneSurroundingsAttachPlan.Decide(enabled, worldReady: true);

            Assert.Equal(SimitoneSurroundingsAttachTiming.Now, plan.Timing);
            Assert.True(plan.AttachesNow);
            Assert.False(plan.IsAsynchronous);
        }

        [Fact]
        public void AColdNeighbourhoodIsAttachedAfterAWorkerHasGeneratedOne()
        {
            // The case the report is about. The lot comes up first and the horizon fills in; the
            // generation runs on the worker's thread and the attach happens on the game thread when
            // it lands.
            var enabled = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, On);
            var plan = SimitoneSurroundingsAttachPlan.Decide(enabled, worldReady: false);

            Assert.Equal(SimitoneSurroundingsAttachTiming.WhenGenerated, plan.Timing);
            Assert.True(plan.IsAsynchronous);
            Assert.False(plan.AttachesNow, "there is no world to attach yet, and making one here is the stall");
        }

        [Fact]
        public void NothingIsAttachedWhenTheSettingsSayTheLotIsPlayedAsItIs()
        {
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var plan = SimitoneSurroundingsAttachPlan.Decide(
                    SimitoneSurroundingsSelection.For(view, On), worldReady: true);

                Assert.Equal(SimitoneSurroundingsAttachTiming.Never, plan.Timing);
                Assert.False(plan.AttachesNow);
                Assert.False(plan.IsAsynchronous);
            }

            //and the same for FreeSO's own surrounding-lot detail being off
            var off = SimitoneSurroundingsAttachPlan.Decide(
                SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 0), worldReady: true);
            Assert.Equal(SimitoneSurroundingsAttachTiming.Never, off.Timing);
        }

        [Fact]
        public void NoTimingIsPossibleThatGeneratesOnTheLotLoadThread()
        {
            // The whole claim in one loop: whatever the settings and whatever the cache says, the
            // answer is one of three values and none of them generates anything.
            foreach (var view in new[]
            {
                NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D, NeighbourhoodView.World3D
            })
            {
                foreach (var detail in new[] { 0, On, On + 1 })
                {
                    foreach (var warm in new[] { true, false })
                    {
                        var plan = SimitoneSurroundingsAttachPlan.Decide(
                            SimitoneSurroundingsSelection.For(view, detail), warm);
                        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
                        Assert.True(plan.Timing == SimitoneSurroundingsAttachTiming.Never
                            || plan.Timing == SimitoneSurroundingsAttachTiming.Now
                            || plan.Timing == SimitoneSurroundingsAttachTiming.WhenGenerated);
                    }
                }
            }
        }
    }
}
