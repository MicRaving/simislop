using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using Simitone.Client.UI.Panels.LiveSubpanels;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The engine half of TS1's fame career: the half-star ladder in
    /// <see cref="VMTS1FameHelper"/> and the two global calls that walk it
    /// (<see cref="VMGenericTS1CallMode.PromoteFameIfNeeded"/> 34 and
    /// <see cref="VMGenericTS1CallMode.DemoteFameIfNeeded"/> 36).
    ///
    /// <para>
    /// These are the numbers the whole feature rests on. <c>VMGenericTS1Call.AdjustFame</c> does
    /// no arithmetic of its own - it reads a sim's fame point total out of person data, hands it
    /// to <see cref="VMTS1FameHelper.Promote"/>/<see cref="VMTS1FameHelper.Demote"/>, and writes
    /// the result back to both the live avatar and the NBR chunk - so if the ladder is wrong the
    /// career is wrong, and a wrong ladder is invisible until someone playtests Studio Town.
    /// These tests stand in for that playtest on the arithmetic.
    /// </para>
    ///
    /// <para>
    /// The tiers, the titles and the point totals come from the career table the TS1 community
    /// compiled out of the retail game; <see cref="VMTS1FameHelper"/> cites it. The property
    /// worth defending is not "these are the right numbers" (that is a research claim, pinned
    /// here so a later edit has to argue with it) but the shape the numbers have to keep:
    ///
    ///   * promotion is monotonic, never needs a second call, and never gives a star back;
    ///   * demotion is the exact inverse and stops at nobody;
    ///   * one call can move several rungs, because a single Studio Town award can be worth more
    ///     than a whole star at that level;
    ///   * Star Power is bounded 0..10 half-stars, and person data is a raw short that a corrupt
    ///     save or a hand-edited value must not be able to walk off either end of.
    /// </para>
    ///
    /// <para>
    /// The last group is the point of the whole exercise: it ties the engine's ladder to
    /// <see cref="RelationshipFameFilter"/>, Simitone's fame tab, across the two assemblies. It
    /// is the only automated proof available that the tab can ever be non-empty - the filter
    /// reads a neighbour's persisted <see cref="VMPersonDataVariable.TS1FameScore"/> and admits
    /// anything strictly above 1, and before this ladder existed nothing in either tree ever
    /// wrote that slot, so the tab was permanently empty.
    /// </para>
    /// </summary>
    public sealed class VMTS1FameHelperTests
    {
        // Re-declared as shorts so the assertions below read as "2 half stars" rather than
        // juggling casts. xUnit's Assert.Equal will not infer a common type across an int
        // literal and a short, so a bare Assert.Equal(8, ...Promote(...)) does not compile.
        private const short NoStars = VMTS1FameHelper.NoStars;
        private const short MaxStarPower = VMTS1FameHelper.MaxStarPower;
        private const short FirstFamePoints = VMTS1FameHelper.FirstFamePoints;
        private const short Superstar = 10;

        /// <summary>
        /// The documented ladder: half stars, the fame points that tier costs, and the title the
        /// retail game shows for it. The titles are the one place this file says "this is what
        /// the game does" rather than "this is what the code does".
        /// </summary>
        public static TheoryData<short, short> FameLadder => new TheoryData<short, short>
        {
            { 0, 0 },      // nobody
            { 1, 2 },      // stepping stone
            { 2, 9 },      // insider
            { 3, 21 },     // name dropper
            { 4, 35 },     // studio fly
            { 5, 52 },     // sell out
            { 6, 78 },     // trendsetter
            { 7, 156 },    // player
            { 8, 300 },    // talk of the town
            { 9, 550 },    // celebrity
            { 10, 900 },   // superstar
        };

        // ---- the ladder itself -------------------------------------------------------------

        [Theory]
        [MemberData(nameof(FameLadder))]
        public void StarPowerIsTheTierTheFamePointTotalHasPaidFor(short halfStars, short famePoints)
        {
            Assert.Equal(halfStars, VMTS1FameHelper.StarPowerForFamePoints(famePoints));

            // A sim promoted from scratch lands on exactly the same tier, which is the property
            // the career relies on: scripts add points, then call promote, and the rating that
            // comes out has to be the tier those points bought.
            Assert.Equal(halfStars, VMTS1FameHelper.Promote(famePoints, NoStars));

            // One point short of a tier is still the tier below - half stars are not rounded.
            if (halfStars > 0)
            {
                Assert.Equal((short)(halfStars - 1), VMTS1FameHelper.StarPowerForFamePoints((short)(famePoints - 1)));
            }
        }

        [Fact]
        public void TheLadderIsTenRungsOfHalfStarsWithMonotonicCosts()
        {
            Assert.Equal((short)(MaxStarPower + 1), (short)VMTS1FameHelper.FamePointsForStars.Length);
            Assert.Equal(Superstar, MaxStarPower); // ten half stars is five full stars

            // Strictly increasing: a tier that cost no more than the one below it would let a
            // single award promote two rungs for the price of one.
            for (short i = 1; i < VMTS1FameHelper.FamePointsForStars.Length; i++)
            {
                Assert.True(VMTS1FameHelper.FamePointsForStars[i] > VMTS1FameHelper.FamePointsForStars[i - 1],
                    "tier " + i + " must cost more than tier " + (i - 1));
            }
        }

        // ---- PromoteFameIfNeeded (34) ------------------------------------------------------

        [Fact]
        public void PromoteDoesNothingUntilTheNextRungIsAffordable()
        {
            // One point short of the first rung: still nobody.
            Assert.Equal(NoStars, VMTS1FameHelper.Promote(1, NoStars));
            // And the first rung is where it starts.
            Assert.Equal((short)1, VMTS1FameHelper.Promote(FirstFamePoints, NoStars));
        }

        [Fact]
        public void PromoteIsIdempotentAndNeverTakesAStarBack()
        {
            var promoted = VMTS1FameHelper.Promote(300, NoStars);
            Assert.Equal((short)8, promoted);

            // Calling again with the same numbers is a no-op - a lot script cannot know whether
            // it has already promoted, so the call has to be safe to repeat.
            Assert.Equal(promoted, VMTS1FameHelper.Promote(300, promoted));
            Assert.Equal(promoted, VMTS1FameHelper.Promote(300, promoted));
        }

        [Fact]
        public void PromoteCrossesEveryRungTheTotalHasPaidForInOneCall()
        {
            // A movie set pays 55-80 points, which is a whole star at that level, so one award
            // has to be able to take a sim up more than one tier.
            Assert.Equal((short)5, VMTS1FameHelper.Promote(55, (short)4));
            Assert.Equal((short)6, VMTS1FameHelper.Promote(80, (short)4));

            // A cheat, or a save that skipped years, can arrive far past the top; everything
            // collapses to the ceiling rather than running off the end of the ladder.
            Assert.Equal(MaxStarPower, VMTS1FameHelper.Promote(32000, NoStars));
        }

        [Fact]
        public void PromoteNeverExceedsTheCeiling()
        {
            Assert.Equal(MaxStarPower, VMTS1FameHelper.Promote(900, MaxStarPower));
            Assert.Equal(MaxStarPower, VMTS1FameHelper.Promote(short.MaxValue, MaxStarPower));
        }

        // ---- DemoteFameIfNeeded (36) --------------------------------------------------------

        [Fact]
        public void DemoteDropsToTheTierTheTotalStillPaysFor()
        {
            // The documented -50 penalty at 4.5 stars leaves 500, which still pays for 4 stars.
            Assert.Equal((short)8, VMTS1FameHelper.Demote(500, (short)9));
            // The -30 penalty at 4 stars leaves 270: one point short of 4 stars' worth, so
            // exactly one rung goes.
            Assert.Equal((short)7, VMTS1FameHelper.Demote(270, (short)8));
            // A total comfortably inside its tier does not cost a star at all.
            Assert.Equal((short)8, VMTS1FameHelper.Demote(350, (short)8));
        }

        [Fact]
        public void DemoteCanDropSeveralRungsAndStopsAtNobody()
        {
            // Fame decays while a sim stays away from Studio Town, and a long absence can take a
            // superstar most of the way back down in one pass.
            Assert.Equal((short)2, VMTS1FameHelper.Demote(15, MaxStarPower));
            // Whatever the total does, a sim with no fame left is nobody - including when a
            // penalty took the total negative.
            Assert.Equal(NoStars, VMTS1FameHelper.Demote(0, (short)4));
            Assert.Equal(NoStars, VMTS1FameHelper.Demote(-40, (short)4));
        }

        [Fact]
        public void DemoteIsIdempotent()
        {
            var demoted = VMTS1FameHelper.Demote(100, MaxStarPower);
            Assert.Equal((short)6, demoted);
            Assert.Equal(demoted, VMTS1FameHelper.Demote(100, demoted));
        }

        [Fact]
        public void DemoteLeavesARatingThatIsAheadOfItsTotalAlone()
        {
            // The point total is not demoted with the rating, so a rating sitting above its
            // total is not "owed" a drop - that is Promote's job, in the other direction.
            Assert.Equal((short)9, VMTS1FameHelper.Demote(600, (short)9));
        }

        // ---- the high watermark (slot 82) --------------------------------------------------

        [Fact]
        public void TheWatermarkRisesWithPromotionAndSurvivesDemotion()
        {
            // Studio Town keeps a demoted sim on the "who's hot" page and still counts them
            // towards another sim's famous-friends requirement, so the watermark has to be a
            // separate, never-decreasing record of the best tier they ever held.
            Assert.Equal((short)8, VMTS1FameHelper.RaiseWatermark(NoStars, (short)8));
            Assert.Equal((short)8, VMTS1FameHelper.RaiseWatermark((short)8, (short)7));
            Assert.Equal((short)8, VMTS1FameHelper.RaiseWatermark((short)8, NoStars));
        }

        [Fact]
        public void TheWatermarkOnlyEverRisesAcrossAFullCareer()
        {
            short watermark = NoStars;
            short stars = NoStars;

            for (int i = 0; i < 40; i++)
            {
                stars = VMTS1FameHelper.Promote((short)(i * 25), stars);
                watermark = VMTS1FameHelper.RaiseWatermark(watermark, stars);

                // Then be beaten for it, as hard as the worst documented penalty allows.
                stars = VMTS1FameHelper.Demote((short)Math.Max(0, i * 25 - 50), stars);
                watermark = VMTS1FameHelper.RaiseWatermark(watermark, stars);

                // The best tier ever held is never below the current one, and both stay on the
                // ladder. A watermark that could fall would drop a sim off the tabloid page.
                Assert.InRange(watermark, stars, MaxStarPower);
            }
        }

        // ---- person data is a raw short; clamp it -----------------------------------------

        [Theory]
        [InlineData(-30000, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(5, 5)]
        [InlineData(10, 10)]
        [InlineData(11, 10)]
        [InlineData(32000, 10)]
        public void OutOfRangeStarPowerIsClampedToTheLadder(int stored, int expected)
        {
            var clamped = VMTS1FameHelper.ClampStarPower((short)stored);
            Assert.Equal((short)expected, clamped);

            // Both directions have to survive it, since a hand-edited or corrupt save is exactly
            // how the value got wrong in the first place.
            Assert.InRange(VMTS1FameHelper.Promote(300, (short)stored), NoStars, MaxStarPower);
            Assert.InRange(VMTS1FameHelper.Demote(0, (short)stored), NoStars, MaxStarPower);
            Assert.InRange(VMTS1FameHelper.RaiseWatermark((short)stored, (short)4), NoStars, MaxStarPower);
        }

        [Fact]
        public void ANegativeFameTotalIsNobodyRatherThanAnException()
        {
            Assert.Equal(NoStars, VMTS1FameHelper.StarPowerForFamePoints(-1));
            Assert.Equal(NoStars, VMTS1FameHelper.StarPowerForFamePoints(short.MinValue));
        }

        [Fact]
        public void FamePointsForReportsTheCostOfEachRungAndZeroForNobody()
        {
            Assert.Equal(NoStars, VMTS1FameHelper.FamePointsFor(NoStars));
            Assert.Equal(FirstFamePoints, VMTS1FameHelper.FamePointsFor((short)1));
            Assert.Equal((short)900, VMTS1FameHelper.FamePointsFor(MaxStarPower));
            // Out of range reads as the nearest real rung rather than throwing.
            Assert.Equal((short)900, VMTS1FameHelper.FamePointsFor((short)999));
            Assert.Equal(NoStars, VMTS1FameHelper.FamePointsFor((short)-999));
        }

        /// <summary>
        /// Promotion and demotion have to track the point total rather than accumulate: the same
        /// score has to produce the same rating however many times a sim wins a set and is then
        /// beaten for it. Drift here would show up as a sim whose stars no longer match the
        /// "friends needed" bar the career panel shows them.
        /// </summary>
        [Fact]
        public void AwardAndPenaltyCyclesConvergeOnTheTierTheScorePaysFor()
        {
            // 60 points is two and a half stars' worth; a -30 penalty leaves 30, which is one
            // and a half stars. Neither total sits on a rung boundary, so the rating has to come
            // out of the mapping rather than out of a counter.
            const short rich = 60;
            const short penalised = 30;
            Assert.Equal((short)5, VMTS1FameHelper.StarPowerForFamePoints(rich));
            Assert.Equal((short)3, VMTS1FameHelper.StarPowerForFamePoints(penalised));

            short stars = NoStars;
            for (int i = 0; i < 10; i++)
            {
                stars = VMTS1FameHelper.Promote(rich, stars);
                Assert.Equal((short)5, stars);
                stars = VMTS1FameHelper.Demote(penalised, stars);
                Assert.Equal((short)3, stars);
            }
        }

        // ---- the two halves meet: does the fame tab ever fill up? ---------------------------

        /// <summary>
        /// A neighbourhood person data block shaped like <c>Neighbour.PersonData</c> (88
        /// entries) with one sim's fame slots set, as <c>AdjustFame</c> writes them.
        /// </summary>
        private static short[] PersonDataWithFame(short famePoints, short halfStars)
        {
            var personData = new short[88];
            personData[(int)VMPersonDataVariable.TS1FameScore] = famePoints;
            personData[(int)VMPersonDataVariable.TS1FameStarPower] = halfStars;
            personData[(int)VMPersonDataVariable.TS1FameStarHighWatermark] = halfStars;
            return personData;
        }

        /// <summary>
        /// The end-to-end check the roadmap asks for: once a sim has been promoted, the
        /// relationship panel's fame tab finds them, because the engine writes the fame point
        /// total the tab filters on. Before <c>VMGenericTS1Call</c> grew calls 34/36 nothing
        /// wrote slot 80 at all and this tab could only ever render an empty list.
        /// </summary>
        [Fact]
        public void TheFameTabFindsASimOnceTheEngineHasGivenThemFame()
        {
            const int famousNid = 12;
            var famous = PersonDataWithFame(FirstFamePoints, (short)1);
            var nobody = PersonDataWithFame(NoStars, NoStars);

            var relationships = new Dictionary<int, List<short>>
            {
                { 11, new List<short>() },
                { 12, new List<short>() },
            };

            var shown = RelationshipFameFilter.Apply(relationships, RelationshipFameFilter.SortFame,
                id => RelationshipFameFilter.FameScoreOf(id == famousNid ? famous : nobody)).ToList();

            Assert.Equal(new[] { famousNid }, shown.Select(x => x.Key).ToArray());
        }

        /// <summary>
        /// ...and a sim who has not earned the first rung is still correctly left out. The
        /// filter's cutoff is exclusive at 1 point, which is one point below the first rung, so
        /// the tab switches on exactly at "on the fame track" rather than at "touched a fame
        /// object once".
        /// </summary>
        [Fact]
        public void TheFameTabStaysEmptyUntilTheFirstRungIsActuallyPaidFor()
        {
            Assert.True(FirstFamePoints > RelationshipFameFilter.MinCelebrityFame,
                "the engine's first rung has to be above the UI cutoff or the tab never fills");

            var relationships = new Dictionary<int, List<short>> { { 11, new List<short>() } };

            var shown = RelationshipFameFilter.Apply(relationships, RelationshipFameFilter.SortFame,
                id => RelationshipFameFilter.FameScoreOf(PersonDataWithFame(1, NoStars))).ToList();

            Assert.Empty(shown);
        }
    }
}
