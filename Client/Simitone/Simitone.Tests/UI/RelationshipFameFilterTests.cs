using FSO.SimAntics.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The celebrity half of the relationship subpanel's category filter, exercised through
    /// <see cref="RelationshipFameFilter"/>.
    ///
    /// <para>
    /// <c>UIRelationshipSubpanel</c> has four two-state buttons and they all share one int,
    /// <c>RelSort</c>: -1 = all, 0 = friends, 1 = family, 2 = fame. <c>UpdateRelView</c> runs
    /// that int through <see cref="RelationshipCategoryFilter.Apply"/> every frame to decide
    /// which of the selected sim's relationships get a tile in the scroll view.
    /// <c>RelationshipCategoryFilter</c> is the whole filter and its own tests live in
    /// <see cref="RelationshipCategoryFilterTests"/>; what is pinned here is the fame half it
    /// delegates to, which is where the celebrity threshold and the person-data read live.
    /// </para>
    ///
    /// <para>
    /// The fame rule is the interesting one because it is a strict comparison against a
    /// neighbour's persisted TS1 person data slot, <see cref="VMPersonDataVariable.TS1FameScore"/>
    /// (80). Two things have to hold for the tab to behave:
    ///
    ///   * the cutoff is EXCLUSIVE - a sim needs a score above
    ///     <see cref="RelationshipFameFilter.MinCelebrityFame"/>, so a score of exactly 1 is
    ///     not a celebrity. Off-by-one here silently drops or admits a whole tier of sims.
    ///   * a neighbour that cannot be scored is not a celebrity. The neighbourhood lookup can
    ///     miss (a relationship to a sim no longer in the neighbourhood) and
    ///     <c>Neighbour.PersonData</c> is documented as nullable, so "unknown" has to read as
    ///     "not famous" instead of throwing.
    ///
    /// <see cref="RelationshipFameFilter.FameScoreOf"/> is pinned separately because the
    /// neighbourhood's person data array is only ever 88 entries long while the whole
    /// <c>VMPersonDataVariable</c> enum runs to 100 - a score read that trusts the array length
    /// would be one truncated save away from an IndexOutOfRangeException on every frame.
    ///
    /// Both members are pure static code: no graphics device, no content, no statics.
    /// </para>
    /// </summary>
    public sealed class RelationshipFameFilterTests
    {
        /// <summary>A relationship table shaped like <c>Neighbour.Relationships</c>.</summary>
        private static List<KeyValuePair<int, List<short>>> Relationships(
            params (int Nid, short[] Motives)[] entries)
        {
            var table = new List<KeyValuePair<int, List<short>>>();
            foreach (var (nid, motives) in entries)
            {
                table.Add(new KeyValuePair<int, List<short>>(nid, motives.ToList()));
            }
            return table;
        }

        /// <summary>Scores keyed by neighbour id, for the lookup delegate.</summary>
        private static Func<int, short?> Scores(params (int Nid, short Fame)[] scores)
        {
            var map = scores.ToDictionary(x => x.Nid, x => x.Fame);
            return nid => map.TryGetValue(nid, out short fame) ? fame : (short?)null;
        }

        // ---- the three non-fame tabs are this filter's pass-through -------------------------

        [Theory]
        [InlineData(-1)]  // All button
        [InlineData(0)]   // Friend button
        [InlineData(1)]   // Fam button
        public void Apply_ReturnsEveryRelationshipForTheNonFameTabs(int relSort)
        {
            // This filter is the fame half only: the friend and family rules live in
            // RelationshipCategoryFilter, which composes this one. So a friend or family tab
            // arriving here is a tab this filter does not narrow, and it has to hand the whole
            // table back rather than guess. If this ever starts dropping entries, the fame
            // filter has grown a rule that belongs to the composed filter above it.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 20 }), (12, new short[] { -50 }));

            var filtered = RelationshipFameFilter.Apply(rel, relSort, Scores((10, (short)500), (11, (short)0)));

            Assert.Equal(new[] { 10, 11, 12 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void Apply_DoesNotConsultTheFameLookupForTheNonFameTabs()
        {
            // A pass-through tab must not cost a person-data read per relationship per frame.
            // This asserts the laziness as well: the lookup would throw if it were called.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 20 }));
            Func<int, short?> boom = _ => throw new InvalidOperationException("lookup must not run");

            var filtered = RelationshipFameFilter.Apply(rel, -1, boom).ToList();

            Assert.Equal(2, filtered.Count);
        }

        // ---- the fame tab ------------------------------------------------------------------

        [Fact]
        public void Apply_KeepsOnlyNeighboursAboveTheCelebrityCutoff()
        {
            var rel = Relationships(
                (10, new short[] { 90 }),    // rising star
                (11, new short[] { 50 }),    // washed up, still famous
                (12, new short[] { 80 }),    // nobody
                (13, new short[] { 0 }));    // deep in the doldrums
            var fame = Scores((10, (short)3), (11, (short)2), (12, (short)0), (13, (short)1));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, fame);

            Assert.Equal(new[] { 10, 11 }, filtered.Select(x => x.Key).ToArray());
        }

        [Theory]
        [InlineData(0, false)]   // no fame at all
        [InlineData(1, false)]   // exactly the cutoff: exclusive, so NOT famous
        [InlineData(2, true)]    // the first score that counts
        [InlineData(100, true)]  // fully famous
        public void Apply_UsesAStrictlyGreaterThanCutoffOnFameScore(short fame, bool expected)
        {
            // MinCelebrityFame is 1 and the comparison is "> MinCelebrityFame", so 1 is out.
            var rel = Relationships((10, new short[] { 80 }));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame,
                Scores((10, fame)));

            Assert.Equal(expected ? new[] { 10 } : Array.Empty<int>(), filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void Apply_ExcludesNeighboursThatCannotBeScored()
        {
            // Two ways a neighbour has no score: it is not in the neighbourhood at all (the
            // delegate returns null), or it is there but its person data was never built.
            // Both must be treated as "not a celebrity" rather than throwing out of UpdateRelView.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }), (12, new short[] { 80 }));
            var fame = Scores((10, (short)9));   // 11 and 12 are absent from the lookup

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, fame);

            Assert.Equal(new[] { 10 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void Apply_FiltersOnTheOtherNeighbourNotTheSelectedSim()
        {
            // The table is keyed by the other sim's neighbour id, and the whole point of the
            // fame tab is to judge the sims being listed, not the sim you are looking through.
            // Recorded lookups pin which id the filter asks about.
            var seen = new List<int>();
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }), (12, new short[] { 80 }));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, nid =>
            {
                seen.Add(nid);
                return (short)(nid == 11 ? 4 : 0);
            }).ToList();

            Assert.Equal(new[] { 11 }, filtered.Select(x => x.Key).ToArray());
            // and it looked each one up exactly once - no repeated per-frame person data reads
            Assert.Equal(new[] { 10, 11, 12 }, seen);
        }

        [Fact]
        public void Apply_PreservesTableOrderAndTheMotiveValuesOnEachEntry()
        {
            // The scroll view is index-addressed, so the fame tab has to keep the relationship
            // table's ordering and hand each entry's motive vector through untouched - it
            // selects rows, it does not rebuild them.
            var rel = Relationships(
                (30, new short[] { 10, 11, 12 }),
                (20, new short[] { -5 }),
                (10, new short[] { 0, 0, 0, 99 }));
            var fame = Scores((30, (short)7), (20, (short)7), (10, (short)0));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, fame).ToList();

            Assert.Equal(new[] { 30, 20 }, filtered.Select(x => x.Key).ToArray());
            Assert.Equal(new short[] { 10, 11, 12 }, filtered[0].Value);
            Assert.Equal(new short[] { -5 }, filtered[1].Value);
        }

        [Fact]
        public void Apply_ProducesAnEmptyListWhenNobodyIsFamous()
        {
            // The current state of affairs, not an edge case: nothing in the engine writes
            // TS1FameScore, so the fame tab always lands here. It must be empty, not broken.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, Scores()).ToList();

            Assert.Empty(filtered);
        }

        // ---- degenerate inputs ------------------------------------------------------------

        [Fact]
        public void Apply_ReturnsAnEmptySequenceForANullRelationshipTable()
        {
            // A neighbour with no relationship block has a null table. UpdateRelView calls this
            // every frame, so a null has to render as an empty tab rather than an exception.
            var filtered = RelationshipFameFilter.Apply(null, RelationshipFameFilter.SortFame, Scores());

            Assert.Empty(filtered);
        }

        [Fact]
        public void Apply_PassesThroughWhenNoFameLookupIsSupplied()
        {
            // Defensive: without a lookup there is nothing to filter on, and showing the whole
            // list is a better failure than dropping every row.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }));

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, null);

            Assert.Equal(new[] { 10, 11 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void Apply_ReturnsAReEnumerableSequence()
        {
            // UpdateRelView enumerates the result every frame via .Select(...).ToList(). If Apply
            // handed back a single-pass iterator the second frame would enumerate nothing.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }));
            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame,
                Scores((10, (short)5)));

            var first = filtered.Select(x => x.Key).ToList();
            var second = filtered.Select(x => x.Key).ToList();

            Assert.Equal(new[] { 10 }, first);
            Assert.Equal(first, second);
        }

        [Fact]
        public void Apply_WithNoRelationshipsProducesAnEmptyResultOnAnyTab()
        {
            foreach (int relSort in new[] { -1, 0, 1, RelationshipFameFilter.SortFame })
            {
                Assert.Empty(RelationshipFameFilter.Apply(
                    Relationships(), relSort, Scores()));
            }
        }

        // ---- FameScoreOf: reading person data slot 80 -------------------------------------

        [Fact]
        public void FameScoreOf_ReturnsNullWhenPersonDataIsNull()
        {
            // Neighbour.PersonData is nullable (a neighbour with no person mode never gets an
            // array), and a null dereference here would be thrown from UpdateRelView every frame.
            Assert.Null(RelationshipFameFilter.FameScoreOf(null));
        }

        [Theory]
        [InlineData(0)]     // empty
        [InlineData(1)]
        [InlineData(80)]    // exactly TS1FameScore's index, which is still out of range
        public void FameScoreOf_ReturnsNullRatherThanThrowingOnATruncatedArray(int length)
        {
            // The neighbourhood allocates 88 entries, but nothing about Neighbour.PersonData
            // guarantees that, and reading slot 80 of a shorter array is an IndexOutOfRange
            // thrown from the render loop. 80 entries is the last length that cannot hold it -
            // 81 is enough, because the slot is the 81st.
            Assert.Null(RelationshipFameFilter.FameScoreOf(new short[length]));
        }

        [Fact]
        public void FameScoreOf_ReadsTheSlotFromTheShortestArrayThatCanHoldIt()
        {
            // 81 entries is the first length where index 80 exists. Below this it is null, at
            // this length it reads - the boundary the previous test stops one short of.
            var personData = new short[81];
            personData[80] = 6;

            Assert.Equal((short)6, RelationshipFameFilter.FameScoreOf(personData));
            Assert.Null(RelationshipFameFilter.FameScoreOf(new short[80]));
        }

        [Fact]
        public void FameScoreOf_ReadsTheFameSlotOfAFullPersonDataArray()
        {
            // 88 entries is what NBRS allocates and what the sim generator produces.
            var personData = new short[88];
            personData[(int)VMPersonDataVariable.TS1FameScore] = 42;

            Assert.Equal((short)42, RelationshipFameFilter.FameScoreOf(personData));
        }

        [Fact]
        public void FameScoreOf_ReadsSlotEightyAndNotTheNeighbouringFameSlots()
        {
            // 80/81/82 are a block - score, star power, high watermark. Only 80 is the score the
            // fame tab is meant to rank on; reading 81 or 82 would rank on a different stat.
            var personData = new short[88];
            personData[80] = 7;
            personData[(int)VMPersonDataVariable.TS1FameStarPower] = 0;
            personData[(int)VMPersonDataVariable.TS1FameStarHighWatermark] = 0;

            Assert.Equal((short)7, RelationshipFameFilter.FameScoreOf(personData));
        }

        [Fact]
        public void FameScoreOf_ReadsTheSlotNamedByVMPersonDataVariableTS1FameScore()
        {
            // Pins the index to the engine's enum rather than a literal 80, so a FreeSO change to
            // the enum is a test failure here instead of a silently mis-read sim.
            Assert.Equal(80, (int)VMPersonDataVariable.TS1FameScore);

            var personData = new short[88];
            personData[(int)VMPersonDataVariable.TS1FameScore] = 5;

            Assert.Equal((short)5, RelationshipFameFilter.FameScoreOf(personData));
        }

        [Fact]
        public void FameScoreOf_ReportsAFameScoreOfZeroForAFreshlyGeneratedSim()
        {
            // What Simitone's person generator produces: an 88-slot array with everything left
            // at zero. This is the reason the fame tab is always empty today, so pin it - when
            // someone implements the fame career track this assertion is the one that should
            // start failing loudly, because the generator has grown real fame values.
            var personData = new short[88];

            Assert.Equal((short)0, RelationshipFameFilter.FameScoreOf(personData));
        }

        // ---- the two halves have to agree --------------------------------------------------

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(2, true)]
        [InlineData(300, true)]
        public void Apply_AndFameScoreOf_AgreeOnWhoIsFamous(short fame, bool expected)
        {
            // Wiring the real reader into the real filter: the score the array holds and the
            // celebrity verdict the tab renders have to come from the same threshold.
            var rel = Relationships((10, new short[] { 80 }));
            var personData = new short[88];
            personData[(int)VMPersonDataVariable.TS1FameScore] = fame;

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame,
                _ => RelationshipFameFilter.FameScoreOf(personData)).ToList();

            Assert.Equal(expected, filtered.Count == 1);
        }

        [Fact]
        public void Apply_TreatsASimWithNoPersonDataAsNotFamous()
        {
            // The end-to-end shape UpdateRelView uses: a relationship whose target neighbour
            // exists but has no person data must not throw and must not be listed.
            var rel = Relationships((10, new short[] { 80 }), (11, new short[] { 80 }));
            short[] noPersonData = null;
            short[] famous = new short[88];
            famous[(int)VMPersonDataVariable.TS1FameScore] = 100;

            var filtered = RelationshipFameFilter.Apply(rel, RelationshipFameFilter.SortFame, nid =>
                RelationshipFameFilter.FameScoreOf(nid == 11 ? famous : noPersonData)).ToList();

            Assert.Equal(new[] { 11 }, filtered.Select(x => x.Key).ToArray());
        }

        // ---- the tab constant the UI buttons are wired to ----------------------------------

        [Fact]
        public void SortFame_IsTwo_WhichIsTheValueTheFameButtonSelects()
        {
            // UIRelationshipSubpanel.RelSort is a bare int with no enum behind it, so the one
            // number the fame button and the filter agree on is only held by this constant.
            // If it moves, the button and the filter move together - which is the point - but
            // a saved RelSort of 2 (it defaults to -1, and nothing persists it) would silently
            // become "no filter" if the two ever diverged.
            Assert.Equal(2, RelationshipFameFilter.SortFame);
        }
    }
}
