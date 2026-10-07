/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Which neighbourhood families the Create-a-Sim screen is allowed to offer, in which mode.
    ///
    /// <para>
    /// The screen has three separate family-shaped questions, and they have three different
    /// answers. Conflating them is what used to break the game:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>Who can be moved in?</b> Every family that does not already live somewhere
    /// (<c>HouseNumber == 0</c>), apart from the townies family. The CAS-created flag plays no
    /// part.</item>
    /// <item><b>Who can be deleted from the family list?</b> Only families the CAS itself
    /// created. The shipped families are content the player did not write, and there is no way to
    /// get them back once the FAMI is gone, so widening the move-in list must not widen what the
    /// delete button will remove.</item>
    /// <item><b>Who can be edited in place?</b> Not the townies family (it has no members to
    /// edit), and only a family the CAS can actually represent: at least one member, and no more
    /// than <see cref="CasValidation.MaxSimsPerFamily"/>.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Why the move-in list cannot be gated on the CAS flag.</b> <c>FAMI.Unknown</c> bit 16 is
    /// the "this family was made in the CAS" flag that <c>SimitoneNeighbourGenerator.CreateFamily</c>
    /// sets (it writes 24 = 16|8). The families that ship inside a stock
    /// <c>Neighborhood.iff</c> do <em>not</em> have it - the shipped
    /// <c>D:\Program Files\Sims\UserData\Neighborhood.iff</c> holds Bachelor (chunk 512),
    /// Roomies (768) and Pleasant (1024) with <c>Unknown == 0</c>, plus two families that are
    /// already placed in a house (Newbie 256 with <c>Unknown == 17</c>, Goth 1280 with
    /// <c>Unknown == 1</c>) and the townies (chunk 0, <c>Unknown == 0</c>). Filtering on
    /// <c>(Unknown &amp; 16) &gt; 0 &amp;&amp; HouseNumber == 0</c> therefore matched
    /// <em>nothing</em> on a stock install: the three unplaced families were all excluded, and
    /// the only two families that had the flag set were excluded because they already had a
    /// house. "Select or create family" came up blank and a move-in could not be started at all
    /// until the player had made a family of their own.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure: a family (or the three fields of one) in, a decision out, with no
    /// Content, no VM, no IO and nothing thrown on null or malformed input. A <c>FAMI</c> is plain
    /// data, so the FAMI overloads are as testable as the primitive ones.
    /// </para>
    /// </summary>
    public static class CasFamilyFilter
    {
        /// <summary>
        /// The chunk id of the townies family. It is always present, always empty
        /// (<c>FamilyGUIDs.Length == 0</c>, <c>FamilyNumber == -1</c>), and is not a family
        /// anybody can move in or delete.
        /// </summary>
        public const int TowniesChunkId = 0;

        /// <summary>
        /// Bit 16 of <c>FAMI.Unknown</c>: "this family was created in the CAS".
        /// <c>SimitoneNeighbourGenerator.CreateFamily</c> writes 24 (16|8) for a new family.
        /// </summary>
        public const int CasCreatedFlag = 16;

        /// <summary>
        /// The townies family - chunk id 0. Never a move-in candidate, never deletable, never
        /// editable.
        /// </summary>
        public static bool IsTownies(int chunkId)
        {
            return chunkId == TowniesChunkId;
        }

        /// <summary>
        /// Whether the CAS created this family, as recorded in <c>FAMI.Unknown</c> bit
        /// <see cref="CasCreatedFlag"/>. Note that a family the player placed into a house also
        /// tends to have bit 1 or 17 set, so the raw value is not a reliable "shipped" test -
        /// which is why <see cref="CanDelete"/> is not simply the negation of this.
        /// </summary>
        public static bool IsCasCreated(int unknown)
        {
            return (unknown & CasCreatedFlag) != 0;
        }

        /// <summary>
        /// Whether a family has no lot yet. 0 is "unplaced"; -1 is what the engine uses for the
        /// townies bucket, and anything above 0 is a real house number, so only 0 is a move-in
        /// candidate.
        /// </summary>
        public static bool IsUnplaced(int houseNumber)
        {
            return houseNumber == 0;
        }

        /// <summary>
        /// Whether the CAS "select or create family" screen should offer this family to move in.
        ///
        /// <para>
        /// Deliberately ignores <paramref name="unknown"/>: the CAS-created flag is not what makes
        /// a family moveable, and trusting it emptied the list on a stock install. The parameter
        /// is kept so callers have to hand over the whole family, and so the regression is pinned
        /// by a test rather than by a code change that could quietly come back.
        /// </para>
        /// </summary>
        /// <param name="chunkId"><c>FAMI.ChunkID</c>.</param>
        /// <param name="houseNumber"><c>FAMI.HouseNumber</c>.</param>
        /// <param name="unknown"><c>FAMI.Unknown</c>. Accepted and ignored.</param>
        public static bool CanMoveIn(int chunkId, int houseNumber, int unknown)
        {
            return !IsTownies(chunkId) && IsUnplaced(houseNumber);
        }

        /// <summary>
        /// Whether the family select screen's delete button may remove this family.
        ///
        /// <para>
        /// Two conditions, both required: the family has to be one the CAS created, and it has to
        /// be <em>in the list</em> to begin with (unplaced, and not the townies family).
        /// </para>
        ///
        /// <para>
        /// The CAS-created half used to be implicit. The list only ever held CAS-created families
        /// - the shipped ones were filtered out by the move-in bug - so nothing else was ever
        /// reachable from the delete button. Now that the shipped Bachelor/Roomies/Pleasant
        /// families are in the list it has to be explicit: they are the only move-in fodder a
        /// stock install has, and there is no way to get a shipped FAMI back once it is gone, so
        /// it must not be one stray click away.
        /// </para>
        ///
        /// <para>
        /// The unplaced half is not redundant. A family that already lives in a house can still
        /// carry the flag - the shipped Newbie family is <c>Unknown == 17</c> (16|1) with
        /// <c>HouseNumber == 7</c> - and the flag on its own would happily call that deletable. It
        /// never reaches the button, but the rule should not depend on that.
        /// </para>
        ///
        /// <para>
        /// <c>TS1CASScreen.DeleteFamily</c> independently refuses chunk id 0 and anything that is
        /// not a member of the current neighbourhood's main resource; this is the check that
        /// keeps the shipped families out of reach in the first place, and the one that decides
        /// whether the button is even clickable.
        /// </para>
        /// </summary>
        /// <param name="chunkId"><c>FAMI.ChunkID</c>.</param>
        /// <param name="houseNumber"><c>FAMI.HouseNumber</c>.</param>
        /// <param name="unknown"><c>FAMI.Unknown</c>.</param>
        public static bool CanDelete(int chunkId, int houseNumber, int unknown)
        {
            return CanMoveIn(chunkId, houseNumber, unknown) && IsCasCreated(unknown);
        }

        /// <summary>
        /// Why a family may or may not be opened in the family editor and written back in place.
        /// </summary>
        public static CasEditEligibility EditEligibility(int chunkId, int memberCount)
        {
            if (IsTownies(chunkId)) return CasEditEligibility.Townies;
            if (memberCount <= 0) return CasEditEligibility.NoMembers;
            if (memberCount > CasValidation.MaxSimsPerFamily) return CasEditEligibility.TooManyMembers;
            return CasEditEligibility.Eligible;
        }

        /// <summary>FAMI overload of <see cref="CanMoveIn(int, int, int)"/>. A null family is not a candidate.</summary>
        public static bool CanMoveIn(FAMI family)
        {
            //a null entry in the resource's chunk list is not a family, so it is not a candidate.
            if (family == null) return false;
            return CanMoveIn(family.ChunkID, family.HouseNumber, family.Unknown);
        }

        /// <summary>FAMI overload of <see cref="CanDelete(int, int, int)"/>. A null family is not deletable.</summary>
        public static bool CanDelete(FAMI family)
        {
            if (family == null) return false;
            return CanDelete(family.ChunkID, family.HouseNumber, family.Unknown);
        }

        /// <summary>Chunk-id form of <see cref="EditEligibility(int, int)"/>.</summary>
        public static CasEditEligibility EditEligibility(FAMI family)
        {
            if (family == null) return CasEditEligibility.NoFamily;
            return EditEligibility(family.ChunkID, family.FamilyGUIDs?.Length ?? 0);
        }

        /// <summary>
        /// The families the family select screen should list: every unplaced, non-townies family,
        /// in the order the game itself would show them.
        ///
        /// <para>
        /// Ordered by <c>FAMI.FamilyNumber</c> (the auto-incrementing id the game uses to
        /// remember creation order) with <c>ChunkID</c> as a tiebreaker, so a stock install comes
        /// up as Bachelor, Roomies, Pleasant rather than in whatever order the chunks happen to
        /// sit in the file - and so the same neighbourhood always produces the same list, which is
        /// what makes a stored selection index meaningful across a refresh.
        /// </para>
        ///
        /// <para>Null input, null entries and a null <c>Unknown</c>/house are all tolerated.</para>
        /// </summary>
        public static List<FAMI> MoveInCandidates(IEnumerable<FAMI> families)
        {
            var result = new List<FAMI>();
            if (families == null) return result;
            foreach (var family in families)
            {
                if (CanMoveIn(family)) result.Add(family);
            }
            result.Sort(CompareByFamilyNumber);
            return result;
        }

        private static int CompareByFamilyNumber(FAMI a, FAMI b)
        {
            if (ReferenceEquals(a, b)) return 0;
            //neither list is ever built with a null entry (CanMoveIn rejects null), so this is
            //purely belt and braces for a caller that sorts its own list with this comparer.
            if (a == null) return 1;
            if (b == null) return -1;
            var byNumber = a.FamilyNumber.CompareTo(b.FamilyNumber);
            if (byNumber != 0) return byNumber;
            return a.ChunkID.CompareTo(b.ChunkID);
        }

        /// <summary>
        /// Re-resolves a selection across a list refresh by family id rather than by index.
        ///
        /// <para>
        /// The panel's <c>Selection</c> is an index into a list that gets rebuilt whenever a
        /// family is created, edited or deleted. Keeping the raw index across a rebuild means the
        /// highlight silently slides onto whichever family happens to land there, and pressing
        /// "Move In" then moves in a family nobody picked. Matching on the id gives back the
        /// right index, and - when the family is genuinely gone - -1, so the accept button stays
        /// disabled instead of acting on a neighbour.
        /// </para>
        /// </summary>
        /// <returns>The index of the family with that chunk id, or -1.</returns>
        public static int IndexOfChunkId(IList<FAMI> families, int chunkId)
        {
            if (families == null) return -1;
            if (chunkId < 0) return -1;
            for (int i = 0; i < families.Count; i++)
            {
                if (families[i] != null && families[i].ChunkID == chunkId) return i;
            }
            return -1;
        }

        /// <summary>
        /// The chunk id the panel currently has selected, or -1 when nothing is selected or the
        /// stored index no longer points at a family. The value a refresh has to re-resolve.
        /// </summary>
        public static int SelectedChunkId(IList<FAMI> families, int selection)
        {
            if (families == null) return -1;
            if (selection < 0 || selection >= families.Count) return -1;
            var family = families[selection];
            return (family == null) ? -1 : family.ChunkID;
        }
    }

    /// <summary>
    /// Outcome of <see cref="CasFamilyFilter.EditEligibility(int, int)"/>. The editor needs to
    /// tell the player <em>why</em> it refused, so this is a reason rather than a bool.
    /// </summary>
    public enum CasEditEligibility
    {
        /// <summary>The family can be opened in the editor.</summary>
        Eligible,

        /// <summary>There was no family to open (a null reference).</summary>
        NoFamily,

        /// <summary>Chunk id 0: the townies family, which has no members.</summary>
        Townies,

        /// <summary>No members, so there is nothing to edit.</summary>
        NoMembers,

        /// <summary>
        /// More members than <see cref="CasValidation.MaxSimsPerFamily"/>. Editing it would have to
        /// drop sims on save, so it is refused rather than silently shrunk.
        /// </summary>
        TooManyMembers,
    }
}
