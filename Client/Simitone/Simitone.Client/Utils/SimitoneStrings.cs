/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Simitone's own UI strings, looked up in a string table of its own with a guaranteed
    /// English fallback.
    ///
    /// <para>
    /// The strings this game shows come from two places. Anything The Sims 1 itself had is in
    /// TS1's <c>GameData/UIText.iff</c> and is already translated by whoever shipped the
    /// player's copy of the game, so those are read straight out of
    /// <see cref="GameFacade.Strings"/> with the game's own numeric table id
    /// (see <c>OptionsStrings</c> for that pattern). Everything Simitone added on top -
    /// the settings strip, the buy-mode search box, the rezone dialogs, the credits screen -
    /// had nowhere to go but a string literal in a <c>.cs</c> file, which is the state this
    /// type exists to fix.
    /// </para>
    ///
    /// <para>
    /// The fix is FreeSO's own mechanism: a <c>.cst</c> file under
    /// <c>Content/UI/uitext/&lt;language&gt;.dir</c>, which <c>ContentStrings</c> picks up in
    /// TS1 mode as well as TSO mode and merges into the "UIText" directory under a table id
    /// taken from the file name. Simitone's table is <see cref="Table"/> -
    /// <c>_f900_simitone.cst</c> becomes table <c>f900</c> - which sits clear of FreeSO's
    /// <c>f1xx</c> TSO tables and of TS1's numeric <c>STR</c> chunk ids, so it cannot shadow
    /// either. Ids within it are grouped by the file that used to hold the literal; see the
    /// comments in the <c>.cst</c> itself, which is the authoritative list.
    /// </para>
    ///
    /// <para>
    /// Every lookup carries the English text as a second argument, so a build with no table at
    /// all (a stripped install, a packaging mistake, a language directory that has not been
    /// translated yet) shows the English it always showed rather than a
    /// <see cref="MissingMarker"/> or a blank label. That is the same contract
    /// <c>OptionsStrings</c> gives the Options menu; the difference is only the table.
    /// </para>
    ///
    /// <para>
    /// A second rule applies to <b>this</b> table and not to a foreign one: an entry carrying a
    /// control character is treated as unusable and falls back, because the strings read out of
    /// here go straight into a <c>UILabel</c> caption and a stray newline or escape out of a
    /// mistranslated entry is drawn as layout, or as nothing at all. A table that came with the
    /// player's copy of the game is exempt, because those strings may legitimately contain
    /// newlines and are not ours to judge - see <see cref="ResolveClean"/> and
    /// <see cref="GetFromTable"/>.
    /// </para>
    ///
    /// <para>
    /// Nothing in here needs Content initialised. The lookup degrades to the fallback when
    /// <see cref="GameFacade.Strings"/> is null, and the string/path/parse helpers are pure,
    /// so the whole type is unit testable headlessly.
    /// </para>
    ///
    /// <para>
    /// <b>One thing the fallback cannot save.</b> The English table has to actually ship, and
    /// its folder with it. <c>ContentStrings</c> picks the language folder once at startup and
    /// hands it straight to <c>Directory.GetFiles</c> without checking that it is there, so a
    /// build with no <c>Content/UI/uitext/english.dir</c> at all takes the game down on the first
    /// string table rather than showing English. The two tests that check the file and the folder
    /// are the floor for that, and the <c>Content/UI</c> glob in <c>Simitone.Client.csproj</c> is
    /// what puts them there.
    /// </para>
    /// </summary>
    public static class SimitoneStrings
    {
        /// <summary>
        /// The id of Simitone's own string table, and the name of the constant
        /// <c>ContentStrings</c> files it under. Fixed: the file name has to keep producing
        /// this id, because that is how the lookup finds it.
        /// </summary>
        public const string Table = "f900";

        /// <summary>
        /// File name of Simitone's own string table. <c>ContentStrings.Load</c> takes the text
        /// between the first and the second <c>_</c>, so the leading <c>_</c> is required and so
        /// is the second one.
        /// </summary>
        public const string TableFileName = "_f900_simitone.cst";

        /// <summary>
        /// What <c>ContentStrings.GetString</c> hands back in place of a string it does not
        /// have: the literal when the table exists but the id does not, an empty string when
        /// the table itself never loaded. Both are unusable, and both have to fall back.
        /// </summary>
        public const string MissingMarker = "***MISSING***";

        /// <summary>Line prefix <c>ContentStrings.Load</c> strips before parsing a table.</summary>
        public const string CommentPrefix = "//";

        /// <summary>Field separator inside a <c>.cst</c> entry, as <c>id ^text^</c>.</summary>
        public const string Delimiter = "^";

        /// <summary>
        /// Folder, relative to the game's working directory, that <c>ContentStrings</c> reads
        /// its own tables from. Spelled with forward slashes because that is how
        /// <c>ContentStrings</c> itself spells it, and the value is compared against the
        /// layout the build produces rather than being opened.
        /// </summary>
        public const string UITextFolder = "Content/UI/uitext/";

        /// <summary>Suffix every language folder carries, as in <c>english.dir</c>.</summary>
        public const string LanguageFolderSuffix = ".dir";

        /// <summary>
        /// The language whose folder holds the English table, and the fallback folder name when
        /// the caller does not name one.
        /// </summary>
        public const string DefaultLanguage = "english";

        /// <summary>
        /// Looks a string up in Simitone's table, falling back to <paramref name="fallback"/>.
        ///
        /// <para>
        /// A null or empty <paramref name="id"/> means "there is no id for this" rather than
        /// "look up an empty key", and goes straight to the fallback - the underlying lookup
        /// throws on a null key, and a string a caller has not been given an id for yet is not
        /// an error worth throwing over.
        /// </para>
        ///
        /// <para>
        /// An entry carrying a control character falls back too - see
        /// <see cref="ResolveClean"/>, which is the rule this adds on top of
        /// <see cref="GetFromTable"/>.
        /// </para>
        /// </summary>
        /// <param name="id">Id within <see cref="Table"/>.</param>
        /// <param name="fallback">English text, shown when the table has no usable entry.</param>
        public static string Get(string id, string fallback = null)
        {
            return ResolveClean(GetFromTable(Table, id, fallback), fallback);
        }

        /// <summary>
        /// Looks a string up in a named table, falling back to <paramref name="fallback"/> when
        /// the table or the id is not there.
        ///
        /// <para>
        /// The table-explicit form is for the strings that stay in TS1's own tables (the CAS
        /// confirmations, the Options menu). Those are already translated; what they lack is a
        /// floor. A raw <c>GameFacade.Strings.GetString</c> renders <see cref="MissingMarker"/>
        /// on screen when the id is not in the table, which is a worse answer than the English
        /// the string was written with.
        /// </para>
        ///
        /// <para>
        /// Deliberately not the control-character rule that <see cref="Get"/> applies. A table
        /// that came with the player's copy of the game is allowed to contain a newline - a
        /// tooltip body is two lines of it in several - and judging another game's text by
        /// Simitone's own rules would throw away translations that are correct.
        /// </para>
        /// </summary>
        /// <param name="table">Table id, e.g. TS1's "128" or <see cref="Table"/>.</param>
        /// <param name="id">Id within the table.</param>
        /// <param name="fallback">English text, shown when the table has no usable entry.</param>
        public static string GetFromTable(string table, string id, string fallback = null)
        {
            if (string.IsNullOrEmpty(id)) return fallback;

            // A null table is a caller that has not filled a string table constant in, and a null
            // GameFacade.Strings is a game that has not built its tables yet. Neither is worth a
            // throw: the fallback is the answer, and it is the answer the player wants.
            var strings = GameFacade.Strings;
            if (strings == null || string.IsNullOrEmpty(table)) return fallback;

            return Resolve(strings.GetString(table, id), fallback);
        }

        /// <summary>
        /// As <see cref="Get(string, string)"/>, but substitutes <paramref name="args"/> into
        /// the <c>%s</c> and <c>%d</c> specifiers of whichever string won.
        ///
        /// <para>
        /// The fallback goes through the same substitution, so the English in the call site is
        /// written with the same specifiers as the table entry and the two cannot disagree
        /// about how many arguments there are.
        /// </para>
        /// </summary>
        public static string Format(string id, string fallback, params string[] args)
        {
            return Substitute(Get(id, fallback), args);
        }

        /// <summary>As <see cref="GetFromTable"/>, but with argument substitution.</summary>
        public static string FormatFromTable(string table, string id, string fallback, params string[] args)
        {
            return Substitute(GetFromTable(table, id, fallback), args);
        }

        /// <summary>
        /// Whether a looked-up value is something a player may be shown. False for null, for
        /// empty and whitespace-only text, and for <see cref="MissingMarker"/>.
        /// </summary>
        public static bool IsUsable(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return !string.Equals(value, MissingMarker, StringComparison.Ordinal);
        }

        /// <summary>
        /// The lookup's decision, on its own: <paramref name="value"/> when it is usable,
        /// <paramref name="fallback"/> otherwise. Split out from the lookup so the rule can be
        /// tested without a string table, and so a caller with a value from some other source
        /// can apply exactly the same rule.
        /// </summary>
        public static string Resolve(string value, string fallback)
        {
            return IsUsable(value) ? value : fallback;
        }

        /// <summary>
        /// Whether a value out of Simitone's own table is safe to put in a label: usable, and
        /// free of control characters.
        /// </summary>
        /// <remarks>
        /// The extra condition is about what a label does with the text rather than about the
        /// text being wrong. Everything read out of <see cref="Table"/> goes straight into a
        /// <c>UILabel</c> caption, and a <c>\n</c> in there is layout, a <c>\t</c> is a gap, and
        /// a <c>\u0001</c> is a glyph the font does not have and which draws as a box. None of
        /// them can be undone downstream, and all of them are a mistranslated or half-edited
        /// entry rather than a string anyone chose to show. Falling back to the English is
        /// strictly better than any of them.
        ///
        /// <para>
        /// No entry in the shipped table needs a control character: the two strings that are
        /// genuinely multi-line (the save guide paragraph and the description below it) are
        /// joined by the call site, which knows why they are separate paragraphs and where the
        /// break belongs.
        /// </para>
        /// </remarks>
        public static bool IsClean(string value)
        {
            if (!IsUsable(value)) return false;
            foreach (var c in value)
            {
                if (char.IsControl(c)) return false;
            }
            return true;
        }

        /// <summary>
        /// <see cref="Get"/>'s decision on its own: <paramref name="value"/> when it is clean,
        /// <paramref name="fallback"/> otherwise. Split out for the same reason
        /// <see cref="Resolve"/> is.
        /// </summary>
        /// <remarks>
        /// A <paramref name="fallback"/> that is itself not clean is still returned, because
        /// there is nothing better to return and the caller's own English is the last line of
        /// defence, not something to be second-guessed here.
        /// </remarks>
        public static string ResolveClean(string value, string fallback)
        {
            return IsClean(value) ? value : fallback;
        }

        /// <summary>
        /// Replaces each <c>%s</c> or <c>%d</c> with the next argument, in order, and is
        /// deliberately the same substitution <c>ContentStrings.GetString(table, id, args)</c>
        /// does, quirk and all.
        ///
        /// <para>
        /// The quirk is what happens on a mismatch. A specifier with no argument left has its
        /// <c>%</c> dropped and its letter left behind, so a mistranslated entry that asks for
        /// one argument too many reads "Rezone 12 as a s lot?" rather than leaking a
        /// <c>%s</c> into a dialog. An argument with no specifier left is dropped too. Matching
        /// this exactly is the point: a call site may be reading from TS1's tables and from
        /// Simitone's with the same arguments, and the two must not format differently.
        /// </para>
        ///
        /// <para>
        /// <b>There is exactly one place they are allowed to differ: an empty or null argument
        /// array.</b> FreeSO reads <c>args.Length</c> unconditionally, so with nothing to fill in
        /// it drops the percent of every specifier and leaves the letter ("Rezone s lot?"), and
        /// with a null array it throws. This returns the format untouched instead. A caller with
        /// no arguments has nothing to say, and a player reading a raw <c>%s</c> is worse off
        /// than one reading their own sentence - which matters because every entry point here is
        /// <c>params</c>, so a forgotten argument arrives as an empty array rather than as a
        /// mistake anyone can see. <c>SimitoneStringsTests</c> runs both substitutions over the
        /// shipped table and pins this as the only divergence, so a FreeSO change to that loop
        /// fails a test rather than a dialog.
        /// </para>
        /// </summary>
        public static string Substitute(string format, string[] args)
        {
            if (string.IsNullOrEmpty(format) || args == null || args.Length == 0) return format;

            var builder = new StringBuilder();
            int next = 0;
            for (int i = 0; i < format.Length; i++)
            {
                var c = format[i];
                if (c == '%' && i < format.Length - 1 && (format[i + 1] == 's' || format[i + 1] == 'd'))
                {
                    if (next < args.Length)
                    {
                        builder.Append(args[next]);
                        next++;
                        i++; // step over the specifier
                    }
                    // Nothing left to fill it with: the '%' goes and the 's' or 'd' is left to be
                    // read as an ordinary character on the next pass, as it is in the game.
                    continue;
                }
                builder.Append(c);
            }
            return builder.ToString();
        }

        /// <summary>
        /// The folder <c>ContentStrings</c> looks under for one language's tables, e.g.
        /// "english.dir". A null or blank language gives the English folder, which is the one
        /// the game falls back to when the player's language has no table directory at all.
        /// </summary>
        public static string LanguageFolder(string language)
        {
            var name = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
            return name.ToLowerInvariant() + LanguageFolderSuffix;
        }

        /// <summary>
        /// The path, relative to the game's working directory, that Simitone's table is
        /// expected at for a language, e.g.
        /// "Content/UI/uitext/english.dir/_f900_simitone.cst".
        ///
        /// <para>
        /// Exists so the expectation is written down once. The build has to put the file there
        /// or a translation silently does nothing, and a test can check the path it built
        /// against this without knowing the layout by heart.
        /// </para>
        /// </summary>
        public static string TablePath(string language, string fileName = TableFileName)
        {
            var name = string.IsNullOrWhiteSpace(fileName) ? TableFileName : fileName.Trim();
            return UITextFolder + LanguageFolder(language) + "/" + name;
        }

        /// <summary>
        /// The same path as <see cref="TablePath"/>, rooted at <paramref name="basePath"/>, for
        /// callers that have to open the file rather than name it.
        /// </summary>
        public static string ResolveTablePath(string basePath, string language = DefaultLanguage)
        {
            var parts = new[] { basePath ?? string.Empty, "Content", "UI", "uitext", LanguageFolder(language), TableFileName };
            return Path.Combine(parts);
        }

        /// <summary>
        /// The table id a <c>.cst</c> file name declares, which is what
        /// <c>ContentStrings.Load</c> files it under: the text between the first and the second
        /// <c>_</c>. Null when the name has no second <c>_</c> and so cannot be loaded at all.
        ///
        /// <para>
        /// Mirrors that loader because the two have to agree: renaming the file without
        /// renaming <see cref="Table"/> would quietly point the lookup at a table that is not
        /// there, and every string would fall back to English.
        /// </para>
        /// </summary>
        public static string TableIdForFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            var secondUnderscore = fileName.IndexOf("_", 1);
            if (secondUnderscore < 1) return null;
            return fileName.Substring(1, secondUnderscore - 1);
        }

        /// <summary>
        /// Parses the body of a <c>.cst</c> file into id/value pairs, the way
        /// <c>ContentStrings.Load</c> does.
        ///
        /// <para>
        /// The game never calls this: it reads the tables through
        /// <see cref="GameFacade.Strings"/>. It exists so a test can read the shipped
        /// <c>.cst</c> and assert on it - that every line is a well formed
        /// <c>id ^text^</c> pair, that no id is repeated, that no text contains a delimiter -
        /// none of which is checkable without parsing, and all of which is a shipped-data
        /// mistake rather than a code one. <c>ContentStrings.Load</c> stays the authority on
        /// the format; this only has to agree with it.
        /// </para>
        /// </summary>
        /// <returns>The table, or an empty table for null or empty input.</returns>
        public static IReadOnlyDictionary<string, string> ParseTable(string content)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(content)) return table;

            // The loader drops whole comment lines before joining, which is what keeps a
            // section header in the file from turning into an entry of its own.
            var lines = new List<string>();
            foreach (var line in SplitLines(content))
            {
                if (line.StartsWith(CommentPrefix, StringComparison.Ordinal)) continue;
                lines.Add(line);
            }
            var joined = string.Join("\r\n", lines);

            int read = 0;
            int index = 0;
            int open;
            while ((open = joined.IndexOf(Delimiter, read, StringComparison.Ordinal)) != -1)
            {
                int close = joined.IndexOf(Delimiter, open + 1, StringComparison.Ordinal);
                if (close == -1) break;

                // Everything between the previous delimiter and this one is the id, of which
                // only the last line counts: the leading newlines and any blank lines are
                // layout, not part of the key.
                var id = joined.Substring(read, open - read).Trim();
                int lastBreak = id.LastIndexOf("\r\n", StringComparison.Ordinal);
                if (lastBreak != -1) id = id.Substring(lastBreak + 2).Trim();
                if (id.Length == 0) id = index.ToString(CultureInfo.InvariantCulture);

                table[id] = joined.Substring(open + 1, close - open - 1);
                read = close + 1;
                index++;
            }
            return table;
        }

        /// <summary>
        /// Splits on CRLF, LF and bare CR, so a file saved on either platform parses the same.
        /// </summary>
        private static IEnumerable<string> SplitLines(string content)
        {
            int start = 0;
            for (int i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c != '\r' && c != '\n') continue;
                yield return content.Substring(start, i - start);
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                start = i + 1;
            }
            if (start < content.Length) yield return content.Substring(start);
        }
    }
}
