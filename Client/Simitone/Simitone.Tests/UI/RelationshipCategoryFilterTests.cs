using FSO.SimAntics.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The four tab filters behind the relationship subpanel's four tab buttons, exercised
    /// through <see cref="RelationshipCategoryFilter"/>.
    ///
    /// <para>
    /// <c>UIRelationshipSubpanel.RelSort</c> is a bare int shared by all four two-state buttons,
    /// and <c>UpdateRelView</c> runs it through this filter every frame to decide which of the
    /// selected sim's relationships get a tile. The friend and family tabs used to be
    /// pass-through - the panel's own comment said so - so the whole list appeared under all
    /// four buttons and the buttons did nothing but change which one looked pressed.
    /// </para>
    ///
    /// <para>
    /// Two rules are worth pinning closely, because both have a wrong answer that still looks
    /// like a working panel:
    ///
    ///   * <b>Family is not "not a friend".</b> A sim's own relatives do not have to be its
    ///     friends, and the family tab is about the household. Getting the two confused gives a
    ///     panel whose family tab quietly shows the social circle instead, which is the one
    ///     thing a player would notice.
    ///   * <b>An unset family slot claims nobody.</b> Person data 61 is zero on a sim that was
    ///     never put in a family. Without the zero check on <em>both</em> sides, every
    ///     family-less sim in the neighbourhood would appear in every family-less viewer's
    ///     family tab.
    /// </para>
    ///
    /// <para>
    /// Every member is pure static code: no graphics device, no content, no statics, and no VM.
    /// </para>
    /// </summary>
    public sealed class RelationshipCategoryFilterTests
    {
        /// <summary>A relationship table shaped like <c>Neighbour.Relationships</c>.</summary>
        private static List<KeyValuePair<int, List<short>>> Relationships(
            params (int Nid, short[] Motives)[] entries)
        {
            var table = new List<KeyValuePair<int, List<short>>>();
            foreach (var (nid, motives) in entries)
            {
                // A null vector stands for a relationship the engine has never written to,
                // which is what Neighbour.Relationships holds for a sim with no relationship
                // data - so the helper builds the real thing rather than a stand-in.
                table.Add(new KeyValuePair<int, List<short>>(nid, motives?.ToList()));
            }
            return table;
        }

        /// <summary>
        /// A vector laid out the way the engine's <c>relationship</c> primitive writes one:
        /// [0] relationship, [1] friend flag, [2] friendship.
        /// </summary>
        private static short[] Vector(short relationship, bool friend, short friendship = 0)
        {
            return new short[] { relationship, friend ? (short)1 : (short)0, friendship };
        }

        /// <summary>Family numbers keyed by neighbour id; an absent id reads as "no family".</summary>
        private static Func<int, short> Families(params (int Nid, short Family)[] families)
        {
            var map = families.ToDictionary(x => x.Nid, x => x.Family);
            return nid => map.TryGetValue(nid, out short family) ? family : RelationshipCategoryFilter.NoFamily;
        }

        private static readonly Func<int, short?> NoFame = _ => null;

        // ---- the tab constants the four buttons are wired to --------------------------------

        [Fact]
        public void TheFourTabsAreTheValuesTheButtonsSelect()
        {
            // UIMainPanel and the subpanel both index the switcher on these, and the filter
            // switches on them. Written out rather than derived, because a renumbering that
            // moved all three together would still be a renumbering the player sees.
            Assert.Equal(-1, RelationshipCategoryFilter.SortAll);
            Assert.Equal(0, RelationshipCategoryFilter.SortFriends);
            Assert.Equal(1, RelationshipCategoryFilter.SortFamily);
            Assert.Equal(2, RelationshipFameFilter.SortFame);

            // The four values are distinct, which is the whole reason a single int can carry
            // "which tab is pressed" without a collision.
            Assert.Equal(4, new[] { RelationshipCategoryFilter.SortAll,
                RelationshipCategoryFilter.SortFriends,
                RelationshipCategoryFilter.SortFamily,
                RelationshipFameFilter.SortFame }.Distinct().Count());
        }

        // ---- all ---------------------------------------------------------------------------

        [Fact]
        public void TheAllTabShowsEverythingIncludingTheHiddenSections()
        {
            var rel = Relationships((10, Vector(0, false)), (11, Vector(0, true)), (12, Vector(0, false)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortAll,
                1, Families((11, 1)), NoFame).ToList();

            Assert.Equal(new[] { 10, 11, 12 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void TheAllTabConsultsNeitherLookup()
        {
            // UpdateRelView runs this every frame for a neighbourhood that can hold a hundred
            // relationships. The All tab is the one a player leaves selected while they play,
            // so a person-data read per relationship per frame is a cost nobody would notice
            // and everybody would pay. The delegates throw if called.
            var rel = Relationships((10, Vector(0, false)), (11, Vector(0, false)));
            Func<int, short> boomFamily = _ => throw new InvalidOperationException("family lookup must not run");
            Func<int, short?> boomFame = _ => throw new InvalidOperationException("fame lookup must not run");

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortAll,
                1, boomFamily, boomFame).ToList();

            Assert.Equal(2, filtered.Count);
        }

        // ---- friends -----------------------------------------------------------------------

        [Fact]
        public void TheFriendTabKeepsOnlyTheRelationshipsTheEngineCallsFriendship()
        {
            var rel = Relationships(
                (10, Vector(80, true, 60)),
                (11, Vector(-40, false, 0)),
                (12, Vector(20, true, 5)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, Families(), NoFame).ToList();

            Assert.Equal(new[] { 10, 12 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void TheFriendTabIgnoresTheFriendshipScoreAlone()
        {
            // The friendship score is a bar the player sees; it is not the friend flag. A sim
            // can have been socialised with without ever becoming a friend, and retail's friend
            // list is the flag, not the score.
            var rel = Relationships((10, Vector(0, false, 90)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, Families(), NoFame).ToList();

            Assert.Empty(filtered);
        }

        [Fact]
        public void TheFriendTabDoesNotConsultTheFamilyOrFameLookups()
        {
            var rel = Relationships((10, Vector(0, true)));
            Func<int, short> boomFamily = _ => throw new InvalidOperationException("family lookup must not run");
            Func<int, short?> boomFame = _ => throw new InvalidOperationException("fame lookup must not run");

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, boomFamily, boomFame).ToList();

            Assert.Single(filtered);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(new short[0])]
        [InlineData(new short[] { 50 })]
        public void TheFriendTabSurvivesAVectorItCannotReadAFlagOutOf(short[] vector)
        {
            // A relationship written by a build that stopped at the first entry has no flag. A
            // null vector is what a neighbour entry with no relationship data looks like, and
            // UpdateRelView calls this every frame, so a throw here is a crash in the render
            // loop rather than a wrong list.
            var rel = Relationships((10, vector));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, Families(), NoFame).ToList();

            Assert.Empty(filtered);
        }

        [Fact]
        public void TheFriendTabReadsTheFlagAndNotTheFriendshipScore()
        {
            // The friendship score at index 2 is the number on the second bar, and the scripts
            // move it for anybody a sim has been socialising with. Treating it as a flag would
            // list every sim anyone has ever talked to; a vector whose flag says no is not a
            // friendship however high its friendship score has climbed.
            Assert.False(RelationshipCategoryFilter.IsFriend(new short[] { 0, 0, 90 }));
            Assert.False(RelationshipCategoryFilter.IsFriend(new short[] { 50, 0, 40 }));
            Assert.True(RelationshipCategoryFilter.IsFriend(new short[] { 50, 1, 0 }));
        }

        [Fact]
        public void AVectorTooShortToHoldTheFlagIsNotAFriendship()
        {
            // The engine pads a relationship vector out to the slot it writes, so a short one
            // is a relationship that has never been written to rather than one written
            // differently - and a friendship tab that listed it would be inventing a friendship
            // out of an empty vector.
            Assert.False(RelationshipCategoryFilter.IsFriend(new short[] { 50 }));
            Assert.False(RelationshipCategoryFilter.IsFriend(new short[0]));
            Assert.False(RelationshipCategoryFilter.IsFriend(null));
        }

        // ---- family ------------------------------------------------------------------------

        [Fact]
        public void TheFamilyTabKeepsOnlyTheSelectedSimsOwnHousehold()
        {
            var rel = Relationships(
                (10, Vector(0, false)),   // same family as the viewer
                (11, Vector(0, false)),   // a different family
                (12, Vector(0, true)));   // a friend, but not family - and must not appear

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFamily,
                3, Families((10, 3), (11, 4), (12, 4)), NoFame).ToList();

            Assert.Equal(new[] { 10 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void TheFamilyTabIsEmptyForASimWithNoFamilyOfItsOwn()
        {
            // Person data 61 is zero on a sim that was never put in a family. Without the check
            // on the viewer's own slot, every family-less sim in the neighbourhood would be
            // listed as a relative of every other family-less sim.
            var rel = Relationships((10, Vector(0, false)), (11, Vector(0, false)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFamily,
                RelationshipCategoryFilter.NoFamily, Families((10, 0), (11, 0)), NoFame).ToList();

            Assert.Empty(filtered);
        }

        [Fact]
        public void TheFamilyTabDoesNotClaimSimsWhoHaveNoFamilyEither()
        {
            // The other side of the same check: a related-looking entry whose own slot is unset.
            var rel = Relationships(
                (10, Vector(0, false)),   // other sim has no family
                (11, Vector(0, false)));  // shares the viewer's

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFamily,
                3, Families((10, 0), (11, 3)), NoFame).ToList();

            Assert.Equal(new[] { 11 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void TheFamilyTabIgnoresANegativeOrOversizedFamilyNumber()
        {
            // Person data is a raw short the shipped scripts write freely. A negative slot is
            // not a family, and clamping it to zero is what keeps it from matching itself.
            Assert.Equal(RelationshipCategoryFilter.NoFamily, RelationshipCategoryFilter.FamilyNumberOf(-5));
            Assert.Equal(RelationshipCategoryFilter.NoFamily, RelationshipCategoryFilter.FamilyNumberOf(0));
            Assert.Equal((short)7, RelationshipCategoryFilter.FamilyNumberOf(7));
        }

        [Fact]
        public void TheFamilyTabDoesNotConsultTheFameLookup()
        {
            var rel = Relationships((10, Vector(0, false)));
            Func<int, short?> boom = _ => throw new InvalidOperationException("fame lookup must not run");

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFamily,
                3, Families((10, 3)), boom).ToList();

            Assert.Single(filtered);
        }

        [Fact]
        public void TheFamilyTabPassesThroughWhenThereIsNoFamilyLookupToUse()
        {
            // A caller that cannot resolve families gets the whole list rather than an empty
            // tab. An empty tab looks like "you have no relatives"; a whole list at least
            // shows the player something to judge.
            var rel = Relationships((10, Vector(0, false)), (11, Vector(0, false)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFamily,
                3, null, NoFame).ToList();

            Assert.Equal(2, filtered.Count);
        }

        // ---- fame, through the composed filter ---------------------------------------------

        [Fact]
        public void TheFameTabIsReachedThroughTheComposedFilterAndNarrowsTheSameWay()
        {
            // The composed filter delegates the celebrity rule rather than re-implementing it,
            // so this is the assertion that the two halves stay one filter.
            var rel = Relationships(
                (10, Vector(0, false)),
                (11, Vector(0, false)),
                (12, Vector(0, false)));
            var fame = new Dictionary<int, short?> { { 10, 4 }, { 11, 1 }, { 12, null } };

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipFameFilter.SortFame,
                1, Families(), nid => fame[nid]).ToList();

            Assert.Equal(new[] { 10 }, filtered.Select(x => x.Key).ToArray());
        }

        [Fact]
        public void TheFameTabDoesNotConsultTheFamilyLookup()
        {
            var rel = Relationships((10, Vector(0, false)));
            Func<int, short> boom = _ => throw new InvalidOperationException("family lookup must not run");

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipFameFilter.SortFame,
                1, boom, _ => (short)9).ToList();

            Assert.Single(filtered);
        }

        // ---- degenerate inputs --------------------------------------------------------------

        [Fact]
        public void ANullTableIsEmptyOnEveryTab()
        {
            foreach (int tab in new[] { RelationshipCategoryFilter.SortAll,
                RelationshipCategoryFilter.SortFriends,
                RelationshipCategoryFilter.SortFamily,
                RelationshipFameFilter.SortFame })
            {
                Assert.Empty(RelationshipCategoryFilter.Apply(null, tab, 1, Families(), NoFame));
            }
        }

        [Fact]
        public void AnUnrecognisedTabShowsEverythingRatherThanNothing()
        {
            // A RelSort the filter has no case for - which is what a stale value would be if the
            // tab set ever changed under a saved one. Dropping every row would be the worst
            // possible answer: the panel would look like it had lost the neighbourhood.
            var rel = Relationships((10, Vector(0, false)), (11, Vector(0, true)));

            var filtered = RelationshipCategoryFilter.Apply(rel, 99, 1, Families(), NoFame).ToList();

            Assert.Equal(2, filtered.Count);
        }

        [Fact]
        public void AnEmptyTableIsEmptyOnEveryTab()
        {
            foreach (int tab in new[] { RelationshipCategoryFilter.SortAll,
                RelationshipCategoryFilter.SortFriends,
                RelationshipCategoryFilter.SortFamily,
                RelationshipFameFilter.SortFame })
            {
                Assert.Empty(RelationshipCategoryFilter.Apply(Relationships(), tab, 1, Families(), NoFame));
            }
        }

        [Fact]
        public void TheFilterKeepsTheTableOrderAndTheMotiveValuesOnEachEntry()
        {
            // The scroll view is index-addressed, so a filter selects rows; it does not rebuild
            // them. The order and the values the bars read have to come through untouched.
            var rel = Relationships(
                (30, Vector(10, true, 11)),
                (20, Vector(-5, false)),
                (10, Vector(0, false)));

            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, Families(), NoFame).ToList();

            Assert.Equal(new[] { 30 }, filtered.Select(x => x.Key).ToArray());
            Assert.Equal(new short[] { 10, 1, 11 }, filtered[0].Value);
        }

        [Fact]
        public void TheFilterIsReEnumerableSoTheSecondFrameSeesTheSameList()
        {
            // UpdateRelView enumerates the result every frame via .Select(...).ToList(). A
            // single-pass iterator would make the list empty on every frame after the first.
            var rel = Relationships((10, Vector(0, true)), (11, Vector(0, false)));
            var filtered = RelationshipCategoryFilter.Apply(rel, RelationshipCategoryFilter.SortFriends,
                1, Families(), NoFame);

            Assert.Equal(new[] { 10 }, filtered.Select(x => x.Key).ToList());
            Assert.Equal(new[] { 10 }, filtered.Select(x => x.Key).ToList());
        }

        // ---- the vector layout the rules are written against --------------------------------

        [Fact]
        public void TheVectorIndicesAreTheOnesTheEngineUses()
        {
            // Pinned to the engine's own enum-free constants by way of the primitive that
            // writes them. A correction to the layout has to be a deliberate change to
            // RelationshipCategoryFilter, and these three are the numbers it would change.
            Assert.Equal(0, RelationshipCategoryFilter.RelationshipIndex);
            Assert.Equal(1, RelationshipCategoryFilter.FriendFlagIndex);
            Assert.Equal(2, RelationshipCategoryFilter.FriendshipIndex);
        }

        [Fact]
        public void TheFamilyNumberSlotIsTheOnesPhoneBookGroupsBy()
        {
            // UICallNeighborPanel builds its family column out of this same slot, so the
            // relationship panel's family tab and the phone book's family list cannot disagree
            // about who is in the household.
            Assert.Equal(61, (int)VMPersonDataVariable.TS1FamilyNumber);
            Assert.NotEqual(RelationshipCategoryFilter.NoFamily, (short)VMPersonDataVariable.TS1FamilyNumber);
        }
    }
}
