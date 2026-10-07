using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.Interfaces;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Locks the "intelligent loading" half of the buy/build catalogue: when a row's name is
    /// resolved, how many times, and what the strip gets handed to index and count.
    ///
    /// <para>
    /// The two rules under test are the ones that only show up with a large custom content set,
    /// because on a stock install neither can go wrong:
    /// </para>
    ///
    /// <para>
    /// <b>1. A row's name is resolved at most once, and only if something actually needs it.</b>
    /// A row's player-facing name lives in the object's catalogue strings, so producing it is a
    /// file read. This used to be done for every row of a category the moment the category was
    /// built - a few hundred rows on a stock install, several thousand on a heavy custom set, all
    /// of it paid up front for rows the player might never scroll to. It is now lazy
    /// (<see cref="UICatalogElement.DisplayName"/>) and memoised, including the memo for a
    /// <em>failed</em> lookup: a row whose content is broken is precisely the row that would
    /// otherwise re-read, and re-throw, on every frame it is on screen.
    /// </para>
    ///
    /// <para>
    /// <b>2. The strip's row list is a list, not a deferred sequence.</b> The scroll container asks
    /// for a row count every frame and for a row's element by index for each row it draws; a
    /// deferred <c>Where</c> re-runs its whole predicate chain for every one of those, which is
    /// invisible on a small category and quadratic-feeling on a large one. The guarantee is now in
    /// the signature of <see cref="BuyCatalogService.SearchCategory"/> rather than in a comment, and
    /// it is asserted as a type.
    /// </para>
    ///
    /// <para>
    /// Resolution is reached through <see cref="UICatalogElement.DisplayNameSource"/> rather than
    /// through <c>Content</c>, so none of this needs a loaded world: rows are synthesised and
    /// given a counting stand-in for the file read. No GraphicsDevice either.
    /// </para>
    /// </summary>
    public sealed class CatalogNameLoadingTests
    {
        // ------------------------------------------------------------------ test fixtures

        private const string SoldAs = "Soma Plasma TV";
        private const string InternalName = "TVExpensive";

        /// <summary>A row as the catalogue hands it over: an internal name and no display name.</summary>
        private static UICatalogElement Row(uint guid, string name, string catalogName = null)
        {
            return new UICatalogElement()
            {
                Item = new ObjectCatalogItem() { GUID = guid, Name = name, CatalogName = catalogName },
            };
        }

        /// <summary>
        /// A row whose name is read through a stand-in for the file read, counting the reads.
        /// </summary>
        private static UICatalogElement CountedRow(
            uint guid, string name, string answer, string catalogName = null, Action onRead = null)
        {
            var row = Row(guid, name, catalogName);
            row.DisplayNameSource = _ =>
            {
                onRead?.Invoke();
                return answer;
            };
            return row;
        }

        // ------------------------------------------------------- laziness: nothing read early

        [Fact]
        public void BuildingARow_ReadsNothing()
        {
            var reads = 0;
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: SoldAs, onRead: () => reads++);

            // Constructing the row is all the category builder does per row. If this ever reads
            // anything, the laziness has been undone and every tab open pays for the whole
            // category again.
            Assert.Equal(0, reads);
            Assert.False(row.DisplayNameResolved);
        }

        [Fact]
        public void BuildingACategoryOfRows_ReadsNothingUntilSomethingAsks()
        {
            // The stock-install case, scaled up: 4000 rows, which is an ordinary size for a
            // heavily-customised content set. Opening the Electronics tab used to do 4000 file
            // reads here; it now does none.
            var reads = 0;
            var full = Enumerable.Range(0, 4000)
                .Select(i => CountedRow(guid: (uint)i, name: $"Item{i}", answer: $"Item {i}", onRead: () => reads++))
                .ToList();

            Assert.Equal(0, reads);

            //Draws the ~12 rows a strip actually shows.
            for (int i = 0; i < 12; i++)
            {
                Assert.Equal($"Item {i}", full[i].DisplayName);
            }

            Assert.Equal(12, reads);
        }

        // ------------------------------------------------------------- laziness: read once only

        [Fact]
        public void ANameIsReadOnFirstUseAndThenRememberedForever()
        {
            var reads = 0;
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: SoldAs, onRead: () => reads++);

            Assert.Equal(SoldAs, row.DisplayName);
            Assert.True(row.DisplayNameResolved);

            //Everything the panel does to a row afterwards. Each of these would be another file
            //read if the answer were not remembered.
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(SoldAs, row.DisplayName);
                Assert.Equal(SoldAs, row.ToString());
                Assert.True(BuyCatalogService.MatchesSearch(row, "plasma"));
            }

            Assert.Equal(1, reads);
        }

        [Fact]
        public void RepeatedSearchesOverOneCategory_OnlyEverReadEachRowOnce()
        {
            // The reason the memo is on the row rather than around the search: the box is filtered
            // on every keystroke, and every keystroke walks every row in the category.
            var reads = 0;
            var full = Enumerable.Range(0, 50)
                .Select(i => CountedRow(
                    guid: (uint)i,
                    name: i % 2 == 0 ? $"Sofa{i}" : $"Table{i}",
                    answer: i % 2 == 0 ? $"Deluxe Sofa {i}" : $"Dining Table {i}",
                    onRead: () => reads++))
                .ToList();

            BuyCatalogService.SearchCategory(full, "s");   //first keystroke pays for the category
            var afterFirst = reads;
            BuyCatalogService.SearchCategory(full, "so");
            BuyCatalogService.SearchCategory(full, "sof");
            BuyCatalogService.SearchCategory(full, "sofa");
            BuyCatalogService.SearchCategory(full, "s");   //and backspacing is not a refund

            Assert.Equal(full.Count, afterFirst);
            Assert.Equal(afterFirst, reads);
        }

        [Fact]
        public void ARowThatCannotBeNamed_IsRememberedAsNamelessRatherThanRetried()
        {
            // A broken custom-content file is the case that matters here. The lookup throws; if
            // the row kept asking, the buy panel would re-read (and re-log, and re-throw) the same
            // dead file on every frame it was drawn.
            var reads = 0;
            var row = Row(guid: 0x2A, name: InternalName);
            row.DisplayNameSource = _ =>
            {
                reads++;
                throw new InvalidOperationException("no such catalogue strings");
            };

            Assert.Null(row.DisplayName);
            Assert.Null(row.DisplayName);
            Assert.Equal(InternalName, row.ToString()); //still labels itself, from the OBJD name
            Assert.Equal(1, reads);
        }

        [Fact]
        public void ARowWithNoNameAtAll_IsAlsoRememberedRatherThanRetried()
        {
            // Same rule, quieter case: the lookup answers null instead of throwing. A null answer
            // has to stick for exactly the same reason.
            var reads = 0;
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: null, onRead: () => reads++);

            Assert.Null(row.DisplayName);
            Assert.Null(row.DisplayName);
            Assert.Equal(1, reads);
        }

        [Fact]
        public void AnAssignedNameIsUsedAsIsAndIsNeverRead()
        {
            // A caller that already knows the name (a cache, a name table shipped with a content
            // set) sets it, and the row must not then go and read one anyway.
            var reads = 0;
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: "from content", onRead: () => reads++);

            row.DisplayName = "from the caller";

            Assert.Equal("from the caller", row.DisplayName);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void AssigningNullMeansUnresolved_NotThatThereIsNoName()
        {
            // The distinction the object-initializer syntax makes easy to get wrong. "I have not
            // told you the name" and "this row has no name" are different claims, and only the
            // first one is worth asking about.
            var reads = 0;
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: SoldAs, onRead: () => reads++);

            row.DisplayName = null;

            Assert.False(row.DisplayNameResolved);
            Assert.Equal(SoldAs, row.DisplayName);
            Assert.Equal(1, reads);
        }

        // ------------------------------------------------------------- what a name is allowed to be

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AnEmptyAnswerIsNoNameRatherThanAnEmptyLabel(string candidate)
        {
            Assert.Null(BuyCatalogService.NormalizeName(candidate));
        }

        [Fact]
        public void ARealAnswerIsPassedThroughUnchanged()
        {
            Assert.Equal(SoldAs, BuyCatalogService.NormalizeName(SoldAs));

            //not trimmed, not case-folded: this is a label, and the strip draws it verbatim.
            Assert.Equal("  Soma Plasma TV  ", BuyCatalogService.NormalizeName("  Soma Plasma TV  "));
        }

        [Fact]
        public void AnEmptyAnswerDoesNotSuppressTheRowsOwnName()
        {
            // UICatalogRoofResProvider returns "" for every roof id. ToString falls back with ??
            // rather than IsNullOrEmpty, so a row left holding "" would draw blank even though the
            // row builder gave it a perfectly good name. This is the case that rule exists for.
            var row = CountedRow(guid: 0, name: "Ridgeline Roof", answer: "");

            Assert.Null(row.DisplayName);
            Assert.Equal("Ridgeline Roof", row.ToString());
        }

        [Fact]
        public void AnEmptyAnswerFallsBackToTheCatalogueName()
        {
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: "", catalogName: "Cheap Television");

            Assert.Equal("Cheap Television", row.DisplayName);
        }

        [Fact]
        public void NothingToReadFrom_ReadsAsNoNameRatherThanThrowing()
        {
            // The stock test process has no Content, which is exactly the "there is nothing here
            // to read" case: a row whose lookup finds no content is a row with no display name,
            // and it still lists and still searches on its OBJD name.
            var row = Row(guid: 0x2A, name: InternalName);

            Assert.Null(row.DisplayName);
            Assert.True(row.DisplayNameResolved);
            Assert.Equal(InternalName, row.ToString());
            Assert.True(BuyCatalogService.MatchesSearch(row, "expensive"));
        }

        [Fact]
        public void DisplayNameForIgnoresTheSourceWhenThereIsNoRow()
        {
            Assert.Null(BuyCatalogService.DisplayNameFor(null, _ => SoldAs));
        }

        [Fact]
        public void SearchStillMatchesTheNameTheRowIsSoldUnder()
        {
            // The whole point of resolving names at all, and the reason the lazy pass was kept in
            // the search rather than removed from it: a player searching for the television must
            // not have to know that the catalogue calls it "TVExpensive".
            var row = CountedRow(guid: 0x2A, name: InternalName, answer: SoldAs);

            Assert.True(BuyCatalogService.MatchesSearch(row, "plasma"));
            Assert.True(BuyCatalogService.MatchesSearch(row, "TVExpensive"));
        }

        // --------------------------------------------------------- the strip's list is a list

        [Fact]
        public void ASearchResultIsIndexableAndCountableInConstantTime()
        {
            // The scroll container asks for Count() every frame and ElementAt(index) for every row
            // it draws. An IList is the only shape that answers both without re-running the
            // predicate, so the guarantee is asserted as a type rather than hoped for.
            var full = Enumerable.Range(0, 100)
                .Select(i => Row(guid: (uint)i, name: $"Sofa{i}"))
                .ToList();

            var result = BuyCatalogService.SearchCategory(full, "sofa");

            Assert.IsAssignableFrom<IList<UICatalogElement>>(result);
            Assert.Equal(100, result.Count);
            Assert.Equal(0U, result[0].Item.GUID);
            Assert.Equal(99U, result[99].Item.GUID);
        }

        [Fact]
        public void AResultIsIndependentOfTheListItCameFrom()
        {
            // The strip holds the result while the category is on screen, and a search is
            // re-run against the category on every keystroke. A result that is a view onto the
            // list it was filtered from would be a second, differently-ordered list of the same
            // rows - so it is a copy.
            var full = new List<UICatalogElement> { Row(1, "Sofa"), Row(2, "Table") };

            var result = BuyCatalogService.SearchCategory(full, "sofa");
            full.Clear();

            Assert.Single(result);
            Assert.Equal("Sofa", result[0].Item.Name);
        }

        [Fact]
        public void AnEmptyResultIsARealEmptyListRatherThanNull()
        {
            // The panel shows its "no items found" label off Count and the strip reads Count too,
            // so the answer has to be an empty list and not a null.
            var result = BuyCatalogService.SearchCategory(new List<UICatalogElement> { Row(1, "Sofa") }, "zzzz");

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void AnEmptyTermStillHandsBackTheCallersOwnList()
        {
            // Unchanged behaviour, and still worth pinning now that the return type is part of the
            // contract: no term means "show the category", with nothing copied and nothing dropped.
            var full = new List<UICatalogElement> { Row(1, "Sofa") };

            Assert.Same(full, BuyCatalogService.SearchCategory(full, ""));
            Assert.Same(full, BuyCatalogService.SearchCategory(full, "   "));
        }

        [Fact]
        public void AnEmptyResultListIsStillSafeToIndexAndCount()
        {
            // The panel's per-frame row count must not have to special-case the empty case.
            var result = BuyCatalogService.SearchCategory(new List<UICatalogElement>(), "sofa");

            Assert.Equal(0, result.Count);
            Assert.False(result.Any());
        }
    }
}
