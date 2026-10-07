/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Which lot the map's Next and Previous buttons land on.
    ///
    /// <para>
    /// <b>What this is for.</b> The retail neighbourhood view had a <c>Next</c> and a
    /// <c>Previous</c> button (<c>NghUI\Next.bmp</c> and <c>NghUI\Previous.bmp</c>, 104x52
    /// four-state arrows, either side of the filter bar) that stepped through the lots on the map
    /// without the player having to aim at one. This is that, and it is deliberately arithmetic on
    /// an ordered list rather than anything that knows about the map: the list the panel hands in
    /// is already the lots the player can actually pick, in the order they appear on the map, so
    /// the two buttons cannot step onto a lot that a filter has hidden or a lot the view has taken
    /// off limits.
    /// </para>
    /// <para>
    /// <b>The rules, all four of which are the interesting part.</b> It wraps at both ends, because
    /// a "next" that stops at the last lot leaves the player with a button that does nothing and no
    /// way to tell that from a broken one. A selection that is not in the list - nothing selected
    /// yet, or a selection a filter has just hidden - goes to the far end in the direction pressed,
    /// so Next picks the first lot on the map and Previous picks the last, which is what a player
    /// who has not chosen anything expects from a pair of arrows. An empty list, or a direction of
    /// zero, is not a step and comes back as <c>null</c> rather than as a throwaway lot number.
    /// </para>
    /// <para>
    /// Pure, and the reason it lives here rather than in the panel: the panel needs a graphics
    /// device, a content manager and a loaded neighbourhood before it can be constructed at all,
    /// so anything worth pinning down about "next lot" would otherwise be untestable.
    /// </para>
    /// </summary>
    public static class NeighLotNavigator
    {
        /// <summary>Step towards the end of the list. Any positive direction is treated as this.</summary>
        public const int Forward = 1;

        /// <summary>Step towards the start of the list. Any negative direction is treated as this.</summary>
        public const int Backward = -1;

        /// <summary>
        /// The lot one step from <paramref name="current"/>, or <c>null</c> when there is nowhere
        /// to step to.
        /// </summary>
        /// <param name="lots">
        /// The selectable lots, in map order. Not sorted here: the caller owns the order, and the
        /// panel's is the order the buttons were added in, which is top of the map first.
        /// </param>
        /// <param name="current">The lot selected now, or <c>null</c> if nothing is.</param>
        /// <param name="direction">
        /// Positive for next, negative for previous, zero (or anything else) for "do not move".
        /// </param>
        public static int? Step(IReadOnlyList<int> lots, int? current, int direction)
        {
            if (lots == null || lots.Count == 0) return null;
            if (direction == 0) return null;

            var forward = direction > 0;
            var index = IndexOf(lots, current);
            if (index < 0)
            {
                // nothing selected, or the selection is not one of the lots we may step to. Next
                // starts at the top of the map, previous at the bottom, so either key does
                // something visible on the first press.
                return forward ? lots[0] : lots[lots.Count - 1];
            }

            int step = index + (forward ? 1 : -1);
            if (step < 0) step = lots.Count - 1;
            else if (step >= lots.Count) step = 0;
            return lots[step];
        }

        /// <summary>
        /// The first lot a step from nothing would land on, which is the lot Next opens on. Split
        /// out because "what does Next pick when nothing is selected" is a rule worth asserting
        /// on its own, and asserting it through <see cref="Step"/> would only ever be a statement
        /// about the <c>null</c> case.
        /// </summary>
        public static int? First(IReadOnlyList<int> lots, int direction)
        {
            return Step(lots, null, direction);
        }

        /// <summary>
        /// Where <paramref name="lot"/> sits in <paramref name="lots"/>, or -1 when it is not
        /// there. A linear search, deliberately: a neighbourhood map is tens of lots, not
        /// thousands, and the list changes whenever a filter is pressed.
        /// </summary>
        public static int IndexOf(IReadOnlyList<int> lots, int? lot)
        {
            if (lots == null || !lot.HasValue) return -1;
            for (int i = 0; i < lots.Count; i++)
            {
                if (lots[i] == lot.Value) return i;
            }
            return -1;
        }

        /// <summary>
        /// The tooltip text for one of the two buttons. On its own rather than inline in the panel
        /// so the two captions cannot drift apart from the two directions.
        /// </summary>
        /// <param name="direction">Positive for next, negative for previous.</param>
        public static string Caption(int direction)
        {
            return direction > 0
                ? SimitoneStrings.Get("250", "Next lot")
                : SimitoneStrings.Get("251", "Previous lot");
        }

        /// <summary>
        /// The CustomUI file name of one of the two buttons' four-state arrow art.
        /// </summary>
        public static string IconName(int direction)
        {
            return direction > 0 ? "ngbh_lot_next.png" : "ngbh_lot_previous.png";
        }
    }
}
