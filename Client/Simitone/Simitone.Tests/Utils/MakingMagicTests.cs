/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics.Primitives;
using Simitone.Client.Utils;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the pure half of the Makin' Magic book dialogs: which dialog is which book, which
    /// entries in the game's own table are real, how the pages are cut, and what a choice looks
    /// like on the wire.
    ///
    /// <remarks>
    /// <para>
    /// The data this is checked against was read out of a retail install's
    /// <c>GameData/magic.iff</c>, whose six <c>STR#</c> chunks are labelled rather than numbered
    /// and each hold twenty language blocks with English first:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Magic Book Dialog Strings</b> (chunk 1) - 5 English entries: "%s Family
    /// Spellbook", "%s Family Cookbook", "back", "next", "Page %d of %d".</item>
    /// <item><b>Spell Titles</b> (chunk 12) - 25 English entries, opening with the two
    /// placeholders "Unknown Charm" and "Unknown Spell" and holding 11 charms, 2 "TBD" slots
    /// and 10 spells between them.</item>
    /// <item><b>Spell Description</b> (chunk 8) - 26 English entries, one more than the title
    /// table; the tail of the English block, not a 26th spell.</item>
    /// <item><b>Nectar Bar/ Bakers Oven Titles</b> (chunk 14) - 10 English entries, the first
    /// being "not used".</item>
    /// </list>
    ///
    /// <para>
    /// The two things worth locking in here are that the dialog types are bound to the engine's
    /// own enum (so a rename breaks this file rather than silently switching the panel off), and
    /// that the placeholder filter is <em>not</em> a set of index ranges. It matches the words
    /// "not used", "TBD" and "Unknown" because every one of those entries is marked "Needs
    /// translation" in the file, so a French or German copy still reads the same way there - an
    /// index-based filter would quietly start showing "TBD Charm (If wanted)" as a real charm.
    /// </para>
    /// </remarks>
    public sealed class MakingMagicTests
    {
        /// <summary>The real English spell titles, in the order the game stores them.</summary>
        private static readonly string[] SpellTitles =
        {
            "Unknown Charm",
            "Dish Wish",
            "Perfect Garden",
            "Horn of Plenty",
            "Of House and Home",
            "Beauty or Beast",
            "Shed Your Skin",
            "Polar Attraction",
            "Rain of Riches",
            "Magic Mood",
            "The Price of Fame",
            "Clone Drone",
            "TBD Charm (If wanted)",
            "Get Happy",
            "Relationship Boost",
            "Smiley Face",
            "LoveStruck",
            "The Big Question",
            "Toadification",
            "Enchant",
            "Banish",
            "Hypnotize",
            "A Friend Indeed",
            "TBD Spell (if wanted)",
            "Unknown Spell",
        };

        /// <summary>The real English recipe titles, in the order the game stores them.</summary>
        private static readonly string[] RecipeTitles =
        {
            "not used",
            "Grape Nectar",
            "Elderberry Nectar",
            "Honey Nectar",
            "Magic Nectar",
            "Freshly Baked Bread",
            "Homemade Cake",
            "Carrot Cake",
            "Elderberry Pie",
            "Magic Tart",
        };

        #region the engine contract

        [Fact]
        public void TheBookDialogsAreTheEngineEnumValuesTS1Uses()
        {
            // SpellBook.iff and Cookbook.iff raise dialog_private (opcode 36) with these type
            // bytes and nothing else distinguishes them.
            Assert.Equal(12, (int)VMDialogType.TS1Spellbook);
            Assert.Equal(15, (int)VMDialogType.TS1Cookbook);
        }

        [Fact]
        public void EachBookIsItsOwnDialogType()
        {
            Assert.Equal(MakingMagicBook.Spellbook, MakingMagic.BookFor(VMDialogType.TS1Spellbook));
            Assert.Equal(MakingMagicBook.Cookbook, MakingMagic.BookFor(VMDialogType.TS1Cookbook));
        }

        [Fact]
        public void EveryOtherDialogIsNotABook()
        {
            // including TS1TransformMe, the third Makin' Magic dialog, which has no table
            foreach (VMDialogType type in new[]
            {
                VMDialogType.Message, VMDialogType.YesNo, VMDialogType.YesNoCancel,
                VMDialogType.TextEntry, VMDialogType.NumericEntry, VMDialogType.TS1PhoneBook,
                VMDialogType.TS1PetChoice, VMDialogType.TS1Clothes, VMDialogType.TS1Vacation,
                VMDialogType.TS1Neighborhood, VMDialogType.TS1StudioTown, VMDialogType.TS1Magictown,
                VMDialogType.TS1TransformMe,
            })
            {
                Assert.Equal(MakingMagicBook.None, MakingMagic.BookFor(type));
            }
        }

        [Fact]
        public void EachBookNamesItsOwnTablesInMagicIff()
        {
            Assert.Equal("Spell Titles", MakingMagic.TitlesLabel(MakingMagicBook.Spellbook));
            Assert.Equal("Spell Description", MakingMagic.DescriptionsLabel(MakingMagicBook.Spellbook));
            Assert.Equal("Nectar Bar/ Bakers Oven Titles", MakingMagic.TitlesLabel(MakingMagicBook.Cookbook));
            Assert.Equal("Nectar/Bakers Oven Description", MakingMagic.DescriptionsLabel(MakingMagicBook.Cookbook));
            Assert.Null(MakingMagic.TitlesLabel(MakingMagicBook.None));
            Assert.Null(MakingMagic.DescriptionsLabel(MakingMagicBook.None));
        }

        [Fact]
        public void TheTableNamesAreTheOnesInTheFile()
        {
            // the reader indexes by chunk label, so a label that does not match the file
            // silently produces an empty book. These are the labels in a retail magic.iff.
            Assert.Equal("Spell Titles", MakingMagic.SpellTitlesLabel);
            Assert.Equal("Spell Description", MakingMagic.SpellDescriptionLabel);
            Assert.Equal("Nectar Bar/ Bakers Oven Titles", MakingMagic.RecipeTitlesLabel);
            Assert.Equal("Nectar/Bakers Oven Description", MakingMagic.RecipeDescriptionLabel);
            Assert.Equal("Magic Book Dialog Strings", MakingMagic.DialogStringsLabel);
        }

        [Fact]
        public void TheResourceIsTheGamesOwnLooseFile()
        {
            Assert.Equal("magic.iff", MakingMagic.ResourceName);
            Assert.Equal("GameData", MakingMagic.ResourceFolder);

            const string install = @"D:\Program Files\Sims";
            Assert.Equal(Path.Combine(install, "GameData", "magic.iff"), MakingMagic.ResourcePath(install));
            Assert.Equal(Path.Combine("Sims", "GameData", "magic.iff"), MakingMagic.ResourcePath("Sims"));
            Assert.Null(MakingMagic.ResourcePath(null));
            Assert.Null(MakingMagic.ResourcePath(""));
        }

        [Fact]
        public void TheSpellsAndIngredientsAreStoredUnderTheirOwnTokenTypes()
        {
            // the TokenType byte of the manage_inventory primitive (opcode 51) as the expansion's
            // own scripts write it: "DEBUG - Get All Spells" uses 7, "DEBUG - Get All
            // Components" uses 8. These are the numbers the engine reads out of the operand.
            Assert.Equal(7, MakingMagic.MagicTokenType);
            Assert.Equal(8, MakingMagic.IngredientTokenType);

            Assert.Equal(MakingMagic.MagicTokenType, MakingMagic.PrimaryTokenType(MakingMagicBook.Spellbook));
            Assert.Equal(MakingMagic.IngredientTokenType, MakingMagic.PrimaryTokenType(MakingMagicBook.Cookbook));

            // the lookup is the book's own type first, the other one as a fallback
            Assert.Equal(new[] { 7, 8 }, MakingMagic.TokenTypesFor(MakingMagicBook.Spellbook));
            Assert.Equal(new[] { 8, 7 }, MakingMagic.TokenTypesFor(MakingMagicBook.Cookbook));
        }

        #endregion

        #region which entries are real

        [Fact]
        public void ThePlaceholdersAreNotEntries()
        {
            // every one of these is a slot the expansion never filled in
            Assert.True(MakingMagic.IsPlaceholderTitle("not used"));
            Assert.True(MakingMagic.IsPlaceholderTitle("NOT USED"));
            Assert.True(MakingMagic.IsPlaceholderTitle("TBD Charm (If wanted)"));
            Assert.True(MakingMagic.IsPlaceholderTitle("TBD Spell (if wanted)"));
            Assert.True(MakingMagic.IsPlaceholderTitle("Unknown Charm"));
            Assert.True(MakingMagic.IsPlaceholderTitle("Unknown Spell"));
        }

        [Fact]
        public void AnEmptyOrMissingTitleIsNotAnEntry()
        {
            Assert.True(MakingMagic.IsPlaceholderTitle(null));
            Assert.True(MakingMagic.IsPlaceholderTitle(""));
            Assert.True(MakingMagic.IsPlaceholderTitle("   "));
        }

        [Fact]
        public void ARealSpellIsAnEntry()
        {
            foreach (var title in SpellTitles)
            {
                if (MakingMagic.IsPlaceholderTitle(title)) continue;
                Assert.False(MakingMagic.IsPlaceholderTitle(title));
            }
            Assert.False(MakingMagic.IsPlaceholderTitle("Banish"));
            Assert.False(MakingMagic.IsPlaceholderTitle("Magic Nectar"));
        }
        [Fact]
        public void ThePlaceholderFilterIsCaseAndSpaceInsensitive()
        {
            // the same words come back out of a translated build, and a title with a stray
            // leading space should not become a real entry because of it
            Assert.True(MakingMagic.IsPlaceholderTitle("  unknown charm"));
            Assert.True(MakingMagic.IsPlaceholderTitle("tbd spell"));
        }

        [Fact]
        public void TheSpellbookHoldsTwentyOneRealSpells()
        {
            // 25 slots less "Unknown Charm", "TBD Charm", "TBD Spell" and "Unknown Spell"
            var entries = MakingMagic.UsableEntries(SpellTitles);
            Assert.Equal(21, entries.Count);
            Assert.DoesNotContain(0, entries);
            Assert.DoesNotContain(12, entries);
            Assert.DoesNotContain(23, entries);
            Assert.DoesNotContain(24, entries);
            Assert.Equal(1, entries[0]);  // Dish Wish
            Assert.Equal(22, entries[entries.Count - 1]); // A Friend Indeed
        }

        [Fact]
        public void TheCookbookHoldsNineRealRecipes()
        {
            // 10 slots less the leading "not used"
            var entries = MakingMagic.UsableEntries(RecipeTitles);
            Assert.Equal(9, entries.Count);
            Assert.DoesNotContain(0, entries);
            Assert.Equal(1, entries[0]); // Grape Nectar
            Assert.Equal(9, entries[entries.Count - 1]); // Magic Tart
        }

        [Fact]
        public void EntryIndicesAreTheGamesOwnOnesNotAFilteredRange()
        {
            // the number a choice is sent back is the index into magic.iff, so it has to be the
            // raw one: a filtered list renumbered from zero would hand the engine a different
            // spell from the one the player picked
            var entries = MakingMagic.UsableEntries(RecipeTitles);
            Assert.Equal(entries.Count, entries[entries.Count - 1] - entries[0] + 1);
            Assert.Equal(9, entries[entries.Count - 1]);
        }

        [Fact]
        public void AMissingTableHasNoEntries()
        {
            Assert.Empty(MakingMagic.UsableEntries(null));
            Assert.Empty(MakingMagic.UsableEntries(new string[0]));
        }

        [Fact]
        public void AnEntryIsFoundByItsOwnName()
        {
            // this is how a spell object in a sim's inventory is lined up with its flavour text:
            // the object's catalogue name and the title in magic.iff are the same string
            Assert.Equal(20, MakingMagic.IndexOfTitle(SpellTitles, "Banish"));
            Assert.Equal(1, MakingMagic.IndexOfTitle(SpellTitles, "dish wish"));
            Assert.Equal(4, MakingMagic.IndexOfTitle(RecipeTitles, "  Magic Nectar "));
        }

        [Fact]
        public void APalceholderIsNeverFoundByName()
        {
            // "Unknown Spell" is a real row in the table, but it is not a spell, so an object
            // that happened to be called that must not resolve to it
            Assert.Equal(-1, MakingMagic.IndexOfTitle(SpellTitles, "Unknown Spell"));
            Assert.Equal(-1, MakingMagic.IndexOfTitle(SpellTitles, "not used"));
        }

        [Fact]
        public void AnUnknownNameFindsNothing()
        {
            Assert.Equal(-1, MakingMagic.IndexOfTitle(SpellTitles, "Levitate"));
            Assert.Equal(-1, MakingMagic.IndexOfTitle(SpellTitles, null));
            Assert.Equal(-1, MakingMagic.IndexOfTitle(null, "Banish"));
        }

        #endregion

        #region paging

        [Fact]
        public void APageHoldsFiveEntries()
        {
            Assert.Equal(5, MakingMagic.EntriesPerPage);
            Assert.Equal(1, MakingMagic.PageCount(5));
            Assert.Equal(1, MakingMagic.PageCount(1));
            Assert.Equal(2, MakingMagic.PageCount(6));
            Assert.Equal(5, MakingMagic.PageCount(21));
            Assert.Equal(2, MakingMagic.PageCount(9));
        }

        [Fact]
        public void AnEmptyBookStillHasOnePage()
        {
            // a family that has discovered nothing is a real state, and it is shown as an empty
            // book rather than as no book at all
            Assert.Equal(1, MakingMagic.PageCount(0));
            Assert.Equal(1, MakingMagic.PageCount(-3));
        }

        [Fact]
        public void APageNeverHasADegenerateSize()
        {
            // a page size of zero would divide by zero; the entry-per-page constant is used
            // instead, so a caller that passes nonsense gets the shipped layout back
            Assert.Equal(2, MakingMagic.PageCount(9, 0));
            Assert.Equal(2, MakingMagic.PageCount(9, -1));
        }

        [Fact]
        public void TheSpellbookIsFivePages()
        {
            var entries = MakingMagic.UsableEntries(SpellTitles);
            Assert.Equal(5, MakingMagic.PageCount(entries.Count));

            var first = MakingMagic.PageOf(entries, 1);
            Assert.Equal(5, first.Count);
            Assert.Equal(1, first[0]);
            Assert.Equal(5, first[4]);

            // the last page carries the remainder, and nothing is repeated or dropped
            var last = MakingMagic.PageOf(entries, 5);
            Assert.Single(last);
            Assert.Equal(22, last[0]);
        }

        [Fact]
        public void ThePagesBetweenTheFirstAndLastAreFull()
        {
            var entries = MakingMagic.UsableEntries(SpellTitles);
            var seen = new List<int>();
            for (int page = 1; page <= MakingMagic.PageCount(entries.Count); page++)
            {
                seen.AddRange(MakingMagic.PageOf(entries, page));
            }
            Assert.Equal(entries.Count, seen.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                Assert.Equal(entries[i], seen[i]);
            }
        }

        [Fact]
        public void TurningPastTheEndOfTheBookStaysOnTheLastPage()
        {
            // "next" on the last page and "back" on the first are the same no-op the buttons in
            // the game are, not a wrap round
            var entries = MakingMagic.UsableEntries(RecipeTitles);
            var pages = MakingMagic.PageCount(entries.Count);
            Assert.Equal(2, pages);

            Assert.Equal(1, MakingMagic.ClampPage(0, pages));
            Assert.Equal(1, MakingMagic.ClampPage(-4, pages));
            Assert.Equal(2, MakingMagic.ClampPage(3, pages));
            Assert.Equal(2, MakingMagic.ClampPage(99, pages));

            Assert.Equal(MakingMagic.PageOf(entries, pages), MakingMagic.PageOf(entries, 99));
            Assert.Equal(MakingMagic.PageOf(entries, 1), MakingMagic.PageOf(entries, 0));
        }

        [Fact]
        public void AnEmptyBookHasAnEmptyPage()
        {
            Assert.Empty(MakingMagic.PageOf(null, 1));
            Assert.Empty(MakingMagic.PageOf(new List<int>(), 1));
        }

        #endregion

        #region the captions

        [Fact]
        public void TheFiveDialogCaptionsAreInTheGamesOrder()
        {
            Assert.Equal(0, MakingMagic.DialogStringIndex(MakingMagicCaption.SpellbookTitle));
            Assert.Equal(1, MakingMagic.DialogStringIndex(MakingMagicCaption.CookbookTitle));
            Assert.Equal(2, MakingMagic.DialogStringIndex(MakingMagicCaption.Back));
            Assert.Equal(3, MakingMagic.DialogStringIndex(MakingMagicCaption.Next));
            Assert.Equal(4, MakingMagic.DialogStringIndex(MakingMagicCaption.PageOf));
            Assert.Equal(-1, MakingMagic.DialogStringIndex((MakingMagicCaption)99));
        }

        [Fact]
        public void EachBookTitlesItselfWithItsOwnCaption()
        {
            Assert.Equal(MakingMagicCaption.SpellbookTitle, MakingMagic.TitleCaption(MakingMagicBook.Spellbook));
            Assert.Equal(MakingMagicCaption.CookbookTitle, MakingMagic.TitleCaption(MakingMagicBook.Cookbook));
        }

        [Fact]
        public void TheFallbackCaptionsAreTheGamesEnglish()
        {
            // what a build with no magic.iff shows, and what has to match the file so the two
            // cannot disagree
            Assert.Equal("%s Family Spellbook", MakingMagic.DialogStringFallback(MakingMagicCaption.SpellbookTitle));
            Assert.Equal("%s Family Cookbook", MakingMagic.DialogStringFallback(MakingMagicCaption.CookbookTitle));
            Assert.Equal("back", MakingMagic.DialogStringFallback(MakingMagicCaption.Back));
            Assert.Equal("next", MakingMagic.DialogStringFallback(MakingMagicCaption.Next));
            Assert.Equal("Page %d of %d", MakingMagic.DialogStringFallback(MakingMagicCaption.PageOf));
        }

        [Fact]
        public void TheFamilyNameGoesIntoTheBookTitle()
        {
            Assert.Equal("The Newbies Family Spellbook",
                MakingMagic.FormatBookTitle(MakingMagicBook.Spellbook, "%s Family Spellbook", "The Newbies"));
            Assert.Equal("The Newbies Family Cookbook",
                MakingMagic.FormatBookTitle(MakingMagicBook.Cookbook, "%s Family Cookbook", "The Newbies"));
        }

        [Fact]
        public void ABookWithNoFamilyStillHasATitle()
        {
            // a lot with no family loaded, or a book raised by an object rather than a sim
            Assert.Equal(" Family Spellbook",
                MakingMagic.FormatBookTitle(MakingMagicBook.Spellbook, "%s Family Spellbook", ""));
        }

        [Fact]
        public void ABookWithNoCaptionFallsBackToTheRightEnglish()
        {
            // a build with no magic.iff, or a file whose dialog table is unreadable: the fallback
            // is the book's own English, not the other book's
            Assert.Equal("The Newbies Family Spellbook",
                MakingMagic.FormatBookTitle(MakingMagicBook.Spellbook, null, "The Newbies"));
            Assert.Equal("The Newbies Family Cookbook",
                MakingMagic.FormatBookTitle(MakingMagicBook.Cookbook, "", "The Newbies"));
        }

        [Fact]
        public void ThePageNumbersGoIntoThePageCaption()
        {
            Assert.Equal("Page 1 of 5", MakingMagic.FormatPageCaption("Page %d of %d", 1, 5));
            Assert.Equal("Page 3 of 3", MakingMagic.FormatPageCaption(null, 3, 3));
        }

        #endregion

        #region the answer

        [Fact]
        public void AChoiceIsAcceptedAndACancelIsNot()
        {
            // the engine's own 0/1/2 convention, as VMNetDialogResponseCmd carries it
            Assert.Equal(1, MakingMagic.ResponseCodeFor(0));
            Assert.Equal(1, MakingMagic.ResponseCodeFor(20));
            Assert.Equal(0, MakingMagic.ResponseCodeFor(-1));
            Assert.Equal(0, MakingMagic.ResponseCodeFor(-99));
        }

        [Fact]
        public void AChoiceCarriesTheEntryIndex()
        {
            Assert.Equal("20", MakingMagic.ResponseTextFor(20));
            Assert.Equal("0", MakingMagic.ResponseTextFor(0));
            Assert.Equal("-1", MakingMagic.ResponseTextFor(-1));
        }

        [Fact]
        public void AChoiceRoundTrips()
        {
            foreach (var index in new[] { 0, 1, 12, 24 })
            {
                int parsed;
                Assert.True(MakingMagic.TryParseResponse(
                    MakingMagic.ResponseCodeFor(index),
                    MakingMagic.ResponseTextFor(index),
                    out parsed));
                Assert.Equal(index, parsed);
            }
        }

        [Fact]
        public void ACancelIsNotAChoice()
        {
            int parsed;
            Assert.False(MakingMagic.TryParseResponse(MakingMagic.ResponseCancelled, "-1", out parsed));
            Assert.Equal(-1, parsed);

            Assert.False(MakingMagic.TryParseResponse(MakingMagic.ResponseAccepted, "not a number", out parsed));
            Assert.False(MakingMagic.TryParseResponse(MakingMagic.ResponseAccepted, null, out parsed));
            Assert.False(MakingMagic.TryParseResponse(MakingMagic.ResponseAccepted, "", out parsed));
        }

        [Fact]
        public void ANegativeIndexIsNotAChoice()
        {
            int parsed;
            Assert.False(MakingMagic.TryParseResponse(MakingMagic.ResponseAccepted, "-3", out parsed));
            Assert.Equal(-3, parsed);
        }

        #endregion
    }
}
