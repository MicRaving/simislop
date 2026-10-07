/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Content.Interfaces;
using Simitone.Client.UI.Panels.LiveSubpanels;
using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The pure, headless half of the buy/build catalogue browser.
    ///
    /// <see cref="UIBuyBrowsePanel"/> owns the buttons, tweens and textures; the three questions it
    /// actually asks about a category - "what sub-sort does this item have in the current lot type",
    /// "is this subcategory button worth showing", and "which items belong in it" - are pure
    /// functions of (subcategory, mode, the full item list). They used to be inline instance methods
    /// reading the panel's <c>Mode</c> and <c>FullCategory</c> fields, which made them untestable
    /// without a live UI and duplicated a subtly-different mask rule in two places.
    ///
    /// The bodies here are exact copies of the original inline logic. In particular the
    /// Normal-mode mask-7 divergence between <see cref="SubcatIsEmpty"/> and <see cref="Filter"/> is
    /// pre-existing behaviour and is preserved deliberately - see the note on <see cref="SubcatIsEmpty"/>.
    /// </summary>
    public static class CatalogQueryService
    {
        /// <summary>
        /// The sub-sort byte for <paramref name="item"/> under <paramref name="mode"/>.
        /// Downtown and friends have their own per-lot-type sort column; everything else (Normal and
        /// Build) uses the generic <c>Subsort</c>, except that an item with no <c>RoomSort</c> is
        /// forced to 0 so it is hidden by every subcategory.
        /// </summary>
        public static byte GetSubsort(ObjectCatalogItem item, UICatalogMode mode)
        {
            switch (mode)
            {
                case UICatalogMode.Downtown:
                    return item.DowntownSort;
                case UICatalogMode.Community:
                    return item.CommunitySort;
                case UICatalogMode.Vacation:
                    return item.VacationSort;
                case UICatalogMode.Studiotown:
                    return item.StudiotownSort;
                case UICatalogMode.Magictown:
                    return item.MagictownSort;
                default:
                    if (item.RoomSort == 0) return 0; //items without a room sort should not appear.
                    return item.Subsort;
            }
        }

        /// <summary>
        /// Whether the subcategory button for <paramref name="cat"/> should render greyed out.
        ///
        /// Build mode ignores the mask entirely - a build button is enabled as soon as the category
        /// has any items at all. Mask bit 8 is the shared "all" button, which is a bare
        /// <c>GetSubsort &gt; 0</c> test rather than a bit test, so it catches every subcategory at
        /// once. Every other mask is a single-bit test.
        ///
        /// NOTE (pre-existing divergence, intentionally preserved): for Normal mode mask bit 7
        /// ("other") this method does <b>not</b> OR in bit 16, while <see cref="Filter"/> does. An
        /// item whose sub-sort is 16-only therefore greys the "other" button out yet still appears in
        /// its results. Do not "fix" the asymmetry here - it is upstream FreeSO behaviour and any
        /// change would alter what the player sees. The behaviour is pinned by tests.
        /// </summary>
        public static bool SubcatIsEmpty(UICatalogSubcat cat, UICatalogMode mode, IEnumerable<UICatalogElement> full)
        {
            var index = cat.MaskBit;
            if (mode == UICatalogMode.Build)
            {
                return full.FirstOrDefault() == null;
            }
            else if (index == 8)
            {
                return !full.Any(x => (GetSubsort(x.Item, mode)) > 0);
            }
            else
            {
                var mask = 1 << index;
                return !full.Any(x => (GetSubsort(x.Item, mode) & mask) > 0);
            }
        }

        /// <summary>
        /// The items shown once <paramref name="cat"/> is selected, in the original list order.
        ///
        /// Build mode re-filters by the mask bit as a build category id and so returns
        /// <paramref name="full"/> unfiltered; the panel re-calls <c>InitCategory</c> first and then
        /// overwrites the result anyway. Mask bit 8 ("all") keeps the bare
        /// <c>GetSubsort &gt; 0</c> test, and the Normal-mode mask-7 case ORs in bit 16 - the one
        /// place where this method and <see cref="SubcatIsEmpty"/> disagree, again on purpose.
        /// </summary>
        public static IEnumerable<UICatalogElement> Filter(UICatalogSubcat cat, UICatalogMode mode, IEnumerable<UICatalogElement> full)
        {
            var index = cat.MaskBit;
            if (mode == UICatalogMode.Build)
            {
                return full;
            }
            else if (index == 8)
            {
                return full.Where(x => (GetSubsort(x.Item, mode)) > 0);
            }
            else
            {
                var mask = 1 << index;
                if (mode == UICatalogMode.Normal && index == 7)
                {
                    mask |= 16;
                }
                return full.Where(x => (GetSubsort(x.Item, mode) & mask) > 0);
            }
        }
    }
}
