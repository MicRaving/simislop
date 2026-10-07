using System.Collections.Generic;
using System.Linq;
using FSO.Content.Interfaces;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Locks the buy/build catalogue's subcategory query rules, which now live in
    /// <see cref="CatalogQueryService"/>. They were previously inline instance methods on
    /// <see cref="UIBuyBrowsePanel"/> reading the panel's <c>Mode</c> and <c>FullCategory</c> fields,
    /// which made them impossible to exercise without a live UI: a whole GraphicsDevice and a
    /// content-pipeline world catalogue just to assert a bit test.
    ///
    /// These are pure functions over plain data, so they are asserted here directly. What is
    /// load-bearing and therefore pinned:
    ///  - the per-lot-type sort column each mode reads, and the "no RoomSort means invisible" rule;
    ///  - the special cases for Build mode and for the shared "all" button (mask bit 8);
    ///  - a genuine, pre-existing asymmetry between the "is this button dead" question
    ///    (<c>SubcatIsEmpty</c>) and the "what does it show" question (<c>Filter</c>) for Normal mode
    ///    mask bit 7. It is upstream FreeSO behaviour, is visible to players, and is NOT a bug this
    ///    refactor is allowed to change - so it is asserted exactly as it stands, and
    ///    <c>FilterNormalModeMaskSeven_OrsInBitSixteenThatSubcatIsEmptyIgnores</c> is the tripwire
    ///    that makes any future "cleanup" of that asymmetry a deliberate, visible decision.
    /// Nothing here touches a GraphicsDevice, Content or audio; items are synthesised structs.
    /// </summary>
    public sealed class CatalogQueryTests
    {
        // An item with every sort column deliberately distinct, so a theory that reads the wrong
        // column cannot accidentally pass.
        private const byte AllDistinct = 0;

        // ------------------------------------------------------------------- test fixtures

        /// <summary>A minimal catalogue item: only the sort fields the query rules actually read.</summary>
        private static ObjectCatalogItem Item(byte roomSort, byte subsort, uint guid = 0)
        {
            return new ObjectCatalogItem()
            {
                GUID = guid,
                RoomSort = roomSort,
                Subsort = subsort,
            };
        }

        /// <summary>Same as <see cref="Item"/> but with each lot type's own sort column filled in.</summary>
        private static ObjectCatalogItem ItemForAllLotTypes(byte roomSort, byte subsort, uint guid = 0)
        {
            // The lot-type columns are deliberately a different value from Subsort: the per-mode
            // theory below then proves each mode reads its own column rather than a shared one.
            var lotType = (byte)(subsort ^ 0x03);
            return new ObjectCatalogItem()
            {
                GUID = guid,
                RoomSort = roomSort,
                Subsort = subsort,
                DowntownSort = lotType,
                CommunitySort = lotType,
                VacationSort = lotType,
                StudiotownSort = lotType,
                MagictownSort = lotType,
            };
        }

        private static UICatalogElement Element(ObjectCatalogItem item)
        {
            return new UICatalogElement()
            {
                Item = item,
                CalcPrice = (int)item.Price,
                Special = null
            };
        }

        private static UICatalogSubcat Subcat(int maskBit)
        {
            return new UICatalogSubcat()
            {
                MaskBit = maskBit,
                StrTable = 150,
                StrInd = 0,
                IconName = "unset"
            };
        }

        private static List<UICatalogElement> Elements(params ObjectCatalogItem[] items)
        {
            return items.Select(Element).ToList();
        }

        /// <summary>The mask bits that carry a normal-mode subcategory, plus the shared "all" (8).</summary>
        private static IEnumerable<int> EveryMaskBit()
        {
            return Enumerable.Range(0, 9);
        }

        /// <summary>Every mode that actually filters by mask. Build is excluded: it ignores MaskBit.</summary>
        private static IEnumerable<UICatalogMode> EveryFilteringMode()
        {
            return new[]
            {
                UICatalogMode.Normal,
                UICatalogMode.Downtown,
                UICatalogMode.Community,
                UICatalogMode.Vacation,
                UICatalogMode.Studiotown,
                UICatalogMode.Magictown,
            };
        }

        /// <summary>Subsort 16 with no bit 7 - the one value the two query methods disagree about.</summary>
        private static List<UICatalogElement> BitSixteenOnlyItem()
        {
            return Elements(Item(roomSort: 1, subsort: 0b0001_0000, guid: 4));
        }

        // ---------------------------------------------------------------------------- GetSubsort

        [Theory]
        [InlineData(UICatalogMode.Downtown, (byte)11)]
        [InlineData(UICatalogMode.Community, (byte)22)]
        [InlineData(UICatalogMode.Vacation, (byte)33)]
        [InlineData(UICatalogMode.Studiotown, (byte)44)]
        [InlineData(UICatalogMode.Magictown, (byte)55)]
        public void GetSubsort_LotTypeMode_ReturnsThatLotTypesOwnSortColumn(UICatalogMode mode, byte expected)
        {
            // Every lot type has a dedicated column in the object catalogue; using Subsort here
            // would reshuffle every button on every apartment-style lot.
            var item = new ObjectCatalogItem()
            {
                GUID = 1,
                RoomSort = 1,
                Subsort = AllDistinct,
                DowntownSort = 11,
                CommunitySort = 22,
                VacationSort = 33,
                StudiotownSort = 44,
                MagictownSort = 55,
            };

            Assert.Equal(expected, CatalogQueryService.GetSubsort(item, mode));
        }

        [Theory]
        [InlineData(UICatalogMode.Normal)]
        [InlineData(UICatalogMode.Build)]
        public void GetSubsort_NormalOrBuildMode_ReturnsSubsort(UICatalogMode mode)
        {
            // Both fall through to the default branch of the switch.
            var item = Item(roomSort: 1, subsort: 0b1000_0011);

            Assert.Equal(0b1000_0011, CatalogQueryService.GetSubsort(item, mode));
        }

        [Theory]
        [InlineData(UICatalogMode.Normal)]
        [InlineData(UICatalogMode.Build)]
        public void GetSubsort_NormalOrBuildMode_ItemWithNoRoomSort_ReturnsZero(UICatalogMode mode)
        {
            // "items without a room sort should not appear" - zeroing the sub-sort is what hides
            // them, because every subcategory filter is a > 0 test.
            var item = Item(roomSort: 0, subsort: 0b0000_0111);

            Assert.Equal(0, CatalogQueryService.GetSubsort(item, mode));
        }

        [Theory]
        [InlineData(UICatalogMode.Downtown)]
        [InlineData(UICatalogMode.Community)]
        [InlineData(UICatalogMode.Vacation)]
        [InlineData(UICatalogMode.Studiotown)]
        [InlineData(UICatalogMode.Magictown)]
        public void GetSubsort_LotTypeMode_ItemWithNoRoomSort_StillReturnsItsLotTypeColumn(UICatalogMode mode)
        {
            // The RoomSort == 0 guard sits only in the default branch. Apartment objects are sorted
            // by a lot-type column and legitimately have no RoomSort, so applying the guard to them
            // would blank whole categories.
            var item = ItemForAllLotTypes(roomSort: 0, subsort: 0b0000_0111);

            Assert.NotEqual(0, CatalogQueryService.GetSubsort(item, mode));
        }

        // ----------------------------------------------------------------------- SubcatIsEmpty

        [Fact]
        public void SubcatIsEmpty_EmptyCategory_ReturnsTrueForEveryModeAndMaskBit()
        {
            // The greyed-out-button decision must never throw on an empty category, whatever the
            // mask bit. Build reaches it via FirstOrDefault() == null, the rest via !Any().
            foreach (var mode in EveryFilteringMode().Concat(new[] { UICatalogMode.Build }))
            {
                foreach (var maskBit in EveryMaskBit().Concat(new[] { 42 }))
                {
                    Assert.True(
                        CatalogQueryService.SubcatIsEmpty(Subcat(maskBit), mode, new List<UICatalogElement>()),
                        $"{mode} mask {maskBit} should read as empty for an empty category");
                }
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void SubcatIsEmpty_NormalModeBitMask_NoItemHasThatBit_ReturnsTrue(int maskBit)
        {
            // Subsorts 1, 2 and 3 are present, so only bits 0 and 1 are set; bits 2..7 are not.
            var full = Elements(
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0010, guid: 2),
                Item(roomSort: 1, subsort: 0b0000_0011, guid: 3));

            Assert.True(CatalogQueryService.SubcatIsEmpty(Subcat(maskBit), UICatalogMode.Normal, full));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(6)]
        public void SubcatIsEmpty_NormalModeBitMask_AnItemHasThatBit_ReturnsFalse(int maskBit)
        {
            var full = Elements(
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 1),
                Item(roomSort: 1, subsort: 0b0100_0000, guid: 2));

            Assert.False(CatalogQueryService.SubcatIsEmpty(Subcat(maskBit), UICatalogMode.Normal, full));
        }

        [Fact]
        public void SubcatIsEmpty_MaskEight_EverySubsortIsZero_ReturnsTrue()
        {
            // Mask bit 8 is the shared "all" button. It is a bare GetSubsort > 0 test, not a bit
            // test, so a category of invisible items must grey it out.
            var full = Elements(
                Item(roomSort: 1, subsort: 0, guid: 1),
                Item(roomSort: 0, subsort: 0b1111_1111, guid: 2)); //no RoomSort -> hidden

            Assert.True(CatalogQueryService.SubcatIsEmpty(Subcat(8), UICatalogMode.Normal, full));
        }

        [Fact]
        public void SubcatIsEmpty_MaskEight_AnySubsortIsPositive_ReturnsFalse()
        {
            // One visible item is enough, whatever its bit.
            var full = Elements(
                Item(roomSort: 1, subsort: 0, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 2));

            Assert.False(CatalogQueryService.SubcatIsEmpty(Subcat(8), UICatalogMode.Normal, full));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(42)]
        public void SubcatIsEmpty_BuildMode_AnyItemExists_ReturnsFalseRegardlessOfMaskBit(int maskBit)
        {
            // Build buttons are enabled off the whole category, never off a mask: a build
            // "subcategory" is a build category id, not a sub-sort bit, and is re-resolved by
            // InitCategory when the button is pressed.
            var full = Elements(Item(roomSort: 1, subsort: 0, guid: 1));

            Assert.False(CatalogQueryService.SubcatIsEmpty(Subcat(maskBit), UICatalogMode.Build, full));
        }

        [Fact]
        public void SubcatIsEmpty_NormalModeMaskSeven_ItemWithOnlyBitSixteen_StillReadsAsEmpty()
        {
            // PRE-EXISTING DIVERGENCE, PINNED DELIBERATELY.
            // SubcatIsEmpty uses a plain 1 << 7, so a sub-sort of 16 alone does not enable the
            // "other" button. Filter ORs in bit 16 for this exact case, so the same item does get
            // listed. The result is a greyed-out button whose contents are not empty. This is
            // upstream behaviour; the refactor must preserve it rather than reconcile it.
            var full = BitSixteenOnlyItem();

            Assert.True(CatalogQueryService.SubcatIsEmpty(Subcat(7), UICatalogMode.Normal, full));
        }

        [Fact]
        public void SubcatIsEmpty_LotTypeModeMaskSeven_ItemWithOnlyBitSixteen_AlsoReadsAsEmpty()
        {
            // The divergence above is Normal-only: the lot-type modes never OR in bit 16, in either
            // method, so the "other" button there is genuinely empty for a bit-16-only category.
            var full = Elements(ItemForAllLotTypes(roomSort: 1, subsort: 0b0001_0000, guid: 4));

            Assert.True(CatalogQueryService.SubcatIsEmpty(Subcat(7), UICatalogMode.Downtown, full));
        }

        // ------------------------------------------------------------------------------- Filter

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(42)]
        public void Filter_BuildMode_ReturnsTheWholeCategoryUnfiltered(int maskBit)
        {
            // MaskBit is a build category id here, not a sub-sort bit, and the panel re-runs
            // InitCategory immediately afterwards and overwrites this result anyway.
            var full = Elements(
                Item(roomSort: 1, subsort: 0, guid: 1),
                Item(roomSort: 1, subsort: 0b1000_0000, guid: 2));

            Assert.Equal(full, CatalogQueryService.Filter(Subcat(maskBit), UICatalogMode.Build, full).ToList());
        }

        [Fact]
        public void Filter_MaskEight_ReturnsEveryItemWithAPositiveSubsort()
        {
            // The shared "all" button is a bare GetSubsort > 0 test - every bit at once.
            var full = Elements(
                Item(roomSort: 1, subsort: 0, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 2),
                Item(roomSort: 1, subsort: 0b1000_0000, guid: 3));

            var filtered = CatalogQueryService.Filter(Subcat(8), UICatalogMode.Normal, full).ToList();

            Assert.Equal(new uint[] { 2, 3 }, filtered.Select(x => x.Item.GUID).ToArray());
        }

        [Fact]
        public void Filter_MaskEight_ExcludesItemsWithNoRoomSort()
        {
            // A rich Subsort cannot rescue an item with no RoomSort - the sub-sort is zeroed first.
            var full = Elements(Item(roomSort: 0, subsort: 0b1111_1111, guid: 1));

            Assert.Empty(CatalogQueryService.Filter(Subcat(8), UICatalogMode.Normal, full));
        }

        [Theory]
        [InlineData(0, new uint[] { 1 })]
        [InlineData(1, new uint[] { 2 })]
        [InlineData(2, new uint[] { 3 })]
        [InlineData(7, new uint[] { 4 })]
        public void Filter_NormalModeBitMask_ReturnsOnlyItemsWithThatBit(int maskBit, uint[] expected)
        {
            var full = Elements(
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0010, guid: 2),
                Item(roomSort: 1, subsort: 0b0000_0100, guid: 3),
                Item(roomSort: 1, subsort: 0b1000_0000, guid: 4));

            var filtered = CatalogQueryService.Filter(Subcat(maskBit), UICatalogMode.Normal, full).ToList();

            Assert.Equal(expected, filtered.Select(x => x.Item.GUID).ToArray());
        }

        [Fact]
        public void FilterNormalModeMaskSeven_OrsInBitSixteenThatSubcatIsEmptyIgnores()
        {
            // PRE-EXISTING DIVERGENCE, PINNED DELIBERATELY.
            // Filter ORs bit 16 into the mask for Normal mode bit 7 ("other") even though
            // SubcatIsEmpty does not, so a sub-sort of 16 is listed under "other". Asserting both
            // halves here is the point of the test: if either is ever changed, the change is
            // deliberate and visible rather than an accident of a copy-paste.
            var full = BitSixteenOnlyItem();

            var filtered = CatalogQueryService.Filter(Subcat(7), UICatalogMode.Normal, full).ToList();
            Assert.Equal(new uint[] { 4 }, filtered.Select(x => x.Item.GUID).ToArray());
            Assert.True(CatalogQueryService.SubcatIsEmpty(Subcat(7), UICatalogMode.Normal, full));
        }

        [Fact]
        public void Filter_NormalModeMaskSeven_ExcludesItemsWithNeitherBitSevenNorBitSixteen()
        {
            var full = Elements(
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0010, guid: 2));

            Assert.Empty(CatalogQueryService.Filter(Subcat(7), UICatalogMode.Normal, full));
        }

        [Fact]
        public void Filter_LotTypeMode_FiltersOnThatLotTypesColumnNotOnSubsort()
        {
            // Subsort 128 but DowntownSort 0: the item belongs under "other" on a normal lot and
            // under no Downtown subcategory at all.
            var full = Elements(new ObjectCatalogItem()
            {
                GUID = 1,
                RoomSort = 1,
                Subsort = 0b1000_0000,
                DowntownSort = 0
            });

            Assert.Single(CatalogQueryService.Filter(Subcat(7), UICatalogMode.Normal, full));
            Assert.Empty(CatalogQueryService.Filter(Subcat(7), UICatalogMode.Downtown, full));
            Assert.Empty(CatalogQueryService.Filter(Subcat(8), UICatalogMode.Downtown, full));
        }

        [Fact]
        public void Filter_NormalMode_KeepsTheCategorysPriceOrder()
        {
            // The panel indexes FilterCategory by scroll position, and InitCategory pre-sorts by
            // price; the filter must not reorder what survives.
            var full = Elements(
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 3),
                Item(roomSort: 1, subsort: 0b0000_0000, guid: 1),
                Item(roomSort: 1, subsort: 0b0000_0001, guid: 2));

            var filtered = CatalogQueryService.Filter(Subcat(0), UICatalogMode.Normal, full).ToList();

            Assert.Equal(new uint[] { 3, 2 }, filtered.Select(x => x.Item.GUID).ToArray());
        }

        [Fact]
        public void SubcatIsEmptyAndFilter_AgreeOnEveryModeAndMaskBitExceptNormalMaskSeven()
        {
            // The one structural invariant the two methods are supposed to share: a button is
            // greyed out exactly when selecting it would show nothing. The single documented
            // exception is Normal mode mask bit 7, which is asserted here by being excluded.
            var full = Elements(
                ItemForAllLotTypes(roomSort: 1, subsort: 0b0000_0000, guid: 1),
                ItemForAllLotTypes(roomSort: 1, subsort: 0b0000_0001, guid: 2),
                ItemForAllLotTypes(roomSort: 1, subsort: 0b1000_0000, guid: 3),
                ItemForAllLotTypes(roomSort: 1, subsort: 0b0001_0000, guid: 4),
                ItemForAllLotTypes(roomSort: 0, subsort: 0b0111_1111, guid: 5));

            foreach (var mode in EveryFilteringMode())
            {
                foreach (var maskBit in EveryMaskBit())
                {
                    if (mode == UICatalogMode.Normal && maskBit == 7) continue; //documented divergence

                    var subcat = Subcat(maskBit);
                    var isEmpty = CatalogQueryService.SubcatIsEmpty(subcat, mode, full);
                    var results = CatalogQueryService.Filter(subcat, mode, full).ToList();

                    Assert.True(
                        isEmpty == (results.Count == 0),
                        $"{mode} mask {maskBit}: SubcatIsEmpty said {isEmpty} but Filter returned {results.Count} items");
                }
            }
        }

        // --------------------------------------------------- static catalogue tables stay unmutated

        [Fact]
        public void DowntownTables_StayParallelAcrossRepeatedReads()
        {
            // InitCategory used to append the shared "other"/"all" subcategories straight onto
            // DTCategories[Mode] the first time the buy panel was opened on a lot type, growing a
            // public static table from 4 to 6 entries and desyncing it from the 4-entry DTIcons
            // table. It now builds a local copy, so the statics stay at 4 and stay parallel no
            // matter how often the panel is opened.
            foreach (var mode in UIBuyBrowsePanel.DTCategories.Keys)
            {
                Assert.True(UIBuyBrowsePanel.DTIcons.ContainsKey(mode), $"{mode} has categories but no icons");
                Assert.Equal(
                    UIBuyBrowsePanel.DTIcons[mode].Count,
                    UIBuyBrowsePanel.DTCategories[mode].Count);
            }

            foreach (var mode in UIBuyBrowsePanel.DTCategories.Keys)
            {
                var subcats = UIBuyBrowsePanel.DTCategories[mode];
                Assert.Equal(4, subcats.Count);
                Assert.Equal(4, UIBuyBrowsePanel.DTIcons[mode].Count);
                Assert.All(subcats, subcat => Assert.InRange(subcat.MaskBit, 0, 3));
            }
        }
    }
}
