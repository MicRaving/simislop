using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the money mapping that the buy/build tools depend on.
    ///
    /// The six build tools and the buy catalogue all ask
    /// <c>ActiveEntity.TSOState.Budget.Value</c> (a <c>uint</c>) whether the player can pay, while
    /// Simitone's actual money lives in the family FAMI resource as a signed <c>int</c> that
    /// <see cref="BudgetService"/> reports, with <see cref="int.MaxValue"/> standing in for
    /// "unlimited" when no family is active. <see cref="BuildBudget"/> is the only thing standing
    /// between those two, and <c>UILotControl.Update</c> re-applies it every frame, so the three
    /// edge cases - no family, broke family, maxed family - are the ones that decide whether a
    /// player can build at all.
    ///
    /// These are pure functions, so they are asserted directly; nothing here needs a VM, a
    /// GraphicsDevice or a loaded lot.
    /// </summary>
    public sealed class BuildBudgetTests
    {
        // ------------------------------------------------- TsoBudgetFor: no family

        [Fact]
        public void WithNoFamily_TheTsoBudgetIsUnlimited()
        {
            // null is what BudgetService.CurrentFamily? yields when the player is on the
            // neighbourhood / house select screens, or on a lot whose vm has no family yet.
            // Anything less than unlimited here would silently lock building on that lot.
            Assert.Equal(uint.MaxValue, BuildBudget.TsoBudgetFor(null));
        }

        // ------------------------------------------------- TsoBudgetFor: broke family

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(-500)]
        [InlineData(int.MinValue)]
        public void ABrokeFamilyGetsNoBudget(int familyBudget)
        {
            // A negative family budget is corrupt data, but it must not reach the uint field: the
            // unchecked cast would wrap to ~4 billion and make the lot infinitely buildable.
            Assert.Equal(0u, BuildBudget.TsoBudgetFor(familyBudget));
        }

        // ------------------------------------------------- TsoBudgetFor: normal family

        [Theory]
        [InlineData(1, 1u)]
        [InlineData(85, 85u)]
        [InlineData(20000, 20000u)]
        [InlineData(1234567, 1234567u)]
        public void ANormalFamilyBudgetIsPassedThroughUnchanged(int familyBudget, uint expected)
        {
            Assert.Equal(expected, BuildBudget.TsoBudgetFor(familyBudget));
        }

        [Fact]
        public void TheLargestFamilyBudgetIsPassedThroughUnchanged()
        {
            // int.MaxValue doubles as the "unlimited" sentinel in the signed budget, so it has to
            // survive the uint conversion too - clamping it away would make a rich family poor.
            Assert.Equal((uint)int.MaxValue, BuildBudget.TsoBudgetFor(int.MaxValue));
        }

        [Fact]
        public void TheTsoBudgetNeverExceedsIntMaxValue()
        {
            // The build tools write `cost > TSOState.Budget.Value` with cost an int, so the
            // comparison is done in long and the budget must stay inside int range to mean
            // anything. A sentinel above int.MaxValue would be a budget no int cost can exceed,
            // which happens to be right, but a *cast* above it would not be.
            Assert.True(BuildBudget.TsoBudgetFor(int.MaxValue) <= int.MaxValue);
            Assert.True(BuildBudget.TsoBudgetFor(null) > int.MaxValue); //explicitly the wider sentinel
        }

        // ------------------------------------- TsoBudgetFor agrees with the build tools' own test

        [Fact]
        public void TheBudgetADeliveredFamilyGetsIsExactlyWhatTheBuildToolsCompareAgainst()
        {
            // This is the actual production check, `cost > Parent.ActiveEntity.TSOState.Budget.Value`
            // from UIWallPlacer/UIFloorPainter/UIRoofer, applied to the value BuildBudget produces.
            var budget = BuildBudget.TsoBudgetFor(20000);

            Assert.False(20000 > budget); //the whole §20,000 room is affordable
            Assert.True(20001 > budget); //one simole more is not
        }

        [Fact]
        public void NoFamilyDoesNotLockTheBuildToolsOut()
        {
            // With no family the seeded 1000000 must remain affordable, otherwise a lot whose
            // family failed to load cannot be built on at all.
            var budget = BuildBudget.TsoBudgetFor(null);

            Assert.False(1000000 > budget);
        }

        [Fact]
        public void ABrokeFamilyLocksTheBuildToolsOut()
        {
            // The mirror of the test above: everything with a cost is out of reach, including the
            // §1 the terrain tools are nominally priced at.
            var budget = BuildBudget.TsoBudgetFor(0);

            Assert.True(1 > budget);
        }

        // ------------------------------------------------- CanAfford (catalogue rows)

        [Fact]
        public void EverythingIsAffordableAgainstTheUnlimitedSentinel()
        {
            // BudgetService.GetBudget() returns int.MaxValue when no family is active.
            Assert.True(BuildBudget.CanAfford(0, int.MaxValue));
            Assert.True(BuildBudget.CanAfford(int.MaxValue, int.MaxValue));
            Assert.True(BuildBudget.CanAfford(1000000, int.MaxValue));
        }

        [Theory]
        [InlineData(19999, 20000)]
        [InlineData(20000, 20000)] //exactly affordable - the stock client's test is strict >
        [InlineData(0, 20000)]
        public void APriceWithinTheBudgetIsAffordable(int price, int budget)
        {
            Assert.True(BuildBudget.CanAfford(price, budget));
        }

        [Theory]
        [InlineData(20001, 20000)]
        [InlineData(int.MaxValue, 20000)]
        public void APriceOverTheBudgetIsNotAffordable(int price, int budget)
        {
            Assert.False(BuildBudget.CanAfford(price, budget));
        }

        [Fact]
        public void ADiscountedPriceAtOrBelowZeroIsAlwaysAffordable()
        {
            // CalcPrice is the post-discount figure, so a heavy discount can drive it to zero or
            // below. Zero is the floor: a broke family must still be able to place free things.
            Assert.True(BuildBudget.CanAfford(0, 0));
            Assert.True(BuildBudget.CanAfford(-1, 0));
        }
    }
}
