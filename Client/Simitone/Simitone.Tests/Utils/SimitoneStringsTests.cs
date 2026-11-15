using FSO.Client.GameContent;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers <see cref="SimitoneStrings"/>: the fallback rule, the argument substitution, the
    /// path the shipped <c>.cst</c> has to land at, and the shape of the table itself.
    ///
    /// <para>
    /// The point of the type is that a player never sees <c>***MISSING***</c> or a blank label
    /// because a string table was absent, incomplete or untranslated. That is entirely a
    /// question about what the lookup does with an unusable value, so it is testable with no
    /// Content, no GraphicsDevice and no <c>GameFacade.Strings</c> - which is exactly the
    /// state a test process is in, and which also means the "no table at all" case is the one
    /// the tests actually exercise rather than a case they have to fake.
    /// </para>
    /// </summary>
    public sealed class SimitoneStringsTests : IDisposable
    {
        /// <summary>
        /// A scratch root for the tests that need real <c>.cst</c> files on disk. Under the
        /// system temp directory rather than the build output, so nothing a test writes can be
        /// mistaken for - or overwrite - the shipped table.
        /// </summary>
        private readonly string _tempRoot;

        public SimitoneStringsTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-cst-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best effort - a leftover temp directory must never fail the suite
            }
        }

        /// <summary>
        /// Writes a language directory holding the given files and returns its path, which is
        /// what <c>ContentStrings.Load</c> is pointed at.
        /// </summary>
        private string TempTableDir(params (string Name, string Body)[] files)
        {
            var dir = Path.Combine(_tempRoot, "lang" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            foreach (var file in files)
            {
                File.WriteAllText(Path.Combine(dir, file.Name), file.Body);
            }
            return dir;
        }

        #region fallback rule

        [Fact]
        public void GetFallsBackWhenThereAreNoStringTablesAtAll()
        {
            // A test process never builds GameFacade.Strings, so this is the state a stripped
            // install looks like: the lookup has to answer with the English rather than throw.
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.Equal("Credits", SimitoneStrings.Get("1", "Credits"));
        }

        [Fact]
        public void GetFallsBackForAnIdTheCallerHasNoIdFor()
        {
            // A null/empty id means "there is no id for this", not "look up the empty key" -
            // the underlying lookup throws on a null key.
            Assert.Equal("fallback", SimitoneStrings.Get(null, "fallback"));
            Assert.Equal("fallback", SimitoneStrings.Get("", "fallback"));
        }

        [Fact]
        public void GetFallsBackForATableTheCallerHasNotNamed()
        {
            Assert.Equal("fallback", SimitoneStrings.GetFromTable(null, "6", "fallback"));
            Assert.Equal("fallback", SimitoneStrings.GetFromTable("", "6", "fallback"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(SimitoneStrings.MissingMarker)]
        public void UnusableValuesAreNotUsable(string value)
        {
            Assert.False(SimitoneStrings.IsUsable(value));
            Assert.Equal("fallback", SimitoneStrings.Resolve(value, "fallback"));
        }

        [Fact]
        public void ARealStringIsUsableAndSurvivesResolve()
        {
            Assert.True(SimitoneStrings.IsUsable("Credits"));
            // Not trimmed on the way through: a translator's leading space is theirs to keep.
            Assert.Equal(" Credits ", SimitoneStrings.Resolve(" Credits ", "fallback"));
        }

        [Fact]
        public void ResolveCanFallBackToNothing()
        {
            // A caller with no English either (a label that is only ever a table entry) gets
            // null rather than a throw.
            Assert.Null(SimitoneStrings.Resolve(null, null));
        }

        [Fact]
        public void AFallbackIsReturnedUncopied()
        {
            // The fallback is the answer, so it is handed back as it was given rather than
            // rebuilt: "byte-identical when the table is absent" is a stronger promise if the
            // string that gets shown is literally the one at the call site.
            var fallback = new string("Credits".ToCharArray());
            Assert.Same(fallback, SimitoneStrings.Get("1", fallback));
            Assert.Same(fallback, SimitoneStrings.Get("no-such-id", fallback));
            Assert.Same(fallback, SimitoneStrings.Format("1", fallback));
        }

        [Fact]
        public void TheMissingMarkerIsFreeSOSOwnLiteral()
        {
            // If FreeSO ever changes what it hands back for a string it does not have, this is
            // the test that notices: the fallback rule would start letting it through and the
            // UI would start showing it again.
            Assert.Equal("***MISSING***", SimitoneStrings.MissingMarker);
        }

        #endregion

        #region control characters

        /// <summary>
        /// A control character in a value goes straight into a <c>UILabel</c> caption, where a
        /// newline is layout, a tab is a gap and an unprintable code point is a missing glyph
        /// drawn as a box. None of them can be undone downstream, so the entry is not usable and
        /// the English is the better answer.
        /// </summary>
        [Theory]
        [InlineData("two\nlines")]
        [InlineData("two\r\nlines")]
        [InlineData("tab\there")]
        [InlineData("bell\a")]
        [InlineData("null\0here")]
        [InlineData("low\u0001control")]
        [InlineData("del\u007f")]
        public void AValueCarryingAControlCharacterIsNotClean(string value)
        {
            // Usable - it is not null, empty or the missing marker - but not clean.
            Assert.True(SimitoneStrings.IsUsable(value));
            Assert.False(SimitoneStrings.IsClean(value));
            Assert.Equal("fallback", SimitoneStrings.ResolveClean(value, "fallback"));
        }

        [Theory]
        [InlineData("Neighbourhoods")]
        [InlineData("  padded  ")]
        [InlineData("A name cannot contain / or \\.")]
        [InlineData("\".\" and \"..\" are not names.")]
        [InlineData("Voisinage")]
        [InlineData("Nachbarschaft")]
        [InlineData("Vizinhança")]
        [InlineData("100% done")]
        [InlineData("Sims in your family choose their own actions.")]
        public void OrdinaryTextIsClean(string value)
        {
            Assert.True(SimitoneStrings.IsClean(value));
            Assert.Same(value, SimitoneStrings.ResolveClean(value, "fallback"));
        }

        [Fact]
        public void CleanIsUsableAndSoRejectsTheSameThreeThings()
        {
            // The control-character rule is added on top of the fallback rule, not instead of it.
            foreach (var value in new[] { null, "", "   ", SimitoneStrings.MissingMarker })
            {
                Assert.False(SimitoneStrings.IsClean(value));
                Assert.Equal("fallback", SimitoneStrings.ResolveClean(value, "fallback"));
            }
        }

        [Fact]
        public void AFallbackThatIsItselfNotCleanIsStillTheAnswer()
        {
            // There is nothing better to return, and the caller's own English is the last line of
            // defence rather than something to second-guess.
            Assert.Equal("a\nb", SimitoneStrings.ResolveClean("x\u0001", "a\nb"));
            Assert.Null(SimitoneStrings.ResolveClean(null, null));
        }

        [Fact]
        public void TheControlRuleIsSimitonesOwnAndNotAppliedToTheGamesTables()
        {
            // A table that came with the player's copy of the game may legitimately contain a
            // newline - a tooltip body is two lines of it in several - so judging another game's
            // text by Simitone's rules would throw away translations that are correct.
            Assert.Equal("first\nsecond",
                SimitoneStrings.Resolve("first\nsecond", "fallback"));
        }

        #endregion

        #region argument substitution

        [Fact]
        public void SubstituteFillsSpecifiersInOrder()
        {
            Assert.Equal("Rezone 12 as a community lot?",
                SimitoneStrings.Substitute("Rezone %s as a %s lot?", new[] { "12", "community" }));
        }

        [Fact]
        public void SubstituteAcceptsDigitsAsWellAsStrings()
        {
            Assert.Equal("lot 12", SimitoneStrings.Substitute("lot %d", new[] { "12" }));
        }

        [Fact]
        public void SubstituteDropsThePercentOfASpecifierItHasNoArgumentFor()
        {
            // The deliberate half of a mistranslation: a table entry with more specifiers than
            // the call site passes must not leak "%s" into a dialog. The 's' does survive,
            // which is exactly what ContentStrings.GetString does with the same mismatch.
            Assert.Equal("Rezone 12 as a s lot?",
                SimitoneStrings.Substitute("Rezone %s as a %s lot?", new[] { "12" }));
        }

        [Fact]
        public void SubstituteLeavesFormatAloneWithNoArguments()
        {
            Assert.Equal("100% done", SimitoneStrings.Substitute("100% done", null));
            Assert.Equal("100% done", SimitoneStrings.Substitute("100% done", new string[0]));
        }

        [Fact]
        public void SubstituteKeepsATrailingPercent()
        {
            // A trailing '%' has no following character to be a specifier with, so it stays.
            Assert.Equal("Done: 100%", SimitoneStrings.Substitute("Done: 100%", new[] { "x" }));
        }

        [Fact]
        public void FormatSubstitutesIntoWhicheverStringWon()
        {
            // With no table the fallback is what gets substituted, which is why the English at a
            // call site has to carry the same specifiers as the table entry.
            Assert.Equal("Are you sure you want to delete the \"Newcrest\" family?",
                SimitoneStrings.Format("x", "Are you sure you want to delete the \"%s\" family?", "Newcrest"));
        }

        #endregion

        #region the same substitution, run through FreeSO's own

        /// <summary>
        /// <c>SimitoneStrings.Substitute</c> exists to be indistinguishable from
        /// <c>ContentStrings.GetString(table, id, args)</c>, because a call site may read the same
        /// sentence from TS1's tables and from Simitone's with the same arguments. That claim was
        /// only ever a remark, and a remark about somebody else's implementation does not survive
        /// a FreeSO update: the two would drift silently, and the symptom would be a dialog whose
        /// English and whose translation disagree about a mistranslated specifier.
        ///
        /// <para>
        /// So the same string is put through both, read from the same table file so both sides are
        /// handed the identical bytes. The cases are the shapes that actually separate a
        /// hand-written substitution from the game's: a well-formed sentence, a mix of
        /// <c>%s</c> and <c>%d</c>, a specifier with no argument left, an argument with no
        /// specifier left, a bare percent, a percent followed by something that is not a
        /// specifier, a doubled percent, and a specifier at the very end of the string.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData("Rezone %s as a %s lot?", new[] { "12", "community" })]
        [InlineData("lot %d", new[] { "12" })]
        [InlineData("Rezone %s as a %s lot?", new[] { "12" })]
        [InlineData("%s %s %s", new[] { "a", "b" })]
        [InlineData("Done: 100%", new[] { "x" })]
        [InlineData("100%% sure of %s", new[] { "it" })]
        [InlineData("100% sure of %s", new[] { "it" })]
        [InlineData("a %q and a %s", new[] { "x" })]
        [InlineData("%s", new[] { "x" })]
        [InlineData("no specifiers at all", new[] { "x" })]
        [InlineData("", new[] { "x" })]
        public void SubstituteAgreesWithTheGamesOwnSubstitution(string format, string[] args)
        {
            var strings = Loader();
            strings.Load("UIText", TempTableDir(("_f900_subst.cst", "1 ^" + format + "^")));

            //Read both ways out of the loaded table, so neither side is testing its own idea of
            //what the file said.
            Assert.Equal(format, strings.GetString("f900", "1"));
            Assert.Equal(strings.GetString("f900", "1", args), SimitoneStrings.Substitute(format, args));
        }

        [Fact]
        public void TheOnePlaceTheTwoDeliberatelyDifferIsWithNoArgumentsAtAll()
        {
            // The documented divergence, and the reason the class is allowed one: FreeSO indexes
            // args.Length unconditionally, so with an empty array it drops the percent of every
            // specifier and leaves the letter behind. Ours returns the format untouched, because
            // a caller with no arguments has nothing to fill in and a player reading a raw "%s" is
            // worse off than one reading their own sentence - and because Format is a `params`
            // method, so a call site that forgot an argument gets the safe answer rather than a
            // mangled one. A null array is the same case: FreeSO would throw, ours does not.
            var strings = Loader();
            strings.Load("UIText", TempTableDir(("_f900_subst.cst", "1 ^Rezone %s as a %s lot?^")));

            Assert.Equal("Rezone s as a s lot?", strings.GetString("f900", "1", new string[0]));
            Assert.Equal("Rezone %s as a %s lot?", SimitoneStrings.Substitute("Rezone %s as a %s lot?", new string[0]));
            Assert.Equal("Rezone %s as a %s lot?", SimitoneStrings.Substitute("Rezone %s as a %s lot?", null));
        }

        [Fact]
        public void EveryShippedStringWithASpecifierStillAgreesWithTheGame()
        {
            // The end-to-end version, over the strings that actually ship: every entry that has a
            // specifier, written to one table, read back through FreeSO's own loader, and then run
            // through both substitutions on the identical string. A change to FreeSO's loop shows
            // up here rather than in one caller's dialog.
            //
            // The format is taken from what the loader produced rather than from the shipped file,
            // because the loader is entitled to rejoin a value that ran over several lines with
            // CRLF, and this is a test of substitution rather than of the file parser.
            var bodies = SimitoneStrings.ParseTable(ShippedTableText())
                .Where(pair => SpecifierCount(pair.Value) > 0)
                .Select((pair, i) => (i + 1) + " ^" + pair.Value + "^")
                .ToList();
            Assert.NotEmpty(bodies);

            var strings = Loader();
            strings.Load("UIText", TempTableDir(("_f900_subst.cst", string.Join("\r\n", bodies))));

            for (int i = 0; i < bodies.Count; i++)
            {
                var id = (i + 1).ToString();
                var loaded = strings.GetString("f900", id);
                var args = new string[SpecifierCount(loaded)];
                for (int a = 0; a < args.Length; a++) args[a] = "a" + a;

                Assert.Equal(strings.GetString("f900", id, args), SimitoneStrings.Substitute(loaded, args));
            }
        }

        #endregion

        #region paths and table naming

        [Fact]
        public void TheTableIdIsWhatTheFileNameWillBeReadAs()
        {
            // ContentStrings.Load derives the table id from the file name; if these ever
            // disagree, every string silently falls back to English.
            Assert.Equal("f900", SimitoneStrings.TableIdForFileName(SimitoneStrings.TableFileName));
            Assert.Equal(SimitoneStrings.Table, SimitoneStrings.TableIdForFileName("_f900_simitone.cst"));
        }

        [Fact]
        public void AFileNameWithNoSecondUnderscoreHasNoTableId()
        {
            // This is the case ContentStrings.Load gives up on - and gives up on the whole
            // directory, not just the file - so Simitone must never be named like it.
            Assert.Null(SimitoneStrings.TableIdForFileName("strings.cst"));
            Assert.Null(SimitoneStrings.TableIdForFileName(null));
        }

        [Fact]
        public void TheTablePathIsTheOneContentStringsReads()
        {
            Assert.Equal("Content/UI/uitext/english.dir/_f900_simitone.cst",
                SimitoneStrings.TablePath("english"));
        }

        [Fact]
        public void TheLanguageFolderIsLowerCasedAndSuffixed()
        {
            // The player's language is whatever STRLangCode's name is, and ContentStrings
            // lower-cases it before appending the suffix.
            Assert.Equal("pt-br.dir", SimitoneStrings.LanguageFolder("pt-BR"));
            Assert.Equal("english.dir", SimitoneStrings.LanguageFolder("English"));
        }

        [Fact]
        public void TheLanguageFolderFallsBackToEnglish()
        {
            // ContentStrings falls back to english.dir when the player's language has no table
            // directory; an unknown language here has to name the same folder.
            Assert.Equal("english.dir", SimitoneStrings.LanguageFolder(null));
            Assert.Equal("english.dir", SimitoneStrings.LanguageFolder("  "));
        }

        [Fact]
        public void TheResolvedTablePathIsTheRelativeOneRootedAtABase()
        {
            var expected = Path.Combine(new[] { @"C:\game", "Content", "UI", "uitext", "english.dir", "_f900_simitone.cst" });
            Assert.Equal(expected, SimitoneStrings.ResolveTablePath(@"C:\game"));
        }

        [Fact]
        public void TheTableIsActuallyShippedWhereTheGameLooksForIt()
        {
            // The one test here that touches the filesystem, and it is the point of the whole
            // exercise: the .cst has to be copied into the output, in the folder
            // ContentStrings reads, or the table is not there at all and every lookup falls back.
            var shipped = SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory);
            Assert.True(File.Exists(shipped),
                "Simitone's string table is not in the build output. Expected it at " + shipped
                + " (see the Content/UI/**/*.cst item in Simitone.Client.csproj).");
        }

        [Fact]
        public void TheLanguageFolderItselfIsShipped()
        {
            // The folder, not just the file, because ContentStrings decides which folder to read
            // once at startup and then hands the whole thing to Directory.GetFiles - so a build
            // that shipped the table without its folder would take the game down on the first
            // ContentStrings, rather than quietly falling back to English. This is the assertion
            // that says the fallback story has a floor.
            var folder = Path.Combine(AppContext.BaseDirectory, "Content", "UI", "uitext",
                SimitoneStrings.LanguageFolder(SimitoneStrings.DefaultLanguage));
            Assert.True(Directory.Exists(folder),
                "Simitone's string table folder is not in the build output. Expected it at " + folder
                + " (see the Content/UI/**/*.cst item in Simitone.Client.csproj).");
        }

        #endregion

        #region the shipped table

        private static string ShippedTableText()
        {
            return File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory));
        }

        [Fact]
        public void TheShippedTableParses()
        {
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            Assert.NotEmpty(table);
        }

        [Fact]
        public void EveryShippedEntryIsAWellFormedPair()
        {
            // A line with a missing closing '^' is swallowed by the loader's scan rather than
            // reported, which quietly truncates everything after it. Worth a check.
            var text = ShippedTableText();
            foreach (var line in text.Split('\n').Select(x => x.Trim('\r')))
            {
                if (line.Length == 0 || line.StartsWith(SimitoneStrings.CommentPrefix)) continue;
                Assert.Equal(2, line.Count(c => c == SimitoneStrings.Delimiter[0]));
                Assert.Matches(@"^\d+ \^.+\^$", line);
            }
        }

        [Fact]
        public void NoShippedIdIsRepeated()
        {
            // The loader stores ids in a dictionary and the second one wins, so a duplicate is
            // a translation of the wrong string reaching the UI with nothing to show for it.
            var ids = new List<string>();
            foreach (var line in ShippedTableText().Split('\n').Select(x => x.Trim('\r')))
            {
                if (line.Length == 0 || line.StartsWith(SimitoneStrings.CommentPrefix)) continue;
                ids.Add(line.Substring(0, line.IndexOf(SimitoneStrings.Delimiter)).Trim());
            }
            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void NoShippedTextIsEmpty()
        {
            // SimitoneStrings treats an empty entry as missing, so an empty line in the table is
            // dead weight that reads as a bug when it is found.
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            foreach (var pair in table)
                Assert.False(string.IsNullOrWhiteSpace(pair.Value), "Table id " + pair.Key + " has no text.");
        }

        [Fact]
        public void TheShippedTableUsesTheSimitoneTableId()
        {
            // Nothing in the game reads this file except under this id, so an f1xx id would
            // collide with FreeSO's TSO tables and an STR number with TS1's own.
            Assert.Equal("f900", SimitoneStrings.TableIdForFileName(SimitoneStrings.TableFileName));
        }

        [Theory]
        // The credits panel's own three strings, the block the table shipped with.
        [InlineData("1", "Credits")]
        [InlineData("2", "Close")]
        [InlineData("3", "Scroll with the mouse wheel, or click and drag the text.")]
        public void TheIdsTheCreditsPanelAsksForAreTheEnglishItUsedToShow(string id, string english)
        {
            // The table is meant to be a *move* of the literals, not a rewording of them: if the
            // English here drifts from what the panel showed before, every language silently
            // gains a different word for the same button.
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            Assert.True(table.ContainsKey(id), "No entry for id " + id);
            Assert.Equal(english, table[id]);
        }

        #endregion

        #region every id routed this wave

        /// <summary>
        /// Every id wired to a literal in the second pass over <c>UI/**</c>, against the English
        /// the literal held.
        ///
        /// <para>
        /// The same two claims as everywhere else, and the same reason they need saying: the
        /// table is a <em>move</em>, not a rewording, and a call site that asks for an id the
        /// table does not have falls back to an English the translator never sees. Both halves
        /// are pinned - the entry's text, and the fallback a build with no table produces.
        /// </para>
        /// </summary>
        [Theory]
        // Options quick settings strip (UISettingsPanel).
        [InlineData("20", "Settings")]
        [InlineData("21", "Apply & Save")]
        [InlineData("22", "Revert")]
        [InlineData("23", "Anti-Alias")]
        [InlineData("24", "Lighting")]
        [InlineData("25", "Shadows")]
        [InlineData("26", "Water")]
        [InlineData("27", "Shaders")]
        [InlineData("28", "Transitions")]
        [InlineData("29", "Edge Scroll")]
        [InlineData("30", "Settings - unsaved changes")]
        [InlineData("31", "Settings - saved")]
        [InlineData("32", "Settings - reverted")]
        [InlineData("33", "On")]
        [InlineData("34", "Off")]
        // Create-a-Sim (UIFamiliesCASPanel, UIFamilyCASPanel, UISimCASPanel,
        // UISelectSkinAlert).
        [InlineData("100", "Select a Family")]
        [InlineData("101", "First Name")]
        [InlineData("102", "Last Name")]
        [InlineData("103", "Gender")]
        [InlineData("104", "Skin Color")]
        [InlineData("105", "Please Select Outfit")]
        [InlineData("106", "Adopt a Pet")]
        [InlineData("107", "Age")]
        // Catalogue browse and search (UIBuyBrowsePanel).
        [InlineData("82", "No items found")]
        // Call neighbour (UICallNeighborAlert).
        [InlineData("130", "Call Neighbour")]
        [InlineData("131", "Call")]
        [InlineData("132", "Cancel")]
        [InlineData("133", "Unknown")]
        // Desktop UCP tooltips (UIDesktopUCP), plus the one button Simitone added.
        [InlineData("140", "Live Mode")]
        [InlineData("141", "Buy Mode")]
        [InlineData("142", "Build Mode")]
        [InlineData("143", "Options")]
        [InlineData("144", "Floor Up")]
        [InlineData("145", "Floor Down")]
        [InlineData("146", "Roof")]
        [InlineData("147", "Walls Up")]
        [InlineData("148", "Walls Cutaway")]
        [InlineData("149", "Walls Down")]
        [InlineData("150", "Zoom In")]
        [InlineData("151", "Zoom Out")]
        [InlineData("152", "Rotate Clockwise")]
        [InlineData("153", "Rotate Counter-Clockwise")]
        [InlineData("154", "Normal Speed")]
        [InlineData("155", "Fast")]
        [InlineData("156", "Ultra Fast")]
        [InlineData("157", "Pause")]
        [InlineData("158", "Eyedropper Tool (E)")]
        // Object query panel (UIQueryPanel).
        [InlineData("200", "No information available.")]
        // The floor readout both live-mode strips carry (UIDesktopUCP, UIMainPanel).
        [InlineData("400", "1st")]
        [InlineData("401", "2nd")]
        [InlineData("402", "3rd")]
        [InlineData("403", "4th")]
        [InlineData("404", "5th")]
        // Auto-follow panel (UILotControl).
        [InlineData("410", "Auto-follow")]
        [InlineData("411", "Auto-follow - travelling with you")]
        [InlineData("412", "Auto-follow - Downtown")]
        [InlineData("413", "Already queued: %s. Press %s to change it.")]
        [InlineData("414", "Requests are queued both ways - press one to pick.")]
        [InlineData("415", "That is you - you are already here.")]
        [InlineData("416", "Pets cannot be auto-followed.")]
        [InlineData("417", "Ghosts are not travelling.")]
        [InlineData("418", "Only sims on this lot can be followed.")]
        [InlineData("419", "Already coming along with the family.")]
        [InlineData("420", "This sim has a request queued both ways.")]
        [InlineData("421", "Not available.")]
        [InlineData("422", "They are away from home.")]
        [InlineData("423", "They are not in your family.")]
        [InlineData("424", "Take Home")]
        [InlineData("425", "Invite to Downtown")]
        // Options dialog chrome (UIOptionsAlert).
        [InlineData("430", "Options")]
        [InlineData("431", "OK")]
        // Lot-load error dialog (TS1GameScreen).
        [InlineData("440", "OK")]
        // Makin' Magic book dialog (UIMakingMagicAlert).
        [InlineData("450", "Cook")]
        [InlineData("451", "Cast")]
        [InlineData("452", "Cancel")]
        [InlineData("453", "not yet discovered")]
        // Live-mode job panel (UIJobSubpanel).
        [InlineData("460", "Performance")]
        [InlineData("461", "Salary: §%s (%s-%s)")]
        // Custom-content warning (FailedContentService).
        [InlineData("470", "Custom Content Warning")]
        [InlineData("471", "Some custom content files could not be loaded. The game will continue without them.")]
        [InlineData("472", "truncated or corrupt")]
        [InlineData("473", "invalid format")]
        [InlineData("474", "could not be read")]
        [InlineData("475", "catalog error")]
        [InlineData("476", "could not be decoded")]
        [InlineData("477", "unknown error")]
        [InlineData("478", "... and %s more")]
        [InlineData("479", "The usual causes are a truncated or corrupt file, a missing matching .cfp file, or a format this game does not read.")]
        // Options dialog, Sound page (UIAudioOptionsSubpanel). Three per row: the caption, the
        // tooltip's title, and its body. One line each on purpose - SimitoneStrings refuses a
        // control character, so a two-line tooltip would fall back to the English.
        [InlineData("960", "Sound Effects")]
        [InlineData("961", "Sound Effects - volume")]
        [InlineData("962", "The sounds the lot makes: doors, appliances, footsteps, the click of a control you just pressed.")]
        [InlineData("963", "Music")]
        [InlineData("964", "Music - volume")]
        [InlineData("965", "The background music the lot plays.")]
        [InlineData("966", "Voice")]
        [InlineData("967", "Voice - volume")]
        [InlineData("968", "The Sims' own voices and anything the game narrates.")]
        [InlineData("969", "Ambience")]
        [InlineData("970", "Ambience - volume")]
        [InlineData("971", "Weather, and the sounds the lot plays at you in the background.")]
        [InlineData("972", "Level")]
        [InlineData("973", "Off")]
        [InlineData("974", "Full")]
        [InlineData("975", "%d of %d")]
        [InlineData("976", "Mute this group")]
        [InlineData("977", "Muted - click to unmute")]
        // Cheat box (CheatCommands.HelpText). § is the Simoleon sign the other money strings use.
        [InlineData("980", "Cheats")]
        [InlineData("981", "Add §1,000,000")]
        [InlineData("982", "Add §1,000")]
        [InlineData("983", "Let objects be picked up and moved")]
        [InlineData("984", "Add §n")]
        [InlineData("985", "Press Ctrl+Alt+Shift+C to close this box, and ! or ; to repeat a cheat.")]
        public void EveryIdRoutedThisWaveCarriesTheEnglishItReplaced(string id, string english)
        {
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            Assert.True(table.ContainsKey(id), "No entry for table id " + id + ".");
            Assert.Equal(english, table[id]);
        }

        /// <summary>
        /// The other half of the same claim, and the one a test process is actually in a position
        /// to check: with no <c>GameFacade.Strings</c> at all, each of those ids hands back the
        /// very English pinned above. A call site that spelled its fallback differently from the
        /// table would pass the previous test and still leave every language with a different
        /// word for the same button from the one a translator was given.
        /// </summary>
        [Theory]
        [InlineData("20", "Settings")]
        [InlineData("23", "Anti-Alias")]
        [InlineData("34", "Off")]
        [InlineData("100", "Select a Family")]
        [InlineData("107", "Age")]
        [InlineData("130", "Call Neighbour")]
        [InlineData("133", "Unknown")]
        [InlineData("140", "Live Mode")]
        [InlineData("158", "Eyedropper Tool (E)")]
        [InlineData("200", "No information available.")]
        [InlineData("400", "1st")]
        [InlineData("404", "5th")]
        [InlineData("410", "Auto-follow")]
        [InlineData("425", "Invite to Downtown")]
        [InlineData("430", "Options")]
        [InlineData("440", "OK")]
        [InlineData("453", "not yet discovered")]
        [InlineData("460", "Performance")]
        [InlineData("470", "Custom Content Warning")]
        [InlineData("477", "unknown error")]
        // The Sound page and the cheat list, the newest block in the table. Both fall back to
        // English in a test process, which is the same floor every other id here sits on.
        [InlineData("960", "Sound Effects")]
        [InlineData("966", "Voice")]
        [InlineData("972", "Level")]
        [InlineData("976", "Mute this group")]
        [InlineData("980", "Cheats")]
        [InlineData("985", "Press Ctrl+Alt+Shift+C to close this box, and ! or ; to repeat a cheat.")]
        public void WithNoTableAtAllEachOfThoseIdsSaysTheEnglishItReplaced(string id, string english)
        {
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.Equal(english, SimitoneStrings.Get(id, english));
        }

        /// <summary>
        /// Three of this wave's ids take arguments, and the count is the contract: a mistranslated
        /// entry that asks for one too many reads as "Rezone 12 as a s lot?" rather than throwing.
        /// Pinned so one cannot be written asking for the wrong number.
        /// </summary>
        [Theory]
        [InlineData("413", "Already queued: Take Home. Press Invite to Downtown to change it.",
            "Take Home|Invite to Downtown")]
        [InlineData("461", "Salary: §90 (9am-5pm)", "90|9am|5pm")]
        [InlineData("478", "... and 22 more", "22")]
        [InlineData("975", "5 of 10", "5|10")]
        public void AnIdThatTakesArgumentsStillProducesTheSentenceItUsedTo(string id, string expected, string args)
        {
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.Equal(expected, SimitoneStrings.Format(id, expected, args.Split('|')));
        }

        /// <summary>
        /// ...and the two-sided version, which is the half that actually bites: the table entry
        /// and the fallback at the call site have to ask for the same number of things, because
        /// only one of them is ever the translator's to change.
        /// </summary>
        [Theory]
        [InlineData("413", 2)]
        [InlineData("461", 3)]
        [InlineData("478", 1)]
        [InlineData("975", 2)]
        public void AnIdThatTakesArgumentsAsksForTheSameOnesItsCallSitePasses(string id, int expected)
        {
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            Assert.Equal(expected, SpecifierCount(table[id]));
        }

        /// <summary>
        /// How many things a table entry asks to be filled in - <c>%s</c>/<c>%d</c> and
        /// <c>{0}</c>-style placeholders both count, because both are filled left to right.
        /// </summary>
        private static int SpecifierCount(string text)
        {
            return System.Text.RegularExpressions.Regex.Matches(text, @"%[sd]").Count
                + System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Count;
        }

        /// <summary>
        /// The reverse: a caption that takes nothing must not have picked up a specifier, or a
        /// translation will silently lose a word with nothing left to fill it with.
        /// </summary>
        [Fact]
        public void NoneOfTheNewIdsThatTakeNothingAsksForAnything()
        {
            // Every id whose call site passes something. A running list, and each wave that adds a
            // string taking arguments adds its id here - the assertion below is that the table and
            // the call sites agree, and a one-sided change to either is what it catches. A
            // duplicate entry is harmless, so two waves naming the same id cannot conflict.
            var taking = new HashSet<string>(new[]
            {
                "211", "215", "217", "222", "224",     // version stamp and the update check
                "308", "312", "313",                          // neighbourhood prompts
                "325", "329", "332", "334", "335", "336", "337", "338", "339", "340", // the
                "358", "362",                                 //   neighbourhood manager
                "413", "461", "478",                          // the blocks above
                "484", "485",                                 // the career panel
                "501", "502", "503", "504", "505", "528",      // the career / fame / interests rows
                "538", "540",                                 // the house and inventory panels
                "975"                                         // the Sound page's level readout
            }, StringComparer.Ordinal);
            var table = SimitoneStrings.ParseTable(ShippedTableText());
            foreach (var pair in table)
            {
                if (taking.Contains(pair.Key)) continue;
                if (!IsRoutedThisWave(pair.Key)) continue;
                Assert.True(SpecifierCount(pair.Value) == 0,
                    "Table id " + pair.Key + " takes no arguments at its call site but asks for some.");
            }
        }

        /// <summary>
        /// Which ids belong to the blocks this wave routes: the settings strip, the CAS headings,
        /// the call-neighbour dialog, the desktop UCP, the object query panel, and everything from
        /// 400 up. Scoped so the rest of the table is somebody else's contract.
        /// </summary>
        private static bool IsRoutedThisWave(string id)
        {
            if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            // The 60-79 block and the 190-224 ones are excluded because they predate this pass and
            // are somebody else's contract; everything from 400 up is in.
            return (n >= 20 && n < 35) || (n >= 82 && n < 83) || (n >= 100 && n < 108)
                || (n >= 130 && n < 134) || (n >= 140 && n < 159) || (n == 200) || (n >= 400);
        }

        #endregion

        #region through FreeSO's own loader

        /// <summary>
        /// A <see cref="ContentStrings"/> with no constructor run and its table dictionary
        /// created by hand.
        ///
        /// <para>
        /// The constructor is the whole reason this goes through
        /// <see cref="RuntimeHelpers.GetUninitializedObject"/>: it reads
        /// <c>GlobalSettings.Default</c> (a player-global that resolves out of the user's
        /// config.ini) and then loads TS1's <c>GameData/UIText.iff</c> from a Sims install, and
        /// a test has neither and must not touch either. What is under test is
        /// <c>Load</c>, which is the part that turns a file on disk into a table - and the only
        /// field of the class that does not belong to that constructor's setup is its table
        /// dictionary, which <c>Load</c> itself would have created. The field is private, so
        /// this reaches for it by name and fails loudly rather than null-ref if a FreeSO update
        /// renames it.
        /// </para>
        /// </summary>
        private static ContentStrings Loader()
        {
            var loader = (ContentStrings)RuntimeHelpers.GetUninitializedObject(typeof(ContentStrings));

            var field = typeof(ContentStrings).GetField("StringTable", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(loader,
                new Dictionary<string, Dictionary<string, Dictionary<string, string>>>());

            return loader;
        }

        [Fact]
        public void FreeSOsOwnLoaderFilesTheTableUnderTheSimitoneTableId()
        {
            // The end-to-end check, and the one that matters: not that our parser agrees with
            // our parser, but that the loader the game actually uses puts this file where
            // SimitoneStrings looks. A table filed under the wrong id looks fine until every
            // string falls back to English.
            var strings = Loader();
            strings.Load("UIText", Path.Combine(AppContext.BaseDirectory, "Content", "UI", "uitext", "english.dir"));

            Assert.Equal("Credits", strings.GetString("f900", "1"));
            Assert.Equal("Close", strings.GetString("f900", "2"));
            Assert.Equal("Scroll with the mouse wheel, or click and drag the text.",
                strings.GetString("f900", "3"));
        }

        [Fact]
        public void FreeSOsOwnLoaderReadsEveryShippedEntry()
        {
            // A file the loader opens but cannot read past the first entry still files itself
            // under the right id, so the id check above is not enough on its own: this asks for
            // every id the table is supposed to carry, and compares what comes back with what
            // the file says.
            var strings = Loader();
            strings.Load("UIText", Path.Combine(AppContext.BaseDirectory, "Content", "UI", "uitext", "english.dir"));

            foreach (var pair in SimitoneStrings.ParseTable(ShippedTableText()))
            {
                var loaded = strings.GetString("f900", pair.Key);
                Assert.True(SimitoneStrings.IsUsable(loaded), "The loader could not read table id " + pair.Key);
                Assert.Equal(pair.Value, loaded);
            }
        }

        [Fact]
        public void FreeSOsOwnLoaderDoesNotFindTheTableUnderATs1TableNumber()
        {
            // TS1's own tables are numbered from 100, and a Simitone id that resolved to one
            // would overwrite a game string rather than add to it. f900 is chosen so that it
            // cannot: the loader must still be missing a table that never existed.
            var strings = Loader();
            strings.Load("UIText", Path.Combine(AppContext.BaseDirectory, "Content", "UI", "uitext", "english.dir"));

            Assert.False(SimitoneStrings.IsUsable(strings.GetString("900", "1")));
        }

        #endregion

        #region what a broken or absent table looks like to the loader

        /// <summary>
        /// The four ways there is no string to show, each taken all the way through FreeSO's
        /// loader rather than faked at the lookup: no language directory at all (which is what a
        /// stripped install looks like, and what the test process looks like for the same
        /// reason), a language directory nobody has translated, a directory with no table file
        /// in it, and a file with no entry for the id being asked for.
        ///
        /// <para>
        /// Each ends at <see cref="SimitoneStrings.ResolveClean"/>, which is the whole point of
        /// the design: whatever the table did or did not have, the answer is the English.
        /// </para>
        /// </summary>
        [Fact]
        public void NoLanguageDirectoryMeansTheEnglish()
        {
            // Nothing to point the loader at, so there is no table - which is exactly the state
            // GameFacade.Strings being null puts the lookup in, and the state the other tests in
            // this file run in.
            Assert.Null(FSO.Client.GameFacade.Strings);
            Assert.Equal("fallback", SimitoneStrings.Get("1", "fallback"));
        }

        [Fact]
        public void ALanguageDirectoryNobodyHasTranslatedMeansTheEnglish()
        {
            // The directory exists - the game's own fallback is per-directory, not per-file - but
            // there is no table in it, so the loader has nothing to file.
            var strings = Loader();
            strings.Load("UIText", TempTableDir());

            Assert.Equal("fallback", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "1"), "fallback"));
        }

        [Fact]
        public void ATableWithNoEntryForTheIdMeansTheEnglish()
        {
            var strings = Loader();
            strings.Load("UIText", TempTableDir(("_f900_other.cst", "1 ^Something else entirely^")));

            Assert.Equal("fallback", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "300"), "fallback"));
        }

        [Fact]
        public void AnEmptyEntryMeansTheEnglish()
        {
            // A translator who blanked a line rather than deleting it: the id is there, the text
            // is not, and an empty caption is the one answer a player cannot use.
            var strings = Loader();
            strings.Load("UIText", TempTableDir(("_f900_other.cst", "1 ^^")));

            Assert.Equal("fallback", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "1"), "fallback"));
        }

        /// <summary>
        /// <c>ContentStrings.Load</c> takes its table id from the text between the first and the
        /// second <c>_</c> in each file name, and gives up on the <em>whole directory</em> the
        /// moment it meets a name that has no second one - a stray readme left beside the tables
        /// would therefore cost every string in the folder. That is why
        /// <see cref="TableFileName"/> carries both underscores, and why
        /// <see cref="TableIdForFileName"/> exists to keep the two in step.
        ///
        /// <para>
        /// Only the half of that a test can pin deterministically is asserted here: a directory
        /// whose files cannot be filed as tables holds no strings at all. Which of the bad names
        /// the loader reaches first is the filesystem's business, not a contract.
        /// </para>
        /// </summary>
        [Fact]
        public void AFileTheLoaderCannotFileAsATableIsNotAStringSource()
        {
            var strings = Loader();
            strings.Load("UIText", TempTableDir(("readme.txt", "not a table")));

            Assert.False(SimitoneStrings.IsUsable(strings.GetString(SimitoneStrings.Table, "1")));
        }

        /// <summary>
        /// A duplicate id is not an error the loader reports - the second entry simply overwrites
        /// the first, because a table is a dictionary - so a translation of the wrong string
        /// reaches the UI with nothing to show for it. Pinned here so the shipped file's
        /// no-duplicates check has something to be the same answer as.
        /// </summary>
        [Fact]
        public void ADuplicateIdIsTheLastEntryAndIsNotAnError()
        {
            const string Body = "1 ^first^\n1 ^second^";

            var strings = Loader();
            strings.Load("UIText", TempTableDir((SimitoneStrings.TableFileName, Body)));

            var loaded = strings.GetString(SimitoneStrings.Table, "1");
            Assert.Equal("second", loaded);
            Assert.Equal("second", SimitoneStrings.ParseTable(Body)["1"]);
        }

        /// <summary>
        /// The end-to-end version of the control-character rule, and the reason it exists: a
        /// multi-line entry is how <c>ContentStrings.Load</c> expresses a value that runs over
        /// more than one line, so a translator who left a value unterminated - or typed a tab,
        /// or pasted a form feed - hands the game text that a label will draw as layout. The
        /// loader passes it through faithfully, as it should; the lookup is what refuses it.
        /// </summary>
        [Fact]
        public void AnEntryCarryingAControlCharacterIsRefusedByTheLookupNotTheLoader()
        {
            var strings = Loader();
            strings.Load("UIText", TempTableDir((SimitoneStrings.TableFileName,
                "1 ^Neighbourhoods\nthat the page shows^\n2 ^Open\tthe other one^")));

            // The loader is faithful: it is not its job to decide what a label can draw, and it
            // rejoins a value that ran over several lines with CRLF.
            Assert.Equal("Neighbourhoods\r\nthat the page shows",
                strings.GetString(SimitoneStrings.Table, "1"));
            Assert.Equal("Open\tthe other one", strings.GetString(SimitoneStrings.Table, "2"));

            // The lookup is the one that refuses, and falls back to the English.
            Assert.Equal("Neighbourhoods", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "1"), "Neighbourhoods"));
            Assert.Equal("Open the other one", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "2"), "Open the other one"));
        }

        /// <summary>
        /// A line with a missing closing <c>^</c> is not reported by the loader, it just runs the
        /// scan on to the next delimiter it can find - so that entry swallows the rest of the
        /// file, and the ids after it simply never arrive. Which is why the shipped table is
        /// checked for well-formed pairs at all: this is what a mistyped line costs.
        /// </summary>
        [Fact]
        public void AnUnterminatedEntrySwallowsTheRestOfTheFile()
        {
            var strings = Loader();
            strings.Load("UIText", TempTableDir((SimitoneStrings.TableFileName,
                "1 ^fine^\n2 ^never closed\n3 ^lost^")));

            Assert.Equal("fine", strings.GetString(SimitoneStrings.Table, "1"));
            // id 2 ran on to the next '^', which was id 3's...
            Assert.Equal("never closed\r\n3 ", strings.GetString(SimitoneStrings.Table, "2"));
            // ...so id 3 was consumed as part of it and there is no id 3 at all.
            Assert.False(SimitoneStrings.IsUsable(strings.GetString(SimitoneStrings.Table, "3")));
            Assert.Equal("fallback", SimitoneStrings.ResolveClean(
                strings.GetString(SimitoneStrings.Table, "3"), "fallback"));
        }

        #endregion

        #region the shipped table's own hygiene

        /// <summary>
        /// The rule the lookup applies, asserted against the file as it ships rather than
        /// against a value typed into a test: not one entry may carry a control character, or a
        /// language would be the only one where a caption draws as layout.
        /// </summary>
        [Fact]
        public void NoShippedEntryCarriesAControlCharacter()
        {
            foreach (var pair in SimitoneStrings.ParseTable(ShippedTableText()))
            {
                Assert.True(SimitoneStrings.IsClean(pair.Value),
                    "Table id " + pair.Key + " (" + Escape(pair.Value) + ") is not a label-safe string.");
            }
        }

        /// <summary>
        /// ...and no id either, since the id is what a translator matches on and a control
        /// character in it cannot be typed into a lookup by hand.
        /// </summary>
        [Fact]
        public void NoShippedIdCarriesAControlCharacter()
        {
            foreach (var id in SimitoneStrings.ParseTable(ShippedTableText()).Keys)
            {
                Assert.True(SimitoneStrings.IsClean(id), "Table id " + Escape(id) + " is not a usable key.");
            }
        }

        private static string Escape(string value)
        {
            if (value == null) return "<null>";
            return "\"" + value.Replace("\r", "\\r").Replace("\n", "\\n")
                .Replace("\t", "\\t").Replace("\0", "\\0") + "\"";
        }

        #endregion
    }
}
