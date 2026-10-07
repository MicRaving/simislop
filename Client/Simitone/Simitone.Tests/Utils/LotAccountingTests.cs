using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the money half of moving in and out of a lot.
    ///
    /// Moving in charges the lot's SIMI purchase value against the family's budget, but evicting
    /// used to refund only the furniture value (FAMI.ValueInArch). The purchase price was
    /// therefore destroyed on every move, and a family that changed houses a few times went broke
    /// with nothing on screen to explain it. These tests pin the corrected refund (including its
    /// overflow clamp) and the matching affordability check.
    ///
    /// The arithmetic is pure, so it is tested against plain ints rather than a FAMI chunk,
    /// which would drag in a whole neighbourhood and a graphics-free IFF load.
    /// </summary>
    public sealed class LotAccountingTests
    {
        [Fact]
        public void EvictionRefundsFurnitureAndTheLotPrice()
        {
            //budget 1000, 400 of furniture, 20000 for the lot -> the 20000 must come back
            Assert.Equal(21400, LotAccounting.RefundOnEvict(1000, 400, 20000));
        }

        [Fact]
        public void RepeatedMovesNoLongerDestroyMoney()
        {
            //the regression: move in at 20000, then move in again at 20000
            var budget = 50000;

            budget -= 20000; //move in
            budget = LotAccounting.RefundOnEvict(budget, 0, 20000); //evict
            budget -= 20000; //move in again
            budget = LotAccounting.RefundOnEvict(budget, 0, 20000); //evict

            Assert.Equal(50000, budget);
        }

        [Fact]
        public void ALotWithNoPurchaseValueRefundsFurnitureOnly()
        {
            //an unpriced lot (no SIMI chunk) keeps the old behaviour
            Assert.Equal(1500, LotAccounting.RefundOnEvict(1000, 500, 0));
        }

        [Fact]
        public void AFamilyWithNothingBankedStillGetsTheLotPriceBack()
        {
            Assert.Equal(20000, LotAccounting.RefundOnEvict(0, 0, 20000));
        }

        [Theory]
        [InlineData(1000, 0, 1000)]     // nothing to refund
        [InlineData(0, 0, 0)]          // nothing at all
        [InlineData(0, 250, 250)]      // broke, but owned furniture
        [InlineData(-500, 0, 500)]     // in debt, refunded to zero
        [InlineData(-500, 0, 20000)]   // in debt, still ends up with the lot price
        public void RefundIsTheSumOfTheThreeParts(int budget, int valueInArch, int price)
        {
            Assert.Equal(budget + valueInArch + price, LotAccounting.RefundOnEvict(budget, valueInArch, price));
        }

        [Fact]
        public void RefundSaturatesAtIntMaxValueInsteadOfWrapping()
        {
            //unchecked int arithmetic would wrap to a negative budget here
            Assert.Equal(int.MaxValue, LotAccounting.RefundOnEvict(int.MaxValue, int.MaxValue, int.MaxValue));
            Assert.Equal(int.MaxValue, LotAccounting.RefundOnEvict(int.MaxValue, 1, 100000));
        }

        [Fact]
        public void RefundSaturatesAtIntMinValueInsteadOfWrapping()
        {
            Assert.Equal(int.MinValue, LotAccounting.RefundOnEvict(int.MinValue, int.MinValue, int.MinValue));
            Assert.Equal(int.MinValue, LotAccounting.RefundOnEvict(int.MinValue, -1, -100000));
        }

        [Fact]
        public void RefundNeverWrapsAtTheExtremes()
        {
            Assert.Equal(int.MaxValue, LotAccounting.RefundOnEvict(int.MaxValue, 0, 1));
            Assert.Equal(int.MaxValue, LotAccounting.RefundOnEvict(0, 0, int.MaxValue));
            Assert.Equal(int.MinValue, LotAccounting.RefundOnEvict(int.MinValue, 0, -1));
            Assert.Equal(int.MinValue, LotAccounting.RefundOnEvict(0, 0, int.MinValue));
        }

        [Theory]
        [InlineData(20000, 20000, true)] // exactly enough - no off-by-one against the player
        [InlineData(20001, 20000, true)]
        [InlineData(19999, 20000, false)]
        [InlineData(0, 0, true)]         // a free lot is affordable
        [InlineData(-1, 0, false)]       // in debt: a free lot still costs 0, which is 1 too much
        [InlineData(-1, -1, true)]       // a negative price is affordable, debt and all
        [InlineData(0, 1, false)]
        public void AffordabilityComparesTheBudgetAgainstThePrice(int budget, int price, bool expected)
        {
            Assert.Equal(expected, LotAccounting.CanAffordMoveIn(budget, price));
        }

        [Fact]
        public void ANegativePriceIsAlwaysAffordable()
        {
            //a corrupt or negative SIMI purchase value must not lock the player out of the lot
            Assert.True(LotAccounting.CanAffordMoveIn(0, -1));
            Assert.True(LotAccounting.CanAffordMoveIn(0, int.MinValue));
        }
    }
}
