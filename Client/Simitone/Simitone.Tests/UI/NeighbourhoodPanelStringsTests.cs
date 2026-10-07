/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
 */

using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The Neighbourhood Management page's own wording, read out of Simitone's string table.
    ///
    /// <para>
    /// <see cref="SimitoneStringsTests"/> covers the table as a mechanism - the fallback rule,
    /// the format, where the file has to land. This covers the <em>content</em> of the block this
    /// page owns, and there are only two things worth saying about it.
    /// </para>
    ///
    /// <para>
    /// <b>The table is a move, not a rewording.</b> Every entry is the English the page showed
    /// before any of it was routed through a table, asserted character for character. A drift
    /// here is invisible in English - the page still reads fine - and shows up in every other
    /// language as a different word for the same button.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing depends on the table being there.</b> A test process never builds
    /// <c>GameFacade.Strings</c>, so these tests run in exactly the state a stripped install is
    /// in, and the "no table" case is the one actually exercised rather than faked. Every
    /// member below is therefore asserted twice: once as the table's text, and once as what the
    /// page produces with no table at all.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodPanelStringsTests
    {
        /// <summary>The shipped English table, parsed the way the game's own loader reads it.</summary>
        private static IReadOnlyDictionary<string, string> Table()
        {
            return SimitoneStrings.ParseTable(
                File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory)));
        }

        /// <summary>
        /// Ids that are filled in at the call site, with the arguments they are filled with, in
        /// order and in the normalised shape <see cref="Specifiers"/> returns. Anything in an
        /// owned block that is not listed here takes none.
        /// </summary>
        private static readonly (string Id, string Specifiers)[] ArgumentTaking =
        {
            // "That is not one of the 9 neighbourhoods." - the slot count.
            ("308", "%0"),
            // The Open confirmation, composed with string.Format: the slot being opened, and
            // the one being closed (absent in the unnamed variant).
            ("312", "%0 %1"),
            ("313", "%0"),
            // "This folder has no Neighborhood.iff in it ..." - the file the game looks for.
            ("325", "%0"),
            // "Names can be at most 48 characters." - the backend's own limit.
            ("329", "%0"),
            // "A name cannot contain any of :*?\"<>|." - the reserved set, spliced in.
            ("332", "%0"),
            // The six status lines, then the stand-in for a slot that is not a slot.
            ("334", "%0"),
            ("335", "%0"),
            ("336", "%0"),
            ("337", "%0 %1"),
            ("338", "%0"),
            ("339", "%0"),
            ("340", "%0"),
            // The row label for a slot the model does not recognise.
            ("358", "%0"),
            // "Delete Springfield and everything in it? ..."
            ("362", "%0"),
            // "Free Will: On" - the state from 190 or 191.
            ("192", "%0"),
        };

        /// <summary>
        /// Whether an id belongs to one of the blocks this wave routes through Simitone's table:
        /// the Options pages' own strings, and the whole of the Neighbourhood Management page.
        /// Scoped because the rest of the table belongs to other panels - a %s over in the
        /// rezone dialogs is somebody else's contract, not this one's.
        /// </summary>
        private static bool IsRoutedHere(string id)
        {
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            return (n >= 170 && n < 180) || (n >= 190 && n < 195) || (n >= 300 && n < 400);
        }

        /// <summary>
        /// The arguments a string asks for, in the order they are filled, as
        /// <c>%0 %1 %2 ...</c>.
        ///
        /// <para>
        /// Deliberately just the count and the order, not the specifier letters. The table uses
        /// two conventions - <c>%s</c>/<c>%d</c>, which <c>SimitoneStrings.Substitute</c> fills,
        /// and <c>{0}</c>/<c>{1}</c>, which the Open confirmation is composed with because the
        /// page has to be able to append a paragraph to it - and both fill left to right. Which
        /// letter is used is a rendering detail; how many things go in is the contract, and it is
        /// the half a mistranslation gets wrong.
        /// </para>
        /// </summary>
        private static string Specifiers(string text)
        {
            var count = Regex.Matches(text, @"%[sd]").Count + Regex.Matches(text, @"\{\d+\}").Count;
            return string.Join(" ", Enumerable.Range(0, count).Select(i => "%" + i));
        }

        // ---- the table carries the English the page used to show ---------------------------

        [Theory]
        // The slot row.
        [InlineData("300", "In use")]
        [InlineData("301", "Not created")]
        [InlineData("302", "No area template")]
        [InlineData("303", "named")]
        [InlineData("304", "open now")]
        [InlineData("305", "All nine neighbourhoods are in use. Delete one to make room for another.")]
        [InlineData("306", "No The Sims install path is known, so a neighbourhood cannot be copied in.")]
        [InlineData("307", "That did not work. See the Simitone log for the reason.")]
        // Why Delete is refused.
        [InlineData("308", "That is not one of the %d neighbourhoods.")]
        [InlineData("309", "The first neighbourhood is the one Simitone starts from, so it cannot be deleted.")]
        [InlineData("310", "This is the neighbourhood that is open right now. Go to another one and come back to delete it.")]
        // The Open switch.
        [InlineData("311", "Open neighbourhood")]
        [InlineData("312", "Open {0}? The neighbourhood that is open now, {1}, will be closed, and anything in it that you have not saved will be lost.")]
        [InlineData("313", "Open {0}? The neighbourhood that is open now will be closed, and anything in it that you have not saved will be lost.")]
        [InlineData("314", "It has changes that have not been saved. Yes saves it and then opens the other one, No opens the other one without saving, and Cancel goes back.")]
        [InlineData("315", "That neighbourhood could not be opened, so the one you were in is still open. See the Simitone log for the reason.")]
        [InlineData("316", "That neighbourhood could not be opened, and the one you were in could not be put back either. Restart Simitone; the Simitone log has the reason.")]
        [InlineData("317", "The neighbourhood could not be saved, so nothing was switched. See the Simitone log for the reason.")]
        [InlineData("318", "That neighbourhood was opened, but its map could not be drawn. See the Simitone log for the reason; if it keeps happening, restart Simitone.")]
        [InlineData("319", "Neighbourhoods can only be opened from the neighbourhood map.")]
        // Why Open is refused.
        [InlineData("320", "This slot has no neighbourhood in it yet. Make a new one or copy one in from The Sims first.")]
        [InlineData("321", "A lot is open. Go back to the neighbourhood map before opening another neighbourhood.")]
        [InlineData("322", "A family is waiting to move in. Finish that, or go back, before opening another neighbourhood.")]
        [InlineData("323", "Simitone cannot tell which neighbourhood is open right now, so it will not put one out from under the other. See the Simitone log for the reason.")]
        [InlineData("324", "This is the neighbourhood that is open right now.")]
        [InlineData("325", "This folder has no %s in it, so the game cannot open it as a neighbourhood. Delete it and copy a new one in.")]
        [InlineData("326", "this neighbourhood")]
        // Why a typed name was refused.
        [InlineData("327", "Type a name first.")]
        [InlineData("328", "\".\" and \"..\" are not names.")]
        [InlineData("329", "Names can be at most %d characters.")]
        [InlineData("330", "A name cannot contain / or \\.")]
        [InlineData("331", "A name cannot contain control characters.")]
        [InlineData("332", "A name cannot contain any of %s.")]
        [InlineData("333", "That name cannot be used.")]
        // The status line each action reports.
        [InlineData("334", "Created %s.")]
        [InlineData("335", "Copied %s from The Sims.")]
        [InlineData("336", "Deleted %s.")]
        [InlineData("337", "Renamed %s to %s.")]
        [InlineData("338", "%s is back to its default name.")]
        [InlineData("339", "Opened %s.")]
        [InlineData("340", "slot %d")]
        // The page: title, action column, and the two dialogs.
        [InlineData("350", "Neighbourhoods")]
        [InlineData("351", "Open")]
        [InlineData("352", "New (blank)")]
        [InlineData("353", "Copy from TS1")]
        [InlineData("354", "Rename...")]
        [InlineData("355", "Reset name")]
        [InlineData("356", "Delete")]
        [InlineData("357", "Close")]
        [InlineData("358", "Slot %d")]
        [InlineData("359", "Rename neighbourhood")]
        [InlineData("360", "Give this neighbourhood a name of its own. The game will use it in its lists; the folder on disk keeps its number.")]
        [InlineData("361", "Delete neighbourhood")]
        [InlineData("362", "Delete %s and everything in it? This cannot be undone.")]
        public void TheTableCarriesTheEnglishThePageUsedToShow(string id, string english)
        {
            var table = Table();
            Assert.True(table.ContainsKey(id), "No entry for table id " + id + ".");
            Assert.Equal(english, table[id]);
        }

        // ---- the table and the call sites agree about the arguments --------------------------

        /// <summary>
        /// A translation and its English cannot disagree about how many arguments there are,
        /// because the count is written down here. A mistranslated entry asking for one too many
        /// renders as "Rezone 12 as a s lot?" rather than throwing, which
        /// <c>SimitoneStrings.Substitute</c> covers; this is what stops one being written in the
        /// first place.
        /// </summary>
        [Fact]
        public void EveryEntryThatTakesArgumentsAsksForTheSameOnesItsCallSitePasses()
        {
            var table = Table();
            foreach (var entry in ArgumentTaking)
            {
                Assert.True(table.ContainsKey(entry.Id), "No entry for table id " + entry.Id + ".");
                Assert.Equal(entry.Specifiers, Specifiers(table[entry.Id]));
            }
        }

        /// <summary>
        /// The other half: a caption that takes nothing must not have picked up a specifier. One
        /// that has is a translation that will silently lose a word, because there is no argument
        /// left to fill it with.
        /// </summary>
        [Fact]
        public void NoEntryThatTakesNoArgumentsAsksForAny()
        {
            var taking = new HashSet<string>(ArgumentTaking.Select(x => x.Id), StringComparer.Ordinal);
            foreach (var pair in Table())
            {
                if (!IsRoutedHere(pair.Key) || taking.Contains(pair.Key)) continue;
                Assert.Equal("", Specifiers(pair.Value));
            }
        }

        // ---- with no table at all, the page is unchanged -------------------------------------

        /// <summary>
        /// The whole point of the design, and the reason a test process can check it: nothing is
        /// ever built into a test run, so <see cref="FSO.Client.GameFacade.Strings"/> is null
        /// here and every member below is its own English fallback. This is the "behaviour is
        /// byte-identical when the table is absent" claim, asserted rather than asserted-to.
        /// </summary>
        [Fact]
        public void WithNoTableAtAllThePageSaysExactlyTheSameWords()
        {
            Assert.Null(FSO.Client.GameFacade.Strings);

            // The row.
            Assert.Equal("In use", NeighbourhoodPanelModel.StateInUse);
            Assert.Equal("Not created", NeighbourhoodPanelModel.StateMissing);
            Assert.Equal("No area template", NeighbourhoodPanelModel.NoAreaTemplate);
            Assert.Equal("named", NeighbourhoodPanelModel.RenamedMarker);
            Assert.Equal("open now", NeighbourhoodPanelModel.LoadedMarker);
            Assert.Equal("All nine neighbourhoods are in use. Delete one to make room for another.",
                NeighbourhoodPanelModel.AllSlotsInUse);
            Assert.Equal("No The Sims install path is known, so a neighbourhood cannot be copied in.",
                NeighbourhoodPanelModel.NoInstallPath);
            Assert.Equal("That did not work. See the Simitone log for the reason.",
                NeighbourhoodPanelModel.ActionFailed);

            // Why Delete is refused, and why Open is.
            Assert.Equal("The first neighbourhood is the one Simitone starts from, so it cannot be deleted.",
                NeighbourhoodPanelModel.DeleteRefusal(NeighbourhoodManager.PrimarySlotId, null));
            Assert.Equal("This is the neighbourhood that is open right now. Go to another one and come back to delete it.",
                NeighbourhoodPanelModel.DeleteRefusal(4, 4));
            Assert.Equal("That is not one of the 9 neighbourhoods.",
                NeighbourhoodPanelModel.DeleteRefusal(-1, null));

            Assert.Equal("This slot has no neighbourhood in it yet. Make a new one or copy one in from The Sims first.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, false, false, 0)));
            Assert.Equal("A lot is open. Go back to the neighbourhood map before opening another neighbourhood.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, 0, inLot: true)));
            Assert.Equal("A family is waiting to move in. Finish that, or go back, before opening another neighbourhood.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, 0, moveInPending: true)));
            Assert.Equal("Simitone cannot tell which neighbourhood is open right now, so it will not put one out from under the other. See the Simitone log for the reason.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, true, null)));
            Assert.Equal("This is the neighbourhood that is open right now.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(4, true, true, 4)));
            Assert.Equal("This folder has no Neighborhood.iff in it, so the game cannot open it as a neighbourhood. Delete it and copy a new one in.",
                NeighbourhoodPanelModel.OpenRefusal(new NeighbourhoodOpenFacts(3, true, false, 0)));

            // The switch's own wording.
            Assert.Equal("Open neighbourhood", NeighbourhoodPanelModel.OpenTitle);
            Assert.Equal("Neighbourhoods can only be opened from the neighbourhood map.",
                NeighbourhoodPanelModel.NoScreenToSwitch);
            Assert.Equal("That neighbourhood could not be opened, so the one you were in is still open. See the Simitone log for the reason.",
                NeighbourhoodPanelModel.OpenFailed);
            Assert.Equal("That neighbourhood could not be opened, and the one you were in could not be put back either. Restart Simitone; the Simitone log has the reason.",
                NeighbourhoodPanelModel.OpenFailedFatal);
            Assert.Equal("The neighbourhood could not be saved, so nothing was switched. See the Simitone log for the reason.",
                NeighbourhoodPanelModel.OpenSaveFailed);
            Assert.Equal("That neighbourhood was opened, but its map could not be drawn. See the Simitone log for the reason; if it keeps happening, restart Simitone.",
                NeighbourhoodPanelModel.OpenMapFailed);

            // The confirmation, in both its shapes.
            var clean = new NeighbourhoodOpenFacts(3, true, true, 0);
            Assert.Equal("Open Springfield? The neighbourhood that is open now, Old Town, will be closed,"
                + " and anything in it that you have not saved will be lost.",
                NeighbourhoodPanelModel.OpenConfirmMessage(clean, "Springfield", "Old Town"));
            Assert.Equal("Open Springfield? The neighbourhood that is open now will be closed, and"
                + " anything in it that you have not saved will be lost.",
                NeighbourhoodPanelModel.OpenConfirmMessage(clean, "Springfield", null));
            Assert.Contains("this neighbourhood", NeighbourhoodPanelModel.OpenConfirmMessage(clean, null, "Old Town"));

            // Why a typed name was refused.
            Assert.Equal("Type a name first.", NeighbourhoodPanelModel.RenameRejection(null));
            Assert.Equal("\".\" and \"..\" are not names.", NeighbourhoodPanelModel.RenameRejection(".."));
            Assert.Equal("Names can be at most 48 characters.",
                NeighbourhoodPanelModel.RenameRejection(new string('x', 49)));
            Assert.Equal("A name cannot contain / or \\.", NeighbourhoodPanelModel.RenameRejection("a/b"));
            Assert.Equal("A name cannot contain control characters.", NeighbourhoodPanelModel.RenameRejection("a\u0001b"));
            Assert.Equal("A name cannot contain any of " + NeighbourhoodPanelModel.ReservedNameChars + ".",
                NeighbourhoodPanelModel.RenameRejection("a:b"));

            // The page's own title.
            Assert.Equal("Neighbourhoods", UINeighbourhoodManagementPanel.Title);
        }

        // ---- the two members that are deliberately not table entries ------------------------

        /// <summary>
        /// The detail line's separator is a <c>const</c> and stays out of the table. A row is
        /// built by joining its parts with it and is read back by splitting on it, so a
        /// translated separator would not translate the page - it would take it apart wrongly.
        /// </summary>
        [Fact]
        public void TheRowSeparatorIsNotATableEntry()
        {
            foreach (var pair in Table())
            {
                if (!IsRoutedHere(pair.Key)) continue;
                // The whole separator, spaces and all: a bare "-" is just punctuation and half the
                // captions contain one.
                Assert.DoesNotContain(NeighbourhoodPanelModel.Separator, pair.Value);
            }
        }

        /// <summary>
        /// The reserved character list is a <c>const</c> for the same kind of reason, and one
        /// step further: it is a property of the filesystem rather than of a language. It is
        /// spliced into the message that names it, so the set is stated once however many
        /// languages say it.
        /// </summary>
        [Fact]
        public void TheReservedCharacterSetIsNotATableEntry()
        {
            foreach (var pair in Table())
            {
                if (!IsRoutedHere(pair.Key)) continue;
                Assert.DoesNotContain(NeighbourhoodPanelModel.ReservedNameChars, pair.Value);
            }
        }

        [Fact]
        public void TheReservedCharacterSetIsTheOneTheMessageNames()
        {
            // The entry carries one specifier, and the call site fills it with this exact
            // constant, so a translation can name the set but cannot change it.
            Assert.Equal("%0", Specifiers(Table()["332"]));
            Assert.Contains(NeighbourhoodPanelModel.ReservedNameChars,
                NeighbourhoodPanelModel.RenameRejection("a" + NeighbourhoodPanelModel.ReservedNameChars[0] + "b"));
        }

        // ---- the Options pages' own strings ---------------------------------------------------

        /// <summary>
        /// The Options pages split their wording in two: anything TS1 had is read out of TS1's
        /// own table 145 by <c>OptionsStrings</c>, and anything Simitone invented has no id
        /// there and goes through Simitone's own table instead. This is the second half, and the
        /// reason it is a separate list is that the first half is not Simitone's to translate.
        /// </summary>
        [Theory]
        [InlineData("170", "Toggle Settings")]
        [InlineData("171", "Description")]
        [InlineData("172", "There are no settings here.")]
        [InlineData("173", "On/Off")]
        [InlineData("180", "Language")]
        [InlineData("190", "On")]
        [InlineData("191", "Off")]
        [InlineData("192", "Free Will: %s")]
        [InlineData("193", "Sims in your family choose their own actions.")]
        [InlineData("194", "Sims in your family wait for you to direct them. Visitors and pets still act on their own.")]
        public void TheOptionsPagesOwnStringsAreInTheTableToo(string id, string english)
        {
            var table = Table();
            Assert.True(table.ContainsKey(id), "No entry for table id " + id + ".");
            Assert.Equal(english, table[id]);
        }

        /// <summary>
        /// "Low", "Medium" and "High" are <em>not</em> in Simitone's table, and must not be: the
        /// Options pages read them out of TS1's own table 145, which came with the player's copy
        /// of the game and is already translated. A copy here would be a second, English-only
        /// answer to a question the game has already answered.
        /// </summary>
        [Theory]
        [InlineData("Low")]
        [InlineData("Medium")]
        [InlineData("High")]
        public void TheGamesOwnRadioCaptionsAreNotDuplicatedIntoSimitonesTable(string word)
        {
            var table = Table();
            foreach (var pair in table)
            {
                Assert.NotEqual(word, pair.Value);
            }
        }

        [Fact]
        public void TheFreeWillWordingIsTheSameWithNoTableAtAll()
        {
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("On", FreeWillSetting.DescribeState(true));
            Assert.Equal("Off", FreeWillSetting.DescribeState(false));
            Assert.Equal("Free Will: On", FreeWillSetting.DescribeToggle(true));
            Assert.Equal("Free Will: Off", FreeWillSetting.DescribeToggle(false));
            Assert.Equal("Sims in your family choose their own actions.", FreeWillSetting.DescribeEffect(true));
            Assert.Equal("Sims in your family wait for you to direct them. Visitors and pets still act on their own.",
                FreeWillSetting.DescribeEffect(false));
        }
    }
}
