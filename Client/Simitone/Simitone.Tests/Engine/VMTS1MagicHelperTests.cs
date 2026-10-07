/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Utils;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The engine half of Makin' Magic's <c>FamilySpellsIntoController</c> (TS1 global call 43):
    /// which neighbourhood-inventory entries count as spells, and the merge that hands the
    /// family's to the sim that asked.
    ///
    /// <para>
    /// This is the counterpart to <c>MakingMagicTests</c> on the Simitone side, and the two meet
    /// on two numbers. The spellbook panel lists the spells the <em>reader</em> owns, because
    /// that is the only inventory it can reach
    /// (<c>MakingMagicCaller.InventoryEntries</c>); the engine's only way to make a family
    /// purchase visible to the rest of the family is 43. The token types are not an engine enum -
    /// they are the <c>TokenType</c> byte of <c>manage_inventory</c> (opcode 51) as the
    /// expansion's own scripts write it, and both sides hard-code them, so they are pinned here
    /// against the client-side copies.
    /// </para>
    ///
    /// <para>
    /// The property that has to hold whatever retail actually did is that the merge is
    /// <b>additive, deduplicated and count-preserving</b>. A spell is bought once for the family;
    /// the call cannot be allowed to consume one, duplicate a family's whole spellbook, or take
    /// a count off a charm somebody is holding. The worst case if the direction of the copy is
    /// wrong is one extra token, and that is the design goal.
    /// </para>
    /// </summary>
    public sealed class VMTS1MagicHelperTests
    {
        // Spell objects, named after the spells the way the catalogue does. The GUIDs are
        // arbitrary but stable: the merge only ever compares them.
        private const uint Banish = 0x1000_0001;
        private const uint LoveStruck = 0x1000_0002;
        private const uint Enchant = 0x1000_0003;
        private const uint Nectar = 0x1000_0011; // a cookbook ingredient
        private const uint BirthdayCake = 0x1000_0012;

        private static InventoryItem Token(int type, uint guid, ushort count = 1)
        {
            return new InventoryItem() { Type = type, GUID = guid, Count = count };
        }

        private static List<InventoryItem> Inventory(params InventoryItem[] items)
        {
            return new List<InventoryItem>(items);
        }

        // ---- the token types, cross-checked against the client side ------------------------

        [Fact]
        public void TheTokenTypesAreTheOnesTheExpansionsOwnScriptsWrite()
        {
            // manage_inventory (opcode 51) TokenType: "DEBUG - Get All Spells" uses 7,
            // "DEBUG - Get All Components" uses 8.
            Assert.Equal(7, VMTS1MagicHelper.MagicTokenType);
            Assert.Equal(8, VMTS1MagicHelper.IngredientTokenType);
            Assert.Equal(new[] { 7, 8 }, VMTS1MagicHelper.TokenTypes);
        }

        [Theory]
        [InlineData(7, true)]   // a spell
        [InlineData(8, true)]   // a cookbook component
        [InlineData(2, false)]  // the sim-data/motive entry type calls 17 and 21 use
        [InlineData(0, false)]
        [InlineData(99, false)]
        public void OnlyTheExpansionsOwnEntryTypesAreMagicTokens(int type, bool expected)
        {
            Assert.Equal(expected, VMTS1MagicHelper.IsMagicToken(Token(type, Banish)));
        }

        [Fact]
        public void ASpentTokenIsNotATokenTheFamilyHas()
        {
            // manage_inventory can leave an entry behind with its count taken to zero, and
            // copying those would put a used-up spell back in the book.
            Assert.False(VMTS1MagicHelper.IsMagicToken(Token(VMTS1MagicHelper.MagicTokenType, Banish, 0)));
        }

        [Fact]
        public void ANullEntryIsNotAToken()
        {
            Assert.False(VMTS1MagicHelper.IsMagicToken(null));
        }

        [Fact]
        public void CountingTokensIgnoresEverythingElseInTheInventory()
        {
            var inventory = Inventory(
                Token(VMTS1MagicHelper.MagicTokenType, Banish),
                Token(2, 0, 600),                              // a motive
                Token(VMTS1MagicHelper.MagicTokenType, Enchant),
                Token(2, 10, 1),                               // an auto-follow request
                Token(VMTS1MagicHelper.IngredientTokenType, Nectar),
                Token(VMTS1MagicHelper.MagicTokenType, LoveStruck, 0)); // spent

            Assert.Equal(3, VMTS1MagicHelper.CountTokens(inventory));
            Assert.Equal(0, VMTS1MagicHelper.CountTokens(null));
        }

        // ---- the hand-off ------------------------------------------------------------------

        [Fact]
        public void ASpellALearnerHoldsIsHandedToTheSimWhoAsked()
        {
            var controller = Inventory();
            var learner = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish));

            var added = VMTS1MagicHelper.MergeFamilyTokens(controller, new[] { controller, learner });

            Assert.Equal(1, added);
            Assert.Equal((uint)Banish, Assert.Single(controller).GUID);
            Assert.Equal(VMTS1MagicHelper.MagicTokenType, controller[0].Type);
            Assert.Equal(1, controller[0].Count);
        }

        [Fact]
        public void TheControllersOwnSpellsAreInTheSourcesTooAndAreNotCopiedTwice()
        {
            // The caller is normally in the family, so the merge reads the caller's own list as
            // well as writing to it. Reading and writing the same list is only safe because of
            // the dedupe, and this is the case that proves it.
            var controller = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish));

            var added = VMTS1MagicHelper.MergeFamilyTokens(controller, new[] { controller });

            Assert.Equal(0, added);
            Assert.Single(controller);
        }

        [Fact]
        public void TwoFamilyMembersWithTheSameSpellContributeItOnce()
        {
            var controller = Inventory();
            var one = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Enchant));
            var two = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Enchant));

            Assert.Equal(1, VMTS1MagicHelper.MergeFamilyTokens(controller, new[] { one, two }));
            Assert.Single(controller);
        }

        [Fact]
        public void CallingItRepeatedlyDoesNotInflateTheSpellbook()
        {
            // A lot script cannot know whether it has already called, and the spellbook panel
            // would otherwise gain a row every time.
            var controller = Inventory();
            var family = new[] { Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish)) };

            VMTS1MagicHelper.MergeFamilyTokens(controller, family);
            VMTS1MagicHelper.MergeFamilyTokens(controller, family);
            VMTS1MagicHelper.MergeFamilyTokens(controller, family);

            Assert.Single(controller);
        }

        [Fact]
        public void ASpellIsNeverConsumedOrItsCountChanged()
        {
            var controller = Inventory();
            var learner = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish, 1));

            VMTS1MagicHelper.MergeFamilyTokens(controller, new[] { controller, learner });

            Assert.Single(learner);
            Assert.Equal(1, learner[0].Count);
            Assert.Equal((ushort)1, Assert.Single(controller).Count);
        }

        [Fact]
        public void CookbookComponentsTravelWithSpellsAndAreNotConfusedWithThem()
        {
            var controller = Inventory();
            var family = new[]
            {
                Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish)),
                Inventory(Token(VMTS1MagicHelper.IngredientTokenType, Nectar)),
                Inventory(Token(VMTS1MagicHelper.IngredientTokenType, BirthdayCake)),
            };

            Assert.Equal(3, VMTS1MagicHelper.MergeFamilyTokens(controller, family));
            Assert.Equal(3, controller.Count);
            Assert.Equal(1, controller.Count(x => x.Type == VMTS1MagicHelper.MagicTokenType));
            Assert.Equal(2, controller.Count(x => x.Type == VMTS1MagicHelper.IngredientTokenType));
        }

        [Fact]
        public void TheSameObjectHeldUnderEitherTokenTypeCountsAsAlreadyHeld()
        {
            // The dedupe is on the GUID, across both token types, not on (type, GUID). A spell
            // is identified by the object its GUID names, and the spellbook panel matches on the
            // catalogue name, so a second row for the same object under the other type would be
            // the same entry twice in the list the player reads. The shipped data keeps the two
            // sets disjoint, so this costs nothing there and it means a hand-edited save cannot
            // grow a duplicate row.
            var controller = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Nectar));
            var family = new[] { Inventory(Token(VMTS1MagicHelper.IngredientTokenType, Nectar)) };

            Assert.Equal(0, VMTS1MagicHelper.MergeFamilyTokens(controller, family));
            Assert.Single(controller);
        }

        [Fact]
        public void NothingElseInTheFamilysInventoryIsEverCopied()
        {
            var controller = Inventory();
            var family = new[]
            {
                Inventory(
                    Token(2, 0, 600),       // motive data
                    Token(2, 10, 1),       // an auto-follow request
                    Token(VMTS1MagicHelper.MagicTokenType, LoveStruck, 0), // spent
                    Token(VMTS1MagicHelper.MagicTokenType, Enchant))
            };
            Assert.Equal(1, VMTS1MagicHelper.MergeFamilyTokens(controller, family));
            Assert.Equal((uint)Enchant, Assert.Single(controller).GUID);
        }

        [Fact]
        public void AFamilyMemberWithNoInventoryContributesNothingRatherThanFailing()
        {
            var controller = Inventory();
            var family = new List<InventoryItem>[] { null, Inventory(), Inventory(
                Token(VMTS1MagicHelper.MagicTokenType, Banish)) };

            Assert.Equal(1, VMTS1MagicHelper.MergeFamilyTokens(controller, family));
        }

        [Fact]
        public void AMissingControllerOrFamilyListIsRefusedRatherThanThrown()
        {
            var inventory = Inventory();
            Assert.Equal(0, VMTS1MagicHelper.MergeFamilyTokens(null, new[] { inventory }));
            Assert.Equal(0, VMTS1MagicHelper.MergeFamilyTokens(inventory, null));
            Assert.Empty(inventory);
        }

        [Fact]
        public void TheCopiedEntryIsASeparateObjectFromTheSourceOne()
        {
            // The controller's list and the family member's are two different saved lists; an
            // aliased entry would mean casting a spell mutated the learner's inventory.
            var controller = Inventory();
            var learner = Inventory(Token(VMTS1MagicHelper.MagicTokenType, Banish));

            VMTS1MagicHelper.MergeFamilyTokens(controller, new[] { learner });

            Assert.NotSame(learner[0], controller[0]);
            Assert.Equal(learner[0].GUID, controller[0].GUID);
            Assert.Equal(learner[0].Type, controller[0].Type);
            Assert.Equal(learner[0].Count, controller[0].Count);
        }

        [Fact]
        public void HasTokenOnlyAnswersForTheExpansionsOwnEntryTypes()
        {
            var inventory = Inventory(Token(2, Banish), Token(VMTS1MagicHelper.MagicTokenType, Enchant));

            Assert.True(VMTS1MagicHelper.HasToken(inventory, VMTS1MagicHelper.MagicTokenType, Enchant));
            // A type the caller is not allowed to merge is never "already there", so the
            // non-magic entry can never be used to block a real spell.
            Assert.False(VMTS1MagicHelper.HasToken(inventory, 2, Banish));
            Assert.False(VMTS1MagicHelper.HasToken(inventory, VMTS1MagicHelper.MagicTokenType, LoveStruck));
            Assert.False(VMTS1MagicHelper.HasToken(null, VMTS1MagicHelper.MagicTokenType, Enchant));
        }
    }
}
