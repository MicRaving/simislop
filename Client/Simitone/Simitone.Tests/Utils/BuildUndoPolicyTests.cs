using System;
using System.IO;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the rule that decides whether a build/buy action may be undone at all: what the
    /// engine charges for a buy, what it refunds for a sale, and whether those are the same number.
    ///
    /// <para>
    /// This is the part of undo that a player would notice going wrong. A stack that is off by one
    /// simoleon per undo does not look broken - the object still disappears, the money still comes
    /// back - it just quietly makes a family poorer every time they change their mind, and nothing
    /// on screen ever says so. So the two formulas are pinned against the engine's own expressions
    /// here, and the exactness decisions are pinned against the cases where they come apart.
    /// </para>
    ///
    /// <para>
    /// Everything is arithmetic over plain arguments, so none of this needs a VM, a catalogue or a
    /// loaded lot. The caller supplies the three numbers the engine would supply.
    /// </para>
    /// </summary>
    public sealed class BuildUndoPolicyTests
    {
        // ------------------------------------------------- what a buy charges

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(300, 300)]
        [InlineData(20000, 20000)]
        [InlineData(1234567, 1234567)]
        public void WithNoDiscountTheBuyChargesTheCataloguePrice(int cataloguePrice, int expected)
        {
            // This is TS1 exactly: VMBuildableAreaInfo.GetDiscountFor returns 0 for any TS1 VM
            // before it looks at the item, so value = (price * 100) / 100 = price.
            int charged;
            Assert.True(BuildUndoPolicy.TryChargedPrice(cataloguePrice, BuildUndoPolicy.Ts1DiscountPercent, out charged));
            Assert.Equal(expected, charged);
        }

        [Theory]
        [InlineData(300, 50, 150)]
        [InlineData(300, 90, 30)]
        [InlineData(300, 100, 0)]
        [InlineData(301, 50, 150)] //integer division truncates, as the engine's does
        public void ABuildableAreaDiscountReducesTheCharge(int cataloguePrice, int discount, int expected)
        {
            int charged;
            Assert.True(BuildUndoPolicy.TryChargedPrice(cataloguePrice, discount, out charged));
            Assert.Equal(expected, charged);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(101)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        public void ADiscountTheEngineCannotProduceIsRefusedRatherThanClamped(int discount)
        {
            // Clamping would hide a caller bug behind a plausible number, and the caller would then
            // record a charge the engine never agreed to.
            int charged;
            Assert.False(BuildUndoPolicy.TryChargedPrice(300, discount, out charged));
            Assert.Equal(0, charged);
        }

        [Fact]
        public void APriceTheEnginesOwnMultiplicationWouldOverflowIsRefused()
        {
            // VMNetBuyObjectCmd.Verify computes (price * (100 - dc)) / 100 in int, so the
            // *product* is what has to fit, not the quotient. int.MaxValue x 100 does not, and a
            // number this file cannot reproduce is not a number it can promise an exact refund
            // against - so the caller is told to record nothing at all.
            int charged;
            Assert.False(BuildUndoPolicy.TryChargedPrice(int.MaxValue, 0, out charged));
            Assert.Equal(0, charged);

            // A discount does not rescue it either, for the same reason: the engine multiplies by
            // (100 - dc) and then divides, so a 50% discount still multiplies int.MaxValue by 50,
            // which also does not fit.
            Assert.False(BuildUndoPolicy.TryChargedPrice(int.MaxValue, 50, out charged));
            Assert.Equal(0, charged);

            //But a discounted price that does fit is used, not refused. 30,000,000 x 50 is well
            //inside an int, so the engine's arithmetic is the real one and its answer can be
            //matched exactly.
            Assert.True(BuildUndoPolicy.TryChargedPrice(30000000, 50, out charged));
            Assert.Equal(15000000, charged);
        }

        [Fact]
        public void ARoundNumberABuyCanActuallyAffordIsNeverRefused()
        {
            // A guard that rejects real catalogue prices would silently turn the whole feature off.
            for (int price = 0; price <= 1000000; price += 7)
            {
                int charged;
                Assert.True(BuildUndoPolicy.TryChargedPrice(price, 0, out charged));
                Assert.Equal(price, charged);
            }
        }

        // ------------------------------------------------- what a sale refunds

        [Theory]
        [InlineData(300, 0, 300)]
        [InlineData(300, 200, 150)]
        [InlineData(400, 100, 300)]
        [InlineData(0, 0, 0)]
        [InlineData(100, 399, 0)] //almost fully worn: truncates to nothing, and never goes below it
        public void TheRefundIsTheObjectsValueLessItsWear(int initialPrice, int wear, int expected)
        {
            // VMMultitileGroup.Price is max(0, min(InitialPrice, InitialPrice * (400 - wear) / 400)).
            int refund;
            Assert.True(BuildUndoPolicy.TryRefundValue(initialPrice, wear, out refund));
            Assert.Equal(expected, refund);
        }

        [Fact]
        public void AnObjectNeverRefundsMoreThanItWasBoughtFor()
        {
            // The Math.Min in the engine's expression, and the reason a heavily worn object is
            // worth nothing rather than something.
            int refund;
            Assert.True(BuildUndoPolicy.TryRefundValue(100, 0, out refund));
            Assert.Equal(100, refund);
            Assert.True(refund <= 100);
        }

        [Fact]
        public void ANegativeInitialPriceRefundsNothingRatherThanGoingNegative()
        {
            // The engine's own Math.Max(0, ...) floor. A corrupt save must not credit the family.
            int refund;
            Assert.True(BuildUndoPolicy.TryRefundValue(-500, 0, out refund));
            Assert.Equal(0, refund);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(400)]
        [InlineData(1000)]
        [InlineData(ushort.MaxValue)]
        public void AWearValueTheEnginesExpressionIsNotMeaningfulForIsRefused(int wear)
        {
            int refund;
            Assert.False(BuildUndoPolicy.TryRefundValue(300, wear, out refund));
            Assert.Equal(0, refund);
        }

        [Fact]
        public void AValueTheEnginesOwnMultiplicationWouldOverflowIsRefused()
        {
            int refund;
            Assert.False(BuildUndoPolicy.TryRefundValue(int.MaxValue, 1, out refund));
            Assert.Equal(0, refund);
        }

        // ------------------------------------------------------ the equality itself

        [Theory]
        [InlineData(300, 300, true)]
        [InlineData(300, 299, false)]
        [InlineData(0, 0, true)]
        [InlineData(0, 1, false)]
        [InlineData(-1, -1, true)]
        public void RefundIsExactIsAnEqualityAndNothingElse(int moved, int returned, bool expected)
        {
            Assert.Equal(expected, BuildUndoPolicy.RefundIsExact(moved, returned));
        }

        // --------------------------------------------- can a placement be undone?

        [Fact]
        public void ANewlyBoughtObjectUndoesForExactlyWhatItCost()
        {
            // The case that has to work: a fresh object has wear 0 and an InitialPrice the buy
            // overwrote with the amount it charged, so deleting it refunds that same amount.
            int charged;
            Assert.True(BuildUndoPolicy.TryChargedPrice(300, 0, out charged));
            Assert.True(BuildUndoPolicy.CanUndoPlacement(charged, initialPrice: 300, wear: 0));
        }

        [Fact]
        public void ABrandNewObjectRefundsItsCataloguePriceForEveryPrice()
        {
            // The invariant the whole place-undo rests on, over the range of prices a TS1 lot can
            // actually hold. If this ever fails, undoing a placement quietly changes the budget.
            for (int price = 0; price <= 200000; price += 13)
            {
                int charged;
                Assert.True(BuildUndoPolicy.TryChargedPrice(price, 0, out charged));
                Assert.True(BuildUndoPolicy.CanUndoPlacement(charged, price, 0),
                    "price " + price + " would not undo exactly");
            }
        }

        [Fact]
        public void APlacementIsNotUndoableWhenTheEngineWouldChargeAndRefundDifferentNumbers()
        {
            // Charged 300, but the object is stamped with a different value: undoing would refund
            // 400 and leave the family 100 richer than it was.
            Assert.False(BuildUndoPolicy.CanUndoPlacement(300, initialPrice: 400, wear: 0));
        }

        [Fact]
        public void APlacementIsUndoableWhenWearAndTheStampHappenToCancelOut()
        {
            // Charged 300, stamped 400, worn down to 300. The two numbers agree, so the budget
            // comes back exactly, and refusing this would be refusing a correct undo.
            Assert.True(BuildUndoPolicy.CanUndoPlacement(300, initialPrice: 400, wear: 100));
        }

        [Fact]
        public void APlacementIsNotUndoableWhenTheObjectsValueCannotEvenBeRead()
        {
            Assert.False(BuildUndoPolicy.CanUndoPlacement(300, initialPrice: 300, wear: 400));
        }

        // ------------------------------------------------ can a sale be undone?

        [Fact]
        public void ASaleOfAnObjectBoughtAtTodaysPriceUndoesExactly()
        {
            // The normal case: the family bought the object this session at catalogue price, so
            // selling it refunds what it cost and buying it back charges the same.
            Assert.True(BuildUndoPolicy.CanUndoSale(refunded: 300, cataloguePrice: 300));
        }

        [Fact]
        public void ASaleIsNotUndoableWhenTheCataloguePriceHasMovedSince()
        {
            // The sale refunds what the object is worth to the family; the re-purchase charges what
            // the catalogue says today. If those differ the budget moves by the difference, so the
            // sale is simply not undoable and the button stays greyed out.
            Assert.False(BuildUndoPolicy.CanUndoSale(refunded: 250, cataloguePrice: 300));
            Assert.False(BuildUndoPolicy.CanUndoSale(refunded: 300, cataloguePrice: 250));
        }

        [Fact]
        public void ASaleIsNotUndoableForAnObjectThatHasAged()
        {
            // Bought at 400, worn down to 250: selling refunds 250, buying back charges 400.
            Assert.False(BuildUndoPolicy.CanUndoSale(refunded: 250, cataloguePrice: 400));
        }

        [Fact]
        public void ASaleUndoesExactlyWhenTheDiscountMakesTheRechargeMatchTheRefund()
        {
            // The reason CanUndoSale takes a discount at all. With a 50% buildable-area discount a
            // 300 catalogue item is charged at 150, so a sale that refunded 150 is exactly
            // reversible - and the same sale would not be if the discount had not applied.
            Assert.True(BuildUndoPolicy.CanUndoSale(refunded: 150, cataloguePrice: 300, discountPercent: 50));
            Assert.False(BuildUndoPolicy.CanUndoSale(refunded: 300, cataloguePrice: 300, discountPercent: 50));
        }

        [Fact]
        public void ASaleOfAFreeObjectUndoesExactly()
        {
            // A §0 object: sold for nothing, bought back for nothing. Still a real action the
            // player may want to take back, and the budget genuinely does come back.
            Assert.True(BuildUndoPolicy.CanUndoSale(refunded: 0, cataloguePrice: 0));
        }

        // --------------------------------------------------------- what the panel says

        [Fact]
        public void BothButtonsHaveACaptionEvenWithNoStringTable()
        {
            // A test process never builds GameFacade.Strings, so this is the fallback path: the
            // English hardcoded at the call site, which is what a stripped install with no
            // Content/UI/uitext folder shows. Nothing here may be empty or a ***MISSING***.
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.False(string.IsNullOrWhiteSpace(BuildUndoPolicy.UndoCaption));
            Assert.False(string.IsNullOrWhiteSpace(BuildUndoPolicy.RedoCaption));
            Assert.Equal("Undo", BuildUndoPolicy.UndoCaption);
            Assert.Equal("Redo", BuildUndoPolicy.RedoCaption);
        }

        [Fact]
        public void TheScopeLineNamesWhatIsNotCovered()
        {
            // The sentence that keeps this from reading as a bug. Walls, floors, roofs and terrain
            // are architecture commands against shared lot state and are out of scope by design, so
            // the player is told which half they have.
            var scope = BuildUndoPolicy.ScopeNote;
            Assert.False(string.IsNullOrWhiteSpace(scope));
            Assert.Contains("Walls", scope);
            Assert.Contains("terrain", scope);
        }

        [Fact]
        public void ATooltipWithNothingBehindItStillCarriesTheScope()
        {
            // The greyed-out state is the one a player reads most, and it is the one most likely to
            // be mistaken for "this button is broken".
            Assert.Equal(BuildUndoPolicy.ScopeNote, BuildUndoPolicy.UndoTooltip(null));
            Assert.Equal(BuildUndoPolicy.ScopeNote, BuildUndoPolicy.RedoTooltip(null));
        }

        [Fact]
        public void ATooltipWithAnEntryNamesTheActionAndKeepsTheScope()
        {
            var entry = new BuildUndoEntry
            {
                Kind = BuildUndoKind.Place,
                Guid = 0x2000u,
                X = 16 * 5, //tile 5
                Y = 16 * 7, //tile 7
                Level = 1,
                Value = 300,
                Settled = true,
                Exact = true
            };

            var tooltip = BuildUndoPolicy.UndoTooltip(entry);
            Assert.Contains("Undo", tooltip);
            Assert.Contains("5, 7", tooltip); //tiles, not sixteenths - a player reads tiles
            Assert.Contains("300", tooltip);
            Assert.Contains(BuildUndoPolicy.ScopeNote, tooltip);

            var redo = BuildUndoPolicy.RedoTooltip(entry);
            Assert.Contains("Redo", redo);
            Assert.Contains(BuildUndoPolicy.ScopeNote, redo);
        }

        [Theory]
        [InlineData(BuildUndoKind.Place, "buy and place")]
        [InlineData(BuildUndoKind.Sell, "sell back")]
        [InlineData(BuildUndoKind.Move, "move the object from")]
        public void EveryKindOfActionDescribesItselfInPlainWords(BuildUndoKind kind, string expected)
        {
            var entry = new BuildUndoEntry
            {
                Kind = kind,
                X = 16,
                Y = 32,
                Level = 1,
                FromX = 48,
                FromY = 64,
                FromLevel = 2,
                Value = 125,
                Settled = true,
                Exact = true
            };
            Assert.Contains(expected, BuildUndoPolicy.DescribeAction(entry, "Undo"));
        }

        [Fact]
        public void AnActionWithNoKindDescribesItselfWithoutThrowing()
        {
            // The one state a record can be in that the stack refuses; a caller logging it must not
            // have to special-case it.
            var text = BuildUndoPolicy.DescribeAction(new BuildUndoEntry(), "Undo");
            Assert.Equal("Undo", text);
            Assert.Equal("Undo", BuildUndoPolicy.DescribeAction(null, "Undo"));
        }

        // ------------------------------------------------- the shipped string table

        [ContentTheory]
        [InlineData("550", "Undo")]
        [InlineData("551", "Redo")]
        [InlineData("552", "Undo covers buying, selling back and moving objects. Walls, floors, roofs and terrain are not covered.")]
        public void TheIdsTheUndoButtonsAskForAreTheEnglishTheyUsedToShow(string id, string english)
        {
            // The table is a move of the literals, not a rewording: if the English here drifts from
            // the fallback at the call site, every language silently gains a different word for the
            // same button.
            var text = File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory));
            var table = SimitoneStrings.ParseTable(text);
            Assert.True(table.ContainsKey(id), "No entry for id " + id);
            Assert.Equal(english, table[id]);
        }
    }
}
