using System.Collections.Generic;
using System.Linq;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.UI.Services;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests that the buy/build catalogue's data and row builders now live in
    /// <see cref="BuyCatalogService"/> without changing anything a caller can observe.
    ///
    /// <para>
    /// The catalogue tables used to be public static fields on <see cref="UIBuyBrowsePanel"/>. They
    /// are now fields on <see cref="BuyCatalogService"/> and the panel forwards to them, so the
    /// two halves of the move are asserted together: the tables have to exist under both names, and
    /// they have to be the <em>same objects</em>, not copies. A copy would leave the panel reading
    /// a different table from the one the tests inspect, and every shape assertion below would pass
    /// while the panel was broken.
    /// </para>
    ///
    /// <para>
    /// The shape invariants themselves (row counts against icon counts, no blank icon names, wall
    /// style arrays in lockstep) are unchanged and already covered by
    /// <c>BuyCatalogDataTests</c>; this class exists to pin the move, and to add the
    /// table-construction rules that were previously only implicit in a static constructor.
    /// </para>
    ///
    /// <para>
    /// <b>Observation, deliberately not an assertion:</b> build subcategories carry
    /// <c>MaskBit = 7 + n</c> (8..18), and <c>UIBuyBrowsePanel.InitSubcategory</c> passes that
    /// mask bit straight back in as the new build <em>category</em>, which is then used to index
    /// <see cref="BuyCatalogService.BuildCategories"/> and <see cref="BuyCatalogService.BuildIcons"/>
    /// - tables that only have three rows. The values are pinned here exactly as they are, because
    /// changing them would be a behaviour change, but clicking a build subcategory looks like it
    /// indexes past the end of those tables. That is upstream FreeSO behaviour and is reported,
    /// not fixed, in this pass.
    /// </para>
    ///
    /// <para>
    /// Pure data: no textures, no <c>Content</c>, no <c>GraphicsDevice</c>. The row builders
    /// (<c>AddFloors</c> and friends) are not exercised here - they read the world's floor and wall
    /// providers, so they need content, and are left to a playtest.
    /// </para>
    /// </summary>
    public sealed class BuyCatalogServiceTests
    {
        /// <summary>Mask bits the panel hardcodes to the shared "other"/"all" buttons.</summary>
        private static bool IsSharedButton(int maskBit) => maskBit == 7 || maskBit == 8;

        // ------------------------------------------------------- forwarding is by reference

        [Fact]
        public void ThePanelForwardsTheSameStringTableRemap()
        {
            Assert.Same(BuyCatalogService.RemapString, UIBuyBrowsePanel.RemapString);
        }

        [Fact]
        public void ThePanelForwardsTheSameBuyCategoryList()
        {
            // same instance, not an equal one: InitCategory copies individual subcategory lists
            // before appending to them, so the list identity is what keeps every panel instance
            // agreeing about the catalogue.
            Assert.Same(BuyCatalogService.Categories, UIBuyBrowsePanel.Categories);
        }

        [Fact]
        public void ThePanelForwardsTheSameIconTables()
        {
            Assert.Same(BuyCatalogService.CatIcons, UIBuyBrowsePanel.CatIcons);
            Assert.Same(BuyCatalogService.BuildIcons, UIBuyBrowsePanel.BuildIcons);
        }

        [Fact]
        public void ThePanelForwardsTheSameDowntownTables()
        {
            Assert.Same(BuyCatalogService.DTCategories, UIBuyBrowsePanel.DTCategories);
            Assert.Same(BuyCatalogService.DTIcons, UIBuyBrowsePanel.DTIcons);
        }

        [Fact]
        public void ThePanelForwardsTheSameBuildCategoryList()
        {
            Assert.Same(BuyCatalogService.BuildCategories, UIBuyBrowsePanel.BuildCategories);
        }

        [Fact]
        public void ThePanelForwardsTheSameWallStyleArrays()
        {
            // AddWallStyles walks both with one index, so they have to be the same arrays the tests
            // count, not copies that could drift.
            Assert.Same(BuyCatalogService.WallStyleIDs, UIBuyBrowsePanel.WallStyleIDs);
            Assert.Same(BuyCatalogService.WallStylePatterns, UIBuyBrowsePanel.WallStylePatterns);
        }

        [Fact]
        public void RepeatedReadsThroughThePanelReturnTheSameObject()
        {
            // a forwarding property that rebuilt its table on read would make the panel's own
            // subcategory buttons and its icon lookups disagree with each other.
            Assert.Same(UIBuyBrowsePanel.Categories, UIBuyBrowsePanel.Categories);
            Assert.Same(UIBuyBrowsePanel.DTCategories, UIBuyBrowsePanel.DTCategories);
        }

        [Fact]
        public void NoneOfTheTablesCanBeReplaced()
        {
            // The tables are public and their contents are deliberately mutable - the row builders
            // copy a subcategory list before appending to it - but the references are readonly, and
            // that is the half that was not. A static field any caller could reassign meant the
            // table a category reads its icon and mask bits from could change mid-construction,
            // which is a null dereference the next time the panel is opened rather than a wrong
            // name. Cheap to state and impossible to state in a comment.
            var names = new[]
            {
                "RemapString", "Categories", "CatIcons", "BuildCategories", "BuildIcons",
                "DTCategories", "DTIcons", "WallStyleIDs", "WallStylePatterns",
            };

            foreach (var name in names)
            {
                var field = typeof(BuyCatalogService).GetField(name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                Assert.NotNull(field);
                Assert.True(field.IsInitOnly, name + " must not be reassignable");
            }
        }

        // ------------------------------------------------------- built categories, constructed

        [Fact]
        public void CategoriesAreBuiltTheFirstTimeAnythingAsksForThem()
        {
            // the tables are built in a static constructor on the service now, not on the panel, so
            // the first read has to fully populate them.
            Assert.NotNull(BuyCatalogService.Categories);
            Assert.Equal(8, BuyCatalogService.Categories.Count);
        }

        [Theory]
        [InlineData(0, 200)]
        [InlineData(1, 201)]
        [InlineData(2, 204)]
        [InlineData(3, 203)]
        [InlineData(4, 205)]
        [InlineData(5, 202)]
        [InlineData(6, 207)]
        [InlineData(7, 206)]
        public void EachBuyCategoryNamesItselfFromItsOwnStringTable(int category, int expectedTable)
        {
            // the four per-category subcategory buttons all read from 200 + RemapString[category].
            // RemapString is a permutation precisely so that no two categories collide, and so that
            // the categories are not in table order either - a "sort it" tidy-up would render
            // eight identical-looking tabs.
            var table = BuyCatalogService.Categories[category].First(x => x.StrTable != 210).StrTable;

            Assert.Equal(expectedTable, table);
            Assert.Equal(200 + BuyCatalogService.RemapString[category], table);
        }

        [Fact]
        public void OnlyTheEntertainmentCategoryGetsThePetsAndMagicSubcategories()
        {
            // mask bits 5 and 6 on table 210 exist only on category 6, which is why the rest of the
            // catalogue looks for them nowhere.
            for (int category = 0; category < BuyCatalogService.Categories.Count; category++)
            {
                var subcats = BuyCatalogService.Categories[category];
                var hasPets = subcats.Any(x => x.MaskBit == 5);
                var hasMagic = subcats.Any(x => x.MaskBit == 6);

                Assert.Equal(category == 6, hasPets);
                Assert.Equal(category == 6, hasMagic);
            }
        }

        [Fact]
        public void EveryBuyCategoryEndsWithTheSharedOtherAndAllSubcategories()
        {
            // mask 7 / table 210 index 1 is "other" and mask 8 / table 210 index 2 is "all"; they
            // are appended to every category rather than being per-category, and they must come
            // last so the buttons read in the order the player expects.
            foreach (var subcats in BuyCatalogService.Categories)
            {
                Assert.Equal(1, subcats.Count(x => x.MaskBit == 7));
                Assert.Equal(1, subcats.Count(x => x.MaskBit == 8));
                Assert.Equal(210, subcats[subcats.Count - 2].StrTable);
                Assert.Equal(1, subcats[subcats.Count - 2].StrInd);
                Assert.Equal(210, subcats[subcats.Count - 1].StrTable);
                Assert.Equal(2, subcats[subcats.Count - 1].StrInd);
            }
        }

        [Fact]
        public void EveryBuyCategoryHasMaskBitsInAscendingOrder()
        {
            // the icon lookup is by mask bit and the buttons are drawn in list order, so an
            // out-of-order table renders buttons whose icons do not match their labels.
            foreach (var subcats in BuyCatalogService.Categories)
            {
                Assert.Equal(
                    subcats.Select(x => x.MaskBit).OrderBy(x => x).ToArray(),
                    subcats.Select(x => x.MaskBit).ToArray());
            }
        }

        // ------------------------------------------------------------- downtown lot types

        [Theory]
        [InlineData(UICatalogMode.Downtown)]
        [InlineData(UICatalogMode.Community)]
        [InlineData(UICatalogMode.Vacation)]
        [InlineData(UICatalogMode.Studiotown)]
        [InlineData(UICatalogMode.Magictown)]
        public void EveryDowntownLotTypeHasExactlyFourRealSubcategoriesAndFourIcons(UICatalogMode mode)
        {
            // the panel only appends the shared "other"/"all" pair when the list is exactly four
            // long, so a fifth real entry would silently drop two buttons.
            Assert.Equal(4, BuyCatalogService.DTCategories[mode].Count);
            Assert.Equal(4, BuyCatalogService.DTIcons[mode].Count);
        }

        [Theory]
        [InlineData(UICatalogMode.Downtown)]
        [InlineData(UICatalogMode.Community)]
        [InlineData(UICatalogMode.Vacation)]
        [InlineData(UICatalogMode.Studiotown)]
        [InlineData(UICatalogMode.Magictown)]
        public void EveryDowntownLotTypeNamesItsSubcategoriesFromTable150(UICatalogMode mode)
        {
            // the lot-type names are one table; a stray 139 or 200 here renders a blank label on
            // the first lot type the player opens.
            Assert.All(BuyCatalogService.DTCategories[mode], x => Assert.Equal(150, x.StrTable));
        }

        [Fact]
        public void TheDowntownTablesCoverEveryDowntownLotTypeAndNothingElse()
        {
            // Normal and Build take different code paths entirely; a stray entry for one of them
            // would be a table the panel never reads.
            var expected = new[]
            {
                UICatalogMode.Downtown,
                UICatalogMode.Community,
                UICatalogMode.Vacation,
                UICatalogMode.Studiotown,
                UICatalogMode.Magictown
            };

            Assert.Equal(
                expected.OrderBy(x => x).ToArray(),
                BuyCatalogService.DTCategories.Keys.OrderBy(x => x).ToArray());
            Assert.Equal(
                expected.OrderBy(x => x).ToArray(),
                BuyCatalogService.DTIcons.Keys.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void TheDowntownAndMagiclandFoodTabsShareAStringBecauseTheyAreTheSameTab()
        {
            // not a coincidence to be fixed: Magicland reuses Downtown's food, shops and outside
            // strings and only swaps in its own magic entry. Asserted so that a "tidy up the
            // duplicated entries" pass is a visible change rather than a silent one.
            var downtown = BuyCatalogService.DTCategories[UICatalogMode.Downtown];
            var magic = BuyCatalogService.DTCategories[UICatalogMode.Magictown];

            Assert.Equal(16, magic[0].StrInd);
            Assert.Equal(17, magic[1].StrInd);
            Assert.Equal(18, magic[3].StrInd);
            //and the one that is genuinely its own
            Assert.Equal(44, magic[2].StrInd);
            Assert.Equal(19, downtown[3].StrInd);
        }

        // --------------------------------------------------------------------- build mode

        [Fact]
        public void TheBuildCategoriesAreArchitectureOutdoorsAndObjects()
        {
            // the three build modes the switcher offers, in the order the panel indexes them.
            Assert.Equal(3, BuyCatalogService.BuildCategories.Count);
            Assert.Equal(3, BuyCatalogService.BuildIcons.Length);
        }

        [Fact]
        public void EveryBuildSubcategoryMaskBitIsSevenPlusTheBuildSubcategoryIndex()
        {
            // Build mode reaches the architecture through bits 7+n, n = 1..11, so the build
            // subcategories never overlap the four buy subcategories in bits 1..4. The offset of
            // 7 is pinned because the panel treats the mask bit as a build category id (see
            // InitSubcategory), and because "tidy up the 7+" would change which bit a style lands
            // on.
            var expected = Enumerable.Range(1, 11).Select(x => 7 + x).ToArray();

            var actual = BuyCatalogService.BuildCategories
                .SelectMany(x => x)
                .Select(x => x.MaskBit)
                .ToArray();

            Assert.Equal(expected.OrderBy(x => x).ToArray(), actual.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void EveryBuildSubcategoryUsesTheBuildNameTable()
        {
            // build mode has its own button names, one table, distinct from the buy and downtown
            // ones. A buy-mode table index here renders a build button with the wrong label.
            foreach (var subcats in BuyCatalogService.BuildCategories)
            {
                Assert.All(subcats, x => Assert.Equal(139, x.StrTable));
            }
        }

        // ------------------------------------------------------------------ wall styles

        [Fact]
        public void TheWallStyleTablesAreParallelAndStartWithThePlainWall()
        {
            // index 0 must be the plain wall: it is the row the player picks by accident, and it
            // is the one whose pattern is 0.
            Assert.Equal(BuyCatalogService.WallStyleIDs.Length, BuyCatalogService.WallStylePatterns.Length);
            Assert.Equal(0x1, BuyCatalogService.WallStyleIDs[0]);
            Assert.Equal(0, BuyCatalogService.WallStylePatterns[0]);
        }

        [Fact]
        public void TheFenceStylesEachGetTheirOwnPattern()
        {
            // three different fence styles sharing one pattern would draw the wrong fence on two
            // of the three build buttons.
            var patterns = BuyCatalogService.WallStylePatterns.Skip(1).ToArray();

            Assert.Equal(patterns.Length, patterns.Distinct().Count());
        }
    }
}
