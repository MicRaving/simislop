using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers <see cref="CasFamilyFilter"/> - the rules that decide which neighbourhood families the
    /// Create-a-Sim screen offers.
    ///
    /// <para>
    /// These exist because of a bug that made the game unplayable on a stock install.
    /// <c>TS1CASScreen.SetFamilies</c> used to filter the move-in list down to families with
    /// <c>(FAMI.Unknown &amp; 16) &gt; 0 &c>&amp;&amp; FAMI.HouseNumber == 0</c>, i.e. "created in the CAS
    /// and not yet in a house". The three families that ship unplaced inside a stock
    /// <c>Neighborhood.iff</c> - Bachelor, Roomies and Pleasant - all carry <c>Unknown == 0</c>, and
    /// the only two shipped families that do have bit 16 (Newbie, 17) are already living in a
    /// house. So the predicate matched <em>nothing</em>: "select or create family" came up blank and
    /// no move-in could be started until the player had built a family of their own.
    /// </para>
    ///
    /// <para>
    /// The matrix below is the whole point. The fix has to widen the move-in list without
    /// accidentally handing the delete button the shipped families, and the two questions are
    /// genuinely different, so both are pinned over the full cross product of
    /// <c>ChunkID</c>/<c>HouseNumber</c>/<c>Unknown</c> rather than through a handful of happy paths.
    /// </para>
    /// </summary>
    public sealed class CasFamilyFilterTests
    {
        /// <summary>
        /// The FAMI chunks of the shipped <c>Neighborhood.iff</c> (<c>&lt;install&gt;\UserData</c>),
        /// field for field, as read out of a stock install. The names come from the FAMs chunk that
        /// shares each family's chunk id.
        ///
        /// <list type="bullet">
        /// <item>512 Bachelor - unplaced, Unknown 0, 1 member</item>
        /// <item>768 Roomies - unplaced, Unknown 0, 2 members</item>
        /// <item>1024 Pleasant - unplaced, Unknown 0, 4 members</item>
        /// <item>1280 Goth - already in house 5, Unknown 1, 3 members</item>
        /// <item>256 Newbie - already in house 7, Unknown 17 (16|1), 2 members</item>
        /// <item>0 Default (townies) - unplaced, Unknown 0, FamilyNumber -1, no members</item>
        /// </list>
        /// </summary>
        private static List<FAMI> ShippedNeighbourhood()
        {
            return new List<FAMI>
            {
                Family(chunkId: 1024, houseNumber: 0, unknown: 0, familyNumber: 9, members: 4), // Pleasant
                Family(chunkId: 768, houseNumber: 0, unknown: 0, familyNumber: 8, members: 2),  // Roomies
                Family(chunkId: 512, houseNumber: 0, unknown: 0, familyNumber: 7, members: 1),  // Bachelor
                Family(chunkId: 1280, houseNumber: 5, unknown: 1, familyNumber: 6, members: 3), // Goth
                Family(chunkId: 256, houseNumber: 7, unknown: 17, familyNumber: 5, members: 2), // Newbie
                Family(chunkId: 0, houseNumber: 0, unknown: 0, familyNumber: -1, members: 0),   // townies
            };
        }

        private static FAMI Family(int chunkId, int houseNumber, int unknown, int familyNumber = 0, int members = 1)
        {
            var guids = new uint[members];
            for (int i = 0; i < members; i++) guids[i] = (uint)(i + 1);
            return new FAMI
            {
                ChunkID = (ushort)chunkId,
                ChunkLabel = "",
                ChunkType = "FAMI",
                HouseNumber = houseNumber,
                Unknown = unknown,
                FamilyNumber = familyNumber,
                FamilyGUIDs = guids,
            };
        }

        // ------------------------------------------------------------- move-in matrix

        /// <summary>
        /// The regression itself: every unplaced, non-townies family is offered for move-in, whatever
        /// <c>Unknown</c> says. The rows cover the three shipped move-in candidates, the two shipped
        /// families that already live in a house, the townies family, a CAS-created family, and the
        /// awkward <c>HouseNumber == -1</c> the engine uses for the townies bucket.
        /// </summary>
        [Theory]
        //shipped, unplaced, no CAS flag: the three the player is meant to be able to move in
        [InlineData(512, 0, 0, true)]    // Bachelor
        [InlineData(768, 0, 0, true)]    // Roomies
        [InlineData(1024, 0, 0, true)]   // Pleasant
        //shipped but already in a house
        [InlineData(256, 7, 17, false)]  // Newbie
        [InlineData(1280, 5, 1, false)]  // Goth
        //townies
        [InlineData(0, 0, 0, false)]
        [InlineData(0, 0, 24, false)]    //even with the CAS flag, chunk 0 is the townies family
        //CAS-created, unplaced
        [InlineData(1792, 0, 24, true)]
        //CAS-created but already in a house
        [InlineData(1536, 68, 24, false)]
        //the townies "house number", and other nonsense
        [InlineData(512, -1, 0, false)]
        [InlineData(512, -1, 24, false)]
        [InlineData(512, 1, 0, false)]
        [InlineData(512, 50, 0, false)]
        public void CanMoveIn_UnplacedAndNotTownies_RegardlessOfTheCasFlag(
            int chunkId, int houseNumber, int unknown, bool expected)
        {
            Assert.Equal(expected, CasFamilyFilter.CanMoveIn(chunkId, houseNumber, unknown));
        }

        /// <summary>
        /// The CAS flag is not merely unused for some rows - it is provably irrelevant. Holding a
        /// chunk id and house fixed and sweeping the whole interesting range of <c>Unknown</c> (and
        /// some nonsense values) must not change the answer.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(24)]
        [InlineData(31)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void CanMoveIn_UnplacedFamily_IgnoresUnknownEntirely(int unknown)
        {
            Assert.True(CasFamilyFilter.CanMoveIn(512, 0, unknown));
            //and the flag cannot talk a placed family into the list either
            Assert.False(CasFamilyFilter.CanMoveIn(512, 7, unknown));
        }

        // ------------------------------------------------------------- delete matrix

        /// <summary>
        /// Widening the move-in list must not widen what the delete button removes. A family is
        /// deletable only when it is both in the list (unplaced, not townies) and CAS-created, so the
        /// shipped families are selectable and visible but not one click from being destroyed.
        /// </summary>
        [Theory]
        //shipped, unplaced, no flag: offered for move-in, NOT deletable
        [InlineData(512, 0, 0, false)]    // Bachelor
        [InlineData(768, 0, 0, false)]    // Roomies
        [InlineData(1024, 0, 0, false)]   // Pleasant
        //shipped and already in a house: not in the list at all, and not deletable
        [InlineData(256, 7, 17, false)]   // Newbie - has the flag, but lives in a house
        [InlineData(1280, 5, 1, false)]   // Goth
        //townies: never, whatever the flag says
        [InlineData(0, 0, 0, false)]
        [InlineData(0, 0, 24, false)]
        //the family the CAS created and the player has not moved in yet: deletable
        [InlineData(1792, 0, 24, true)]
        [InlineData(1792, 0, 16, true)]
        //CAS-created but already in a house: not in the list, not deletable
        [InlineData(1536, 68, 24, false)]
        [InlineData(512, -1, 24, false)]
        public void CanDelete_OnlyUnplacedCasCreatedFamilies(int chunkId, int houseNumber, int unknown, bool expected)
        {
            Assert.Equal(expected, CasFamilyFilter.CanDelete(chunkId, houseNumber, unknown));
        }

        /// <summary>
        /// The invariant the two questions share: anything the delete button will act on is also in
        /// the list, so there is no family that can be removed without having been shown to the
        /// player first.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(0, 0, 24)]
        [InlineData(256, 7, 17)]
        [InlineData(512, 0, 0)]
        [InlineData(768, 0, 0)]
        [InlineData(1024, 0, 0)]
        [InlineData(1280, 5, 1)]
        [InlineData(1536, 68, 24)]
        [InlineData(1792, 0, 24)]
        [InlineData(1792, -1, 24)]
        [InlineData(5, 0, -1)]
        [InlineData(5, 0, int.MaxValue)]
        public void CanDelete_ImpliesCanMoveIn(int chunkId, int houseNumber, int unknown)
        {
            //deletable is a strictly stronger claim than "is in the list", so nothing can be deleted
            //that the player was never offered.
            if (CasFamilyFilter.CanDelete(chunkId, houseNumber, unknown))
            {
                Assert.True(CasFamilyFilter.CanMoveIn(chunkId, houseNumber, unknown));
            }
        }

        [Fact]
        public void CanDelete_NeverTrueForAShippedFamily()
        {
            //the one-line statement of the whole fix: the shipped neighbourhood is moveable but
            //nothing in it is deletable.
            foreach (var family in ShippedNeighbourhood())
            {
                Assert.False(CasFamilyFilter.CanDelete(family));
            }
        }

        // ------------------------------------------------------------- bit helpers

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, false)]
        [InlineData(8, false)]
        [InlineData(16, true)]
        [InlineData(17, true)]
        [InlineData(24, true)]
        [InlineData(31, true)]
        [InlineData(-1, true)]
        [InlineData(int.MinValue, false)]  //only the sign bit is set
        public void IsCasCreated_ReadsBit16(int unknown, bool expected)
        {
            Assert.Equal(expected, CasFamilyFilter.IsCasCreated(unknown));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(5, false)]
        [InlineData(68, false)]
        [InlineData(-1, false)]
        [InlineData(int.MaxValue, false)]
        public void IsUnplaced_OnlyHouseNumberZero(int houseNumber, bool expected)
        {
            Assert.Equal(expected, CasFamilyFilter.IsUnplaced(houseNumber));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(512, false)]
        [InlineData(1024, false)]
        [InlineData(-1, false)]
        public void IsTownies_OnlyChunkIdZero(int chunkId, bool expected)
        {
            Assert.Equal(expected, CasFamilyFilter.IsTownies(chunkId));
        }

        // ------------------------------------------------------------- the shipped install

        [Fact]
        public void MoveInCandidates_OnAStockInstall_OffersTheThreeShippedFamilies()
        {
            var candidates = CasFamilyFilter.MoveInCandidates(ShippedNeighbourhood());

            //the bug: this list was empty, so no move-in could be started.
            Assert.Equal(new ushort[] { 512, 768, 1024 }, candidates.Select(x => x.ChunkID).ToArray());
        }

        [Fact]
        public void MoveInCandidates_OnAStockInstall_ExcludesTowniesAndPlacedFamilies()
        {
            var candidates = CasFamilyFilter.MoveInCandidates(ShippedNeighbourhood());

            Assert.DoesNotContain(candidates, x => x.ChunkID == 0);          //townies
            Assert.DoesNotContain(candidates, x => x.HouseNumber != 0);    //Goth and Newbie live in houses
            Assert.All(candidates, x => Assert.True(x.FamilyGUIDs.Length > 0));
        }

        [Fact]
        public void MoveInCandidates_IsOrderedByFamilyNumber()
        {
            //FamilyNumber is the game's own auto-incrementing creation order, so a stock install
            //reads Bachelor, Roomies, Pleasant rather than in file order, and the list is stable
            //across refreshes - which is what makes a stored selection index mean anything.
            var shuffled = ShippedNeighbourhood().OrderByDescending(x => x.ChunkID).ToList();
            var candidates = CasFamilyFilter.MoveInCandidates(shuffled);

            Assert.Equal(new ushort[] { 512, 768, 1024 }, candidates.Select(x => x.ChunkID).ToArray());
            Assert.Equal(new int[] { 7, 8, 9 }, candidates.Select(x => x.FamilyNumber).ToArray());
        }

        [Fact]
        public void MoveInCandidates_TiesOnFamilyNumber_FallBackToChunkId()
        {
            var families = new List<FAMI>
            {
                Family(chunkId: 900, houseNumber: 0, unknown: 0, familyNumber: 3),
                Family(chunkId: 100, houseNumber: 0, unknown: 0, familyNumber: 3),
            };
            Assert.Equal(new ushort[] { 100, 900 }, CasFamilyFilter.MoveInCandidates(families).Select(x => x.ChunkID).ToArray());
        }

        [Fact]
        public void MoveInCandidates_NeverMutatesTheInputList()
        {
            var all = ShippedNeighbourhood();
            var before = all.Select(x => x.ChunkID).ToArray();

            CasFamilyFilter.MoveInCandidates(all);

            Assert.Equal(before, all.Select(x => x.ChunkID).ToArray());
        }

        [Fact]
        public void MoveInCandidates_TolerantOfEmptyAndNullInput()
        {
            Assert.Empty(CasFamilyFilter.MoveInCandidates(null));
            Assert.Empty(CasFamilyFilter.MoveInCandidates(new List<FAMI>()));

            //a null entry in the chunk list is not a family and must not become a list entry
            var withHole = ShippedNeighbourhood();
            withHole.Insert(0, null);
            var candidates = CasFamilyFilter.MoveInCandidates(withHole);
            Assert.DoesNotContain(candidates, x => x == null);
            Assert.Equal(3, candidates.Count);
        }

        [Fact]
        public void CanMoveIn_NullFamily_IsNotACandidate()
        {
            Assert.False(CasFamilyFilter.CanMoveIn((FAMI)null));
            Assert.False(CasFamilyFilter.CanDelete((FAMI)null));
        }

        // ------------------------------------------------------------- selection across a refresh

        [Fact]
        public void SelectedChunkId_ReadsTheFamilyBehindTheStoredIndex()
        {
            var families = CasFamilyFilter.MoveInCandidates(ShippedNeighbourhood());

            Assert.Equal(512, CasFamilyFilter.SelectedChunkId(families, 0));
            Assert.Equal(768, CasFamilyFilter.SelectedChunkId(families, 1));
            Assert.Equal(1024, CasFamilyFilter.SelectedChunkId(families, 2));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)]
        [InlineData(int.MaxValue)]
        public void SelectedChunkId_OutOfRangeOrNegative_IsNothingSelected(int selection)
        {
            var families = CasFamilyFilter.MoveInCandidates(ShippedNeighbourhood());
            Assert.Equal(-1, CasFamilyFilter.SelectedChunkId(families, selection));
            Assert.Equal(-1, CasFamilyFilter.SelectedChunkId(null, selection));
        }

        [Fact]
        public void IndexOfChunkId_FindsTheFamilyWhereverItMovedTo()
        {
            //a family that sorts in ahead of the selected one pushes it down a slot, so a stale
            //index would move in the wrong family.
            var before = CasFamilyFilter.MoveInCandidates(ShippedNeighbourhood());
            var selectedId = CasFamilyFilter.SelectedChunkId(before, 2);
            Assert.Equal(1024, selectedId);

            var after = CasFamilyFilter.MoveInCandidates(new List<FAMI>
            {
                Family(chunkId: 512, houseNumber: 0, unknown: 0, familyNumber: 7, members: 1),
                Family(chunkId: 768, houseNumber: 0, unknown: 0, familyNumber: 8, members: 2),
                Family(chunkId: 1024, houseNumber: 0, unknown: 0, familyNumber: 9, members: 4),
                Family(chunkId: 2048, houseNumber: 0, unknown: 24, familyNumber: 6, members: 1),
            });

            Assert.Equal(3, CasFamilyFilter.IndexOfChunkId(after, selectedId));
            //without the id match the stale index 2 would have moved in "Roomies"
            Assert.Equal(768, after[2].ChunkID);
        }

        [Fact]
        public void IndexOfChunkId_DeletedFamily_ResolvesToNothing()
        {
            //the deleted family is gone, and -1 leaves the accept button disabled rather than
            //pointing at whatever slid into its slot.
            var afterDelete = CasFamilyFilter.MoveInCandidates(new List<FAMI>
            {
                Family(chunkId: 512, houseNumber: 0, unknown: 0, familyNumber: 7, members: 1),
                Family(chunkId: 768, houseNumber: 0, unknown: 0, familyNumber: 8, members: 2),
            });

            Assert.Equal(-1, CasFamilyFilter.IndexOfChunkId(afterDelete, 1024));
            Assert.Equal(-1, CasFamilyFilter.IndexOfChunkId(new List<FAMI>(), 1024));
            Assert.Equal(-1, CasFamilyFilter.IndexOfChunkId(null, 1024));
        }

        [Fact]
        public void IndexOfChunkId_IgnoresNullEntries()
        {
            var families = new List<FAMI> { null, Family(512, 0, 0, 7) };
            Assert.Equal(1, CasFamilyFilter.IndexOfChunkId(families, 512));
            Assert.Equal(-1, CasFamilyFilter.IndexOfChunkId(families, 0));
            Assert.Equal(-1, CasFamilyFilter.IndexOfChunkId(families, -5));
        }

        // ------------------------------------------------------------- in-place edit eligibility

        [Theory]
        [InlineData(512, 1, CasEditEligibility.Eligible)]
        [InlineData(512, 4, CasEditEligibility.Eligible)]
        [InlineData(512, 8, CasEditEligibility.Eligible)]
        [InlineData(0, 4, CasEditEligibility.Townies)]
        [InlineData(0, 0, CasEditEligibility.Townies)]
        [InlineData(512, 0, CasEditEligibility.NoMembers)]
        [InlineData(512, -1, CasEditEligibility.NoMembers)]
        [InlineData(512, 9, CasEditEligibility.TooManyMembers)]
        [InlineData(512, 40, CasEditEligibility.TooManyMembers)]
        public void EditEligibility_FollowsTheTowniesAndMemberCountRules(
            int chunkId, int memberCount, CasEditEligibility expected)
        {
            Assert.Equal(expected, CasFamilyFilter.EditEligibility(chunkId, memberCount));
        }

        [Fact]
        public void EditEligibility_AllowsShippedPlacedFamilies()
        {
            //editing a family in place is entered from the lot it lives in, and the shipped Goth
            //and Newbie families live in houses. The CAS-created flag plays no part here either, and
            //the member count cap is the only size rule.
            var goth = ShippedNeighbourhood().First(x => x.ChunkID == 1280);
            Assert.Equal(CasEditEligibility.Eligible, CasFamilyFilter.EditEligibility(goth));
        }

        [Fact]
        public void EditEligibility_NullFamily_IsNoFamily()
        {
            Assert.Equal(CasEditEligibility.NoFamily, CasFamilyFilter.EditEligibility((FAMI)null));
        }

        [Fact]
        public void EditEligibility_NullGuidList_CountsAsNoMembers()
        {
            var broken = Family(512, 0, 0, 7);
            broken.FamilyGUIDs = null;
            Assert.Equal(CasEditEligibility.NoMembers, CasFamilyFilter.EditEligibility(broken));
        }

        [Fact]
        public void EditEligibility_CapsAtTheCasFamilyLimit()
        {
            //the size rule is the shared CAS cap, not a number of its own, so the two cannot drift
            Assert.Equal(CasEditEligibility.Eligible,
                CasFamilyFilter.EditEligibility(512, CasValidation.MaxSimsPerFamily));
            Assert.Equal(CasEditEligibility.TooManyMembers,
                CasFamilyFilter.EditEligibility(512, CasValidation.MaxSimsPerFamily + 1));
        }
    }
}
