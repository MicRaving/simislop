using FSO.Client;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// BudgetService is the single authority for the player's money. All of it hangs off
    /// <see cref="GameFacade.Screens"/> - a plain static field that is null in a headless test
    /// host, and null in the real game too whenever the player is on the neighbourhood or house
    /// select screens rather than a loaded lot. That "no family is active" state is therefore a
    /// genuine production state, not a test-only stub, and it is the only branch of
    /// BudgetService that can be reached without constructing a screen (and therefore without a
    /// GraphicsDevice).
    ///
    /// The contract being locked in here is the sentinel convention: int.MaxValue means
    /// "unlimited" everywhere except on screen, where the HUD must read §0.
    /// </summary>
    public sealed class BudgetServiceTests
    {
        /// <summary>
        /// Guards the precondition the rest of the class relies on. If some other test ever
        /// wires a game screen up, these tests must fail loudly rather than silently start
        /// asserting the wrong branch.
        /// </summary>
        private static void AssertNoActiveGameScreen()
        {
            Assert.Null(GameFacade.Screens);
            Assert.Null(BudgetService.CurrentFamily);
        }

        [Fact]
        public void WithNoActiveFamily_TheBudgetIsTheUnlimitedSentinel()
        {
            AssertNoActiveGameScreen();

            Assert.Equal(int.MaxValue, BudgetService.GetBudget());
        }

        [Fact]
        public void WithNoActiveFamily_NothingIsSpentInArchitecture()
        {
            AssertNoActiveGameScreen();

            Assert.Equal(0, BudgetService.GetSpentInArchitecture());
        }

        [Fact]
        public void WithNoActiveFamily_TheTotalValueIsStillTheUnlimitedSentinel()
        {
            AssertNoActiveGameScreen();

            // budget (int.MaxValue) + 0. Asserted explicitly because the alternative is a
            // silent integer overflow to int.MinValue, which is what would show up in the HUD.
            Assert.Equal(int.MaxValue, BudgetService.GetTotalValue());
        }

        [Fact]
        public void WithNoActiveFamily_EverythingIsAffordable()
        {
            AssertNoActiveGameScreen();

            Assert.True(BudgetService.CanAfford(0));
            Assert.True(BudgetService.CanAfford(1));
            Assert.True(BudgetService.CanAfford(int.MaxValue));
            // an unlimited budget must not be defeated by a negative price either
            Assert.True(BudgetService.CanAfford(-500));
        }

        [Fact]
        public void WithNoActiveFamily_TheDisplayBudgetIsZeroRatherThanTheSentinel()
        {
            AssertNoActiveGameScreen();

            // UIDesktopUCP.GetMoney() reads this and renders "§" + value, so leaking the
            // sentinel here would put §2,147,483,647 on screen.
            Assert.Equal(0, BudgetService.GetDisplayBudget());
        }
    }
}
