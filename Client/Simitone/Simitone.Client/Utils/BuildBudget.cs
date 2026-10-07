/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Translation between Simitone's notion of money and the TSO avatar budget that the stock
    /// build/buy controls read.
    ///
    /// The six build tools (<c>UIWallPlacer</c>, <c>UIFloorPainter</c>, <c>UIWallPainter</c>,
    /// <c>UIGrassPaint</c>, <c>UIRoofer</c>, <c>UITerrainFlatten</c>/<c>UITerrainRaiser</c>) and
    /// the buy catalogue all gate affordability on
    /// <c>Parent.ActiveEntity.TSOState.Budget.Value</c>. In TS1 mode nothing ever writes that
    /// field - <c>VMNetAsyncResponseCmd</c> explicitly skips TS1 - so it is stuck at whatever
    /// Simitone seeded it with (1000000) and every build tool is permanently affordable.
    ///
    /// Simitone's real budget lives in <see cref="BudgetService"/> (the family's FAMI resource).
    /// <see cref="TsoBudgetFor"/> converts it into the uint the VM field holds, and
    /// <see cref="CanAfford"/> is the matching test for the (signed, sentinel-bearing) budget
    /// figure that the catalogue panel compares prices against.
    ///
    /// Both members are deliberately pure: the sync point in <c>UILotControl.Update</c> calls them
    /// every frame, and the catalogue calls <see cref="CanAfford"/> while building a row, so
    /// neither may touch content, textures or the graphics device.
    /// </summary>
    public static class BuildBudget
    {
        /// <summary>
        /// The value to store in <c>VMTSOEntityState.Budget.Value</c> for a given family budget.
        ///
        /// - <paramref name="familyBudget"/> null means "no family is active" (the player is on the
        ///   neighbourhood/house select screens, or the lot has no vm yet). <see cref="BudgetService"/>
        ///   reports that same state as int.MaxValue, i.e. unlimited, so the VM is given
        ///   <see cref="uint.MaxValue"/> and the build tools stay enabled rather than locking the
        ///   player out of building on a lot whose family failed to load.
        /// - Zero or negative means the family is broke (or the FAMI budget is corrupt). Clamped to
        ///   0 so the build tools correctly refuse everything; a negative uint would wrap to
        ///   ~4 billion and make the lot infinitely buildable.
        /// - Anything else is clamped to <see cref="int.MaxValue"/> so a large-but-representable
        ///   budget can never overflow the long-promoted comparison the build tools perform.
        /// </summary>
        /// <param name="familyBudget">
        /// The family's budget, or null when no family is active. See <see cref="BudgetService.CurrentFamily"/>.
        /// </param>
        /// <returns>The uint to assign to the avatar's TSO budget.</returns>
        public static uint TsoBudgetFor(int? familyBudget)
        {
            if (familyBudget == null) return uint.MaxValue; //no family -> unlimited, as BudgetService reports
            if (familyBudget.Value <= 0) return 0u;
            return (uint)Math.Min(int.MaxValue, familyBudget.Value);
        }

        /// <summary>
        /// Whether <paramref name="price"/> is within <paramref name="budget"/>, using
        /// <see cref="BudgetService"/>'s conventions: <see cref="int.MaxValue"/> is the
        /// "unlimited" sentinel and nothing is out of reach against it.
        ///
        /// This is the same test the stock client applies to catalogue rows
        /// (<c>elem.SetDisabled(elem.Info.CalcPrice &gt; Budget)</c>), kept here so the rule lives
        /// in one place and can be exercised without a UI. A discounted price that has fallen to or
        /// below zero is always affordable, matching the stock behaviour.
        /// </summary>
        /// <param name="price">The price to pay, typically <c>UICatalogElement.CalcPrice</c>.</param>
        /// <param name="budget">The budget to pay from, typically <c>BudgetService.GetBudget()</c>.</param>
        /// <returns>True when the price can be paid.</returns>
        public static bool CanAfford(int price, int budget)
        {
            if (budget == int.MaxValue) return true; //unlimited sentinel
            return price <= budget;
        }
    }
}
