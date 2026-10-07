using System.Linq;
using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The buy/build catalogue is driven entirely by public static lookup tables on
    /// <see cref="UIBuyBrowsePanel"/>. The panel's static constructor and its category tables
    /// are pure data - no textures, no Content, no GraphicsDevice - so the shape of those tables
    /// can be asserted directly.
    ///
    /// The invariants here are the ones that would otherwise only surface as an
    /// IndexOutOfRangeException the first time a player opens the buy panel on that lot type.
    ///
    /// The panel indexes icon tables directly by subcategory MaskBit:
    ///     name = CatIcons[category][cat.MaskBit];        // Normal mode
    ///     name = DTIcons[Mode][cat.MaskBit];              // Downtown/Community/Vacation/...
    ///     name = BuildIcons[category][i];                 // Build mode
    /// with MaskBit 7 and 8 special-cased to the shared "other"/"all" buttons.
    /// </summary>
    public sealed class BuyCatalogDataTests
    {
        /// <summary>MaskBits the panel never looks up in an icon table - they are hardcoded.</summary>
        private static bool IsSharedButton(int maskBit) => maskBit == 7 || maskBit == 8;

        // ------------------------------------------------------- RemapString (string tables)

        [Fact]
        public void RemapString_IsAPermutationOfTheEightStringTables()
        {
            // UIBuyBrowsePanel's static ctor walks i = 0..7 and builds StrTable = 200 + RemapString[i].
            // If two categories mapped to the same value, both would render the same category
            // names; if a value were >= 8, it would read from the wrong string table entirely.
            Assert.Equal(8, UIBuyBrowsePanel.RemapString.Length);
            Assert.Equal(Enumerable.Range(0, 8).ToArray(), UIBuyBrowsePanel.RemapString.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void TheBuyCategoryTablesAllHaveExactlyEightRows()
        {
            // Categories, RemapString and CatIcons are indexed in lockstep by the category id
            // that UIMainPanel passes in, so a row added to one but not the others would throw.
            Assert.Equal(8, UIBuyBrowsePanel.Categories.Count);
            Assert.Equal(UIBuyBrowsePanel.Categories.Count, UIBuyBrowsePanel.CatIcons.Length);
            Assert.Equal(UIBuyBrowsePanel.Categories.Count, UIBuyBrowsePanel.RemapString.Length);
        }

        [Fact]
        public void EveryBuyCategoryHasExactlyOneSubcategoryPerMaskBit()
        {
            // MaskBit is what the object lookup filters on. A duplicate would make one button
            // permanently unsatisfiable; a subcategory built with a bad table/index would render
            // as a blank label.
            for (int category = 0; category < UIBuyBrowsePanel.Categories.Count; category++)
            {
                var subcats = UIBuyBrowsePanel.Categories[category];

                Assert.Equal(subcats.Count, subcats.Select(s => s.MaskBit).Distinct().Count());

                // Two families of string table are legitimate here:
                //   200 + modi  - the per-category subcategory names (8 of them, 200..207)
                //   210         - the shared "other"/"all"/"pets"/"magic" entries, which are
                //                 appended to every category rather than being per-category
                // Anything else is a typo and would render a blank button label.
                Assert.All(subcats, s => Assert.True(
                    (s.StrTable >= 200 && s.StrTable <= 207) || s.StrTable == 210,
                    $"category {category} uses string table {s.StrTable}"));
            }
        }

        // ------------------------------------------------------ Normal-mode icon table bounds

        [Fact]
        public void EveryNormalModeMaskBitHasAnIcon()
        {
            // "cat_" + name + ".png" is loaded for whatever comes back, so an out-of-range
            // MaskBit here is a hard crash on opening the buy panel.
            for (int category = 0; category < UIBuyBrowsePanel.Categories.Count; category++)
            {
                var icons = UIBuyBrowsePanel.CatIcons[category];
                foreach (var subcat in UIBuyBrowsePanel.Categories[category])
                {
                    if (IsSharedButton(subcat.MaskBit)) continue;
                    Assert.InRange(subcat.MaskBit, 0, icons.Length - 1);
                }
            }
        }

        [Fact]
        public void NoNormalModeIconNameIsBlank()
        {
            // a blank name would resolve to "cat_.png" and fail to load.
            foreach (var row in UIBuyBrowsePanel.CatIcons)
            {
                Assert.All(row, icon => Assert.False(string.IsNullOrWhiteSpace(icon)));
            }
        }

        // ------------------------------------------------------ Downtown & friends icon bounds

        [Fact]
        public void EveryDowntownModeMaskBitHasAnIcon()
        {
            foreach (var mode in UIBuyBrowsePanel.DTCategories.Keys)
            {
                Assert.True(UIBuyBrowsePanel.DTIcons.ContainsKey(mode), $"{mode} has categories but no icons");
                var icons = UIBuyBrowsePanel.DTIcons[mode];
                foreach (var subcat in UIBuyBrowsePanel.DTCategories[mode])
                {
                    if (IsSharedButton(subcat.MaskBit)) continue;
                    Assert.InRange(subcat.MaskBit, 0, icons.Count - 1);
                }
            }
        }

        [Fact]
        public void EveryDowntownModeHasExactlyFourSubcategories()
        {
            // UIBuyBrowsePanel only appends the shared "other"/"all" subcategories when the list
            // is exactly four long, so adding a fifth real subcategory to one lot type would
            // silently drop those two buttons.
            //
            // The panel used to append to these public static lists *in place* on first open,
            // so the "4" was only observable because no test here ever constructed a panel.
            // InitCategory now builds a local copy and appends to that, so the static tables stay
            // at four and remain parallel with DTIcons.
            foreach (var mode in UIBuyBrowsePanel.DTCategories.Keys)
            {
                Assert.Equal(4, UIBuyBrowsePanel.DTCategories[mode].Count);
                Assert.Equal(4, UIBuyBrowsePanel.DTIcons[mode].Count);
            }
        }

        [Fact]
        public void EveryDowntownModeIconNameIsNonBlank()
        {
            foreach (var mode in UIBuyBrowsePanel.DTIcons.Keys)
            {
                Assert.All(UIBuyBrowsePanel.DTIcons[mode], icon => Assert.False(string.IsNullOrWhiteSpace(icon)));
            }
        }

        // -------------------------------------------------------------- build mode icon bounds

        [Fact]
        public void EveryBuildCategoryHasOneIconPerSubcategory()
        {
            // Build mode indexes BuildIcons[category][i] by loop position, not by MaskBit.
            Assert.Equal(UIBuyBrowsePanel.BuildCategories.Count, UIBuyBrowsePanel.BuildIcons.Length);
            for (int category = 0; category < UIBuyBrowsePanel.BuildCategories.Count; category++)
            {
                Assert.True(UIBuyBrowsePanel.BuildIcons[category].Length >= UIBuyBrowsePanel.BuildCategories[category].Count,
                    $"build category {category} has {UIBuyBrowsePanel.BuildCategories[category].Count} subcategories but only {UIBuyBrowsePanel.BuildIcons[category].Length} icons");
                Assert.All(UIBuyBrowsePanel.BuildIcons[category], icon => Assert.False(string.IsNullOrWhiteSpace(icon)));
            }
        }

        [Fact]
        public void EveryBuildSubcategoryHasADistinctMaskBit()
        {
            foreach (var subcats in UIBuyBrowsePanel.BuildCategories)
            {
                Assert.Equal(subcats.Count, subcats.Select(s => s.MaskBit).Distinct().Count());
            }
        }

        // --------------------------------------------------------------------- wall styles

        [Fact]
        public void WallStyleIdsAndPatternsAreParallelArrays()
        {
            // AddWallStyles() walks both with the same index; a length mismatch would either
            // skip a fence style or throw.
            Assert.Equal(UIBuyBrowsePanel.WallStyleIDs.Length, UIBuyBrowsePanel.WallStylePatterns.Length);
            Assert.Equal(5, UIBuyBrowsePanel.WallStyleIDs.Length);
        }

        // ------------------------------------------------------------------ neighbourhood config

        [Fact]
        public void ThereIsOneNeighborhoodViewConfigPerSelectableNeighborhood()
        {
            // PopulateScreen does `Neighborhoods[mode - 1]` with mode coming from the frontend,
            // so a missing trailing entry would be an out-of-range read rather than a fallback.
            //
            // NOTE (an observation, deliberately not an assertion): index 5 - neighborhood 6 -
            // is an empty NeighborhoodViewConfig whose Graphic is null. If that slot is ever
            // reachable it will fail inside the texture provider rather than degrade gracefully.
            // That is a production-code question, not a table-shape question.
            var configs = UINeighborhoodSelectionPanel.Neighborhoods;
            Assert.Equal(7, configs.Length);
            Assert.All(configs, config => Assert.NotNull(config));
        }
    }
}
