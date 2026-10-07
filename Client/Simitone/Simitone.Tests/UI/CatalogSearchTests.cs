using System.Collections.Generic;
using System.Linq;
using FSO.Content.Interfaces;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Locks the buy/build catalogue's search rules, which live in
    /// <see cref="BuyCatalogService"/> (<see cref="BuyCatalogService.MatchesSearch"/>,
    /// <see cref="BuyCatalogService.SearchCategory"/>,
    /// <see cref="BuyCatalogService.SearchApplies"/>) behind the search box
    /// <c>UIMainPanel</c> draws for the buy and build strips.
    ///
    /// <para>
    /// The box itself needs a live screen, a GraphicsDevice (a <c>UITextBox</c> builds its sliced
    /// background from a game texture) and a loaded world, so none of the interesting part of the
    /// feature can be reached from a test that way. All of the interesting part is a pure function
    /// of a row and a string, and that is what is here.
    /// </para>
    ///
    /// <para>
    /// What is load-bearing and therefore pinned:
    ///  - the match is a case-insensitive <em>substring</em>, not a prefix and not equality, and
    ///    it is tried against three different names per row (see the display-name fixture);
    ///  - an empty or whitespace-only term matches everything, including rows with no name at
    ///    all - that is what makes the box a filter and what lets the panel's hint ("is there
    ///    text?") and its filter agree;
    ///  - the search is scoped to the caller's list. A term that names an item in another
    ///    category cannot reach it, because the panel only ever passes the category it is showing;
    ///  - the rows that keep the result in list order (the strip scrolls by index) and the special
    ///    build rows, which have no guid at all and must therefore survive a search;
    ///  - the one asymmetry with the unsearched list - placeholder rows are dropped by a search
    ///    but not by an empty one - which is inherited from the panel this replaced and is called
    ///    out here so that changing it is a visible decision;
    ///  - when a search is meaningful at all: never in Build mode before a subcategory is open,
    ///    because until then the list on screen belongs to a different catalogue category.
    /// Nothing here touches a GraphicsDevice, <c>Content</c> or audio; rows are synthesised.
    /// </para>
    /// </summary>
    public sealed class CatalogSearchTests
    {
        // ------------------------------------------------------------------ test fixtures

        /// <summary>
        /// The name an object is sold under, which for TS1 exists only in the object's catalogue
        /// strings - <c>TS1ObjectProvider.Init</c> puts the internal OBJD chunk label in
        /// <c>Item.Name</c> and leaves <c>CatalogName</c> null, so this is the only place a
        /// player-recognisable name is to be found. Set on a minority of rows on purpose: a
        /// predicate that only ever looked at this field would pass a happy-path test and fail
        /// for every row whose strings did not load.
        /// </summary>
        private const string SoldAs = "Soma Plasma TV";

        /// <summary>A stand-in for an OBJD chunk label, which is all the TS1 catalogue has.</summary>
        private const string InternalName = "TVExpensive";

        /// <summary>A stand-in for a <c>CatalogName</c>, which only the downloads catalogue fills in.</summary>
        private const string CatalogName = "Cheap Television";

        private static ObjectCatalogItem Item(uint guid, string name, string catalogName = null)
        {
            return new ObjectCatalogItem()
            {
                GUID = guid,
                Name = name,
                CatalogName = catalogName,
            };
        }

        private static UICatalogElement Element(uint guid, string name, string catalogName = null, string displayName = null)
        {
            return new UICatalogElement()
            {
                Item = Item(guid, name, catalogName),
                DisplayName = displayName,
                Special = null
            };
        }

        /// <summary>A row that is a real catalogued object, sold under <see cref="SoldAs"/>.</summary>
        private static UICatalogElement Television() => Element(guid: 0x2A, name: InternalName, displayName: SoldAs);

        /// <summary>
        /// A row that is a floor/wallpaper/roof/wall style/terrain tool: spliced in by
        /// <see cref="BuyCatalogService"/>'s row builders, so it has no guid at all and its name
        /// comes from its resource provider rather than from an object.
        /// </summary>
        private static UICatalogElement SpecialRow(string name) => Element(guid: 0, name: name);

        private static List<UICatalogElement> List(params UICatalogElement[] rows) => rows.ToList();

        /// <summary>Every mode the buy catalogue can be in, build included.</summary>
        private static IEnumerable<UICatalogMode> EveryMode()
        {
            return new[]
            {
                UICatalogMode.Normal,
                UICatalogMode.Downtown,
                UICatalogMode.Community,
                UICatalogMode.Vacation,
                UICatalogMode.Studiotown,
                UICatalogMode.Magictown,
                UICatalogMode.Build,
            };
        }

        // ------------------------------------------------------------------- MatchesSearch

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("\t")]
        [InlineData("   ")]
        public void MatchesSearch_NoUsableTerm_MatchesEveryRow(string term)
        {
            // The box is a filter, not a validator: an empty box shows the category. The whitespace
            // cases matter separately from "" because the field's own emptiness test is
            // IsNullOrWhiteSpace (UITextBox.HasText) - if the two disagreed, a box holding a single
            // space would show the hint text and a filtered strip at the same time.
            Assert.True(BuyCatalogService.MatchesSearch(Television(), term));
            Assert.True(BuyCatalogService.MatchesSearch(SpecialRow("Concrete Floor"), term));
        }

        [Fact]
        public void MatchesSearch_NoUsableTerm_MatchesEvenARowWithNoNameAtAll()
        {
            // Roof rows used to carry "", and a row with nothing to match is still a row the player
            // can see. An empty term must not quietly drop it.
            Assert.True(BuyCatalogService.MatchesSearch(Element(guid: 7, name: null), ""));
        }

        [Theory]
        [InlineData("tv")]
        [InlineData("TV")]
        [InlineData("tV")]
        [InlineData("Tv")]
        public void MatchesSearch_Term_IsACaseInsensitiveSubstringOfTheSoldAsName(string term)
        {
            Assert.True(BuyCatalogService.MatchesSearch(Television(), term));
        }

        [Theory]
        [InlineData("soma")]
        [InlineData("plasma")]
        [InlineData("plasma tv")]
        [InlineData("Soma Plasma TV")]
        [InlineData("a plasma t")]
        public void MatchesSearch_Term_MatchesAnyPartOfTheNameNotJustItsStart(string term)
        {
            // Substring, not prefix: a player who remembers "plasma" but not "Soma" has to find it.
            Assert.True(BuyCatalogService.MatchesSearch(Television(), term));
        }

        [Fact]
        public void MatchesSearch_Term_IsNotTrimmedAwayFromTheName()
        {
            // Deliberately not a trim-and-retry: the term is matched as typed, so what the player
            // sees in the box is what is being searched for. (The one concession to stray input is
            // that a whitespace-ONLY term is treated as no term at all, pinned above.)
            Assert.False(BuyCatalogService.MatchesSearch(Television(), " tv "));
        }

        [Fact]
        public void MatchesSearch_SoldAsNamePresent_MatchesOnItRatherThanOnTheInternalName()
        {
            // The whole reason the panel resolves display names at all: this row's catalogue name is
            // "TVExpensive", and a player searching for the television must not have to know that.
            var row = Television();

            Assert.True(BuyCatalogService.MatchesSearch(row, "plasma"));
            Assert.True(BuyCatalogService.MatchesSearch(row, "TVExpensive")); //still searchable, just not required
        }

        [Fact]
        public void MatchesSearch_DisplayNameIsNull_FallsBackToTheCatalogueRowName()
        {
            // Objects whose content failed to load have no display name (see
            // BuyCatalogService.DisplayNameFor). They must still be findable by their row name
            // rather than becoming invisible to the only search in the game.
            var row = Element(guid: 0x2A, name: InternalName, displayName: null);

            Assert.True(BuyCatalogService.MatchesSearch(row, "expensive"));
        }

        [Fact]
        public void MatchesSearch_OnlyCatalogNameIsSet_MatchesOnIt()
        {
            var row = Element(guid: 0x2A, name: InternalName, catalogName: CatalogName, displayName: null);

            Assert.True(BuyCatalogService.MatchesSearch(row, "cheap"));
        }

        [Fact]
        public void MatchesSearch_DisplayNameIsEmptyString_StillFallsThroughToTheOtherNames()
        {
            // An empty display name is "no name", not a name. UICatalogRoofResProvider returns ""
            // for every id, and a predicate that stopped at the first non-null field would make
            // every roof row unfindable by the name the row builder already gave it.
            var row = Element(guid: 0, name: "Ridgeline Roof", displayName: "");

            Assert.True(BuyCatalogService.MatchesSearch(row, "ridgeline"));
        }

        [Fact]
        public void MatchesSearch_RowWithNoNameAnywhere_IsNotAMatchForARealTerm()
        {
            // The complement of the empty-term rule: with something to match, a nameless row cannot
            // be a hit. It is not an error, just not a result.
            Assert.False(BuyCatalogService.MatchesSearch(Element(guid: 7, name: null), "tv"));
        }

        [Fact]
        public void MatchesSearch_NullRow_ReturnsFalseRatherThanThrowing()
        {
            // The panel's list can hold a null row if a category's contents are odd, and a
            // keystroke must never be the thing that finds out.
            Assert.False(BuyCatalogService.MatchesSearch(null, "tv"));
        }

        [Fact]
        public void MatchesSearch_TermIsNullOrEmptyOnEveryName_DoesNotThrow()
        {
            // Null-name rows are the common case for the build tools, and IndexOf on a null
            // reference is the classic way a search box takes a panel down on the first keypress.
            Assert.False(BuyCatalogService.MatchesSearch(Element(guid: 1, name: null, catalogName: null, displayName: null), "x"));
            Assert.False(BuyCatalogService.MatchesSearch(Element(guid: 1, name: "", catalogName: "", displayName: ""), "x"));
        }

        [Fact]
        public void MatchesSearch_SpecialRowWithNoGuid_IsStillSearchable()
        {
            // Floors, wallpapers, roofs, wall styles and terrain tools have no guid - they are not
            // objects. A guid-based rule that dropped them would delete the entire architecture
            // half of the build catalogue from search.
            var row = SpecialRow("Volcanic Rock");

            Assert.True(BuyCatalogService.MatchesSearch(row, "volcanic"));
        }

        // ------------------------------------------------------------------ SearchCategory

        [Fact]
        public void SearchCategory_NoUsableTerm_ReturnsTheCallersOwnListUnchanged()
        {
            var full = List(Television(), SpecialRow("Concrete Floor"));

            var result = BuyCatalogService.SearchCategory(full, "");

            Assert.Same(full, result);
        }

        [Fact]
        public void SearchCategory_NullList_ReturnsNothingRatherThanThrowing()
        {
            // A panel whose category has not been built yet (or a build subcategory on its way out)
            // must not fault a keystroke.
            Assert.Empty(BuyCatalogService.SearchCategory(null, "tv"));
        }

        [Fact]
        public void SearchCategory_KeepsTheCategorysOwnOrder()
        {
            // The strip scrolls and selects by index, so the result set has to stay in the order
            // InitCategory sorted it into - which is price order - or a row jumps when the player
            // types a character.
            var full = List(
                Element(guid: 1, name: "Cheap Sofa"),
                Element(guid: 2, name: "Soma Plasma TV", displayName: SoldAs),
                Element(guid: 3, name: "Expensive Sofa"));

            var guids = BuyCatalogService.SearchCategory(full, "sofa")
                .Select(x => x.Item.GUID)
                .ToArray();

            Assert.Equal(new uint[] { 1, 3 }, guids);
        }

        [Fact]
        public void SearchCategory_KeepsSpecialRowsAlongsideObjectRows()
        {
            // Architecture sits in the same strip as catalogued objects, and the two have different
            // shapes (no guid for the specials). Filtering must not lose either.
            var full = List(
                SpecialRow("Volcanic Rock Floor"),
                Element(guid: 9, name: "Rock Chair"));

            var names = BuyCatalogService.SearchCategory(full, "rock")
                .Select(x => x.ToString())
                .ToArray();

            Assert.Equal(new[] { "Volcanic Rock Floor", "Rock Chair" }, names);
        }

        [Fact]
        public void SearchCategory_SkipsNullRowsWithoutFailingTheSearch()
        {
            var full = List(null, Television(), null);

            var result = BuyCatalogService.SearchCategory(full, "plasma").ToList();

            Assert.Single(result);
        }

        [Fact]
        public void SearchCategory_IsScopedToTheCallersCategory()
        {
            // The scoping is structural rather than a filter: the panel passes the one category it
            // is showing, and switching category replaces the panel (and clears the box). Asserted
            // from both sides so that adding a "search the whole catalogue" shortcut later is a
            // visible change to this test rather than a silent one.
            var electronics = List(Television());
            var seating = List(Element(guid: 40, name: "Soma Plasma TV"));

            var fromElectronics = BuyCatalogService.SearchCategory(electronics, "plasma").ToList();
            var fromSeating = BuyCatalogService.SearchCategory(seating, "plasma").ToList();

            Assert.Single(fromElectronics);
            Assert.DoesNotContain(40, fromElectronics.Select(x => (int)x.Item.GUID));
            Assert.Single(fromSeating);
            Assert.DoesNotContain((int)Television().Item.GUID, fromSeating.Select(x => (int)x.Item.GUID));
        }

        [Fact]
        public void SearchCategory_TermMatchesNothing_ReturnsAnEmptyList()
        {
            // The panel shows its "no items found" label off this, and the strip is indexed by the
            // result count, so the answer has to be a real (empty) sequence and not a null.
            var full = List(Television(), SpecialRow("Concrete Floor"));

            var result = BuyCatalogService.SearchCategory(full, "zzzz");

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void SearchCategory_TermIsAlwaysAppliedToTheWholeCategoryNotToTheLastResult()
        {
            // THE REASON THE PANEL SNAPSHOTS ITS PRE-SEARCH LIST. Typing "tele" and then backspacing
            // to "t" must show everything containing a "t" - including "Table", which the narrower
            // term hid. That only works because the panel re-filters the whole category each
            // keystroke instead of narrowing its own last answer; asserted here from both sides, so
            // that chaining the filter onto its own output (the bug) is a visible failure.
            var full = List(
                Element(guid: 1, name: "Television"),
                Element(guid: 2, name: "Table"),
                Element(guid: 3, name: "Sofa"));

            var narrowed = BuyCatalogService.SearchCategory(full, "tele").ToList();
            var typedThenBackspaced = BuyCatalogService.SearchCategory(full, "t").ToList();
            var accumulated = BuyCatalogService.SearchCategory(narrowed, "t").ToList();

            Assert.Equal(new uint[] { 1 }, narrowed.Select(x => x.Item.GUID).ToArray());
            //same answer as typing "t" once, in the same order
            Assert.Equal(
                BuyCatalogService.SearchCategory(full, "t").Select(x => x.Item.GUID),
                typedThenBackspaced.Select(x => x.Item.GUID));
            //whereas accumulating lost the row for good: "Table" contained a "t" all along
            Assert.DoesNotContain(2U, accumulated.Select(x => x.Item.GUID));
        }

        [Fact]
        public void SearchCategory_DropsRowsWithNoBuyableObjectButAnEmptyTermKeepsThem()
        {
            // PRE-EXISTING ASYMMETRY, PINNED DELIBERATELY.
            // A handful of TS1 catalogue rows carry guid 0xFFFFFFFF: a price and a name, but no
            // object behind them, so selecting one does nothing at all. They stay in the unsearched
            // strip (that is what the strip has always shown) and are dropped from a search result.
            // Changing either half of that is a deliberate decision, not a tidy-up.
            var placeholder = Element(guid: uint.MaxValue, name: "Placeholder");
            var real = Television();
            var full = List(placeholder, real);

            Assert.Equal(2, BuyCatalogService.SearchCategory(full, "").Count());

            var searched = BuyCatalogService.SearchCategory(full, "t").ToList();
            Assert.DoesNotContain(searched, x => x.Item.GUID == uint.MaxValue);
            Assert.Contains(searched, x => x.Item.GUID == real.Item.GUID);
        }

        // -------------------------------------------------------------------- SearchApplies

        [Fact]
        public void SearchApplies_BuildModeBeforeASubcategoryIsOpen_IsFalse()
        {
            // Each build subcategory is really a different catalogue category, and
            // UIBuyBrowsePanel.OpenSubcategory only loads it on the subcategory's own click. Until
            // then the list on screen is the buy-mode category the mode switcher landed on, so
            // searching it would answer a question about the wrong shelf.
            Assert.False(BuyCatalogService.SearchApplies(UICatalogMode.Build, choosingSub: true));
        }

        [Fact]
        public void SearchApplies_BuildModeAfterASubcategoryIsOpen_IsTrue()
        {
            Assert.True(BuyCatalogService.SearchApplies(UICatalogMode.Build, choosingSub: false));
        }

        [Theory]
        [InlineData(UICatalogMode.Normal)]
        [InlineData(UICatalogMode.Downtown)]
        [InlineData(UICatalogMode.Community)]
        [InlineData(UICatalogMode.Vacation)]
        [InlineData(UICatalogMode.Studiotown)]
        [InlineData(UICatalogMode.Magictown)]
        public void SearchApplies_EveryBuyMode_IsTrueWhicheverSubcategoryState(UICatalogMode mode)
        {
            // Buy mode's category list is the right list from the moment the panel opens, so the
            // box is live both on the subcategory buttons and once one has been chosen - which is
            // also what lets a player search a whole category without picking a tab first.
            Assert.True(BuyCatalogService.SearchApplies(mode, choosingSub: true));
            Assert.True(BuyCatalogService.SearchApplies(mode, choosingSub: false));
        }

        [Fact]
        public void SearchApplies_BuildModeIsTheOnlyModeWithADeadState()
        {
            var dead = EveryMode()
                .Where(mode => !BuyCatalogService.SearchApplies(mode, choosingSub: true))
                .ToArray();

            Assert.Equal(new[] { UICatalogMode.Build }, dead);
        }

        // ------------------------------------------------------------- UICatalogElement naming

        [Fact]
        public void ToString_PrefersTheNameTheRowIsSoldUnder()
        {
            // UICatalogElement.ToString is what a row's label falls back to wherever the panel does
            // not have a name of its own, so it has to answer with the name the player knows.
            Assert.Equal(SoldAs, Television().ToString());
        }

        [Fact]
        public void ToString_WithoutADisplayName_FallsBackToTheRowName()
        {
            Assert.Equal(InternalName, Element(guid: 1, name: InternalName, displayName: null).ToString());
        }

        [Fact]
        public void ToString_WithNoNameAtAll_ReadsAsUnknownRatherThanThrowing()
        {
            Assert.Equal("(unknown)", Element(guid: 1, name: null, displayName: null).ToString());
        }

        [Fact]
        public void AnUnresolvedRowStartsWithNoDisplayName()
        {
            // A row whose name has not been read yet reports none rather than a guessed-at label,
            // and reports that it is unresolved so a caller can tell the two apart. If reading the
            // field defaulted to something, ToString could not tell "no name" from "the catalogue
            // name", and a search would be matching on a value nothing ever set. (The lazy
            // resolution itself - one read, remembered, including when it finds nothing - is
            // pinned by CatalogNameLoadingTests.)
            var row = Element(guid: 1, name: InternalName, displayName: null);

            Assert.False(row.DisplayNameResolved);
        }
    }
}
