/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The decisions behind <see cref="BuildUndoStack"/>: when a build/buy action's money
    /// round-trips exactly, and the words the panel says about it.
    ///
    /// <para>
    /// <b>Why this is the hard part of undo in this engine.</b> Placing an object is not "create an
    /// entity and take money": it is <c>VMNetBuyObjectCmd</c>, which on the server recomputes the
    /// price from the world catalogue, applies the buildable-area discount, charges the family and
    /// only then creates the object - and only overwrites the new object's
    /// <c>MultitileGroup.InitialPrice</c> with the amount it actually charged. Selling one back
    /// is <c>VMNetDeleteObjectCmd</c>, which refunds <c>MultitileGroup.Price</c>, and that getter
    /// is <em>not</em> the initial price: it is
    /// <c>max(0, min(InitialPrice, InitialPrice * (400 - wear) / 400))</c>.
    /// </para>
    ///
    /// <para>
    /// So an undo that "deletes the thing I just placed" refunds the right number only when the
    /// object has not aged, and an undo that "buys the thing I just sold" charges the right number
    /// only when the catalogue still prices it at the price it was bought for. Both are usually
    /// true and neither is always true, and the difference between the two numbers is exactly the
    /// kind of quiet drift an undo stack must not introduce: the budget would never come back to
    /// where it was.
    /// </para>
    ///
    /// <para>
    /// The rule this type applies is therefore: <b>compute what the engine will do, and refuse the
    /// action if the two numbers are not the same number.</b> An action that cannot be undone
    /// exactly does not enter the stack at all, rather than entering it and being wrong.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure arithmetic over its arguments. The catalogue and the VM are read by
    /// the caller (<c>UIObjectHolder</c>) and handed in as plain numbers, so the rules are pinned
    /// by tests with no content pipeline and no loaded lot. The one exception is
    /// <see cref="CanUndoSale"/>/<see cref="CanUndoPlacement"/>, which take a <see cref="VM"/>
    /// only to read the buildable-area discount out of it - and that read is itself a pure function
    /// of the lot, so the discounting rule is available separately as
    /// <see cref="DiscountPercentFor"/> and the VM-taking overloads are thin.
    /// </para>
    /// </summary>
    public static class BuildUndoPolicy
    {
        // ---------------------------------------------------------------------------------
        // The two formulas, mirrored from the engine.
        //
        // Both are written as the engine writes them and then checked for the one thing an
        // `int` cannot survive. The engine multiplies first and divides second, both in `int`, so
        // it is the *product* that has to fit - a catalogue price high enough to overflow it is not
        // a price this file can reproduce, and so not a price it can promise an exact refund
        // against. Where that happens the caller is told the value is unusable and the action is
        // not recorded, which is the correct answer rather than a cop-out: the engine would be
        // charging a wrapped number and this stack has no business agreeing with it.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// The amount <c>VMNetBuyObjectCmd.Verify</c> charges for a catalogue item:
        /// <c>(price * (100 - discountPercent)) / 100</c>.
        /// </summary>
        /// <param name="cataloguePrice">The item's <c>ObjectCatalogItem.Price</c>.</param>
        /// <param name="discountPercent">
        /// The buildable-area discount, read by the caller from
        /// <c>VMBuildableAreaInfo.GetDiscountFor</c>. Zero in TS1, which is what the engine charges
        /// there; the parameter is kept because the rule is the rule and the tests pin the
        /// discounting case too.
        /// </param>
        /// <param name="charged">The amount charged, when the arithmetic fits.</param>
        /// <returns>
        /// False when the engine's own multiplication - <c>price * (100 - discount)</c>, which it
        /// performs in <c>int</c> before dividing - does not fit, or when the discount is not a
        /// percentage the engine can produce. <paramref name="charged"/> is then 0 and the caller
        /// must not record anything.
        /// </returns>
        public static bool TryChargedPrice(int cataloguePrice, int discountPercent, out int charged)
        {
            charged = 0;

            // A discount outside 0..100 is not a thing the engine can produce, and clamping it
            // here would hide a caller bug behind a plausible number.
            if (discountPercent < 0 || discountPercent > 100) return false;

            long scaled = (long)cataloguePrice * (100L - discountPercent);
            if (scaled < int.MinValue || scaled > int.MaxValue) return false;

            long value = scaled / 100L;
            // Truncation towards zero has to match the engine's int division exactly, and it does
            // for every value the engine itself can reach. A discount that would make even the
            // quotient unrepresentable - int.MaxValue halved by a discount, say - is refused,
            // because there the engine is computing a different number in a different width and
            // agreeing with it is not something this file can promise.
            if (value < int.MinValue || value > int.MaxValue) return false;

            charged = (int)value;
            return true;
        }

        /// <summary>
        /// The amount <c>VMNetDeleteObjectCmd</c> refunds for an object:
        /// <c>VMMultitileGroup.Price</c>, which is
        /// <c>max(0, min(InitialPrice, InitialPrice * (400 - wear) / 400))</c>.
        /// </summary>
        /// <param name="initialPrice">The group's <c>InitialPrice</c>.</param>
        /// <param name="wear">
        /// The object's <c>VMTS1ObjectState.Wear</c>. Zero for a brand new object, which is why a
        /// placement is normally refundable for exactly what it cost. TS1's wear is a ushort and
        /// the engine's arithmetic assumes it stays under 400, so a value at or above that is
        /// treated as unusable rather than being allowed to drive the factor negative.
        /// </param>
        /// <param name="refund">The amount refunded, when the arithmetic fits.</param>
        /// <returns>
        /// False when the inputs are outside the range the engine's own expression is meaningful
        /// for, or when the multiplication would overflow an <c>int</c>. <paramref name="refund"/>
        /// is 0 and the caller must not record anything.
        /// </returns>
        public static bool TryRefundValue(int initialPrice, int wear, out int refund)
        {
            refund = 0;
            if (wear < 0 || wear >= 400) return false;

            long scaled = (long)initialPrice * (400L - wear);
            if (scaled < int.MinValue || scaled > int.MaxValue) return false;

            // Math.Min before the divide is the engine's order and it matters for a negative
            // InitialPrice: min(negative, negative) is the more negative of the two, and the
            // clamp below is what the engine's own Math.Max does.
            long value = Math.Min((long)initialPrice, scaled / 400L);
            if (value < 0) value = 0;
            if (value > int.MaxValue) return false;

            refund = (int)value;
            return true;
        }

        /// <summary>
        /// Whether a money movement round-trips: the amount that left is the amount that comes
        /// back, to the simoleon.
        /// </summary>
        /// <remarks>
        /// The single test every undoable entry has to pass. Both numbers are already known when
        /// it is asked - one from the action, one from what the inverse action will do - so this
        /// is an equality and not an estimate.
        /// </remarks>
        /// <param name="moved">The amount the forward action charged or refunded.</param>
        /// <param name="returned">The amount the inverse action will charge or refund.</param>
        public static bool RefundIsExact(int moved, int returned)
        {
            return moved == returned;
        }

        // ---------------------------------------------------------------------------------
        // The three per-kind decisions.
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Whether undoing a <b>placement</b> is exact.
        /// </summary>
        /// <remarks>
        /// Undoing a placement is a sale: the refund is the new object's
        /// <c>MultitileGroup.Price</c>, and it has to equal what the buy charged. A freshly
        /// created object has <c>Wear == 0</c> and an <c>InitialPrice</c> the buy overwrote with
        /// the amount it charged, so this is true; it is checked rather than assumed because the
        /// two numbers come from two different places and the whole stack is only as trustworthy
        /// as this comparison.
        /// </remarks>
        /// <param name="charged">What the buy charged.</param>
        /// <param name="initialPrice">The placed object's <c>MultitileGroup.InitialPrice</c>.</param>
        /// <param name="wear">The placed object's wear.</param>
        public static bool CanUndoPlacement(int charged, int initialPrice, int wear)
        {
            int refund;
            return TryRefundValue(initialPrice, wear, out refund) && RefundIsExact(charged, refund);
        }

        /// <summary>
        /// Whether undoing a <b>sale</b> is exact.
        /// </summary>
        /// <remarks>
        /// Undoing a sale is a purchase: the charge is recomputed from the catalogue, and it has
        /// to equal what the sale refunded. This is where exactness genuinely fails in practice.
        /// The sale refunds the object's own value, which is whatever it was bought for less its
        /// wear, while the re-purchase charges today's catalogue price; so an object that was
        /// bought at a discount, bought in an earlier session, or has since aged is one whose
        /// undo would move the budget by the difference. Those sales are simply not undoable, and
        /// the player is told so by the button being greyed out rather than by a number quietly
        /// going missing.
        /// </remarks>
        /// <param name="refunded">What the sale refunded, i.e. the group's <c>Price</c>.</param>
        /// <param name="cataloguePrice">The item's current <c>ObjectCatalogItem.Price</c>.</param>
        /// <param name="discountPercent">The buildable-area discount, zero in TS1.</param>
        public static bool CanUndoSale(int refunded, int cataloguePrice, int discountPercent)
        {
            int charged;
            return TryChargedPrice(cataloguePrice, discountPercent, out charged) && RefundIsExact(refunded, charged);
        }

        /// <summary>
        /// The buildable-area discount, in TS1, as a percentage.
        /// </summary>
        /// <remarks>
        /// <c>VMBuildableAreaInfo.GetDiscountFor</c> returns 0 for any TS1 VM before it even looks
        /// at the item, so a buy here charges the catalogue price and nothing else. That makes
        /// zero the right default for a caller that cannot reach the VM - and it is the reason
        /// the exactness rules below are worth writing down at all: with no discount and no wear
        /// the two numbers coincide, so the arithmetic is a formality in TS1 and a real gate in
        /// anything that changes either input.
        /// </remarks>
        public const int Ts1DiscountPercent = 0;

        /// <summary>
        /// As <see cref="CanUndoSale"/>, for TS1, where nothing is discounted.
        /// </summary>
        /// <param name="refunded">What the sale refunded.</param>
        /// <param name="cataloguePrice">The item's catalogue price.</param>
        public static bool CanUndoSale(int refunded, int cataloguePrice)
        {
            return CanUndoSale(refunded, cataloguePrice, Ts1DiscountPercent);
        }

        // ---------------------------------------------------------------------------------
        // What the panel says.
        // ---------------------------------------------------------------------------------

        /// <summary>Button caption for undo.</summary>
        public static string UndoCaption
        {
            get { return SimitoneStrings.Get("550", "Undo"); }
        }

        /// <summary>Button caption for redo.</summary>
        public static string RedoCaption
        {
            get { return SimitoneStrings.Get("551", "Redo"); }
        }

        /// <summary>
        /// One line saying what undo covers and what it does not, for the button's tooltip.
        /// </summary>
        /// <remarks>
        /// Stated in the UI on purpose. The gap between "there is undo" and "there is undo for the
        /// wall you just drew" is the difference between a feature and a bug report, and a player
        /// has no other way of finding out which half they have.
        /// </remarks>
        public static string ScopeNote
        {
            get
            {
                return SimitoneStrings.Get("552",
                    "Undo covers buying, selling back and moving objects. Walls, floors, roofs and terrain are not covered.");
            }
        }

        /// <summary>Tooltip on the undo button: what the next undo will do, and the scope.</summary>
        /// <param name="entry">The action undo would take, or null when there is nothing to take.</param>
        public static string UndoTooltip(BuildUndoEntry entry)
        {
            var scope = ScopeNote;
            if (entry == null) return scope;
            return DescribeAction(entry, "Undo") + " " + scope;
        }

        /// <summary>Tooltip on the redo button: what the next redo will do, and the scope.</summary>
        /// <param name="entry">The action redo would repeat, or null when there is nothing to repeat.</param>
        public static string RedoTooltip(BuildUndoEntry entry)
        {
            var scope = ScopeNote;
            if (entry == null) return scope;
            return DescribeAction(entry, "Redo") + " " + scope;
        }

        /// <summary>
        /// The human sentence for one action, e.g. "Undo: sell back the object at (3, 4, 1)." or
        /// "Redo: buy and place an object at (3, 4, 1) for §300."
        /// </summary>
        /// <param name="entry">The action.</param>
        /// <param name="verb">"Undo" or "Redo" - what is about to happen to it.</param>
        /// <remarks>
        /// The tile is in 1/16ths, which is the net commands' unit and not one a player reads, so
        /// it is divided back down to a tile index here. The direction is a raw
        /// <c>Direction</c> bitfield and is deliberately not decoded: every spelling of those four
        /// values this codebase could print is a guess, and a wrong compass word in a tooltip is
        /// worse than none.
        /// </remarks>
        public static string DescribeAction(BuildUndoEntry entry, string verb)
        {
            if (entry == null) return verb;
            var at = " at tile (" + (entry.X / 16) + ", " + (entry.Y / 16) + ", " + entry.Level + ")";
            switch (entry.Kind)
            {
                case BuildUndoKind.Place:
                    return verb + ": buy and place an object" + at + " for §" + entry.Value;
                case BuildUndoKind.Sell:
                    return verb + ": sell back the object" + at + " for §" + entry.Value;
                case BuildUndoKind.Move:
                    return verb + ": move the object from tile ("
                        + (entry.X / 16) + ", " + (entry.Y / 16) + ", " + entry.Level + ") back to ("
                        + (entry.FromX / 16) + ", " + (entry.FromY / 16) + ", " + entry.FromLevel + ")";
                default:
                    return verb;
            }
        }
    }
}
