/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics.Model;
using Simitone.Client.Utils;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the rule behind the plumbbob's motive state: which of a sim's needs the diamond
    /// above their head is showing, and how much faster it turns because of it.
    ///
    /// <remarks>
    /// <para>
    /// The mapping is pure - eight shorts in, a struct out - so it can be checked exhaustively
    /// here rather than being looked for on a lot. What matters:
    /// </para>
    /// <list type="bullet">
    /// <item><b>The winner is the lowest need.</b> Not a fixed pick, and not "the one the player
    /// is hovering": a sim whose bladder has run out shows their bladder whatever else is low,
    /// because that is the need that will get them moving.</item>
    /// <item><b>A tie goes to the earlier need in the cycle.</b> Two needs sitting at exactly
    /// the same value is ordinary - a sim who has just finished eating and used the bathroom -
    /// and the answer must be stable, or the plumbbob flickers between two motives.</item>
    /// <item><b>The bands are the resting look plus two steps.</b> A comfortable sim gets no
    /// boost at all, so a sim in no trouble looks exactly as it did before this existed.</item>
    /// <item><b>The scale is the engine's.</b> Motives are -100..100
    /// (<c>VMAvatar.SetMotiveData</c> clamps them there), so the band edges are checked against
    /// both ends of the real range rather than against round numbers.</item>
    /// </list>
    /// </remarks>
    public sealed class PlumbbobMotiveTests
    {
        /// <summary>Every need content, i.e. nothing is wrong with this sim.</summary>
        private const short Full = 100;

        /// <summary>Evaluation with every argument at <paramref name="value"/>.</summary>
        private static PlumbbobPresentation All(short value)
        {
            return PlumbbobMotive.Evaluate(value, value, value, value, value, value, value, value);
        }

        /// <summary>Evaluation of one sim's eight needs, in <c>Priority</c> order.</summary>
        private static PlumbbobPresentation Evaluate(short[] needs)
        {
            return PlumbbobMotive.Evaluate(needs[0], needs[1], needs[2], needs[3],
                needs[4], needs[5], needs[6], needs[7]);
        }

        #region the watch list

        [Fact]
        public void TheWatchListHasEightDistinctEntries()
        {
            // PlumbbobMotive.Evaluate stack-allocates WatchedNeeds shorts and reads
            // Priority[worst] out of the result, so the two have to stay the same length and the
            // list must not name the same need twice.
            Assert.Equal(8, PlumbbobMotive.WatchedNeeds);
            Assert.Equal(PlumbbobMotive.WatchedNeeds, PlumbbobMotive.Priority.Length);
            Assert.Equal(PlumbbobMotive.WatchedNeeds, PlumbbobMotive.Priority.Distinct().Count());
        }

        [Fact]
        public void TheWatchListIsTheOrderACycleWouldVisit()
        {
            // TS1's own need order: the ones that stop a sim doing anything at all come first,
            // and the two that are only a mood come last. Bound to the engine's enum so a rename
            // breaks this rather than quietly changing what the plumbbob watches.
            Assert.Equal(new[]
            {
                VMMotive.Hunger,
                VMMotive.Bladder,
                VMMotive.Energy,
                VMMotive.Hygiene,
                VMMotive.Comfort,
                VMMotive.Fun,
                VMMotive.Social,
                VMMotive.Room,
            }, PlumbbobMotive.Priority);
        }

        [Fact]
        public void MoodIsNotWatched()
        {
            // it is a mood rather than a need, and the live-mode button next to the plumbbob
            // already draws it as a bar (UILiveButton reads VMMotive.Mood every frame)
            Assert.DoesNotContain(VMMotive.Mood, PlumbbobMotive.Priority);
            Assert.DoesNotContain(VMMotive.HappyLife, PlumbbobMotive.Priority);
            Assert.DoesNotContain(VMMotive.HappyWeek, PlumbbobMotive.Priority);
            Assert.DoesNotContain(VMMotive.HappyDay, PlumbbobMotive.Priority);
        }

        #endregion

        #region the winner

        [Fact]
        public void AContentSimHasNothingToShow()
        {
            var presentation = All(Full);
            Assert.Equal(VMMotive.Hunger, presentation.Motive); // the cycle's first need, by the tie rule
            Assert.Equal(Full, presentation.Value);
            Assert.Equal(PlumbbobUrgency.Calm, presentation.Urgency);
            Assert.Equal(0f, presentation.SpinBoost);
        }

        [Fact]
        public void TheLowestNeedWins()
        {
            // only comfort is low; everything else is full
            var presentation = PlumbbobMotive.Evaluate(Full, Full, Full, Full, -40, Full, Full, Full);
            Assert.Equal(VMMotive.Comfort, presentation.Motive);
            Assert.Equal(-40, presentation.Value);
            Assert.Equal(PlumbbobUrgency.Urgent, presentation.Urgency);
        }

        [Fact]
        public void TheLowestOfSeveralLowNeedsWins()
        {
            // hunger is low and bladder is empty: the empty one is what is shown
            var presentation = PlumbbobMotive.Evaluate(10, -80, Full, Full, Full, Full, Full, Full);
            Assert.Equal(VMMotive.Bladder, presentation.Motive);
            Assert.Equal(-80, presentation.Value);
        }

        [Fact]
        public void ATieGoesToTheEarlierNeedInTheCycle()
        {
            // hunger and energy are at exactly the same value, so the plumbbob must not flicker
            // between them from one frame to the next
            var first = PlumbbobMotive.Evaluate(-20, Full, -20, Full, Full, Full, Full, Full);
            var second = PlumbbobMotive.Evaluate(-20, Full, -20, Full, Full, Full, Full, Full);
            Assert.Equal(first.Motive, second.Motive);
            Assert.Equal(VMMotive.Hunger, first.Motive);
        }

        [Fact]
        public void ATieGoesToTheEarlierNeedEvenAtTheEndOfTheCycle()
        {
            // only social and room are low, and social comes earlier in the cycle
            var presentation = PlumbbobMotive.Evaluate(Full, Full, Full, Full, Full, Full, 5, 5);
            Assert.Equal(VMMotive.Social, presentation.Motive);
        }

        [Fact]
        public void EachWatchedNeedCanBeTheOneThatWins()
        {
            // every entry in the list has to be reachable, or the cycle order is decorative.
            // `values` is in Priority order, which is the order Evaluate takes its arguments in:
            // emptying slot i has to make the plumbbob show Priority[i], which is the property
            // that catches the two lists drifting apart.
            for (int i = 0; i < PlumbbobMotive.Priority.Length; i++)
            {
                var values = new short[] { Full, Full, Full, Full, Full, Full, Full, Full };
                values[i] = -50;

                var presentation = PlumbbobMotive.Evaluate(values[0], values[1], values[2], values[3],
                    values[4], values[5], values[6], values[7]);
                Assert.Equal(PlumbbobMotive.Priority[i], presentation.Motive);
                Assert.Equal(-50, presentation.Value);
            }
        }

        #endregion

        #region the bands

        [Fact]
        public void TheBandEdgesAreTheEngineScale()
        {
            // 0 is "empty", which is where TS1 puts a need it has run out of, and -100 is the
            // floor VMAvatar.SetMotiveData clamps to. Neither end may fall off the table.
            Assert.Equal(PlumbbobUrgency.Urgent, PlumbbobMotive.UrgencyFor(-100));
            Assert.Equal(PlumbbobUrgency.Urgent, PlumbbobMotive.UrgencyFor(short.MinValue));
            Assert.Equal(PlumbbobUrgency.Calm, PlumbbobMotive.UrgencyFor(100));
            Assert.Equal(PlumbbobUrgency.Calm, PlumbbobMotive.UrgencyFor(short.MaxValue));
        }

        [Fact]
        public void AFullBarIsNotYetAProblem()
        {
            // matches how the live-mode motive bars read: the top of the bar is not a problem,
            // an empty one is
            Assert.Equal(PlumbbobUrgency.Calm, PlumbbobMotive.UrgencyFor(PlumbbobMotive.CalmThreshold));
            Assert.Equal(PlumbbobUrgency.Concerned, PlumbbobMotive.UrgencyFor(PlumbbobMotive.CalmThreshold - 1));
        }

        [Fact]
        public void AnEmptyBarIsAProblem()
        {
            Assert.Equal(PlumbbobUrgency.Concerned, PlumbbobMotive.UrgencyFor(PlumbbobMotive.ConcernedThreshold));
            Assert.Equal(PlumbbobUrgency.Urgent, PlumbbobMotive.UrgencyFor(PlumbbobMotive.ConcernedThreshold - 1));
        }

        [Fact]
        public void TheEvaluationUsesTheSameBandsAsTheLookup()
        {
            foreach (var value in new short[] { -100, -1, 0, 1, 39, 40, 41, 100 })
            {
                Assert.Equal(PlumbbobMotive.UrgencyFor(value), All(value).Urgency);
                Assert.Equal(PlumbbobMotive.SpinBoostFor(PlumbbobMotive.UrgencyFor(value)), All(value).SpinBoost);
            }
        }

        #endregion

        #region the spin

        [Fact]
        public void AContentSimSpinsAtTheRestingRate()
        {
            // no boost at all, so a sim in no trouble is drawn exactly as it was before the
            // plumbbob had an opinion
            Assert.Equal(0f, PlumbbobMotive.SpinBoostFor(PlumbbobUrgency.Calm));
            Assert.Equal(PlumbbobMotive.CalmSpinBoost, PlumbbobMotive.SpinBoostFor(PlumbbobUrgency.Calm));
        }

        [Fact]
        public void TheSpinGetsFasterAsTheNeedGetsWorse()
        {
            Assert.True(PlumbbobMotive.SpinBoostFor(PlumbbobUrgency.Concerned) > 0f);
            Assert.True(PlumbbobMotive.SpinBoostFor(PlumbbobUrgency.Urgent)
                > PlumbbobMotive.SpinBoostFor(PlumbbobUrgency.Concerned));
        }

        [Fact]
        public void TheBoostIsSmallEnoughNotToBeAbsurd()
        {
            // the resting spin is 3 rad/s, and the boost is added on top of it. Anything much
            // over a few rad/s turns the diamond into a blur.
            Assert.InRange(PlumbbobMotive.UrgentSpinBoost, 1f, 4f);
            Assert.InRange(PlumbbobMotive.ConcernedSpinBoost, 0.5f, 2f);
            Assert.True(PlumbbobMotive.ConcernedSpinBoost < PlumbbobMotive.UrgentSpinBoost);
        }

        [Fact]
        public void AnUnknownBandIsTreatedAsTheRestingOne()
        {
            // defensive rather than defensive-coding: a band added to the enum without a spin
            // boost should leave a sim alone, not spin it at random
            Assert.Equal(PlumbbobMotive.CalmSpinBoost,
                PlumbbobMotive.SpinBoostFor((PlumbbobUrgency)99));
        }

        #endregion

        #region the switch

        [Fact]
        public void TheMotiveStateIsOnByDefault()
        {
            // there is no ini key for it - GlobalSettings and WorldConfig have nothing
            // plumbbob-shaped - so the flag is on and the Options page is the follow-up that
            // gives the player a way to turn it off
            Assert.True(PlumbbobMotive.Enabled);
        }

        #endregion

        #region not a hover feature

        [Fact]
        public void OneSimsNeedsCannotLeakOntoTheNextSim()
        {
            // The call site asks about every sim carrying a diamond, once per frame, in a loop
            // over ObjectQueries.Avatars - so the answers have to be independent of each other
            // and of the order they were asked in. A cached "last evaluation", or any other state
            // carried between calls, would hand a sim its neighbour's spin and the diamond would
            // flicker between them as the camera moves.
            //
            // Three sims that have to stay distinguishable - a content one, a desperate one and a
            // merely low one - so a leak shows up as the wrong band rather than as two answers
            // that happen to match.
            var needs = new[]
            {
                new short[] { Full, Full, Full, Full, Full, Full, Full, Full },
                new short[] { -90, -90, -90, -90, Full, Full, Full, Full },
                new short[] { 10, Full, Full, Full, Full, Full, Full, Full },
            };

            var forwards = new PlumbbobPresentation[needs.Length];
            for (int i = 0; i < needs.Length; i++) forwards[i] = Evaluate(needs[i]);

            // same three sims, asked in the opposite order, each answer still landing against its
            // own sim rather than against the call that preceded it
            var backwards = new PlumbbobPresentation[needs.Length];
            for (int i = needs.Length - 1; i >= 0; i--) backwards[i] = Evaluate(needs[i]);

            for (int i = 0; i < needs.Length; i++)
            {
                Assert.Equal(forwards[i].Motive, backwards[i].Motive);
                Assert.Equal(forwards[i].Value, backwards[i].Value);
                Assert.Equal(forwards[i].Urgency, backwards[i].Urgency);
                Assert.Equal(forwards[i].SpinBoost, backwards[i].SpinBoost);
            }

            // and the three really are different answers, so the loop above compared something
            Assert.Equal(PlumbbobUrgency.Calm, forwards[0].Urgency);
            Assert.Equal(PlumbbobUrgency.Urgent, forwards[1].Urgency);
            Assert.Equal(PlumbbobUrgency.Concerned, forwards[2].Urgency);
        }

        [Fact]
        public void TheAnswerIsTheSimsOwnAndNobodyElses()
        {
            // Asked twice about the same needs with something else asked in between, the answer is
            // the first sim's - the property that lets the plumbbob be a standing fact about
            // every sim on the lot rather than something that only exists under the cursor
            var before = PlumbbobMotive.Evaluate(Full, 5, Full, Full, Full, Full, Full, Full);
            PlumbbobMotive.Evaluate(-100, Full, Full, Full, Full, Full, Full, Full);
            var after = PlumbbobMotive.Evaluate(Full, 5, Full, Full, Full, Full, Full, Full);

            Assert.Equal(before.Motive, after.Motive);
            Assert.Equal(before.Value, after.Value);
            Assert.Equal(before.SpinBoost, after.SpinBoost);
        }

        [Fact]
        public void ANullAvatarIsAContentSimRatherThanADefault()
        {
            // ForAvatar takes a VMAvatar and the lot loop is full of them; a null must read as
            // "nothing to show" (the resting look) rather than as the bottom of the scale, which
            // would put the fastest spin in the game over a sim that does not exist
            var presentation = PlumbbobMotive.ForAvatar(null);
            Assert.Equal(PlumbbobUrgency.Calm, presentation.Urgency);
            Assert.Equal(PlumbbobMotive.CalmSpinBoost, presentation.SpinBoost);
        }

        #endregion
    }
}
