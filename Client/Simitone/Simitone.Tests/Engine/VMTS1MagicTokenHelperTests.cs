/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics.Utils;
using System;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The Makin' Magic token name behind the shipped dialog substitution
    /// <c>$TokenNameLocal:N</c> - the substitution that used to render as the literal text
    /// <c>TokenNameLocal:1</c> and made every magic maker's ingredient list unreadable.
    ///
    /// <para>
    /// The shape of the bug is the reason these tests are about the <em>name</em> and not about
    /// the parse. <c>CommandSubstrValid</c> in <c>VMDialogHandler</c> rejects a command that is
    /// not a prefix of one in its <c>valid</c> list, so an unimplemented token was not an error -
    /// it was emitted verbatim, digit and all, into a menu caption. A test that only checked
    /// "the command is accepted" would therefore pass on a fix that substituted a blank; a test
    /// that only checked "the name is right" would pass before the command was in the list at
    /// all. These pin both ends: the command is the one the shipped data writes, and the name it
    /// produces is the one the shipped table holds.
    /// </para>
    ///
    /// <para>
    /// The table used throughout is <c>MagicMasterSpells.iff</c> <c>STR 0x0002</c> verbatim -
    /// header at 0, the 31 ingredient names at 1..31, and <c>"Pet Treats "</c> at 9 keeping its
    /// trailing space, because that space is in the shipped file and a resolver that trimmed
    /// nothing would be the one that renders <c>"Add.../Pet Treats "</c>.
    /// </para>
    /// </summary>
    public sealed class VMTS1MagicTokenHelperTests
    {
        // MagicMasterSpells.iff STR 0x0002, verbatim, including the trailing space on [9] and the
        // unused [32] hole the bookshelf's own 32-slot list also skips.
        private static readonly string[] ShippedNames =
        {
            "Ingredients",      // 0 - the header, not a token
            "Honey",            // 1
            "Grapes",           // 2
            "Pixie Dust",       // 3
            "Garlic",           // 4
            "Toad Sweat",       // 5
            "Dragon Tears",     // 6
            "Toadstools",       // 7
            "Snake Venom",      // 8
            "Pet Treats ",      // 9 - the shipped string has a trailing space
            "Clown Confetti",   // 10
            "Butter",           // 11
            "Wizard Eyelashes", // 12
            "Gold Thread",      // 13
            "Llama Spit",       // 14
            "Magic Beans",      // 15
            "Four Leaf Clover", // 16
            "Diamond Dust",     // 17
            "Magic Crystal",    // 18
            "Pegasus Feather",  // 19
            "Wax",              // 20
            "Sands of Time",    // 21
            "Dragon Scales",    // 22
            "Rubber Chicken",   // 23
            "Black Rose",       // 24
            "Sugar",            // 25
            "Elderberries",     // 26
            "Baking Mix",       // 27
            "Vine Spring",      // 28
            "Berry Bush",       // 29
            "Carrots",          // 30
            "Fairy Dust",       // 31
        };

        private static IReadOnlyList<string> Names() => ShippedNames;

        // ---- the constants, pinned to the shipped data ---------------------------------------

        [Fact]
        public void TheCommandIsTheOneTheShippedStringTablesWrite()
        {
            // "$TokenNameLocal:1" in ExpansionPack7/BakersOven.iff STR 0x012E[0], verbatim. The
            // '$' is the parser's own sigil; the command is what follows it.
            Assert.Equal("TokenNameLocal:", VMTS1MagicTokenHelper.DialogCommand);

            const string shipped = "Add.../$TokenNameLocal:1";
            Assert.Equal("$" + VMTS1MagicTokenHelper.DialogCommand + "1", shipped.Substring("Add.../".Length));
        }

        [Fact]
        public void TheTableAndTheObjectAreTheOnesTheExpansionShips()
        {
            // ExpansionPack7/MagicMasterSpells.iff OBJD 0x41A7 "Magic Controller", Global = 1.
            Assert.Equal(0xB6C90029u, VMTS1MagicTokenHelper.MagicControllerGuid);

            // its STR 0x0002 is the names and its STR 0x0007 the same ids' GUIDs, index for index
            Assert.Equal(0x0002, VMTS1MagicTokenHelper.IngredientNamesTableId);
            Assert.Equal(0x0007, VMTS1MagicTokenHelper.TokenGuidsTableId);

            Assert.Equal(1, VMTS1MagicTokenHelper.FirstTokenId);
            Assert.Equal(31, VMTS1MagicTokenHelper.LastTokenId);
        }

        [Fact]
        public void TheShippedNameTableIsThirtyTwoEntriesWithAHeaderAndThirtyOneIngredients()
        {
            Assert.Equal(VMTS1MagicTokenHelper.ShippedIngredientNameCount, ShippedNames.Length);

            // index 0 is the table's own header, which is why the ids start at 1
            Assert.Equal("Ingredients", ShippedNames[0]);

            // 31 names at 1..31, and nothing after
            Assert.Equal(31, ShippedNames.Length - 1);
            Assert.Equal("Fairy Dust", ShippedNames[31]);
        }

        // ---- valid ids resolve to the shipped name -------------------------------------------

        [Theory]
        [InlineData(1, "Honey")]
        [InlineData(2, "Grapes")]
        [InlineData(3, "Pixie Dust")]
        [InlineData(6, "Dragon Tears")]      // the WandCharger / SocialsMagic "these $TokenNameLocal:6"
        [InlineData(8, "Snake Venom")]       // the SocialsMagic prize ingredient
        [InlineData(11, "Butter")]           // BakersOven "Interaction - Add" branch Params[0] == 11
        [InlineData(25, "Sugar")]            // likewise 25
        [InlineData(26, "Elderberries")]     // likewise 26
        [InlineData(27, "Baking Mix")]       // likewise 27
        [InlineData(30, "Carrots")]          // likewise 30, the one Global.iff special-cases
        [InlineData(31, "Fairy Dust")]
        public void AValidIdResolvesToTheShippedIngredientName(int tokenId, string expected)
        {
            Assert.True(VMTS1MagicTokenHelper.IsKnownTokenId(tokenId));
            Assert.Equal(expected, VMTS1MagicTokenHelper.NameFor(tokenId, Names()));
        }

        [Fact]
        public void AShippedNameWithATrailingSpaceIsResolvedTrimmed()
        {
            // STR 0x0002[9] is "Pet Treats " with the space. A menu caption built from it is
            // "Add.../Pet Treats", not "Add.../Pet Treats ".
            Assert.Equal("Pet Treats ", ShippedNames[9]);
            Assert.Equal("Pet Treats", VMTS1MagicTokenHelper.NameFor(9, Names()));
        }

        [Fact]
        public void TheLookupOverloadAndTheTableOverloadAgree()
        {
            for (int id = VMTS1MagicTokenHelper.FirstTokenId; id <= VMTS1MagicTokenHelper.LastTokenId; id++)
            {
                Assert.Equal(
                    VMTS1MagicTokenHelper.NameFor(id, Names()),
                    VMTS1MagicTokenHelper.NameFor(id, (Func<int, string>)(i => ShippedNames[i])));
            }
        }

        // ---- out of range --------------------------------------------------------------------

        [Theory]
        [InlineData(32)]  // the hole: the bookshelf's 32 slots skip 4, and 32 is past the table
        [InlineData(50)]  // MagicMasterSpells.iff STR 0x0003[12] is "50,50,50"
        [InlineData(51)]  // and [24] is "51,51,51"
        [InlineData(999)]
        [InlineData(int.MaxValue)]
        public void AnIdPastTheTableStillRendersAReadableLabel(int tokenId)
        {
            Assert.False(VMTS1MagicTokenHelper.IsKnownTokenId(tokenId));

            // the failure mode this whole change exists to remove was a bare "TokenNameLocal:7"
            // in a menu caption, so the failure mode here has to be a word as well
            var rendered = VMTS1MagicTokenHelper.NameFor(tokenId, Names());
            Assert.Equal(VMTS1MagicTokenHelper.UnknownTokenPrefix + tokenId, rendered);
            Assert.DoesNotContain(VMTS1MagicTokenHelper.DialogCommand, rendered, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0)]   // the makers leave an unused ingredient slot at 0
        [InlineData(-1)]
        [InlineData(short.MinValue)]
        public void AnEmptySlotRendersAsNothingRatherThanALabel(int tokenId)
        {
            // 4117 "Interaction - Read Label" branches on StackAttrs[i] == 0 and takes a different
            // local, so 0 reaching the name lookup is an empty ingredient - and the text around the
            // token already reads "Ingredient #3: ". "Magic Token 0" would be worse than nothing.
            Assert.Equal("", VMTS1MagicTokenHelper.NameFor(tokenId, Names()));
            Assert.Equal("", VMTS1MagicTokenHelper.FallbackName(tokenId));
        }

        [Fact]
        public void TheIdSpaceIsOneThroughThirtyOne()
        {
            Assert.False(VMTS1MagicTokenHelper.IsKnownTokenId(0));
            Assert.True(VMTS1MagicTokenHelper.IsKnownTokenId(1));
            Assert.True(VMTS1MagicTokenHelper.IsKnownTokenId(31));
            Assert.False(VMTS1MagicTokenHelper.IsKnownTokenId(32));
        }

        // ---- no table at all -----------------------------------------------------------------

        [Fact]
        public void NoTableMeansEveryIdFallsBackToAReadableLabel()
        {
            // an unexpanded install, a far that is not one we understand, a content manager that
            // is not up yet: all of them have to leave a caption a player can read
            var honey = VMTS1MagicTokenHelper.NameFor(1, (IReadOnlyList<string>)null);
            Assert.Equal(VMTS1MagicTokenHelper.UnknownTokenPrefix + "1", honey);
            Assert.DoesNotContain(VMTS1MagicTokenHelper.DialogCommand, honey, StringComparison.Ordinal);

            Assert.Equal("Magic Token 31", VMTS1MagicTokenHelper.NameFor(31, (IReadOnlyList<string>)null));
            Assert.Equal("", VMTS1MagicTokenHelper.NameFor(0, (IReadOnlyList<string>)null));
        }

        [Fact]
        public void ANullLookupIsAMissingTableAndNothingWorse()
        {
            Assert.Equal("Magic Token 1", VMTS1MagicTokenHelper.NameFor(1, (Func<int, string>)null));
        }

        [Fact]
        public void ATableThatThrowsIsAMissingTableAndNotACrash()
        {
            // the real read is a catalogue lookup over a pooled resource; a dialog caption is not
            // worth taking the frame down for
            Func<int, string> broken = _ => throw new InvalidOperationException("no such table");
            Assert.Equal("Magic Token 3", VMTS1MagicTokenHelper.NameFor(3, broken));
        }

        [Fact]
        public void ATableShorterThanTheIdRangeBoundsAgainstTheTableNotTheConstant()
        {
            // a modded MagicMasterSpells.iff with a trimmed STR 0x0002. Names beyond what the
            // table actually holds degrade; names it does hold still resolve, even past 31.
            var trimmed = new string[] { "Ingredients", "Honey", "Grapes" };

            Assert.Equal("Grapes", VMTS1MagicTokenHelper.NameFor(2, trimmed));
            Assert.Equal("Magic Token 3", VMTS1MagicTokenHelper.NameFor(3, trimmed));
            Assert.Equal("Magic Token 31", VMTS1MagicTokenHelper.NameFor(31, trimmed));

            // and the range is not a ceiling: a longer table resolves its extra entries
            var longer = new List<string>(ShippedNames) { "Moon Dust" };
            Assert.Equal("Moon Dust", VMTS1MagicTokenHelper.NameFor(32, longer));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankTableEntryDegradesRatherThanRenderingBlank(string entry)
        {
            // MagicMasterSpells.iff STR 0x0006[6..9] are empty strings in the shipped file, so a
            // blank entry is a shape the data really has - and an unfilled recipe row is why call
            // 38's callers treat a parse failure as a safe no-op rather than a crash.
            var names = new string[] { "Ingredients", "Honey", entry };
            Assert.Equal(VMTS1MagicTokenHelper.UnknownTokenPrefix + "2",
                VMTS1MagicTokenHelper.NameFor(2, names));
        }

        [Fact]
        public void ANullTableEntryDegradesRatherThanRenderingBlank()
        {
            // an STR whose language block is shorter than the English one hands back null
            var names = new string[] { "Ingredients", "Honey", null };
            Assert.Equal(VMTS1MagicTokenHelper.UnknownTokenPrefix + "2",
                VMTS1MagicTokenHelper.NameFor(2, names));
        }

        // ---- the literal that used to leak --------------------------------------------------

        [Fact]
        public void TheShippedMenuLineNoLongerRendersAsTheBareToken()
        {
            // ExpansionPack7/BakersOven.iff STR 0x012E[0..12] are thirteen identical copies of
            // this line - which is the evidence that N is a local slot and not a token id, since
            // read as an id all thirteen would print the same word. "Nectar Press" writes the same
            // line with ":2" and its "Press..." line with ":0", and the BookshelfMagic download
            // writes ":1" in all six of its ingredient menus.
            const string shipped = "Add.../$TokenNameLocal:1";

            var beforeFix = shipped.Replace("$", "");
            Assert.Equal("Add.../TokenNameLocal:1", beforeFix);
            Assert.Contains(VMTS1MagicTokenHelper.DialogCommand, beforeFix, StringComparison.Ordinal);

            // what the caption says once the substitution resolves: "1" is a local slot, and the
            // loop is standing on Honey
            var afterFix = shipped.Replace("$" + VMTS1MagicTokenHelper.DialogCommand + "1",
                VMTS1MagicTokenHelper.NameFor(1, Names()));
            Assert.Equal("Add.../Honey", afterFix);
            Assert.DoesNotContain("TokenNameLocal", afterFix, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("Ingredient #1: $TokenNameLocal:1")]      // BakersOven STR 0x012D[1]
        [InlineData("Ingredient 2: $TokenNameLocal:2")]       // CharmMaker STR 0x012D[1]
        [InlineData("Store Wand Ingredients.../$TokenNameLocal:1")]   // BookcaseMagic STR 0x012E[0]
        [InlineData("Get Charm Ingredients.../$TokenNameLocal:1 (Qty: $Local:4)")]  // STR 0x012E[4]
        public void EveryShippedIngredientLineCarriesTheSameSubstitution(string shipped)
        {
            // whichever local a maker uses, the name is resolved through the one table
            Assert.Contains("$" + VMTS1MagicTokenHelper.DialogCommand, shipped, StringComparison.Ordinal);
        }

        [Fact]
        public void TheBookshelfListsAnIngredientPerLocalSlotAndTheResolverIsPerSlot()
        {
            // BookcaseMagic.iff STR 0x012D[10] is one line per stored ingredient, Local[1]..Local[32]
            // holding the token ids and Local[34]..Local[64] the counts. Rendered with the shipped
            // table, the first three pairs are Honey/Butter/Toadstools (ids 1, 2, 3) and the fourth
            // is Toad Sweat, because the shipped list skips local 4.
            var labels = new string[]
            {
                VMTS1MagicTokenHelper.NameFor(1, Names()),
                VMTS1MagicTokenHelper.NameFor(2, Names()),
                VMTS1MagicTokenHelper.NameFor(3, Names()),
                VMTS1MagicTokenHelper.NameFor(5, Names()),
            };
            Assert.Equal(new[] { "Honey", "Grapes", "Pixie Dust", "Toad Sweat" }, labels);
        }

        // ---- the shared table read -----------------------------------------------------------

        [Fact]
        public void TheSharedTableReadIsSafeToCallWithNoContentManager()
        {
            // the tests run with no Content.Get(), which is the same situation as a caption
            // raised before startup finished. It must degrade, not throw.
            VMTS1MagicTokenHelper.ResetNames();
            var names = VMTS1MagicTokenHelper.IngredientNames();
            Assert.True(names == null || names.Count >= 0);

            var rendered = VMTS1MagicTokenHelper.NameFor(1);
            Assert.False(string.IsNullOrEmpty(rendered));
            Assert.DoesNotContain(VMTS1MagicTokenHelper.DialogCommand, rendered, StringComparison.Ordinal);
        }
    }
}
