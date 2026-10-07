/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Utils;
using Simitone.Client.UI.Screens;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Single authority for "how much money does the player have right now".
    /// The budget used to be read from two divergent places - TS1GameScreen.ActiveFamily.Budget
    /// (the FAMI resource as loaded from disk) and vm.TS1State.CurrentFamily.Budget (the VM's
    /// runtime copy) - which could disagree. Everything now goes through here.
    /// </summary>
    public static class BudgetService
    {
        /// <summary>
        /// The game screen currently being shown, if it is a gameplay lot screen.
        /// </summary>
        private static TS1GameScreen CurrentGameScreen
        {
            get
            {
                var screens = GameFacade.Screens;
                if (screens == null) return null;
                return screens.CurrentUIScreen as TS1GameScreen;
            }
        }

        /// <summary>
        /// The family that is currently being played. Prefers the VM's runtime copy
        /// (vm.TS1State.CurrentFamily) and falls back to the screen's ActiveFamily
        /// (eg. while on the neighbourhood/house select screens, before a lot is loaded).
        /// </summary>
        public static FAMI CurrentFamily
        {
            get
            {
                var screen = CurrentGameScreen;
                if (screen == null) return null;

                var vm = screen.vm;
                if (vm != null)
                {
                    var state = vm.TS1State;
                    if (state != null && state.CurrentFamily != null) return state.CurrentFamily;
                }

                return screen.ActiveFamily;
            }
        }

        /// <summary>
        /// The current family budget, or int.MaxValue when no family is active
        /// (matches the "no limit" convention used by UILotControl.Budget).
        /// </summary>
        public static int GetBudget()
        {
            var family = CurrentFamily;
            if (family == null) return int.MaxValue;
            return family.Budget;
        }

        /// <summary>
        /// The value of the architecture currently placed on the lot, 0 if there is none.
        /// </summary>
        public static int GetSpentInArchitecture()
        {
            var screen = CurrentGameScreen;
            var vm = screen?.vm;
            if (vm == null || vm.Context == null || vm.Context.Architecture == null) return 0;
            return VMArchitectureStats.GetArchValue(vm.Context.Architecture);
        }

        /// <summary>
        /// Budget plus the value of everything built on the lot.
        /// </summary>
        public static int GetTotalValue()
        {
            return GetBudget() + GetSpentInArchitecture();
        }

        /// <summary>
        /// True when the current family can afford the given price. Always true when the
        /// budget is unlimited (no active family) or when the price is free.
        /// </summary>
        public static bool CanAfford(int price)
        {
            var budget = GetBudget();
            if (budget == int.MaxValue) return true;
            return price <= budget;
        }

        /// <summary>
        /// The budget as it should be shown to the player. Unlike <see cref="GetBudget"/>
        /// this never returns the int.MaxValue "unlimited" sentinel - there is no family to
        /// show money for in that case, so the HUD reads §0 rather than §2,147,483,647.
        /// </summary>
        public static int GetDisplayBudget()
        {
            var family = CurrentFamily;
            return family == null ? 0 : family.Budget;
        }
    }
}
