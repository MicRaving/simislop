/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Content;
using FSO.Content.TS1;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Simitone.Client.UI.Services
{
    /// <summary>
    /// Tells the player which custom content files the engine could not load.
    ///
    /// <para>
    /// The engine's content pass reads every <c>.bcf</c>/<c>.cmx</c> in the game folder to find
    /// the animations, appearances and skeletons in it, and a file that is truncated, corrupt or
    /// in a format it does not recognise is recorded in
    /// <see cref="FSO.Content.Content.FailedContentFiles"/> and skipped. That is the right
    /// behaviour - the game keeps running - but it is silent, so a player whose custom sims came
    /// out invisible, bald or unanimated has no way to tell that a file of theirs was the reason.
    /// This service is the one place that turns that list into something a player can read.
    /// </para>
    ///
    /// <para>
    /// <b>Where it fires.</b> Once per session, on the first lot load. The failures are
    /// discovered during content init, which finishes before any lot exists, so the list is
    /// already final by then; and a lot load is the first moment there is a game in front of the
    /// player to interrupt. The latch is static because the list is process-wide: showing the
    /// same warning again on every lot switch would be noise, and re-entering live mode through
    /// the family editor builds a new screen (and so a new instance of this service) for the same
    /// already-reported list.
    /// </para>
    ///
    /// <para>
    /// The wording is Simitone's own and is deliberately <em>not</em> run through
    /// <c>GameFacade.Strings</c>: it names file names and error kinds, and no shipped TS1 string
    /// table has an entry for it. It is read out of Simitone's own table instead (ids 470 to 479
    /// of <c>_f900_simitone.cst</c>), with the English kept as the second argument of every
    /// lookup, so a build with no table at all still says exactly what it always said. The
    /// message is split the same way <see cref="DialogService.ShowAlert(string, string, string)"/>
    /// expects - the sentence a player reads, and the technical detail underneath it.
    /// </para>
    /// </summary>
    public sealed class FailedContentService
    {
        /// <summary>
        /// Set once the warning has been shown. Static because the list of failures is, too - see
        /// the class remarks.
        /// </summary>
        private static bool s_reported;

        /// <summary>How many files are named before the list is cut off.</summary>
        public const int MaxListedFiles = 3;

        /// <summary>
        /// A failure the engine logged purely to explain itself, rather than a file it could not
        /// read. Not something the player can act on, so it is not reported.
        /// </summary>
        public const string InformationalErrorType = "DebugInfo";

        private readonly DialogService _dialogs;

        /// <summary>
        /// Creates the service over the screen's dialogs.
        /// </summary>
        /// <param name="dialogs">Where the warning is shown.</param>
        public FailedContentService(DialogService dialogs)
        {
            _dialogs = dialogs;
        }

        /// <summary>
        /// Shows the warning, once per session, if there is anything in it worth showing.
        /// </summary>
        /// <remarks>
        /// Never throws. A report about a broken game install must not be the thing that stops
        /// the lot loading, so every failure here - including a dialog that will not open - is
        /// logged and swallowed.
        /// </remarks>
        public void ReportFailedContentOnce()
        {
            try
            {
                if (s_reported) return;
                var detail = BuildDetail(FSO.Content.Content.FailedContentFiles);
                if (detail == null) return;

                s_reported = true;
                // ids 470 and 471 of Simitone's own table. The English is the second argument of
                // each lookup, so a build with no table says what it always said.
                _dialogs.ShowAlert(
                    SimitoneStrings.Get("470", "Custom Content Warning"),
                    SimitoneStrings.Get("471",
                        "Some custom content files could not be loaded. The game will continue without them."),
                    detail);
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Could not show the failed content warning.", ex);
            }
        }

        /// <summary>
        /// The player-facing detail block: the first few files and what went wrong with each, then
        /// a count of the rest and the usual causes.
        /// </summary>
        /// <param name="failures">The engine's failure list, which may be null or empty.</param>
        /// <returns>
        /// The detail block, or null when there is nothing worth showing the player (nothing
        /// failed, or everything that failed was an informational entry).
        /// </returns>
        public static string BuildDetail(IEnumerable<TS1BCFProvider.FailedFileInfo> failures)
        {
            var files = DistinctFailures(failures);
            if (files.Count == 0) return null;

            var message = new StringBuilder();
            var listed = Math.Min(files.Count, MaxListedFiles);
            for (int i = 0; i < listed; i++)
            {
                var file = files[i];
                message.Append("* ").Append(file.Filename).Append(" (").Append(ShortReason(file.ErrorType)).AppendLine(")");
            }

            if (files.Count > MaxListedFiles)
            {
                var remaining = (files.Count - MaxListedFiles).ToString();
                // id 478: the count is an argument, not part of the words, so a language can put
                // the number where it belongs. The English at the call site is already composed,
                // so with no table this is the line it has always been.
                message.Append(SimitoneStrings.Format("478", "... and " + remaining + " more", remaining))
                    .AppendLine();
            }

            message.AppendLine();
            // id 479. The file names and the .cfp extension in it are the things to go and look
            // at, so they stay as they are; the sentence around them is Simitone's.
            message.AppendLine(SimitoneStrings.Get("479",
                "The usual causes are a truncated or corrupt file, a missing matching .cfp file, or a format this game does not read."));
            return message.ToString();
        }

        /// <summary>
        /// The failures worth reporting, one entry per file.
        /// </summary>
        /// <remarks>
        /// Pure. Two things are dropped here: informational entries, and repeats of a file the
        /// engine already logged once - the same file can fail its animation, appearance and
        /// skeleton passes in turn, and a player does not need to be told the same three times.
        /// The first entry for a file wins, which keeps the reason the engine hit first, and two
        /// failures that carry no file name at all count as one: they cannot be told apart, and
        /// a line with nothing in it is worth showing once.
        /// </remarks>
        /// <param name="failures">The engine's failure list, which may be null.</param>
        public static List<TS1BCFProvider.FailedFileInfo> DistinctFailures(IEnumerable<TS1BCFProvider.FailedFileInfo> failures)
        {
            if (failures == null) return new List<TS1BCFProvider.FailedFileInfo>();

            return failures
                .Where(f => f != null)
                .Where(f => !string.Equals(f.ErrorType, InformationalErrorType, StringComparison.Ordinal))
                .GroupBy(f => f.Filename ?? string.Empty)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// A short, player-readable name for an error kind.
        /// </summary>
        /// <remarks>
        /// Pure. The engine's <c>ErrorType</c> is an exception type name for anything it did not
        /// classify, and a raw type name is not something to put in front of a player - but it is
        /// also the only clue they have, so an unrecognised kind is passed through rather than
        /// hidden.
        ///
        /// <para>
        /// The six kinds the engine does classify are ids 472 to 477 of Simitone's own table, with
        /// the English as the second argument - so a test, and a build with no table, still get
        /// the same words. An unrecognised kind is a type name, not a sentence, and is never
        /// translated.
        /// </para>
        /// </remarks>
        /// <param name="errorType">The engine's error kind.</param>
        public static string ShortReason(string errorType)
        {
            switch (errorType)
            {
                case "EndOfStream": return SimitoneStrings.Get("472", "truncated or corrupt");
                case "InvalidData": return SimitoneStrings.Get("473", "invalid format");
                case "IOException": return SimitoneStrings.Get("474", "could not be read");
                case "CatalogError": return SimitoneStrings.Get("475", "catalog error");
                case "DecodeError": return SimitoneStrings.Get("476", "could not be decoded");
                case null: return SimitoneStrings.Get("477", "unknown error");
                default: return errorType;
            }
        }
    }
}
