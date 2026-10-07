/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One inclusive run of lot numbers.
    /// </summary>
    /// <remarks>
    /// A range rather than a bare first/last pair because a neighbourhood's lots are not one
    /// contiguous run: the base and Unleashed maps hold 1-20 (the residential lots a family may
    /// move into) and 50-80 (the community lots beside them), with the Downtown and Vacation
    /// numbers sitting in the middle between them and belonging to two other views entirely. A
    /// single bracket for those two would hand the Downtown lots to the base map, which is the
    /// mistake this type exists to make impossible.
    /// </remarks>
    public readonly struct NeighLotRange
    {
        public NeighLotRange(int first, int last)
        {
            First = first;
            Last = last;
        }

        /// <summary>First lot number, inclusive.</summary>
        public int First { get; }

        /// <summary>Last lot number, inclusive.</summary>
        public int Last { get; }

        /// <summary>How many lot numbers this run holds.</summary>
        public int Count
        {
            get { return IsEmpty ? 0 : (Last - First) + 1; }
        }

        /// <summary>
        /// Whether the run holds nothing. A run starting below lot 1 is empty: the game has no
        /// lot 0, so a zero the caller meant as "there is no range" is read as "no range" rather
        /// than as a one-lot run at lot 0.
        /// </summary>
        public bool IsEmpty
        {
            get { return First < 1 || Last < First; }
        }

        /// <summary>Whether <paramref name="lot"/> falls in this run.</summary>
        public bool Contains(int lot)
        {
            return !IsEmpty && lot >= First && lot <= Last;
        }
    }

    /// <summary>
    /// Which neighbourhood view a lot belongs to, and which numbers that view's lots come from.
    ///
    /// <para>
    /// <b>The one thing this exists to say is that residential is view 4.</b> The eight views are
    /// the eight <c>LotLocations.iff</c> STR chunks and the eight backgrounds in
    /// <see cref="UINeighborhoodSelectionPanel.Neighborhoods"/>, and they are numbered the way The
    /// Sims 1 numbered them: <b>1</b> is the base residential map (<c>Nbhd\NScreen.BMP</c>), 2 is
    /// Downtown, 3 is Vacation Island, 4 is Unleashed, 5 is Studio Town, 6 ships empty, 7 is
    /// Magic Town. Simitone opens on <b>4</b> - deliberately, because the Unleashed map is the
    /// unzoned one and hybrid play needs that - so <see cref="ResidentialDefault"/> is
    /// <see cref="Unleashed"/>.
    /// </para>
    /// <para>
    /// <b>View 1 is still reachable, and that is the point.</b> An earlier wave had "residential"
    /// wired to <see cref="BaseResidential"/>, which made <see cref="Unleashed"/> the map nothing
    /// produced; a following wave gave the Unleashed map a button of its own, which made view 1
    /// unreachable instead, because the switcher only ever offered 2, 3, 4, 5 and 7 and nothing
    /// else produced a 1. Both maps now have a button and both are produced by the mapping, so
    /// the default is a player-facing preference rather than a way of losing a view: the base
    /// map's art ships, it is just not where you start.
    /// </para>
    /// <para>
    /// <b>Why view 1 is safe to switch to.</b> The map is not the reason a neighbourhood is
    /// unreachable; the <em>data</em> is. The shipped <c>LotLocations.iff</c> carries a populated
    /// location table for view 1 as well as for view 4, with the same lot numbers, so a player who
    /// switches to the base map gets a map with the same lots on it rather than an empty one, and
    /// nothing is copied or rewritten to get there. This is a change of background and of camera
    /// framing, not a change of neighbourhood.
    /// </para>
    /// <para>
    /// Pure: no graphics, no content, no game state. Every number here is also a number the panel
    /// and the switcher already had, so there is nothing to look up.
    /// </para>
    /// </summary>
    public static class NeighbourhoodModeMap
    {
        /// <summary>The base residential neighbourhood, The Sims 1's own map.</summary>
        public const ushort BaseResidential = 1;

        /// <summary>Downtown.</summary>
        public const ushort Downtown = 2;

        /// <summary>Vacation Island.</summary>
        public const ushort Vacation = 3;

        /// <summary>The Sims: Unleashed - the unzoned map, and Simitone's default.</summary>
        public const ushort Unleashed = 4;

        /// <summary>
        /// Which view a residential lot belongs to, and therefore which view a player opens on and
        /// which view "return to neighbourhood" goes back to. Unleashed, because it is the unzoned
        /// map and hybrid play needs that.
        /// </summary>
        /// <remarks>
        /// Named rather than written as a literal at each use site, because it is a <em>default</em>
        /// and not a rule: <see cref="BaseResidential"/> is the same lot set with a different
        /// background, it is a valid answer, and both remain reachable from the switcher. The
        /// lot-number ranges in <see cref="ForHouse"/> are what decide which view a lot is on;
        /// this only says which of the two residential views wins when a lot is residential.
        /// </remarks>
        public const ushort ResidentialDefault = Unleashed;

        /// <summary>Studio Town (Superstar).</summary>
        public const ushort Studiotown = 5;

        /// <summary>Magic Town (Makin' Magic).</summary>
        public const ushort Magictown = 7;

        /// <summary>Lowest view id the panel's table has.</summary>
        public const int FirstMode = 1;

        /// <summary>Highest view id the panel's table has. Slot 6 ships with no background.</summary>
        public const int LastMode = 8;

        /// <summary>
        /// The lot numbers the base and Unleashed maps hold, in the two runs they actually are.
        /// The Downtown (21-31) and Vacation (40-49) numbers are not in here even though they sit
        /// between 20 and 50: they belong to views 2 and 3.
        /// </summary>
        private static readonly NeighLotRange[] Residential =
        {
            new NeighLotRange(1, 20),
            new NeighLotRange(50, 80)
        };

        /// <summary>
        /// The neighbourhood a lot belongs to, as the two-digit view id the panel and the switcher
        /// use. Anything outside the four expansion ranges is residential, which is
        /// <see cref="ResidentialDefault"/>.
        /// </summary>
        /// <param name="house">The lot number the player is standing on.</param>
        public static ushort ForHouse(short house)
        {
            if (house >= 21 && house <= 31) return Downtown;
            if (house >= 40 && house <= 49) return Vacation;
            if (house >= 81 && house <= 89) return Studiotown;
            if (house >= 90 && house <= 99) return Magictown;
            return ResidentialDefault;
        }

        /// <summary>
        /// The lot numbers <paramref name="mode"/> draws its lots from, in the order a new lot
        /// would take them. A fresh array per call so a caller cannot edit the table.
        /// </summary>
        /// <remarks>
        /// The two residential-range views answer with the same two runs: they are two backgrounds
        /// over one set of lots, and a view that claimed a different set would be claiming lots it
        /// has no table entry for.
        /// </remarks>
        public static NeighLotRange[] LotRangesForMode(ushort mode)
        {
            switch (mode)
            {
                case Downtown: return new[] { new NeighLotRange(21, 31) };
                case Vacation: return new[] { new NeighLotRange(40, 49) };
                case Studiotown: return new[] { new NeighLotRange(81, 89) };
                case Magictown: return new[] { new NeighLotRange(90, 99) };
                default: return new[]
                {
                    new NeighLotRange(Residential[0].First, Residential[0].Last),
                    new NeighLotRange(Residential[1].First, Residential[1].Last)
                };
            }
        }

        /// <summary>Every lot number <paramref name="mode"/> can hold, in ascending order.</summary>
        public static List<int> LotNumbersForMode(ushort mode)
        {
            var lots = new List<int>();
            foreach (var range in LotRangesForMode(mode))
            {
                for (int lot = range.First; lot <= range.Last; lot++) lots.Add(lot);
            }
            return lots;
        }

        /// <summary>Whether <paramref name="lot"/> is one of the numbers <paramref name="mode"/> holds.</summary>
        public static bool OwnsLot(ushort mode, int lot)
        {
            foreach (var range in LotRangesForMode(mode))
            {
                if (range.Contains(lot)) return true;
            }
            return false;
        }

        /// <summary>
        /// The views a player may travel to from the map, in switcher order, excluding the one
        /// they are on. Both residential views are in the list whichever one the map opens on,
        /// which is what keeps the base map reachable now that the default is Unleashed: changing
        /// the default would otherwise have taken the base neighbourhood's button away.
        /// </summary>
        /// <param name="from">The view the player is on.</param>
        public static List<ushort> TravelTargets(ushort from)
        {
            var targets = new List<ushort>();
            foreach (var mode in new[] { BaseResidential, Downtown, Vacation, Unleashed, Studiotown, Magictown })
            {
                if (mode != from) targets.Add(mode);
            }
            return targets;
        }

        /// <summary>
        /// Whether <paramref name="mode"/> is a view the panel can actually draw, i.e. it is
        /// inside the table and it is not the deliberately empty slot 6. The switcher uses it to
        /// avoid offering a trip that the panel would answer by logging a warning and falling back
        /// to the base map.
        /// </summary>
        public static bool HasView(ushort mode)
        {
            if (mode < FirstMode || mode > LastMode) return false;
            return mode != 6;
        }

        /// <summary>
        /// The tooltip for the button that travels to <paramref name="mode"/>. Two of the six have
        /// captions in Simitone's string table because they are the two whose art or destination
        /// this change introduced: the base neighbourhood and Unleashed. The other four keep the
        /// tooltips they have always had, which are none - their plates have carried their own
        /// names since before either existed.
        /// </summary>
        public static string TravelTooltip(ushort mode)
        {
            switch (mode)
            {
                case BaseResidential: return SimitoneStrings.Get("258", "The base neighbourhood");
                case Unleashed: return SimitoneStrings.Get("259", "Unleashed");
                default: return null;
            }
        }

        /// <summary>
        /// The CustomUI plate for the button that travels to <paramref name="mode"/>. The four
        /// expansions that always had one keep it; the base neighbourhood keeps the arrow it
        /// always had; Unleashed is the one plate this change had to add.
        /// </summary>
        public static string TravelIcon(ushort mode)
        {
            switch (mode)
            {
                case Downtown: return "ngbh_downt.png";
                case Vacation: return "ngbh_vacat.png";
                case Unleashed: return "ngbh_unleashed.png";
                case Studiotown: return "ngbh_studio.png";
                case Magictown: return "ngbh_magic.png";
                // the base map has no plate of its own, and never did: the button that goes there
                // is the "back" arrow
                default: return "ngbh_back.png";
            }
        }

        /// <summary>
        /// The icon the wipe draws over the map while travelling to <paramref name="mode"/>. The
        /// base map has none of its own either, so it borrows the generic transition plate, which
        /// is what the "back to the base neighbourhood" button has always passed.
        /// </summary>
        public static string TransitionIcon(ushort mode)
        {
            return mode == BaseResidential ? "trans_normal.png" : TravelIcon(mode);
        }
    }
}
