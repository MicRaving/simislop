/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The neighbourhood map's new controls: the lot filters, the Next/Previous pair and the
    /// Add Lot button, plus the view-id table that makes the base map reachable again.
    ///
    /// <para>
    /// All of it is pure - no graphics device, no content, no audio, no neighbourhood - so every
    /// case below runs headlessly. That is the point of the design rather than a convenience:
    /// the map panel cannot be constructed in a test at all, so anything worth asserting about
    /// "which lots are on show" and "what does Next pick" has to live behind a method that does
    /// not need one.
    /// </para>
    /// <para>
    /// Two of the checks are about <em>shipped data</em> rather than about code, and both exist
    /// because the failure they catch is silent. The button art is looked up by bare file name
    /// through <c>CustomUIProvider</c>, so a renamed or missing PNG is not an exception - the
    /// texture comes back null and the button draws nothing. And the filter captions come from a
    /// string table, so a missing entry is not an error either: the call site's English is shown
    /// instead and the page simply reads as untranslated.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLotFilterTests
    {
        // ------------------------------------------------------------------ the shipped art

        /// <summary>
        /// Every CustomUI file name the map chrome names, resolved the way
        /// <c>CustomUIProvider</c> resolves one: by bare file name, case-insensitively, anywhere
        /// under <c>Content/uigraphics</c>. Returns the names that are not there.
        /// </summary>
        private static List<string> MissingCustomUI(IEnumerable<string> names)
        {
            var root = Path.Combine(AppContext.BaseDirectory, "Content", "uigraphics");
            Assert.True(Directory.Exists(root), "no Content/uigraphics next to the tests, at " + root + ".");

            //the provider keys on Path.GetFileName(...).ToLowerInvariant(), so this is its own index
            var shipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*.png", SearchOption.AllDirectories))
            {
                shipped.Add(Path.GetFileName(file));
            }

            return names.Where(name => name != null && !shipped.Contains(name)).ToList();
        }

        [Fact]
        public void EveryButtonTheMapChromeNamesIsActuallyShipped()
        {
            var wanted = new List<string> { "ngbh_filterbar.png" };
            foreach (var filter in NeighLotFilters.Each()) wanted.Add(NeighLotFilters.IconName(filter));
            wanted.Add("ngbh_addlot.png");
            wanted.Add(NeighLotNavigator.IconName(NeighLotNavigator.Forward));
            wanted.Add(NeighLotNavigator.IconName(NeighLotNavigator.Backward));
            foreach (var mode in new ushort[] { 1, 2, 3, 4, 5, 7 })
            {
                wanted.Add(NeighbourhoodModeMap.TravelIcon(mode));
                wanted.Add(NeighbourhoodModeMap.TransitionIcon(mode));
            }

            var missing = MissingCustomUI(wanted);
            Assert.True(missing.Count == 0,
                "the map chrome names CustomUI art that does not ship: " + string.Join(", ", missing));
        }

        [Fact]
        public void EachFilterHasItsOwnArt()
        {
            // four filters, four plates: a shared name would mean three of the buttons showing the
            // same glyph, which no assertion about the strings would ever notice
            var names = NeighLotFilters.Each().Select(NeighLotFilters.IconName).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
            Assert.All(names, name => Assert.EndsWith(".png", name, StringComparison.Ordinal));
        }

        // ------------------------------------------------------------------ the filters

        private static NeighLotFacts OccupiedLot(int lot = 1) =>
            new NeighLotFacts(lot, true, LotZoning.ZoneResidential);

        private static NeighLotFacts VacantResidentialLot(int lot = 2) =>
            new NeighLotFacts(lot, false, LotZoning.ZoneResidential);

        private static NeighLotFacts VacantCommunityLot(int lot = 3) =>
            new NeighLotFacts(lot, false, LotZoning.ZoneCommunity);

        private static NeighLotFacts StudioLot(int lot = 85) =>
            new NeighLotFacts(lot, false, LotZoning.ZoneStudio);

        /// <summary>
        /// A map with one lot of every kind, which is the smallest set that can tell the four
        /// filters apart: a lot that is occupied and residential, one vacant and residential, one
        /// vacant and community.
        /// </summary>
        private static NeighLotFacts[] MixedMap()
        {
            return new[]
            {
                OccupiedLot(1),
                VacantResidentialLot(2),
                VacantCommunityLot(3),
                StudioLot(85)
            };
        }

        [Fact]
        public void TheFilterSetIsTheFourFiltersInToolbarOrder()
        {
            var each = NeighLotFilters.Each();
            Assert.Equal(4, each.Length);
            Assert.All(each, filter => Assert.True(NeighLotFilters.IsSingle(filter),
                filter + " is not one filter."));
            Assert.Equal(NeighLotFilters.All, each.Aggregate(NeighLotFilter.None, (set, f) => set | f));
        }

        [Fact]
        public void EachReturnsAFreshArraySoTheTableCannotBeReordered()
        {
            var first = NeighLotFilters.Each();
            first[0] = NeighLotFilter.Community;
            Assert.Equal(NeighLotFilter.Occupied, NeighLotFilters.Each()[0]);
        }

        [Theory]
        [InlineData(NeighLotFilter.None, false)]
        [InlineData(NeighLotFilter.Occupied, true)]
        [InlineData(NeighLotFilter.Vacant, true)]
        [InlineData(NeighLotFilter.Residential, true)]
        [InlineData(NeighLotFilter.Community, true)]
        // two bits is two filters, not one
        [InlineData(NeighLotFilter.Occupied | NeighLotFilter.Vacant, false)]
        public void IsSingleIsAboutOneBitAndNotAboutValidity(NeighLotFilter filter, bool expected)
        {
            Assert.Equal(expected, NeighLotFilters.IsSingle(filter));
        }

        [Fact]
        public void OnlyTheFourFiltersAndTheEmptySetAreKnown()
        {
            Assert.True(NeighLotFilters.IsKnown(NeighLotFilter.None));
            Assert.True(NeighLotFilters.IsKnown(NeighLotFilters.All));
            Assert.False(NeighLotFilters.IsKnown((NeighLotFilter)(1 << 9)));
            Assert.True(NeighLotFilters.IsValidSet(NeighLotFilters.All));
            Assert.False(NeighLotFilters.IsValidSet(NeighLotFilters.All | (NeighLotFilter)(1 << 9)));
        }

        [Theory]
        [InlineData(NeighLotFilter.Occupied, true)]
        [InlineData(NeighLotFilter.Vacant, false)]
        [InlineData(NeighLotFilter.Residential, true)]
        [InlineData(NeighLotFilter.Community, false)]
        public void OccupiedAndVacantAreComplements(NeighLotFilter filter, bool expected)
        {
            Assert.Equal(expected, NeighLotFilters.Matches(filter, OccupiedLot()));
        }

        [Theory]
        [InlineData(NeighLotFilter.Residential, false)]
        [InlineData(NeighLotFilter.Community, true)]
        public void ResidentialAndCommunityAreComplements(NeighLotFilter filter, bool expected)
        {
            // a community lot, which is a lot a family may not live on
            Assert.Equal(expected, NeighLotFilters.Matches(filter, VacantCommunityLot()));
        }

        [Fact]
        public void TheStudioZoneBelongsToCommunityBecauseItIsNotResidential()
        {
            // LotZoning.ZoneStudio is a display fallback, documented as "not residential without
            // being community". A community filter that tested for exactly 1 would leave Studio
            // Town's nine lots belonging to neither filter, and the bar would offer two buttons
            // that both match nothing on the map it exists to describe.
            Assert.False(StudioLot().CanMoveIn);
            Assert.True(NeighLotFilters.Matches(NeighLotFilter.Community, StudioLot()));
            Assert.False(NeighLotFilters.Matches(NeighLotFilter.Residential, StudioLot()));
        }

        [Fact]
        public void ANegativeZoneIsStillResidential()
        {
            // the zone dictionary is a short, but a corrupt or hand-edited file can hold anything
            // in it, and "not a community lot" is the only question being asked
            var odd = new NeighLotFacts(4, false, -1);
            Assert.True(odd.CanMoveIn);
            Assert.True(NeighLotFilters.Matches(NeighLotFilter.Residential, odd));
        }

        [Fact]
        public void NoFilterKeepsEveryLot()
        {
            Assert.True(NeighLotFilters.Matches(NeighLotFilter.None, OccupiedLot()));
            Assert.True(NeighLotFilters.Passes(NeighLotFilter.None, StudioLot()));
        }

        [Fact]
        public void FiltersIntersect()
        {
            // MixedMap: 1 occupied+residential, 2 vacant+residential, 3 vacant+community,
            // 85 vacant+studio (which is "community" for filtering purposes)
            var map = MixedMap();
            Assert.Equal(1, NeighLotFilters.VisibleCount(NeighLotFilter.Occupied, map));
            Assert.Equal(3, NeighLotFilters.VisibleCount(NeighLotFilter.Vacant, map));
            Assert.Equal(2, NeighLotFilters.VisibleCount(NeighLotFilter.Residential, map));
            Assert.Equal(2, NeighLotFilters.VisibleCount(NeighLotFilter.Community, map));
            // the empty residential block and the empty community block of the same map
            Assert.Equal(1, NeighLotFilters.VisibleCount(
                NeighLotFilter.Vacant | NeighLotFilter.Residential, map));
            //lot 1 is occupied but residential, so nothing is both
            Assert.Equal(0, NeighLotFilters.VisibleCount(
                NeighLotFilter.Occupied | NeighLotFilter.Community, map));
        }

        [Fact]
        public void TwoComplementaryFiltersOnTogetherShowNothing()
        {
            // deliberately allowed: it is the honest answer to "show me the lots that are both
            // occupied and vacant", and the count readout is what tells the player so
            var map = MixedMap();
            Assert.Equal(0, NeighLotFilters.VisibleCount(
                NeighLotFilter.Occupied | NeighLotFilter.Vacant, map));
            Assert.Equal(0, NeighLotFilters.VisibleCount(NeighLotFilters.All, map));
        }

        [Fact]
        public void AMaskWithABitThisBuildDoesNotKnowHidesNothing()
        {
            // a caller passing a stale or foreign mask must not be able to empty the map
            var map = MixedMap();
            var foreign = NeighLotFilters.All | (NeighLotFilter)(1 << 9);
            Assert.Equal(4, NeighLotFilters.VisibleCount(foreign, map));
        }

        [Fact]
        public void ToggleFlipsOneFilterAndRefusesAnythingElse()
        {
            var set = NeighLotFilter.None;
            set = NeighLotFilters.Toggle(set, NeighLotFilter.Vacant);
            Assert.Equal(NeighLotFilter.Vacant, set);
            set = NeighLotFilters.Toggle(set, NeighLotFilter.Residential);
            Assert.Equal(NeighLotFilter.Vacant | NeighLotFilter.Residential, set);
            set = NeighLotFilters.Toggle(set, NeighLotFilter.Vacant);
            Assert.Equal(NeighLotFilter.Residential, set);

            // a two-bit mask is not a filter, and the empty set is not a button
            Assert.Equal(NeighLotFilter.Residential,
                NeighLotFilters.Toggle(NeighLotFilter.Residential, NeighLotFilter.Occupied | NeighLotFilter.Vacant));
            Assert.Equal(NeighLotFilter.Residential,
                NeighLotFilters.Toggle(NeighLotFilter.Residential, NeighLotFilter.None));

            // a set this build does not understand is replaced rather than propagated
            Assert.Equal(NeighLotFilter.None,
                NeighLotFilters.Toggle(NeighLotFilters.All | (NeighLotFilter)(1 << 9), NeighLotFilter.Vacant));
        }

        [Fact]
        public void AFilterNothingMatchesIsUnavailable()
        {
            // a Downtown-style map: every lot community, none residential
            var downtown = new[] { VacantCommunityLot(21), VacantCommunityLot(22) };
            Assert.False(NeighLotFilters.IsAvailable(NeighLotFilter.Residential, downtown));
            Assert.True(NeighLotFilters.IsAvailable(NeighLotFilter.Community, downtown));
            Assert.True(NeighLotFilters.IsAvailable(NeighLotFilter.Vacant, downtown));
            Assert.False(NeighLotFilters.IsAvailable(NeighLotFilter.Occupied, downtown));
        }

        [Fact]
        public void AFilterIsAvailableWhenEvenOneLotMatches()
        {
            var map = new[] { VacantCommunityLot(21), VacantResidentialLot(2) };
            Assert.True(NeighLotFilters.IsAvailable(NeighLotFilter.Residential, map));
            Assert.True(NeighLotFilters.IsAvailable(NeighLotFilter.Vacant, map));
        }

        [Fact]
        public void AnEmptyMapHasNoFiltersAtAll()
        {
            // including the empty set: "no filter matches anything here" and "there is nothing
            // here" are the same answer, and the bar hides itself rather than showing dead buttons
            Assert.False(NeighLotFilters.IsAvailable(NeighLotFilter.None, new NeighLotFacts[0]));
            Assert.False(NeighLotFilters.IsAvailable(NeighLotFilter.None, null));
            Assert.True(NeighLotFilters.IsAvailable(NeighLotFilter.None, MixedMap()));
        }

        [Fact]
        public void NullAndEmptyLotsAreNotAFailure()
        {
            Assert.Equal(0, NeighLotFilters.VisibleCount(NeighLotFilter.None, null));
            Assert.Empty(NeighLotFilters.VisibleLots(NeighLotFilter.None, null));
            Assert.False(NeighLotFilters.IsAvailable(NeighLotFilter.Occupied, null));
        }

        [Fact]
        public void VisibleLotsKeepsTheOrderItWasGiven()
        {
            // the panel hands its list over in map order, so "next" walks the map
            var lots = NeighLotFilters.VisibleLots(NeighLotFilter.Vacant, MixedMap());
            Assert.Equal(new[] { 2, 3, 85 }, lots);
        }

        [Fact]
        public void EveryFilterHasItsOwnNameAndTooltip()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var tips = new HashSet<string>(StringComparer.Ordinal);
            foreach (var filter in NeighLotFilters.Each())
            {
                var name = NeighLotFilters.Name(filter);
                var tip = NeighLotFilters.Tooltip(filter);
                Assert.False(string.IsNullOrWhiteSpace(name), filter + " has no name.");
                Assert.False(string.IsNullOrWhiteSpace(tip), filter + " has no tooltip.");
                Assert.True(names.Add(name), "two filters share the name '" + name + "'.");
                Assert.True(tips.Add(tip), "two filters share the tooltip '" + tip + "'.");
            }
            Assert.NotEqual(NeighLotFilters.Name(NeighLotFilter.Occupied),
                NeighLotFilters.Tooltip(NeighLotFilter.Occupied));
        }

        [Fact]
        public void TheEmptySetIsNotAButtonAndHasNoArtOrCaption()
        {
            Assert.Null(NeighLotFilters.IconName(NeighLotFilter.None));
            Assert.Null(NeighLotFilters.Name(NeighLotFilter.None));
            Assert.Null(NeighLotFilters.Tooltip(NeighLotFilter.None));
            Assert.Null(NeighLotFilters.IconName((NeighLotFilter)(1 << 9)));
        }

        [Fact]
        public void TheCountReadoutNamesBothNumbers()
        {
            Assert.Equal("Lots shown: 12 of 12", NeighLotFilters.CountText(12, 12));
            Assert.Equal("Lots shown: 0 of 12", NeighLotFilters.CountText(0, 12));
            Assert.Equal("Lots shown: 3 of 4", NeighLotFilters.CountText(
                NeighLotFilter.Vacant, MixedMap()));
        }

        // ------------------------------------------------------------------ Next / Previous

        private static readonly int[] MapOrder = { 5, 2, 9, 1, 7 };

        [Fact]
        public void NextWalksTheListInTheOrderItWasGiven()
        {
            Assert.Equal(2, NeighLotNavigator.Step(MapOrder, 5, NeighLotNavigator.Forward));
            Assert.Equal(7, NeighLotNavigator.Step(MapOrder, 1, NeighLotNavigator.Forward));
        }

        [Fact]
        public void NextWrapsAtTheEndAndPreviousAtTheStart()
        {
            // a "next" that stopped at the last lot would be a button that does nothing, with no
            // way for a player to tell that from a broken one
            Assert.Equal(5, NeighLotNavigator.Step(MapOrder, 7, NeighLotNavigator.Forward));
            Assert.Equal(7, NeighLotNavigator.Step(MapOrder, 5, NeighLotNavigator.Backward));
            // MapOrder is {5, 2, 9, 1, 7}: previous from 2 is 5, and from 5 wraps to 7
            Assert.Equal(5, NeighLotNavigator.Step(MapOrder, 2, NeighLotNavigator.Backward));
        }

        [Fact]
        public void WithNothingSelectedNextTakesTheTopOfTheMapAndPreviousTheBottom()
        {
            Assert.Equal(5, NeighLotNavigator.First(MapOrder, NeighLotNavigator.Forward));
            Assert.Equal(7, NeighLotNavigator.First(MapOrder, NeighLotNavigator.Backward));
        }

        [Fact]
        public void ASelectionThatIsNotInTheListIsTreatedAsNoSelection()
        {
            // which is what a filter that just hid the selected lot leaves behind, except the
            // panel resets the selection in that case; this is the belt to that pair of braces
            Assert.Equal(5, NeighLotNavigator.Step(MapOrder, 99, NeighLotNavigator.Forward));
            Assert.Equal(7, NeighLotNavigator.Step(MapOrder, 99, NeighLotNavigator.Backward));
        }

        [Fact]
        public void AnEmptyMapIsNowhereToStepTo()
        {
            Assert.Null(NeighLotNavigator.Step(new int[0], null, NeighLotNavigator.Forward));
            Assert.Null(NeighLotNavigator.Step(new int[0], 1, NeighLotNavigator.Backward));
            Assert.Null(NeighLotNavigator.Step(null, 1, NeighLotNavigator.Forward));
            Assert.Null(NeighLotNavigator.First(null, NeighLotNavigator.Forward));
        }

        [Fact]
        public void ADirectionOfZeroIsNotAStep()
        {
            Assert.Null(NeighLotNavigator.Step(MapOrder, 5, 0));
        }

        [Fact]
        public void OneLotStepsToItselfRatherThanNowhere()
        {
            var one = new[] { 4 };
            Assert.Equal(4, NeighLotNavigator.Step(one, null, NeighLotNavigator.Forward));
            Assert.Equal(4, NeighLotNavigator.Step(one, 4, NeighLotNavigator.Forward));
            Assert.Equal(4, NeighLotNavigator.Step(one, 4, NeighLotNavigator.Backward));
        }

        [Fact]
        public void AnyPositiveDirectionIsForwardAndAnyNegativeOneIsBackward()
        {
            Assert.Equal(2, NeighLotNavigator.Step(MapOrder, 5, 3));
            Assert.Equal(7, NeighLotNavigator.Step(MapOrder, 5, -7));
        }

        [Fact]
        public void IndexOfFindsALotAndReportsAMiss()
        {
            Assert.Equal(2, NeighLotNavigator.IndexOf(MapOrder, 9));
            Assert.Equal(-1, NeighLotNavigator.IndexOf(MapOrder, 99));
            Assert.Equal(-1, NeighLotNavigator.IndexOf(MapOrder, null));
            Assert.Equal(-1, NeighLotNavigator.IndexOf(null, 1));
        }

        [Fact]
        public void TheTwoArrowsAreNamedAndDrawnForTheirDirection()
        {
            Assert.Equal("Next lot", NeighLotNavigator.Caption(NeighLotNavigator.Forward));
            Assert.Equal("Previous lot", NeighLotNavigator.Caption(NeighLotNavigator.Backward));
            Assert.NotEqual(NeighLotNavigator.IconName(NeighLotNavigator.Forward),
                NeighLotNavigator.IconName(NeighLotNavigator.Backward));
            Assert.Equal(NeighLotNavigator.IconName(NeighLotNavigator.Forward),
                NeighLotNavigator.IconName(1));
            Assert.Equal(NeighLotNavigator.IconName(NeighLotNavigator.Backward),
                NeighLotNavigator.IconName(-1));
        }

        // ------------------------------------------------------------------ Add Lot

        [Fact]
        public void ANewLotTakesTheLowestNumberNobodyHas()
        {
            // the lot numbers are what the family records, the zoning file and the house file names
            // are all keyed on, so a gap is worth more than a fresh number above the top
            Assert.Equal(1, NeighAddLot.NextFreeLot(new[] { 2, 3, 4 }, 1, 5));
            Assert.Equal(4, NeighAddLot.NextFreeLot(new[] { 1, 2, 3, 5 }, 1, 5));
            Assert.Equal(1, NeighAddLot.NextFreeLot(new int[0], 1, 5));
            Assert.Equal(1, NeighAddLot.NextFreeLot(null, 1, 5));
        }

        [Fact]
        public void AFullSetOfRunsHasNoFreeLot()
        {
            Assert.Null(NeighAddLot.NextFreeLot(new[] { 1, 2, 3 }, 1, 3));
            Assert.Null(NeighAddLot.NextFreeLot(new int[0], 1, 0));
            Assert.Null(NeighAddLot.NextFreeLot(new int[0], 5, 1));
            Assert.Null(NeighAddLot.NextFreeLot(new int[0], 0, 0));
            Assert.Null(NeighAddLot.NextFreeLot(new int[0], (IReadOnlyList<NeighLotRange>)null));
        }

        [Fact]
        public void ALotsHouseFileIsNamedTheWayTheGameNamesIt()
        {
            Assert.Equal("House07.iff", NeighAddLot.HouseFileName(7));
            Assert.Equal("House00.iff", NeighAddLot.HouseFileName(0));
            Assert.Equal("House99.iff", NeighAddLot.HouseFileName(99));
            Assert.Null(NeighAddLot.HouseFileName(-1));
        }

        private static NeighAddLotFacts RoomToAdd() =>
            new NeighAddLotFacts(true, new[] { 21, 22, 23 },
                NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown), true);

        [Fact]
        public void AddLotCreatesWhenThereIsRoomAndAWayToDoIt()
        {
            var plan = NeighAddLot.Plan(RoomToAdd());
            Assert.Equal(NeighAddLotDecision.Create, plan.Decision);
            Assert.False(plan.IsRefusal);
            // the slot the refusals above already worked out, so a writer has the number in hand
            Assert.Equal(24, plan.Lot);
            Assert.Equal("House24.iff", NeighAddLot.HouseFileName(plan.Lot.Value));
            Assert.Null(plan.Message);
        }

        [Fact]
        public void AddLotRefusesWhenThisGameCannotWriteTheFiles()
        {
            // the case every build is in today, and the one the button exists to explain
            var plan = NeighAddLot.Plan(new NeighAddLotFacts(true, new[] { 21, 22 },
                NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown), false));
            Assert.Equal(NeighAddLotDecision.NoLotCreationPath, plan.Decision);
            Assert.True(plan.IsRefusal);
            Assert.Null(plan.Lot);
            Assert.Equal("Simitone cannot add a lot to a neighbourhood.", plan.Message);
            Assert.Equal(NeighAddLot.NoCreationPathDetail, plan.Detail);
            Assert.Equal("Add Lot", plan.Title);
        }

        [Fact]
        public void AddLotRefusesBeforeTheGameQuestionWhenThereIsNoRoom()
        {
            // a full map cannot be fixed by a better writer, so the more specific answer wins
            var plan = NeighAddLot.Plan(new NeighAddLotFacts(true, new[] { 21, 22, 23 },
                new[] { new NeighLotRange(21, 23) }, false));
            Assert.Equal(NeighAddLotDecision.EveryLotInUse, plan.Decision);
            Assert.Contains("in use", plan.Message, StringComparison.Ordinal);
            Assert.Equal(NeighAddLot.EveryLotInUseDetail, plan.Detail);
        }

        [Fact]
        public void AddLotRefusesAMapWithNoLotNumbers()
        {
            // an empty run is how "this map has no lot table" is spelled
            var plan = NeighAddLot.Plan(new NeighAddLotFacts(true, new int[0],
                new[] { new NeighLotRange(0, 0) }, true));
            Assert.Equal(NeighAddLotDecision.NoLotTable, plan.Decision);
            Assert.Equal(NeighAddLot.NoLotTableDetail, plan.Detail);

            var unknown = NeighAddLot.Plan(new NeighAddLotFacts(true, null, null, true));
            Assert.Equal(NeighAddLotDecision.NoLotTable, unknown.Decision);
        }

        [Fact]
        public void AddLotRefusesARequestThatDidNotComeFromTheMap()
        {
            // asked first, and alone: a request from anywhere else is not a request about this map
            var plan = NeighAddLot.Plan(new NeighAddLotFacts(false, new[] { 21 },
                NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown), true));
            Assert.Equal(NeighAddLotDecision.NotOnMapScreen, plan.Decision);
            Assert.Equal("A lot can only be added from the neighbourhood map.", plan.Message);
            Assert.Null(plan.Detail);
        }

        [Fact]
        public void EveryRefusalSaysSomethingAndNamesTheButton()
        {
            var downtown = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown);
            var refusals = new[]
            {
                new NeighAddLotFacts(false, new[] { 21 }, downtown, true),
                new NeighAddLotFacts(true, null, null, true),
                new NeighAddLotFacts(true, new[] { 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31 }, downtown, false),
                new NeighAddLotFacts(true, new[] { 21 }, downtown, false)
            };

            foreach (var facts in refusals)
            {
                var plan = NeighAddLot.Plan(facts);
                Assert.True(plan.IsRefusal, plan.Decision + " was expected to be a refusal.");
                Assert.Equal("Add Lot", plan.Title);
                Assert.False(string.IsNullOrWhiteSpace(plan.Message), plan.Decision + " has no message.");
                Assert.EndsWith(".", plan.Message, StringComparison.Ordinal);
                Assert.Null(plan.Lot);
            }
        }

        [Fact]
        public void TheFactsslotCountIsTheRunsNotTheMap()
        {
            Assert.Equal(11, RoomToAdd().SlotsOnMap);
            Assert.Equal(3, RoomToAdd().LotsOnMapCount);
            Assert.Equal(24, RoomToAdd().NextFreeLot);

            // Downtown really is 21..31
            var full = new NeighAddLotFacts(true,
                Enumerable.Range(21, 11).ToArray(), downtownRanges(), true);
            Assert.Equal(11, full.SlotsOnMap);
            Assert.Null(full.NextFreeLot);

            // the base map's two runs are 20 + 31, with the Downtown and Vacation numbers between
            // them belonging to other views
            var baseMap = new NeighAddLotFacts(true, new[] { 1, 2, 3 },
                NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.BaseResidential), true);
            Assert.Equal(51, baseMap.SlotsOnMap);
            Assert.Equal(4, baseMap.NextFreeLot);

            var noRange = new NeighAddLotFacts(true, new[] { 1 }, new NeighLotRange[0], true);
            Assert.Equal(0, noRange.SlotsOnMap);
        }

        private static NeighLotRange[] downtownRanges() =>
            NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown);

        [Fact]
        public void ANewLotOnTheBaseMapSkipsTheLotsOtherViewsOwn()
        {
            // 1-20 then 50-80, so filling the residential block first and only then the community
            // block - a new lot must never be handed a Downtown or Vacation number
            var baseMap = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.BaseResidential);
            var taken = new List<int>();
            for (int i = 0; i < 51; i++)
            {
                var next = NeighAddLot.NextFreeLot(taken, baseMap);
                Assert.True(next.HasValue, "the map filled up at " + i + " lots.");
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Downtown, next.Value));
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Vacation, next.Value));
                taken.Add(next.Value);
            }
            Assert.Equal(51, taken.Count);   // the whole of 1..20 and then all of 50..80
            Assert.Equal(20, taken[19]);     // the last of the residential block
            Assert.Equal(50, taken[20]);     // and then straight to the community block
            Assert.Null(NeighAddLot.NextFreeLot(taken, baseMap));
        }

        // ------------------------------------------------------------------ which view is which

        /// <summary>
        /// <b>Residential is the Unleashed map, and the base map is still one button away.</b>
        /// Simitone opens on the unzoned map (view 4) because hybrid play needs that, and a while
        /// ago "residential" was wired to the base map instead (view 1), which left Unleashed with
        /// no way to be reached. This is the test that says the default is view 4 and not view 1;
        /// the ones below say view 1 is still a real, drawable, reachable map with the same lots on
        /// it, so moving the default did not take it away.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(20)]
        [InlineData(32)]
        [InlineData(39)]
        [InlineData(50)]
        [InlineData(80)]
        [InlineData(100)]
        [InlineData(-1)]
        public void ResidentialIsTheUnleashedNeighbourhood(short house)
        {
            Assert.Equal(NeighbourhoodModeMap.Unleashed, NeighbourhoodModeMap.ForHouse(house));
            Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighbourhoodModeMap.ForHouse(house));
        }

        [Fact]
        public void TheDefaultResidentialViewIsUnleashedAndTheBaseMapIsStillASeparateView()
        {
            // The default is a preference, not a rule: the two residential views stay distinct
            // constants so nothing can quietly collapse them into one.
            Assert.Equal(NeighbourhoodModeMap.Unleashed, NeighbourhoodModeMap.ResidentialDefault);
            Assert.NotEqual(NeighbourhoodModeMap.BaseResidential, NeighbourhoodModeMap.ResidentialDefault);
            Assert.Equal((ushort)1, NeighbourhoodModeMap.BaseResidential);
            Assert.Equal((ushort)4, NeighbourhoodModeMap.Unleashed);
        }

        /// <summary>
        /// The other half of the same claim, and the reason the id above is not just a number:
        /// view 1 really is the base game's own map and view 4 really is Unleashed, read out of
        /// the panel's own view table rather than out of a comment.
        /// </summary>
        [Fact]
        public void ViewOneIsTheBaseGamesMapAndViewFourIsUnleashed()
        {
            var baseNeighbourhood = UINeighborhoodSelectionPanel.Neighborhoods[
                NeighbourhoodModeMap.BaseResidential - 1];
            var unleashed = UINeighborhoodSelectionPanel.Neighborhoods[
                NeighbourhoodModeMap.Unleashed - 1];

            Assert.Equal("Nbhd", NeighbourhoodAnimations.ViewFolder(baseNeighbourhood));
            Assert.Equal("Community", NeighbourhoodAnimations.ViewFolder(unleashed));
            //and both are drawable, which is what makes reaching for view 1 a trip and not a
            //fallback to the default
            Assert.False(string.IsNullOrWhiteSpace(baseNeighbourhood.Graphic));
            Assert.False(string.IsNullOrWhiteSpace(unleashed.Graphic));
        }

        [Fact]
        public void TheBaseMapIsStillOneButtonFromTheDefaultView()
        {
            // The reason the default could move: the base map is a travel target from the view the
            // game opens on, it has a plate, a caption and a wipe, so Unleashed being the default
            // is a preference rather than a loss.
            var targets = NeighbourhoodModeMap.TravelTargets(NeighbourhoodModeMap.ResidentialDefault);

            Assert.Contains<ushort>(NeighbourhoodModeMap.BaseResidential, targets);
            Assert.DoesNotContain<ushort>(NeighbourhoodModeMap.ResidentialDefault, targets);
            Assert.True(NeighbourhoodModeMap.HasView(NeighbourhoodModeMap.BaseResidential));
            Assert.Equal("The base neighbourhood",
                NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.BaseResidential));
            Assert.Equal("ngbh_back.png", NeighbourhoodModeMap.TravelIcon(NeighbourhoodModeMap.BaseResidential));
            Assert.Equal("trans_normal.png",
                NeighbourhoodModeMap.TransitionIcon(NeighbourhoodModeMap.BaseResidential));
        }

        [Theory]
        [InlineData(21, NeighbourhoodModeMap.Downtown)]
        [InlineData(26, NeighbourhoodModeMap.Downtown)]
        [InlineData(31, NeighbourhoodModeMap.Downtown)]
        [InlineData(40, NeighbourhoodModeMap.Vacation)]
        [InlineData(49, NeighbourhoodModeMap.Vacation)]
        [InlineData(81, NeighbourhoodModeMap.Studiotown)]
        [InlineData(89, NeighbourhoodModeMap.Studiotown)]
        [InlineData(90, NeighbourhoodModeMap.Magictown)]
        [InlineData(99, NeighbourhoodModeMap.Magictown)]
        public void TheExpansionRangesAreUnchanged(short house, ushort expected)
        {
            Assert.Equal(expected, NeighbourhoodModeMap.ForHouse(house));
        }

        [Fact]
        public void LotNinetyIsMagicAndNotStudio()
        {
            // the 81..90 Studio range this tree already had to fix once: lot 90 resolving to
            // Studio meant returning from a Magic Town lot opened the Studio Town map
            Assert.Equal(NeighbourhoodModeMap.Magictown, NeighbourhoodModeMap.ForHouse(90));
            Assert.Equal(NeighbourhoodModeMap.Studiotown, NeighbourhoodModeMap.ForHouse(89));
        }

        [Theory]
        [InlineData(NeighbourhoodModeMap.Downtown, 21, 31)]
        [InlineData(NeighbourhoodModeMap.Vacation, 40, 49)]
        [InlineData(NeighbourhoodModeMap.Studiotown, 81, 89)]
        [InlineData(NeighbourhoodModeMap.Magictown, 90, 99)]
        public void AnExpansionsOwnOneRun(ushort mode, int first, int last)
        {
            var ranges = NeighbourhoodModeMap.LotRangesForMode(mode);
            Assert.Single(ranges);
            Assert.Equal(first, ranges[0].First);
            Assert.Equal(last, ranges[0].Last);
            // and the run is exactly the set of numbers ForHouse sends to this view
            Assert.Equal(last - first + 1, NeighbourhoodModeMap.LotNumbersForMode(mode).Count);
            Assert.Equal(mode, NeighbourhoodModeMap.ForHouse((short)first));
            Assert.Equal(mode, NeighbourhoodModeMap.ForHouse((short)last));
        }

        [Fact]
        public void TheTwoViewsOverTheSameLotsShareThem()
        {
            // Unleashed is a second background over the base map's lots, not a different set: a view
            // that claimed different numbers would be claiming lots its location table has no entry
            // for, and Add Lot would hand out numbers nothing else in the file has a slot for.
            var baseMap = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.BaseResidential);
            var unleashed = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Unleashed);
            Assert.Equal(baseMap.Length, unleashed.Length);
            for (int i = 0; i < baseMap.Length; i++)
            {
                Assert.Equal(baseMap[i].First, unleashed[i].First);
                Assert.Equal(baseMap[i].Last, unleashed[i].Last);
            }
        }

        [Fact]
        public void TheResidentialRunsAreTheResidentialAndCommunityBlocks()
        {
            var ranges = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.BaseResidential);
            Assert.Equal(2, ranges.Length);
            Assert.Equal(1, ranges[0].First);
            Assert.Equal(20, ranges[0].Last);
            Assert.Equal(50, ranges[1].First);
            Assert.Equal(80, ranges[1].Last);
            Assert.Equal(51, NeighbourhoodModeMap.LotNumbersForMode(NeighbourhoodModeMap.BaseResidential).Count);
        }

        [Fact]
        public void NoViewClaimsAnotherViewsLots()
        {
            // the Downtown and Vacation numbers sit between 20 and 50, inside the bracket the base
            // map used to be described by. A single first/last pair would have handed them over.
            // The base map is not the default any more, so ownership is asserted with OwnsLot -
            // which both residential views answer to - rather than with the default id.
            var baseMap = NeighbourhoodModeMap.LotNumbersForMode(NeighbourhoodModeMap.BaseResidential);
            foreach (var lot in baseMap)
            {
                Assert.Equal(NeighbourhoodModeMap.ResidentialDefault, NeighbourhoodModeMap.ForHouse((short)lot));
                Assert.True(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.BaseResidential, lot));
                Assert.True(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Unleashed, lot));
            }
            for (int lot = 21; lot <= 31; lot++)
            {
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.BaseResidential, lot));
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Unleashed, lot));
                Assert.True(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Downtown, lot));
            }
            for (int lot = 40; lot <= 49; lot++)
            {
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.BaseResidential, lot));
                Assert.False(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Unleashed, lot));
                Assert.True(NeighbourhoodModeMap.OwnsLot(NeighbourhoodModeMap.Vacation, lot));
            }
        }

        [Fact]
        public void ARunThatIsNotARunIsEmpty()
        {
            // a zero the caller meant as "no range" is not a one-lot run at lot 0, and an inverted
            // pair is not a run backwards
            Assert.True(new NeighLotRange(0, 0).IsEmpty);
            Assert.True(new NeighLotRange(5, 1).IsEmpty);
            Assert.Equal(0, new NeighLotRange(0, 0).Count);
            Assert.False(new NeighLotRange(0, 0).Contains(0));
            Assert.False(new NeighLotRange(5, 1).Contains(3));

            var good = new NeighLotRange(21, 31);
            Assert.False(good.IsEmpty);
            Assert.Equal(11, good.Count);
            Assert.True(good.Contains(21));
            Assert.True(good.Contains(31));
            Assert.False(good.Contains(32));
        }

        [Fact]
        public void TheLotNumberTableIsAFreshArrayEachTime()
        {
            var first = NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown);
            first[0] = new NeighLotRange(1, 2);
            Assert.Equal(21, NeighbourhoodModeMap.LotRangesForMode(NeighbourhoodModeMap.Downtown)[0].First);
        }

        [Fact]
        public void SlotSixIsNotATravelDestination()
        {
            // it ships with no background, so travelling there would log a warning and fall back
            Assert.False(NeighbourhoodModeMap.HasView(6));
            Assert.True(NeighbourhoodModeMap.HasView(NeighbourhoodModeMap.BaseResidential));
            Assert.True(NeighbourhoodModeMap.HasView(NeighbourhoodModeMap.Magictown));
            Assert.False(NeighbourhoodModeMap.HasView(0));
            Assert.False(NeighbourhoodModeMap.HasView(9));
        }

        [Fact]
        public void EveryViewCanReachEveryOtherOne()
        {
            var views = new ushort[] { 1, 2, 3, 4, 5, 7 };
            foreach (var from in views)
            {
                var targets = NeighbourhoodModeMap.TravelTargets(from);
                Assert.DoesNotContain<ushort>(from, targets);
                Assert.Equal(views.Length - 1, targets.Count);
                //both of the two the change is about are reachable from every other view, which is
                //the whole point of the Unleashed button existing
                if (from != NeighbourhoodModeMap.BaseResidential)
                {
                    Assert.Contains<ushort>(NeighbourhoodModeMap.BaseResidential, targets);
                }
                if (from != NeighbourhoodModeMap.Unleashed)
                {
                    Assert.Contains<ushort>(NeighbourhoodModeMap.Unleashed, targets);
                }
                Assert.All(targets, target => Assert.True(NeighbourhoodModeMap.HasView(target)));
            }
        }

        [Fact]
        public void EveryViewsButtonIsDrawnFromItsOwnPlate()
        {
            // six views, six plates: five of the expansion/arrow shapes and the one Unleashed
            // plate this change had to add
            var names = new ushort[] { 1, 2, 3, 4, 5, 7 }.Select(NeighbourhoodModeMap.TravelIcon).ToList();
            Assert.Equal(6, names.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void TheBaseMapBorrowsTheGenericWipeBecauseItHasNoPlate()
        {
            //and the other five are named for themselves
            Assert.Equal("trans_normal.png", NeighbourhoodModeMap.TransitionIcon(NeighbourhoodModeMap.BaseResidential));
            foreach (ushort mode in new ushort[] { 2, 3, 4, 5, 7 })
            {
                Assert.Equal(NeighbourhoodModeMap.TravelIcon(mode), NeighbourhoodModeMap.TransitionIcon(mode));
            }
        }

        [Fact]
        public void TheTwoNewlyNamedViewsHaveCaptionsAndTheOtherFourDoNot()
        {
            //the other four plates have carried their own names since before there were captions
            Assert.Equal("The base neighbourhood",
                NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.BaseResidential));
            Assert.Equal("Unleashed", NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.Unleashed));
            foreach (ushort mode in new ushort[] { 2, 3, 5, 7 })
            {
                Assert.Null(NeighbourhoodModeMap.TravelTooltip(mode));
            }
        }

        // ------------------------------------------------------------------ the wording

        private static IReadOnlyDictionary<string, string> Table()
        {
            return SimitoneStrings.ParseTable(
                File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory)));
        }

        private static int SpecifierCount(string text)
        {
            return Regex.Matches(text, @"%[sd]").Count + Regex.Matches(text, @"\{\d+\}").Count;
        }

        /// <summary>The ids the map chrome's call sites own, in one place.</summary>
        private static readonly string[] Owned =
        {
            "240", "241", "242", "243", "244", "245", "246", "247", "248", "249",
            "250", "251", "252", "253", "254", "255", "256", "257", "258", "259", "269"
        };

        [Theory]
        [InlineData("240", "Occupied lots")]
        [InlineData("241", "Vacant lots")]
        [InlineData("242", "Residential lots")]
        [InlineData("243", "Community lots")]
        [InlineData("244", "Show only the lots with a family living in them")]
        [InlineData("245", "Show only the lots with nobody living in them")]
        [InlineData("246", "Show only the lots a family can move into")]
        [InlineData("247", "Show only the lots a family cannot move into")]
        [InlineData("248", "Filter lots - click the bar to show them all")]
        [InlineData("249", "Lots shown: %d of %d")]
        [InlineData("250", "Next lot")]
        [InlineData("251", "Previous lot")]
        [InlineData("252", "Add a lot")]
        [InlineData("253", "Add Lot")]
        [InlineData("254", "A lot can only be added from the neighbourhood map.")]
        [InlineData("255", "This map has no lot table, so there is nowhere to put a new lot.")]
        [InlineData("256", "Every lot on this map is in use, so there is nowhere to put another.")]
        [InlineData("257", "Simitone cannot add a lot to a neighbourhood.")]
        [InlineData("258", "The base neighbourhood")]
        [InlineData("259", "Unleashed")]
        [InlineData("269", "No lot on this map matches that filter.")]
        public void TheTableCarriesTheEnglishTheCallSiteFallsBackTo(string id, string english)
        {
            // a missing entry is not an error - SimitoneStrings falls back to the English written
            // at the call site - so without this the page would simply read as untranslated and
            // nothing would fail
            var table = Table();
            Assert.True(table.ContainsKey(id), "No entry for table id " + id + ".");
            Assert.Equal(english, table[id]);
        }

        [Fact]
        public void OnlyTheCountReadoutTakesArguments()
        {
            var table = Table();
            // "Lots shown: %d of %d" is filled with the number on show, then the number the map
            // has - in that order, so a mistranslation that swapped them would still parse
            Assert.Equal(2, SpecifierCount(table["249"]));
            foreach (var id in Owned.Where(x => x != "249"))
            {
                Assert.Equal(0, SpecifierCount(table[id]));
            }
        }

        [Fact]
        public void TheMapChromeSaysTheSameWordsWithNoTableAtAll()
        {
            // a test process never builds GameFacade.Strings, so this is the state a stripped
            // install is in and the case that is actually exercised rather than faked
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Occupied lots", NeighLotFilters.Name(NeighLotFilter.Occupied));
            Assert.Equal("Vacant lots", NeighLotFilters.Name(NeighLotFilter.Vacant));
            Assert.Equal("Residential lots", NeighLotFilters.Name(NeighLotFilter.Residential));
            Assert.Equal("Community lots", NeighLotFilters.Name(NeighLotFilter.Community));
            Assert.Equal("Show only the lots with a family living in them",
                NeighLotFilters.Tooltip(NeighLotFilter.Occupied));
            Assert.Equal("Filter lots - click the bar to show them all", NeighLotFilters.BarTooltip());
            Assert.Equal("No lot on this map matches that filter.", NeighLotFilters.UnavailableTooltip(NeighLotFilter.Occupied));
            Assert.Equal("Lots shown: 2 of 2", NeighLotFilters.CountText(2, 2));
            Assert.Equal("Next lot", NeighLotNavigator.Caption(NeighLotNavigator.Forward));
            Assert.Equal("Previous lot", NeighLotNavigator.Caption(NeighLotNavigator.Backward));
            Assert.Equal("The base neighbourhood",
                NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.BaseResidential));
            Assert.Equal("Unleashed", NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.Unleashed));
        }

        [Fact]
        public void TheCountReadoutIsFormattedTheSameWayWithAndWithoutATable()
        {
            // the substitution is the table's and the fallback's shared risk: they are written with
            // the same specifiers, and a mismatch shows up as "Lots shown: d of d"
            Assert.Equal("Lots shown: 1 of 9", NeighLotFilters.CountText(1, 9));
            Assert.Equal("Lots shown: 100 of 100", NeighLotFilters.CountText(100, 100));
        }
    }
}
