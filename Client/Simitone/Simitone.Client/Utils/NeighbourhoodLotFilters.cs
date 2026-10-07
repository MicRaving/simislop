/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The neighbourhood map's lot filters, as a set of independent toggles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A flags value rather than a list, because the set is what a lot is tested against and the
    /// test has to be one AND over the whole set: a lot is on show when it satisfies every filter
    /// that is switched on, and no filter switched on means every lot is on show. That is the
    /// same shape as the retail toolbar this is modelled on - <c>NghUI\Filter*Btn.bmp</c> in the
    /// shipped <c>UIGraphics.far</c>, thirteen 200x52 four-state buttons along the top of the map -
    /// and it is why the filters are stored as a mask and not as a "current filter" field: retail
    /// let a player narrow the map down by several things at once.
    /// </para>
    /// <para>
    /// <b>Which filters exist is a question about the data, not about the art.</b> Retail's
    /// thirteen are lot <em>category</em> filters - dog/cat, food, gardening, lodging, magic,
    /// model, movie, music, recreation, rides, shopping, small animal, spa - and a category is not
    /// something this tree can read: the audit behind these gaps records that the install ships no
    /// <c>OBJT</c> chunk anywhere, so "which objects are on this lot" is not answerable, and
    /// <c>LotLocations.iff</c> carries nothing but a lot number and a map position. What the map
    /// <em>does</em> know about a lot is who lives there and how it is zoned, and those are the
    /// four filters here. Inventing thirteen category filters whose answer is always "no lots"
    /// would be thirteen dead buttons.
    /// </para>
    /// <para>
    /// <b>Two of the four are complements of each other</b> - a lot is occupied or it is not, it is
    /// residential or it is not - and the UI does not stop a player switching both on. That is
    /// deliberate: it shows nothing, which is the honest answer to "show me the lots that are both
    /// occupied and vacant", and it is visible in the count readout rather than silently doing
    /// nothing. A filter that nothing on the current map can match is a fourth case, and is
    /// reported separately by <see cref="IsAvailable"/>: its button is drawn dead, which is the
    /// fourth state of every retail filter button and the reason those strips are four wide.
    /// </para>
    /// <para>
    /// Pure: no graphics device, no content, no audio, no game state. The panel reads the two
    /// facts off the provider and puts them in a <see cref="NeighLotFacts"/>; everything after that
    /// is arithmetic.
    /// </para>
    /// </remarks>
    [Flags]
    public enum NeighLotFilter
    {
        /// <summary>No filter. Every lot on the map is on show.</summary>
        None = 0,

        /// <summary>Only lots a family is living in.</summary>
        Occupied = 1 << 0,

        /// <summary>Only lots nobody is living in - the ones a family could be moved into.</summary>
        Vacant = 1 << 1,

        /// <summary>Only lots zoned residential, i.e. a family may live there.</summary>
        Residential = 1 << 2,

        /// <summary>Only lots that are not zoned residential, i.e. a family may not live there.</summary>
        Community = 1 << 3,
    }

    /// <summary>
    /// What the neighbourhood map knows about one lot, reduced to the two facts a filter can ask
    /// about. Immutable and free of references so a whole map's worth fits in a list without
    /// having to be invalidated when the provider is reloaded.
    /// </summary>
    public readonly struct NeighLotFacts
    {
        public NeighLotFacts(int lot, bool occupied, short zone)
        {
            Lot = lot;
            Occupied = occupied;
            Zone = zone;
        }

        /// <summary>The lot/house number, as the location table and the house files spell it.</summary>
        public int Lot { get; }

        /// <summary>Whether a family is living on the lot right now.</summary>
        public bool Occupied { get; }

        /// <summary>
        /// The lot's zone as the neighbourhood loaded it, or as
        /// <see cref="LotZoning.DefaultZoneForLot"/> fell back for a lot the zoning file never
        /// names. The panel always supplies an effective zone for this reason: a lot with no
        /// zoning line is still a lot a filter has to have an opinion about.
        /// </summary>
        public short Zone { get; }

        /// <summary>
        /// Whether a family may live on this lot - the same question
        /// <c>UIHouseSelectPanel</c> asks to decide between a price and "this is a community lot",
        /// and deliberately the same one. It is <c>zone &gt; 0</c> that means no, not
        /// <c>zone == 1</c>: <see cref="LotZoning.ZoneStudio"/> exists as a display fallback for
        /// the StudioTown lots and is "not residential without being community", so a filter that
        /// tested for exactly 1 would leave StudioTown's nine lots belonging to neither filter.
        /// </summary>
        public bool CanMoveIn
        {
            get { return Zone <= LotZoning.ZoneResidential; }
        }
    }

    /// <summary>
    /// Everything about the neighbourhood map's lot filters that is not a widget: the set of
    /// filters, what each one is called, whether a lot passes, and what is left on show.
    ///
    /// <para>
    /// The wording comes from Simitone's own string table, with the English hardcoded here as the
    /// fallback, so a build with no table at all still labels the bar. Nothing here needs Content
    /// initialised, which is what lets it be tested headlessly - see
    /// <c>NeighbourhoodFilterTests</c>.
    /// </para>
    /// </summary>
    public static class NeighLotFilters
    {
        /// <summary>Every filter, in the order the bar lays them out.</summary>
        public const NeighLotFilter All =
            NeighLotFilter.Occupied | NeighLotFilter.Vacant |
            NeighLotFilter.Residential | NeighLotFilter.Community;

        /// <summary>
        /// The filters one at a time, in toolbar order. A fresh array per call so a caller cannot
        /// reorder the shared table.
        /// </summary>
        public static NeighLotFilter[] Each()
        {
            return new[]
            {
                NeighLotFilter.Occupied,
                NeighLotFilter.Vacant,
                NeighLotFilter.Residential,
                NeighLotFilter.Community
            };
        }

        /// <summary>
        /// Whether <paramref name="filter"/> is exactly one of the four. A mask with two bits in
        /// it is not a filter - it is two - and the toggle and the matching rule both need to be
        /// able to say so rather than quietly answering for half of it.
        /// </summary>
        public static bool IsSingle(NeighLotFilter filter)
        {
            return filter != NeighLotFilter.None && (filter & (filter - 1)) == 0;
        }

        /// <summary>Whether <paramref name="filter"/> is one of the four, or the empty set.</summary>
        public static bool IsKnown(NeighLotFilter filter)
        {
            return filter == NeighLotFilter.None || (filter & ~All) == 0;
        }

        /// <summary>
        /// Whether <paramref name="set"/> holds any bit outside the four filters, which would be a
        /// mask from somewhere else rather than a filter set.
        /// </summary>
        public static bool IsValidSet(NeighLotFilter set)
        {
            return (set & ~All) == 0;
        }

        /// <summary>The CustomUI file name of the filter's four-state button art.</summary>
        public static string IconName(NeighLotFilter filter)
        {
            switch (filter)
            {
                case NeighLotFilter.Occupied: return "ngbh_filt_occupied.png";
                case NeighLotFilter.Vacant: return "ngbh_filt_vacant.png";
                case NeighLotFilter.Residential: return "ngbh_filt_residential.png";
                case NeighLotFilter.Community: return "ngbh_filt_community.png";
                default: return null;
            }
        }

        /// <summary>The short caption, for the button's tooltip.</summary>
        public static string Name(NeighLotFilter filter)
        {
            switch (filter)
            {
                case NeighLotFilter.Occupied: return SimitoneStrings.Get("240", "Occupied lots");
                case NeighLotFilter.Vacant: return SimitoneStrings.Get("241", "Vacant lots");
                case NeighLotFilter.Residential: return SimitoneStrings.Get("242", "Residential lots");
                case NeighLotFilter.Community: return SimitoneStrings.Get("243", "Community lots");
                default: return null;
            }
        }

        /// <summary>
        /// The long caption, which says what the filter does rather than naming it - the tooltip
        /// a player reads before pressing a button they cannot see the effect of.
        /// </summary>
        public static string Tooltip(NeighLotFilter filter)
        {
            switch (filter)
            {
                case NeighLotFilter.Occupied:
                    return SimitoneStrings.Get("244", "Show only the lots with a family living in them");
                case NeighLotFilter.Vacant:
                    return SimitoneStrings.Get("245", "Show only the lots with nobody living in them");
                case NeighLotFilter.Residential:
                    return SimitoneStrings.Get("246", "Show only the lots a family can move into");
                case NeighLotFilter.Community:
                    return SimitoneStrings.Get("247", "Show only the lots a family cannot move into");
                default: return null;
            }
        }

        /// <summary>Tooltip for a filter nothing on the current map can match.</summary>
        public static string UnavailableTooltip(NeighLotFilter filter)
        {
            return SimitoneStrings.Get("269", "No lot on this map matches that filter.");
        }

        /// <summary>
        /// The bar's own caption, which names what the bar is for and its one non-button gesture.
        /// </summary>
        /// <remarks>
        /// One sentence rather than a caption plus a hint, so a translator is not asked to fit two
        /// ideas into a strip 44 pixels tall - and because the two halves cannot then be reordered
        /// into something that reads as a different control.
        /// </remarks>
        public static string BarTooltip()
        {
            return SimitoneStrings.Get("248", "Filter lots - click the bar to show them all");
        }

        /// <summary>
        /// Whether one lot satisfies one filter. <see cref="NeighLotFilter.None"/> matches
        /// everything, which is what makes it the identity of the AND below rather than a filter
        /// that hides the whole map.
        /// </summary>
        public static bool Matches(NeighLotFilter filter, NeighLotFacts facts)
        {
            switch (filter)
            {
                case NeighLotFilter.None: return true;
                case NeighLotFilter.Occupied: return facts.Occupied;
                case NeighLotFilter.Vacant: return !facts.Occupied;
                case NeighLotFilter.Residential: return facts.CanMoveIn;
                case NeighLotFilter.Community: return !facts.CanMoveIn;
                default: return false;
            }
        }

        /// <summary>
        /// Whether a lot survives the whole set: every filter that is switched on has to match.
        /// The empty set keeps every lot, so a map with no filters is the unfiltered map.
        /// </summary>
        public static bool Passes(NeighLotFilter set, NeighLotFacts facts)
        {
            if (!IsValidSet(set)) return true; //a mask we do not understand hides nothing
            foreach (var filter in Each())
            {
                if ((set & filter) == 0) continue;
                if (!Matches(filter, facts)) return false;
            }
            return true;
        }

        /// <summary>
        /// The set with <paramref name="filter"/> flipped. An unknown filter is refused by
        /// returning the set unchanged, because the alternative - adding a bit nothing can match -
        /// would empty the map for a caller that passed a typo.
        /// </summary>
        public static NeighLotFilter Toggle(NeighLotFilter set, NeighLotFilter filter)
        {
            if (!IsSingle(filter)) return set;
            if (!IsValidSet(set)) return NeighLotFilter.None;
            return set ^ filter;
        }

        /// <summary>
        /// Whether any lot on this map can match <paramref name="filter"/> at all. Drives the
        /// button's fourth (dead) state: on a Downtown map, which has no residential lots, the
        /// residential filter is not a filter the player can use.
        /// </summary>
        public static bool IsAvailable(NeighLotFilter filter, IReadOnlyList<NeighLotFacts> lots)
        {
            if (filter == NeighLotFilter.None) return lots != null && lots.Count > 0;
            if (lots == null) return false;
            for (int i = 0; i < lots.Count; i++)
            {
                if (Matches(filter, lots[i])) return true;
            }
            return false;
        }

        /// <summary>How many of <paramref name="lots"/> survive <paramref name="set"/>.</summary>
        public static int VisibleCount(NeighLotFilter set, IReadOnlyList<NeighLotFacts> lots)
        {
            if (lots == null) return 0;
            int visible = 0;
            for (int i = 0; i < lots.Count; i++)
            {
                if (Passes(set, lots[i])) visible++;
            }
            return visible;
        }

        /// <summary>
        /// The lot numbers that survive <paramref name="set"/>, in the order they were given -
        /// which is the panel's map order, so "next" walks the map rather than the numbering.
        /// </summary>
        public static List<int> VisibleLots(NeighLotFilter set, IReadOnlyList<NeighLotFacts> lots)
        {
            var result = new List<int>();
            if (lots == null) return result;
            for (int i = 0; i < lots.Count; i++)
            {
                if (Passes(set, lots[i])) result.Add(lots[i].Lot);
            }
            return result;
        }

        /// <summary>
        /// The count readout the bar shows. Both numbers are always given, including when nothing
        /// is filtered, because "12 of 12" is the answer to "did that button do anything" and it
        /// is the only feedback a player gets from a filter that happens to keep every lot.
        /// </summary>
        public static string CountText(int visible, int total)
        {
            return SimitoneStrings.Format("249", "Lots shown: %d of %d",
                visible.ToString(CultureInfo.InvariantCulture),
                total.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Convenience overload: the readout for a set over a list of lots.</summary>
        public static string CountText(NeighLotFilter set, IReadOnlyList<NeighLotFacts> lots)
        {
            var total = lots?.Count ?? 0;
            return CountText(VisibleCount(set, lots), total);
        }
    }
}
