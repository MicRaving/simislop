/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Content;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Makin' Magic's own text, read out of the expansion's loose <c>GameData/magic.iff</c>.
    ///
    /// <para>
    /// Nothing in the content pipeline opens that file. <c>ContentStrings</c> reads
    /// <c>GameData/UIText.iff</c> and the world providers read
    /// <c>GameData/Global/Global.far</c>, so the spell and recipe tables - the only place The
    /// Sims 1 ever wrote down what a spell is called and what it does - are unreachable from
    /// the game's own string lookup. This is the reader that gets at them, and it is the reason
    /// the book panels can say more than "spellbook".
    /// </para>
    ///
    /// <para>
    /// <b>Shape of the file.</b> Six <c>STR#</c> chunks, each identified only by its 64-byte
    /// label ("Spell Titles", "Magic Book Dialog Strings", ...), each holding twenty language
    /// blocks back to back with English (US) first. <c>IffFile</c> hands back one
    /// <see cref="STR"/> per chunk and <see cref="STR.GetString(int)"/> resolves the player's
    /// language through <c>STR.DefaultLangCode</c>, which the game already sets from
    /// <c>GlobalSettings.LanguageCode</c> at startup - so a localized Simitone shows a
    /// localized book without any of that logic being repeated here.
    /// </para>
    ///
    /// <para>
    /// Two chunks share the label "Spell Description": an empty one and the real 520-string
    /// one. Tables are therefore indexed by label and an empty chunk never displaces a populated
    /// one, which is why the reader has to look at the contents rather than just recording the
    /// last chunk it saw with each name.
    /// </para>
    ///
    /// <para>
    /// Every failure path is a fallback to English rather than an exception: a build with no
    /// Makin' Magic installed has no <c>magic.iff</c> at all, and a book panel that threw in
    /// that case would take the dialog down with it.
    /// </para>
    /// </remarks>
    public sealed class MakingMagicTables
    {
        private static readonly string[] NoStrings = new string[0];

        /// <summary>The tables, keyed by chunk label. Never null.</summary>
        private readonly Dictionary<string, string[]> tables;

        private MakingMagicTables(Dictionary<string, string[]> tables)
        {
            this.tables = tables ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        }

        /// <summary>True when at least one of the six tables was read.</summary>
        public bool Loaded
        {
            get { return tables.Count > 0; }
        }

        /// <summary>The labels that were actually read, for the log line.</summary>
        public IEnumerable<string> Labels
        {
            get { return tables.Keys; }
        }

        #region the shared instance

        private static readonly object LoadLock = new object();
        private static MakingMagicTables shared;

        /// <summary>
        /// The tables for the running game, opened once on first use from the configured The
        /// Sims install. Never null: a build with no expansion installed gets an empty set, and
        /// every lookup falls back to English.
        /// </summary>
        public static MakingMagicTables Shared
        {
            get
            {
                if (shared != null) return shared;
                lock (LoadLock)
                {
                    if (shared != null) return shared;

                    var install = TryGetInstallPath();
                    var path = MakingMagic.ResourcePath(install);
                    shared = TryLoad(path);
                    if (shared.Loaded)
                    {
                        SimitoneLog.Info($"magic tables: read {shared.tables.Count} string table(s) from {path}");
                    }
                    else
                    {
                        SimitoneLog.Info($"magic tables: none available (looked in '{path ?? "<no install path>"}')"
                            + " - the Makin' Magic books will show English fallback text");
                    }
                    return shared;
                }
            }
        }

        /// <summary>
        /// The Sims install the content manager is pointed at, or null. Read defensively: this
        /// is a mutable static that startup fills in, and a book dialog raised in the middle of
        /// a lot change must not be the thing that notices.
        /// </summary>
        private static string TryGetInstallPath()
        {
            try
            {
                return Content.TS1HybridBasePath;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion

        #region reading

        /// <summary>
        /// Opens a <c>magic.iff</c> and indexes its string tables by chunk label. Returns an
        /// empty instance - never null, never throwing - for a missing, unreadable or
        /// unrecognised file.
        /// </summary>
        /// <param name="path">Path of the resource, or null/empty.</param>
        public static MakingMagicTables TryLoad(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new MakingMagicTables(null);

            IffFile iff = null;
            try
            {
                iff = new IffFile(path);

                // chunks are parsed lazily, so this is where the file is actually read
                var chunks = iff.List<STR>();
                var tables = new Dictionary<string, string[]>(StringComparer.Ordinal);
                if (chunks != null)
                {
                    foreach (var chunk in chunks)
                    {
                        Index(tables, chunk);
                    }
                }
                return new MakingMagicTables(tables);
            }
            catch (Exception e)
            {
                SimitoneLog.Warn($"magic tables: could not read '{path}', falling back to English", e);
                return new MakingMagicTables(null);
            }
            finally
            {
                // the strings have been copied out; there is no reason to hold the whole
                // resource alive in the timed-reference cache for the rest of the session
                try { iff?.MarkThrowaway(); } catch (Exception) { }
            }
        }

        /// <summary>
        /// Files one chunk under its label, in the player's language.
        /// </summary>
        /// <remarks>
        /// An empty chunk is skipped rather than stored, because <c>magic.iff</c> ships two
        /// chunks called "Spell Description" and only one of them has anything in it. Storing
        /// the empty one would make the real table depend on chunk order.
        /// <para>
        /// The array is sized from the English block (<c>STR.Length</c> counts language 0's
        /// strings) and missing entries are blanked rather than left null, because a language
        /// block is allowed to be shorter than the English one - a translation that has not
        /// finished the last few spells would otherwise hand the panel null captions.
        /// </para>
        /// </remarks>
        private static void Index(Dictionary<string, string[]> tables, STR chunk)
        {
            if (chunk == null) return;
            var label = (chunk.ChunkLabel ?? "").Trim();
            if (label.Length == 0) return;

            var count = chunk.Length;
            if (count <= 0) return;

            var strings = new string[count];
            for (int i = 0; i < count; i++)
            {
                strings[i] = chunk.GetString(i) ?? "";
            }

            // a populated chunk replaces an empty one under the same name; two populated chunks
            // with one name would be a file we do not understand, and the first one wins
            string[] existing;
            if (tables.TryGetValue(label, out existing) && existing.Length > 0) return;
            tables[label] = strings;
        }

        #endregion

        #region lookups

        /// <summary>
        /// The raw strings of a named table, in the player's language. Empty when the table is
        /// not there; never null.
        /// </summary>
        public IReadOnlyList<string> Table(string label)
        {
            string[] found;
            if (!string.IsNullOrEmpty(label) && tables.TryGetValue(label ?? "", out found)) return found;
            return NoStrings;
        }

        /// <summary>The entry titles for a book, in the game's own order.</summary>
        public IReadOnlyList<string> Titles(MakingMagicBook book)
        {
            return Table(MakingMagic.TitlesLabel(book));
        }

        /// <summary>
        /// The description of one entry, or an empty string when there is none.
        /// </summary>
        /// <remarks>
        /// Out of range rather than null, because the description table is one entry longer
        /// than the title table for the spells and exactly the same length for the recipes -
        /// this asks for the index the caller already has rather than trying to reconcile the
        /// two tables.
        /// </remarks>
        public string Description(MakingMagicBook book, int entryIndex)
        {
            if (entryIndex < 0) return "";
            var descriptions = Table(MakingMagic.DescriptionsLabel(book));
            return entryIndex < descriptions.Count ? (descriptions[entryIndex] ?? "") : "";
        }

        /// <summary>
        /// One of the dialog's own five captions, with the English from
        /// <see cref="MakingMagic.DialogStringFallback"/> whenever the file is not there or has
        /// no usable entry.
        /// </summary>
        public string Dialog(MakingMagicCaption caption)
        {
            var index = MakingMagic.DialogStringIndex(caption);
            var fallback = MakingMagic.DialogStringFallback(caption);
            if (index < 0) return fallback;

            var table = Table(MakingMagic.DialogStringsLabel);
            if (index >= table.Count) return fallback;

            return SimitoneStrings.Resolve(table[index], fallback);
        }

        #endregion
    }
}
