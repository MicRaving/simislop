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
    /// What "add a lot to this neighbourhood" resolved to. See <see cref="NeighAddLot.Plan"/>.
    /// </summary>
    public enum NeighAddLotDecision
    {
        /// <summary>
        /// There is a lot slot to create and a way to create it. Nothing in this tree ever
        /// produces this today - see the remarks on <see cref="NeighAddLot"/> - but the case is
        /// spelled out so the refusal below is a decision rather than a constant.
        /// </summary>
        Create,

        /// <summary>The request did not come from the neighbourhood map.</summary>
        NotOnMapScreen,

        /// <summary>
        /// The map being shown has no lot numbers to work with, so there is nothing to add a lot
        /// to and nowhere to put it.
        /// </summary>
        NoLotTable,

        /// <summary>
        /// Every lot slot this map could hold is already in the location table, so a new lot would
        /// have to go outside it.
        /// </summary>
        EveryLotInUse,

        /// <summary>
        /// There is room, and this game still cannot write the files a new lot needs.
        /// </summary>
        NoLotCreationPath,
    }

    /// <summary>
    /// What the map knows when a player presses "add a lot": the lots already on the map and the
    /// runs of lot numbers this map draws from. Deliberately a snapshot rather than a query of the
    /// provider, so the decision can be taken - and asserted - without a neighbourhood.
    /// </summary>
    public readonly struct NeighAddLotFacts
    {
        private readonly IReadOnlyList<int> _lots;

        /// <param name="onMapScreen">Whether the neighbourhood map is the screen the request came from.</param>
        /// <param name="lotsOnMap">The lot numbers the current map's location table holds.</param>
        /// <param name="lotRanges">
        /// The lot numbers this map could hold, in the order a new lot would take them - see
        /// <see cref="NeighbourhoodModeMap.LotRangesForMode"/>. An empty list, or one whose runs
        /// are all empty, is how "this map has no lot table" is spelled.
        /// </param>
        /// <param name="lotCreationAvailable">Whether this game has a lot-creation path.</param>
        public NeighAddLotFacts(bool onMapScreen, IReadOnlyList<int> lotsOnMap,
            IReadOnlyList<NeighLotRange> lotRanges, bool lotCreationAvailable)
        {
            OnMapScreen = onMapScreen;
            _lots = lotsOnMap;
            LotRanges = lotRanges;
            LotCreationAvailable = lotCreationAvailable;
        }

        /// <summary>Whether the neighbourhood map is the screen the request came from.</summary>
        public bool OnMapScreen { get; }

        /// <summary>Whether this game has a lot-creation path. False today; see <see cref="NeighAddLot"/>.</summary>
        public bool LotCreationAvailable { get; }

        /// <summary>The runs of lot numbers this map could hold, or <c>null</c> for an unknown map.</summary>
        public IReadOnlyList<NeighLotRange> LotRanges { get; }

        /// <summary>The lot numbers the map currently holds, or <c>null</c> for an unknown map.</summary>
        public IReadOnlyList<int> LotsOnMap
        {
            get { return _lots; }
        }

        /// <summary>How many lots the map holds. Zero for a map that holds none.</summary>
        public int LotsOnMapCount
        {
            get { return _lots?.Count ?? 0; }
        }

        /// <summary>
        /// How many lots this map could hold - the size of its lot-number runs, which the game
        /// fixes rather than the neighbourhood. Zero when it has none, which is how "this map has
        /// no lot table" is spelled.
        /// </summary>
        public int SlotsOnMap
        {
            get
            {
                if (LotRanges == null) return 0;
                int total = 0;
                foreach (var range in LotRanges) total += range.Count;
                return total;
            }
        }

        /// <summary>
        /// The lot number a new lot would take, or <c>null</c> when the ranges are empty or full.
        /// </summary>
        public int? NextFreeLot
        {
            get { return NeighAddLot.NextFreeLot(_lots, LotRanges); }
        }
    }

    /// <summary>
    /// The answer to "add a lot": what to do, and - when the answer is no - the sentence to show
    /// the player and the untranslated detail to show underneath it.
    /// </summary>
    public readonly struct NeighAddLotPlan
    {
        public NeighAddLotPlan(NeighAddLotDecision decision, int? lot, string title, string message, string detail)
        {
            Decision = decision;
            Lot = lot;
            Title = title;
            Message = message;
            Detail = detail;
        }

        /// <summary>What to do.</summary>
        public NeighAddLotDecision Decision { get; }

        /// <summary>
        /// The lot number a created lot would be given, or <c>null</c> for a refusal. Carried even
        /// though nothing creates it, so that the "which number would it be" half of the question
        /// is answered - and tested - while the "make it" half is not.
        /// </summary>
        public int? Lot { get; }

        /// <summary>Alert title, already localised.</summary>
        public string Title { get; }

        /// <summary>Alert body, already localised, and a whole sentence on its own.</summary>
        public string Message { get; }

        /// <summary>
        /// Untranslated detail, or <c>null</c>. Technical detail is not translatable and
        /// <c>DialogService.ShowAlert</c> appends it after a blank line for exactly that reason.
        /// </summary>
        public string Detail { get; }

        /// <summary>True when this plan is a refusal, i.e. nothing was created.</summary>
        public bool IsRefusal
        {
            get { return Decision != NeighAddLotDecision.Create; }
        }
    }

    /// <summary>
    /// The rule behind the map's "add a lot" button: whether a lot can be added, and, when it
    /// cannot, the reason in one sentence.
    ///
    /// <para>
    /// <b>The short answer is that it cannot, and that is a property of the data rather than a
    /// missing button.</b> A TS1 neighbourhood's lots are not a folder you can drop a file into.
    /// They are a fixed, index-addressed table - one line of <c>LotLocations.iff</c> per lot, each
    /// line a lot number and a position on the map, read by STR id and read positionally - plus
    /// one <c>HouseNN.iff</c> per lot in the user's house folder, plus a family record in
    /// <c>Neighborhood.iff</c>, plus a zoning line. Adding one means writing all four, and the
    /// first one alone is the problem: the table is the size the game shipped it at, so a new lot
    /// has to displace an existing line and the displaced lot's house has to go somewhere.
    /// </para>
    /// <para>
    /// <b>Why there is a button at all, then.</b> Two reasons, and the second is the one that
    /// matters. It was on the retail bar, so a player who remembers it looks for it; and a button
    /// that explains itself is worth more than a missing one, because the alternative to a player
    /// pressing "add a lot" and being told why it is not there is a player who never asks. The
    /// refusal is deliberately specific - it names the table, the house files and the family
    /// record - so that a bug report about it is actionable.
    /// </para>
    /// <para>
    /// <b>What the data does support is kept and tested anyway</b>: which lot number a new lot
    /// would take, and what its file would be called. That is the pure-arithmetic half of the work
    /// and it is what a first <see cref="NeighAddLotDecision.Create"/> implementation would use.
    /// It is also what fixes the order of the refusals, because it is what decides whether there
    /// is room: a map with no room cannot be fixed by a better writer, so "every lot in use" is
    /// answered before "this game cannot write one", and a request that did not come from the map
    /// is answered before both. Only the last is a statement about this game rather than about the
    /// neighbourhood.
    /// </para>
    /// <para>
    /// Pure. Nothing here touches the disk, the provider or the screen; the panel reads the facts
    /// and shows whatever comes back.
    /// </para>
    /// </summary>
    public static class NeighAddLot
    {
        /// <summary>
        /// The untranslated detail attached to the "this game cannot" refusal. A constant so the
        /// sentence and the code that cannot do the work cannot drift apart.
        /// </summary>
        public const string NoCreationPathDetail =
            "Adding a lot means adding a line to the neighbourhood's LotLocations.iff (a fixed, " +
            "index-addressed table), creating HouseNN.iff, and adding a family to Neighborhood.iff. " +
            "Nothing in Simitone writes any of those, so there is no safe way to do it from inside " +
            "a play session.";

        /// <summary>The untranslated detail attached to the "no room" refusal.</summary>
        public const string EveryLotInUseDetail =
            "A neighbourhood's lot table only holds the lots the game shipped it with. A new lot " +
            "would have to take the place of one that is already in it.";

        /// <summary>The untranslated detail attached to the "no lot table" refusal.</summary>
        public const string NoLotTableDetail =
            "This map has no lot numbers to work from, so the game has nowhere to look for its lots.";

        /// <summary>
        /// The lot number a new lot would take, or <c>null</c> when every run is full or there are
        /// none.
        /// </summary>
        /// <remarks>
        /// The lowest number nobody has, run by run and in the order the runs were given. Lowest
        /// rather than highest because the lot numbers are what the neighbourhood's family
        /// records, its zoning file and its house file names are all keyed on: filling a gap leaves
        /// the existing keys valid, and the next number above the top of a range the game did not
        /// ship is a number nothing else in the file has a slot for.
        /// </remarks>
        /// <param name="lotsOnMap">Lot numbers already on the map.</param>
        /// <param name="lotRanges">The runs of numbers this map could hold, in the order they should be filled.</param>
        public static int? NextFreeLot(IEnumerable<int> lotsOnMap, IReadOnlyList<NeighLotRange> lotRanges)
        {
            if (lotRanges == null) return null;
            var taken = new HashSet<int>();
            if (lotsOnMap != null)
            {
                foreach (var lot in lotsOnMap) taken.Add(lot);
            }
            foreach (var range in lotRanges)
            {
                if (range.IsEmpty) continue;
                for (int lot = range.First; lot <= range.Last; lot++)
                {
                    if (!taken.Contains(lot)) return lot;
                }
            }
            return null;
        }

        /// <summary>
        /// The one-run form of <see cref="NextFreeLot(IEnumerable{int}, IReadOnlyList{NeighLotRange})"/>,
        /// for a caller that has a single contiguous run rather than a map's whole set.
        /// </summary>
        /// <param name="lotsOnMap">Lot numbers already taken.</param>
        /// <param name="first">First lot number of the run, inclusive.</param>
        /// <param name="last">Last lot number of the run, inclusive.</param>
        public static int? NextFreeLot(IEnumerable<int> lotsOnMap, int first, int last)
        {
            return NextFreeLot(lotsOnMap, new[] { new NeighLotRange(first, last) });
        }

        /// <summary>
        /// The file a lot's data lives in, e.g. <c>House07.iff</c> for lot 7 - the same two-digit
        /// name the game's own house files use and the one <c>SaveService</c> and
        /// <c>TravelSave</c> write, so a name produced here is a name the rest of the tree
        /// resolves.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        public static string HouseFileName(int lot)
        {
            if (lot < 0) return null;
            return "House" + lot.ToString("D2", CultureInfo.InvariantCulture) + ".iff";
        }

        /// <summary>
        /// Decides what a request to add a lot should do.
        /// </summary>
        /// <remarks>
        /// The refusals are in the order they are asked for, cheapest and most specific first, and
        /// the order is the contract: a request that did not come from the map is not a request
        /// about this map; a map with no lot numbers cannot be added to whatever the game could
        /// write; a map with no room needs a bigger table, not a writer. Only the "cannot write
        /// one" case is a statement about this game rather than about the neighbourhood, so it
        /// comes last.
        /// </remarks>
        public static NeighAddLotPlan Plan(NeighAddLotFacts facts)
        {
            var title = SimitoneStrings.Get("253", "Add Lot");

            if (!facts.OnMapScreen)
            {
                return new NeighAddLotPlan(NeighAddLotDecision.NotOnMapScreen, null, title,
                    SimitoneStrings.Get("254", "A lot can only be added from the neighbourhood map."),
                    null);
            }

            if (facts.SlotsOnMap <= 0)
            {
                return new NeighAddLotPlan(NeighAddLotDecision.NoLotTable, null, title,
                    SimitoneStrings.Get("255", "This map has no lot table, so there is nowhere to put a new lot."),
                    NoLotTableDetail);
            }

            if (!facts.NextFreeLot.HasValue)
            {
                return new NeighAddLotPlan(NeighAddLotDecision.EveryLotInUse, null, title,
                    SimitoneStrings.Get("256", "Every lot on this map is in use, so there is nowhere to put another."),
                    EveryLotInUseDetail);
            }

            if (!facts.LotCreationAvailable)
            {
                return new NeighAddLotPlan(NeighAddLotDecision.NoLotCreationPath, null, title,
                    SimitoneStrings.Get("257", "Simitone cannot add a lot to a neighbourhood."),
                    NoCreationPathDetail);
            }

            // The one branch nothing reaches today. It names the slot the two refusals above
            // already worked out, so the number a future writer needs is defined and tested even
            // while the writing is not.
            return new NeighAddLotPlan(NeighAddLotDecision.Create, facts.NextFreeLot, title, null, null);
        }
    }
}
