/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls.Catalog;
using FSO.Client.UI.Panels.LotControls;
using FSO.Content;
using FSO.Content.Interfaces;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// The buy/build catalogue's data and its content-to-row builders, lifted out of
    /// <see cref="UIBuyBrowsePanel"/>.
    ///
    /// <para>
    /// The panel is a view: buttons, labels, tweens and textures. Everything that describes
    /// <em>what</em> is in the catalogue - the eight buy subcategory tables and their icon names,
    /// the build subcategory tables and theirs, the per-lot-type (downtown and friends) tables,
    /// the wall-style id/pattern pairs - plus the row builders that splice the world's floors,
    /// wallpapers, roofs, wall styles and terrain tools into a category list. None of it is UI,
    /// and none of it needs a graphics device, which is what
    /// <see cref="Utils.CatalogQueryService"/> established for the query half; this is the table
    /// and construction half.
    /// </para>
    ///
    /// <para>
    /// <see cref="UIBuyBrowsePanel"/> keeps every one of these reachable at the name it always had
    /// (as forwarding properties), because the shape of these tables is the thing the catalogue
    /// tests assert and because a row count that stops agreeing with its icon table is a hard
    /// crash the first time the panel is opened.
    /// </para>
    ///
    /// <para>
    /// The tables are public and readable, exactly as they were, and their contents are mutable -
    /// <c>InitCategory</c> copies a subcategory list before appending to it, so nothing here is
    /// mutated after initialisation. The <em>references</em> are not: every one of them is
    /// <c>static readonly</c>, so the table a row reads its icon and its mask bits from cannot be
    /// swapped out from under it by some other caller. They used to be plain <c>static</c> fields,
    /// which made "replace the whole catalogue table" a thing any code could do at any moment with
    /// no trace - and the catalogue is read during a category's construction, so a swap there is a
    /// null dereference on the next open rather than a wrong name.
    /// </para>
    ///
    /// <para>
    /// The search box's rules live here for the same reason the subcategory rules live in
    /// <see cref="Utils.CatalogQueryService"/>: which rows a name matches, and when a search is
    /// meaningful at all, are questions about the catalogue's rows and not about the panel, and
    /// they are pure functions of (row, term) or (mode, state) - so they can be pinned by tests
    /// without a GraphicsDevice, a live panel, or a whole world's worth of loaded objects.
    /// </para>
    ///
    /// <para>
    /// The name-resolution half lives here too, because it is the other half of the same feature:
    /// the names <see cref="DisplayNameFor"/> reads are the names the search then matches. It is
    /// the one part that is not pure - it can go to a file - which is why it is reached through
    /// an injectable source and why <see cref="NormalizeName"/> exists as a separate, pure rule.
    /// </para>
    /// </summary>
    public static class BuyCatalogService
    {
        /// <summary>
        /// Maps a buy category to the string table its four subcategory names live in. A
        /// permutation of 0..7: two categories sharing a value would render the same names, and a
        /// value outside the range would read the wrong table.
        /// </summary>
        public static readonly int[] RemapString = new int[]
        {
            0, //seat
            1, //surf
            4, //
            3,
            5,
            2,
            7,
            6
        };

        /// <summary>
        /// The eight buy categories, built once by the static constructor. Each entry carries four
        /// mask-bit subcategories from string table 200+<c>RemapString</c>, plus (for every
        /// category) the shared "pets"/"magic" pair on the entertainment category, and the shared
        /// "other"/"all" pair.
        /// </summary>
        public static readonly List<List<UICatalogSubcat>> Categories;

        /// <summary>
        /// Icon names for <see cref="Categories"/>, indexed by <c>[category][maskBit]</c>. Mask
        /// bits 7 and 8 are the hardcoded "other"/"all" buttons and are never looked up here.
        /// </summary>
        public static readonly string[][] CatIcons = new string[][]
        {
            new string[]
            {
                "seat_dine",
                "seat_loun",
                "seat_sofa",
                "seat_beds"
            },
            new string[]
            {
                "surf_count",
                "surf_tabl",
                "surf_endt",
                "surf_desk"
            },
            new string[]
            {
                "appl_stov",
                "appl_frig",
                "appl_smal",
                "appl_larg"
            },
            new string[]
            {
                "elec_ent",
                "elec_vide",
                "elec_audi",
                "elec_phon"
            },

            new string[]
            {
                "plum_toil",
                "plum_show",
                "plum_sink",
                "plum_hott"
            },

            new string[]
            {
                "deco_pain",
                "deco_scul",
                "deco_rugs",
                "deco_plan"
            },
            new string[]
            {
                "misc_recr",
                "misc_know",
                "misc_crea",
                "misc_ward",
                "misc_pets",
                "misc_pets",
                "misc_magi",
            },
            new string[]
            {
                "ligh_tabl",
                "ligh_stan",
                "ligh_wall",
                "ligh_hang"
            },
        };

        /// <summary>
        /// Build mode's categories: architecture, outdoors and objects, keyed by the build
        /// category id the panel is given.
        /// </summary>
        public static readonly List<List<UICatalogSubcat>> BuildCategories = new List<List<UICatalogSubcat>>()
        {
            new List<UICatalogSubcat>() //architecture
            {
                new UICatalogSubcat() { MaskBit = 7+6, StrTable = 139, StrInd = 2}, //wall
                new UICatalogSubcat() { MaskBit = 7+8, StrTable = 139, StrInd = 3}, //wallpaper
                new UICatalogSubcat() { MaskBit = 7+9, StrTable = 139, StrInd = 7}, //floor
                new UICatalogSubcat() { MaskBit = 7+10, StrTable = 139, StrInd = 10}, //roof
            },
            new List<UICatalogSubcat>() //outdoors
            {
                new UICatalogSubcat() { MaskBit = 7+4, StrTable = 139, StrInd = 6}, //trees
                new UICatalogSubcat() { MaskBit = 7+11, StrTable = 139, StrInd = 0}, //terrain
                new UICatalogSubcat() { MaskBit = 7+7, StrTable = 139, StrInd = 1}, //water
            },
            new List<UICatalogSubcat>() //objects
            {
                new UICatalogSubcat() { MaskBit = 7+1, StrTable = 139, StrInd = 8}, //door
                new UICatalogSubcat() { MaskBit = 7+2, StrTable = 139, StrInd = 9}, //window
                new UICatalogSubcat() { MaskBit = 7+3, StrTable = 139, StrInd = 4}, //staircase
                new UICatalogSubcat() { MaskBit = 7+5, StrTable = 139, StrInd = 5}, //fireplaces
            },
        };

        /// <summary>
        /// Icon names for <see cref="BuildCategories"/>, indexed <c>[category][loopIndex]</c> -
        /// build mode indexes these by position rather than by mask bit, so the row count may
        /// exceed the subcategory count.
        /// </summary>
        public static readonly string[][] BuildIcons = new string[][]
        {
            new string[]
            {
                "build_wall",
                "build_walp",
                "build_flor",
                "build_roof",
            },
            new string[]
            {
                "build_tree",
                "build_terr",
                "build_watr",
            },
            new string[]
            {
                "build_door",
                "build_wind",
                "build_stai",
                "build_fire"
            },
        };

        /// <summary>
        /// Subcategories for the downtown-style lot types, keyed by lot type. Four real entries
        /// each; the panel appends the shared "other"/"all" pair to a local copy, so these stay at
        /// four and stay parallel with <see cref="DTIcons"/>.
        /// </summary>
        public static readonly Dictionary<UICatalogMode, List<UICatalogSubcat>> DTCategories = new Dictionary<UICatalogMode, List<UICatalogSubcat>>()
        {
            {
                UICatalogMode.Downtown,
                new List<UICatalogSubcat>()
                {
                    new UICatalogSubcat() { MaskBit = 0, StrTable = 150, StrInd = 16}, //food
                    new UICatalogSubcat() { MaskBit = 1, StrTable = 150, StrInd = 17}, //shops
                    new UICatalogSubcat() { MaskBit = 2, StrTable = 150, StrInd = 18}, //outside
                    new UICatalogSubcat() { MaskBit = 3, StrTable = 150, StrInd = 19}, //street
                }
            },

            {
                UICatalogMode.Community,
                new List<UICatalogSubcat>()
                {
                    new UICatalogSubcat() { MaskBit = 0, StrTable = 150, StrInd = 32}, //food
                    new UICatalogSubcat() { MaskBit = 1, StrTable = 150, StrInd = 33}, //shops
                    new UICatalogSubcat() { MaskBit = 2, StrTable = 150, StrInd = 34}, //outside
                    new UICatalogSubcat() { MaskBit = 3, StrTable = 150, StrInd = 35}, //street
                }
            },

            {
                UICatalogMode.Vacation,
                new List<UICatalogSubcat>()
                {
                    new UICatalogSubcat() { MaskBit = 0, StrTable = 150, StrInd = 24}, //lodging
                    new UICatalogSubcat() { MaskBit = 1, StrTable = 150, StrInd = 25}, //shops
                    new UICatalogSubcat() { MaskBit = 2, StrTable = 150, StrInd = 26}, //recreation
                    new UICatalogSubcat() { MaskBit = 3, StrTable = 150, StrInd = 27}, //ameneties
                }
            },

            {
                UICatalogMode.Studiotown,
                new List<UICatalogSubcat>()
                {
                    new UICatalogSubcat() { MaskBit = 0, StrTable = 150, StrInd = 40}, //food
                    new UICatalogSubcat() { MaskBit = 1, StrTable = 150, StrInd = 41}, //shops
                    new UICatalogSubcat() { MaskBit = 2, StrTable = 150, StrInd = 42}, //studio
                    new UICatalogSubcat() { MaskBit = 3, StrTable = 150, StrInd = 43}, //spa
                }
            },

            {
                UICatalogMode.Magictown,
                new List<UICatalogSubcat>()
                {
                    new UICatalogSubcat() { MaskBit = 0, StrTable = 150, StrInd = 16}, //food
                    new UICatalogSubcat() { MaskBit = 1, StrTable = 150, StrInd = 17}, //shops
                    new UICatalogSubcat() { MaskBit = 2, StrTable = 150, StrInd = 44}, //magico
                    new UICatalogSubcat() { MaskBit = 3, StrTable = 150, StrInd = 18}, //outside
                }
            },
        };

        /// <summary>
        /// Icon names for <see cref="DTCategories"/>, indexed by <c>[lotType][maskBit]</c>, with
        /// mask bits 7 and 8 again hardcoded to "other"/"all".
        /// </summary>
        public static readonly Dictionary<UICatalogMode, List<string>> DTIcons = new Dictionary<UICatalogMode, List<string>>()
        {
            {
                UICatalogMode.Downtown,
                new List<string>()
                {
                    "dt_food", //food
                    "dt_shop", //shops
                    "dt_out", //outside
                    "dt_street", //street
                }
            },

            {
                UICatalogMode.Community,
                new List<string>()
                {
                    "dt_food", //food
                    "dt_shop", //shops
                    "dt_out", //outside
                    "dt_street", //street
                }
            },

            {
                UICatalogMode.Vacation,
                new List<string>()
                {
                    "vac_lodg", //food
                    "dt_shop", //shops
                    "vac_recr", //recreation
                    "vac_amen", //amenities
                }
            },

            {
                UICatalogMode.Studiotown,
                new List<string>()
                {
                    "dt_food", //food
                    "dt_shop", //shops
                    "st_studio", //studio
                    "st_spa", //spa
                }
            },

            {
                UICatalogMode.Magictown,
                new List<string>()
                {
                    "dt_food", //food
                    "dt_shop", //shops
                    "misc_magi", //magic
                    "dt_out", //outside
                }
            },
        };

        /// <summary>
        /// The wall styles the build catalogue offers, in row order: a plain wall then the four
        /// fence/banister styles.
        /// </summary>
        public static readonly short[] WallStyleIDs =
        {
            0x1, //wall
            0x2, //picket fence
            0xD, //iron fence
            0xC, //privacy fence
            0xE //banisters
        };

        /// <summary>
        /// The wall pattern each entry of <see cref="WallStyleIDs"/> is drawn with, in the same
        /// order - the two arrays are walked in lockstep.
        /// </summary>
        public static readonly short[] WallStylePatterns =
        {
            0, //wall
            248, //picket fence
            250, //iron fence
            249, //privacy fence
            251, //banisters
        };

        static BuyCatalogService()
        {
            Categories = new List<List<UICatalogSubcat>>();

            for (int i = 0; i < 8; i++)
            {
                //init buy categories.
                var cat = new List<UICatalogSubcat>();
                var modi = RemapString[i];
                for (int j = 0; j < 4; j++)
                {
                    cat.Add(new UICatalogSubcat()
                    {
                        StrTable = 200 + modi,
                        MaskBit = j,
                        StrInd = j
                    });
                }

                if (i == 6)
                {
                    cat.Add(new UICatalogSubcat()
                    {
                        StrTable = 210,
                        MaskBit = 5,
                        StrInd = 3, //pets
                    });

                    cat.Add(new UICatalogSubcat()
                    {
                        StrTable = 210,
                        MaskBit = 6,
                        StrInd = 4, //magic
                    });
                }

                cat.Add(new UICatalogSubcat()
                {
                    StrTable = 210,
                    MaskBit = 7,
                    StrInd = 1, //other
                });

                cat.Add(new UICatalogSubcat()
                {
                    StrTable = 210,
                    MaskBit = 8,
                    StrInd = 2, //all
                });
                Categories.Add(cat);
            }
        }

        /// <summary>
        /// Splices the world's floor styles into <paramref name="fullCategory"/> as special
        /// rows, each opening the floor painter.
        /// </summary>
        /// <remarks>
        /// The special rows bypass the panel's discount pass, so each one's
        /// <see cref="UICatalogElement.CalcPrice"/> is seeded from the list price here - the same
        /// seeding the stock client does for every row it builds.
        /// </remarks>
        /// <param name="fullCategory">The panel's category list, inserted into at the front.</param>
        /// <param name="category">14 for floors, 16 for terrain floors.</param>
        public static void AddFloors(List<UICatalogElement> fullCategory, sbyte category)
        {
            var res = new UICatalogFloorResProvider();

            var floors = Content.Get().WorldFloors.List();

            for (int i = 0; i < floors.Count; i++)
            {
                var floor = (FloorReference)floors[i];

                if ((category == 14) != (floor.ID >= 65534)) continue;
                fullCategory.Insert(0, new UICatalogElement
                {
                    Item = new ObjectCatalogItem()
                    {
                        Name = floor.Name,
                        Category = category,
                        Price = (uint)floor.Price,
                    },
                    //see remarks
                    CalcPrice = (int)floor.Price,
                    Special = new UISpecialCatalogElement
                    {
                        Control = typeof(UIFloorPainter),
                        ResID = floor.ID,
                        Res = res,
                        Parameters = new List<int> { (int)floor.ID } //pattern
                    }
                });
            }
        }

        /// <summary>
        /// Splices the world's wallpapers into <paramref name="fullCategory"/> (category 15) as
        /// special rows, each opening the wall painter.
        /// </summary>
        public static void AddWallpapers(List<UICatalogElement> fullCategory)
        {
            var res = new UICatalogWallpaperResProvider();

            var walls = Content.Get().WorldWalls.List();

            for (int i = 0; i < walls.Count; i++)
            {
                var wall = (WallReference)walls[i];
                fullCategory.Insert(0, new UICatalogElement
                {
                    Item = new ObjectCatalogItem()
                    {
                        Name = wall.Name,
                        Category = 15,
                        Price = (uint)wall.Price,
                    },
                    CalcPrice = (int)wall.Price, //see AddFloors
                    Special = new UISpecialCatalogElement
                    {
                        Control = typeof(UIWallPainter),
                        ResID = wall.ID,
                        Res = res,
                        Parameters = new List<int> { (int)wall.ID } //pattern
                    }
                });
            }
        }

        /// <summary>
        /// Splices the world's roof styles into <paramref name="fullCategory"/> (category 17).
        /// </summary>
        /// <remarks>
        /// Roof style selection is free - the money is charged per wall/room by the build tools
        /// themselves - so both the list price and <see cref="UICatalogElement.CalcPrice"/> stay
        /// at 0.
        /// </remarks>
        public static void AddRoofs(List<UICatalogElement> fullCategory)
        {
            var res = new UICatalogRoofResProvider();

            var total = Content.Get().WorldRoofs.Count;

            for (int i = 0; i < total; i++)
            {
                sbyte category = 17;
                var name = GetRoofName(i);
                fullCategory.Insert(0, new UICatalogElement
                {
                    Item = new ObjectCatalogItem()
                    {
                        Name = name,
                        Category = category,
                        Price = 0,
                    },
                    CalcPrice = 0,
                    Special = new UISpecialCatalogElement
                    {
                        Control = typeof(UIRoofer),
                        ResID = (uint)i,
                        Res = res,
                        Parameters = new List<int> { i } //pattern
                    }
                });
            }
        }

        /// <summary>
        /// Splices the five wall styles into <paramref name="fullCategory"/> (category 13) at
        /// their table positions, each opening the wall placer.
        /// </summary>
        public static void AddWallStyles(List<UICatalogElement> fullCategory)
        {
            var res = new UICatalogWallResProvider();

            for (int i = 0; i < WallStyleIDs.Length; i++)
            {
                var walls = Content.Get().WorldWalls;
                var style = walls.GetWallStyle((ulong)WallStyleIDs[i]);
                fullCategory.Insert(i, new UICatalogElement
                {
                    Item = new ObjectCatalogItem()
                    {
                        Name = style.Name,
                        Category = 13,
                        Price = (uint)style.Price,
                    },
                    CalcPrice = (int)style.Price, //see AddFloors
                    Special = new UISpecialCatalogElement
                    {
                        Control = typeof(UIWallPlacer),
                        ResID = (ulong)WallStyleIDs[i],
                        Res = res,
                        Parameters = new List<int> { WallStylePatterns[i], WallStyleIDs[i] } //pattern, style
                    }
                });
            }
        }

        /// <summary>
        /// Splices the three terrain tools into <paramref name="fullCategory"/> (category 18) at
        /// the front, in the order raise, flatten, grass.
        /// </summary>
        public static void AddTerrainTools(List<UICatalogElement> fullCategory)
        {
            var res = new UICatalogWallResProvider();

            fullCategory.Insert(0, new UICatalogElement
            {
                Item = new ObjectCatalogItem()
                {
                    Name = "Raise/Lower Terrain",
                    Category = 18,
                    Price = 1,
                },
                CalcPrice = 1, //see AddFloors
                Special = new UISpecialCatalogElement
                {
                    Control = typeof(UITerrainRaiser),
                    ResID = 0,
                    Res = new UICatalogTerrainResProvider(),
                    Parameters = new List<int> { }
                }
            });

            fullCategory.Insert(0, new UICatalogElement
            {
                Item = new ObjectCatalogItem()
                {
                    Name = "Flatten Terrain",
                    Category = 18,
                    Price = 1,
                },
                CalcPrice = 1,
                Special = new UISpecialCatalogElement
                {
                    Control = typeof(UITerrainFlatten),
                    ResID = 1,
                    Res = new UICatalogTerrainResProvider(),
                    Parameters = new List<int> { }
                }
            });

            fullCategory.Insert(0, new UICatalogElement
            {
                Item = new ObjectCatalogItem()
                {
                    Name = "Grass Tool",
                    Category = 18,
                    Price = 1,
                },
                CalcPrice = 1,
                Special = new UISpecialCatalogElement
                {
                    Control = typeof(UIGrassPaint),
                    ResID = 2,
                    Res = new UICatalogTerrainResProvider(),
                    Parameters = new List<int> { }
                }
            });
        }

        /// <summary>
        /// A display name for roof style <paramref name="index"/>, replacing the empty string these
        /// rows used to carry (which made every roof row read as "(unknown)").
        /// </summary>
        /// <remarks>
        /// Roofs have no catalogue strings of their own - <c>UICatalogRoofResProvider.GetName</c>
        /// returns "" for every id - but the content provider does know each roof by its texture
        /// file name (<c>WorldRoofProvider.IDToName</c>), and that name is unique per roof, so it
        /// is what we use. If the provider has no entry to name (content not loaded, index out of
        /// range) we fall back to the localised subcategory label - the roof build button is
        /// table 139 index 10, see <see cref="BuildCategories"/> - which is the best available
        /// answer even though it repeats across rows. If that lookup comes back empty too, the
        /// name is left "" exactly as it was before.
        /// </remarks>
        private static string GetRoofName(int index)
        {
            try
            {
                var name = Content.Get().WorldRoofs.IDToName(index);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch
            {
                //no roof content, or an index the provider does not have - fall through
            }

            var subcatLabel = GameFacade.Strings?.GetString("139", "10");
            if (string.IsNullOrEmpty(subcatLabel) || subcatLabel == "***MISSING***") return "";
            return subcatLabel;
        }

        // --------------------------------------------------------------------------------- search

        /// <summary>
        /// The guid a catalogue row carries when it is a placeholder rather than something the
        /// player could actually buy. TS1 writes it for a handful of rows that have a price and a
        /// name but no object behind them.
        /// </summary>
        private const uint NotForSaleGUID = uint.MaxValue;

        /// <summary>
        /// Whether the search box can usefully filter the panel's list right now.
        ///
        /// <para>
        /// Buy mode: always. The list on screen is the category's items (or the subcategory's slice
        /// of them), and either is a set of names worth searching.
        /// </para>
        ///
        /// <para>
        /// Build mode: only once a subcategory is open. Each build subcategory is really a
        /// <em>different</em> catalogue category - <c>OpenSubcategory</c> re-runs
        /// <c>InitCategory</c> with the build category id it is named for - so before one is
        /// chosen the panel's list is still the buy-mode category the switcher landed on, and the
        /// subcategory buttons are covering the screen anyway. Filtering that list would show
        /// search results for a category the player is not shopping.
        /// </para>
        /// </summary>
        public static bool SearchApplies(UICatalogMode mode, bool choosingSub)
        {
            return !(choosingSub && mode == UICatalogMode.Build);
        }

        /// <summary>
        /// Whether <paramref name="item"/> is a hit for <paramref name="query"/>.
        ///
        /// <para>
        /// Three names are tried, in the order the player would expect them to be found: the name
        /// the object is sold under (<see cref="UICatalogElement.DisplayName"/>, read from the
        /// object's catalogue strings), the catalogue's own row name, and its catalogue name. A
        /// search that only matched the first would fail on any object whose strings did not load,
        /// and one that only matched the second would send the player looking for "TVExpensive".
        /// </para>
        ///
        /// <para>
        /// Matching is a case-insensitive substring test: a player typing "tv" wants the plasma
        /// set, the wall-mounted set and "anything with tv in it", not an exact title.
        /// </para>
        ///
        /// <para>
        /// An empty or whitespace-only term matches everything, including rows with no name at
        /// all. That is what makes the box a filter rather than a validator: an empty box shows the
        /// category, and clearing the box shows the category again.
        /// </para>
        ///
        /// <para>
        /// Reading <see cref="UICatalogElement.DisplayName"/> is the expensive step, and the
        /// first read of a row is the only one that costs anything - the row remembers it. So a
        /// search over a category is a pass over strings already in memory after the first time,
        /// and a category nobody searches costs nothing until its rows are drawn.
        /// </para>
        /// </summary>
        public static bool MatchesSearch(UICatalogElement item, string query)
        {
            if (item == null) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;

            return NameContains(item.DisplayName, query)
                || NameContains(item.Item.Name, query)
                || NameContains(item.Item.CatalogName, query);
        }

        /// <summary>
        /// The rows of <paramref name="full"/> that survive <paramref name="query"/>, in list order
        /// (the panel scrolls by index, so the order the category was built in is the order the
        /// strip must keep). A null list yields no rows; an empty term yields the list untouched.
        /// </summary>
        ///
        /// <para>
        /// The return type is a list, and that is load-bearing rather than incidental. The strip's
        /// scroll container asks the panel how many rows it has on every single frame, and asks
        /// for a row's element by index for each row it draws. A deferred <c>Where</c> answering
        /// through <c>Count()</c> and <c>ElementAt()</c> re-runs the whole predicate chain on each
        /// of those, so a category holding a few thousand custom items costs thousands of
        /// comparisons per frame just to size a scrollbar, and every visible row costs another
        /// full sweep. An <see cref="IList{T}"/> is O(1) for both, and this is the one place the
        /// strip's row list is built, so the guarantee is expressed in the signature rather than
        /// left to each caller to remember.
        /// </para>
        ///
        /// <para>
        /// Only the caller's own list is searched. A term that would match an item in a different
        /// category cannot reach it from here - the panel passes the category it is showing, and
        /// switching category replaces the panel, so a search box is a search within one
        /// category's list by construction.
        /// </para>
        ///
        /// <para>
        /// Note the asymmetry with an empty term, which is inherited from the panel this replaces
        /// and is pinned by tests: <see cref="NotForSaleGUID"/> rows are dropped from a search but
        /// not from the unsearched list. A search that surfaces a row which cannot be bought is
        /// worse than a search that hides one.
        /// </para>
        /// </summary>
        public static IList<UICatalogElement> SearchCategory(IList<UICatalogElement> full, string query)
        {
            if (full == null) return new List<UICatalogElement>();
            if (string.IsNullOrWhiteSpace(query)) return full;

            //Counted before the pass, not after: a row whose name is already resolved costs
            //nothing to match against, and this is the one place in the search box where answering
            //the question can cost a content read per row. How many rows had to be paid for is not
            //visible anywhere in the UI, so it is written down.
            var unresolved = 0;
            foreach (var row in full)
            {
                if (row != null && !row.DisplayNameResolved) unresolved++;
            }

            var result = new List<UICatalogElement>(full.Count);
            foreach (var row in full)
            {
                if (row == null || row.Item.GUID == NotForSaleGUID) continue;
                if (MatchesSearch(row, query)) result.Add(row);
            }

            if (unresolved > 0)
            {
                SimitoneLog.Info($"catalog: search for \"{query}\" resolved the names of " +
                    $"{unresolved} of {full.Count} rows from content");
            }

            return result;
        }

        private static bool NameContains(string name, string query)
        {
            return !string.IsNullOrEmpty(name)
                && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ------------------------------------------------------------------------------ row names

        /// <summary>
        /// A name, or null for "this row has no name". An empty string is not a name.
        ///
        /// <para>
        /// <c>UICatalogRoofResProvider.GetName</c> returns "" for every id, and a row left holding
        /// "" would draw a blank label in the strip - and, because <c>ToString</c> falls back with
        /// <c>??</c> rather than <c>IsNullOrEmpty</c>, it would also suppress the perfectly good
        /// <see cref="ObjectCatalogItem.Name"/> the row builder already put there. Every name this
        /// service hands out goes through here so that those two cases cannot be told apart.
        /// </para>
        /// </summary>
        public static string NormalizeName(string candidate)
        {
            return string.IsNullOrEmpty(candidate) ? null : candidate;
        }

        /// <summary>
        /// The name <paramref name="elem"/> is sold under, or null if it has none. Never throws.
        ///
        /// <para>
        /// This has to exist at all because the catalogue does not carry the names. The TS1
        /// catalogue (<c>TS1ObjectProvider.Init</c>) fills in <see cref="ObjectCatalogItem.Name"/>
        /// from the OBJD chunk label - the internal name, "TVExpensive", "ChairWoodLegsNew" - and
        /// leaves <c>CatalogName</c> null, so the only "Soma Plasma TV" in the game lives in the
        /// object's CTSS chunk. The row builders here have the same gap in reverse: a floor,
        /// wallpaper, wall style or terrain tool is not an object at all and is named by its own
        /// resource provider.
        /// </para>
        ///
        /// <para>
        /// <paramref name="source"/> overrides where the name comes from, and exists for two
        /// reasons. It is how the tests reach this logic without a loaded world, and it is how a
        /// caller that already has the name - a cache, a name table built alongside a content set
        /// - can supply it instead of letting every row go back to content. Null means "read the
        /// row's own content", which is the answer that can cost a file read.
        /// </para>
        ///
        /// <para>
        /// Callers are expected to ask at most once per row and to remember the answer (see
        /// <c>UICatalogElement.DisplayName</c>). That memo is what makes this affordable: a row
        /// can be drawn, searched, recounted and deselected any number of times and still cost one
        /// lookup for its whole life, and a category nobody searches or scrolls through costs
        /// nothing at all. Note that "remember the answer" has to include remembering a <em>null</em>
        /// - a row whose content is missing is the case that would otherwise re-read, and
        /// re-throw, on every frame the row is on screen.
        /// </para>
        /// </summary>
        /// <param name="elem">The row to name. Null yields null.</param>
        /// <param name="source">
        /// Where to read the name from, or null to read the row's own content.
        /// </param>
        public static string DisplayNameFor(UICatalogElement elem, Func<UICatalogElement, string> source)
        {
            if (elem == null) return null;

            string candidate;
            try
            {
                candidate = source != null ? source(elem) : ResolveFromContent(elem);
            }
            catch (Exception ex)
            {
                // A row whose content will not load is a row with no display name. It still lists
                // and still searches on its OBJD label, and one bad row must not take the buy
                // panel down with it - the panel is opened from a mode button, so a throw here
                // would look like the game had crashed. Logged because a custom content set that
                // produces these in bulk is a broken content set, and the player's only other
                // clue is an item that quietly will not appear.
                SimitoneLog.Warn($"catalog: could not read the name of {Describe(elem)}", ex);
                candidate = null;
            }

            //CatalogName is the last resort, and is itself null on TS1.
            return NormalizeName(candidate) ?? NormalizeName(elem.Item.CatalogName);
        }

        private static string ResolveFromContent(UICatalogElement elem)
        {
            // A special row (floor, wallpaper, roof, wall style, terrain tool) has no object
            // behind it; its provider is what names it.
            if (elem.Special != null) return elem.Special.Res?.GetName(elem.Special.ResID);

            // Guid 0 is a row that is really a placeholder (the builders above leave it 0 for
            // the rows they do not fill in), and NotForSaleGUID is not an object either.
            var guid = elem.Item.GUID;
            if (guid == 0 || guid == NotForSaleGUID) return null;

            //Content is null before it has been initialised, which is not an error here - it just
            //means this row has no name to be had.
            var worldObj = Content.Get()?.WorldObjects?.Get(guid);
            var ctss = worldObj?.Resource?.Get<CTSS>(worldObj.OBJ.CatalogStringsID);
            return ctss?.GetString(0);
        }

        /// <summary>
        /// Identifies a row in a log line. Best effort by design - this runs on the failure path,
        /// so it must not be the thing that throws.
        /// </summary>
        private static string Describe(UICatalogElement elem)
        {
            try
            {
                if (elem == null) return "a null catalogue row";
                if (elem.Special != null) return $"special row {elem.Special.ResID}";
                if (elem.Item.Name != null) return $"0x{elem.Item.GUID:X8} ({elem.Item.Name})";
                return $"0x{elem.Item.GUID:X8}";
            }
            catch (Exception)
            {
                return "a catalogue row";
            }
        }
    }
}
