using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The inventory panel's section filter, exercised through
    /// <see cref="InventorySections"/>, and the family MagiCoin balance's caption through
    /// <see cref="MagiCoinFormat"/>.
    ///
    /// <para>
    /// Two token sections were hidden outright. One of them is Vacation's - the six vacation
    /// tokens, the hotel key, the prize tokens, and the Vacation score itself - so the whole
    /// Vacation reward chain, which the engine and the shipped scripts both implement, could be
    /// earned and never seen. The other is a second section nothing in the tree identifies.
    /// Both are shown now; the first has a button of its own and the second is deliberately
    /// left unnamed rather than given a guess.
    /// </para>
    ///
    /// <para>
    /// Pure static code: no graphics device, no content, no statics.
    /// </para>
    /// </summary>
    public sealed class InventorySectionsTests
    {
        // ---- the sections that used to be hidden ----------------------------------------------

        [Fact]
        public void TheVacationSectionIsNoLongerHidden()
        {
            // This is the whole point of the change. A section that is hidden from the "Other"
            // tab and has no button of its own is a section that does not exist as far as a
            // player is concerned, however completely the engine implements it.
            Assert.DoesNotContain(InventorySections.VacationTokenType, InventorySections.HiddenByDefault);
            Assert.True(InventorySections.Matches(InventorySections.VacationTokenType, InventorySections.SortAll));
        }

        [Fact]
        public void TheUnidentifiedSectionIsNoLongerHiddenEither()
        {
            // Nothing in this tree says what section 2 holds. It is shown rather than hidden,
            // but with no button and no name: visible, and not mislabelled.
            Assert.DoesNotContain(InventorySections.UnidentifiedTokenType, InventorySections.HiddenByDefault);
            Assert.True(InventorySections.Matches(InventorySections.UnidentifiedTokenType, InventorySections.SortAll));
        }

        [Fact]
        public void TheTwoSectionsWithButtonsOfTheirOwnAreStillHeldBackFromOther()
        {
            // Otherwise "Other" would be the whole inventory three times over, and the two
            // buttons that were already there would stop narrowing anything.
            Assert.Contains(InventorySections.MagicTokenType, InventorySections.HiddenByDefault);
            Assert.Contains(InventorySections.IngredientTokenType, InventorySections.HiddenByDefault);
            Assert.False(InventorySections.Matches(InventorySections.MagicTokenType, InventorySections.SortAll));
            Assert.False(InventorySections.Matches(InventorySections.IngredientTokenType, InventorySections.SortAll));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(9)]
        [InlineData(255)]
        public void EveryOtherSectionTypeIsShownByTheOtherTab(int tokenType)
        {
            // The token type is a raw byte the shipped scripts write, so an unknown value has to
            // read as "show it" rather than being dropped by a whitelist.
            Assert.True(InventorySections.Matches(tokenType, InventorySections.SortAll));
        }

        [Fact]
        public void TheHiddenSetIsAFreshSetEachTime()
        {
            // This used to be a public field on the panel, so anything holding a reference to it
            // could change what every panel instance considered visible.
            var first = InventorySections.HiddenByDefault;
            first.Add(99);
            Assert.DoesNotContain(99, InventorySections.HiddenByDefault);
        }

        // ---- the four buttons -------------------------------------------------------------------

        [Fact]
        public void TheButtonsAreTheFourSortsThePanelOffers()
        {
            Assert.Equal(4, InventorySections.ButtonSorts.Length);
            Assert.Equal(InventorySections.MagicTokenType, InventorySections.ButtonSorts[0]);
            Assert.Equal(InventorySections.IngredientTokenType, InventorySections.ButtonSorts[1]);
            Assert.Equal(InventorySections.SortAll, InventorySections.ButtonSorts[2]);
            Assert.Equal(InventorySections.VacationTokenType, InventorySections.ButtonSorts[3]);
        }

        [Fact]
        public void EveryButtonSortIsDistinct()
        {
            Assert.Equal(InventorySections.ButtonSorts.Length, InventorySections.ButtonSorts.Distinct().Count());
        }

        [Fact]
        public void EveryButtonSortSelectsSomething()
        {
            // A button that selects a value nothing can satisfy renders as a permanently empty
            // list with no explanation, and the player has no way to tell it apart from a bug.
            foreach (int sort in InventorySections.ButtonSorts)
            {
                Assert.True(InventorySections.Matches(sort, sort));
            }
        }

        [Fact]
        public void TheVacationButtonShowsOnlyVacationTokens()
        {
            Assert.True(InventorySections.Matches(6, InventorySections.VacationTokenType));
            foreach (int other in new[] { 2, 5, 7, 8 })
            {
                Assert.False(InventorySections.Matches(other, InventorySections.VacationTokenType));
            }
        }

        [Fact]
        public void TheOtherTabIsNotTheSameAsEverySort()
        {
            Assert.NotEqual(InventorySections.SortAll, InventorySections.MagicTokenType);
            Assert.Equal(-1, InventorySections.SortAll);
        }

        // ---- the token types are the engine's, not ours -----------------------------------------

        [Fact]
        public void TheMagicTokenTypesAreTheOnesTheSpellbookUses()
        {
            // MakingMagic.MagicTokenType / IngredientTokenType are the same two numbers, read
            // out of the TokenType byte of opcode 51 in the expansion's own scripts. The
            // inventory buttons and the spellbook panel showing different sections would put
            // the same tokens under two different headings.
            Assert.Equal(MakingMagic.MagicTokenType, InventorySections.MagicTokenType);
            Assert.Equal(MakingMagic.IngredientTokenType, InventorySections.IngredientTokenType);
        }

        // ---- the MagiCoin balance -----------------------------------------------------------------

        [Fact]
        public void TheMagiCoinCaptionNamesTheBalance()
        {
            Assert.Equal("MagiCoins: 1,250", MagiCoinFormat.Caption(1250));
        }

        [Fact]
        public void ABalanceOfZeroIsStillABalance()
        {
            // A family that has never been to Magic Town reads as zero, and the panel has to say
            // so: an empty caption is indistinguishable from the panel not having run yet.
            Assert.Equal("MagiCoins: 0", MagiCoinFormat.Caption(0));
        }

        [Fact]
        public void AnUnreadBalanceReadsAsZeroRatherThanAsNothing()
        {
            // The same reason a null relationship table renders as an empty list: a state the
            // panel can be in before the neighbourhood answers has to draw something.
            Assert.Equal("MagiCoins: 0", MagiCoinFormat.Caption(null));
        }

        [Fact]
        public void ATranslatedCaptionStillCarriesTheNumber()
        {
            // %s is the number, and it is the number the whole caption exists to show.
            Assert.Contains("1250", MagiCoinFormat.Caption(1250).Replace(",", ""));
        }

        [Fact]
        public void TheCaptionIdIsTheOneTheTableCarries()
        {
            Assert.Equal("540", MagiCoinFormat.CaptionId);
        }
    }
}
