/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// One block of the credits screen: a heading plus the lines printed under it.
    /// </summary>
    /// <remarks>
    /// Immutable by construction. <see cref="Lines"/> is a read-only view over a private copy,
    /// so a caller cannot reach in and mutate the shared <see cref="CreditsData.Sections"/>
    /// table through it.
    /// </remarks>
    public sealed class CreditSection
    {
        /// <summary>The heading this block is printed under, e.g. <c>"FreeSO"</c>.</summary>
        public string Title { get; }

        /// <summary>The body lines of the block, in print order.</summary>
        public IReadOnlyList<string> Lines { get; }

        /// <summary>
        /// Creates a section from its lines.
        /// </summary>
        /// <param name="title">Heading text. Stored verbatim; the table keeps them short.</param>
        /// <param name="lines">
        /// Body lines, in print order. A null entry is stored as an empty string rather than
        /// being dropped, so a line count always means what it looks like it means.
        /// </param>
        public CreditSection(string title, params string[] lines)
        {
            Title = title;
            Lines = Freeze(lines);
        }

        /// <summary>
        /// Creates a section from any sequence of lines (the <see cref="IEnumerable{T}"/> twin
        /// of the <c>params</c> overload, for callers that build a list first).
        /// </summary>
        /// <param name="title">Heading text. Stored verbatim.</param>
        /// <param name="lines">Body lines, in print order.</param>
        public CreditSection(string title, IEnumerable<string> lines)
        {
            Title = title;
            Lines = Freeze(lines);
        }

        private static IReadOnlyList<string> Freeze(IEnumerable<string> source)
        {
            if (source == null) return new ReadOnlyCollection<string>(new List<string>());
            var copy = source.Select(line => line ?? string.Empty).ToList();
            return new ReadOnlyCollection<string>(copy);
        }
    }

    /// <summary>What a laid-out row of the credits screen is for.</summary>
    public enum CreditLineKind
    {
        /// <summary>Vertical spacer. Carries no text.</summary>
        Blank,

        /// <summary>A section heading.</summary>
        Heading,

        /// <summary>A wrapped body line.</summary>
        Body
    }

    /// <summary>
    /// A single row of the credits scroll view: the text to draw and how to style it.
    /// </summary>
    /// <remarks>
    /// Produced by <see cref="CreditsData.BuildDisplayLines"/>, which is pure and headless, so
    /// the panel never has to decide layout - it only turns rows into labels.
    /// </remarks>
    public sealed class CreditLine
    {
        /// <summary>The text of the row. Empty for <see cref="CreditLineKind.Blank"/>.</summary>
        public string Text { get; }

        /// <summary>What the row is for.</summary>
        public CreditLineKind Kind { get; }

        /// <summary>True when this row is a section heading.</summary>
        public bool IsHeading
        {
            get { return Kind == CreditLineKind.Heading; }
        }

        /// <summary>Creates a row.</summary>
        /// <param name="text">Text to draw. Null becomes an empty string.</param>
        /// <param name="kind">What the row is for.</param>
        public CreditLine(string text, CreditLineKind kind)
        {
            Text = text ?? string.Empty;
            Kind = kind;
        }
    }

    /// <summary>
    /// The credits screen's text, as a static table of sections and plain lines.
    ///
    /// <para>
    /// The stock game ships a credits screen, but it lives in <c>Credits.iff</c> inside the
    /// player's own installation: it is an EA asset, loaded from content at runtime, and it
    /// cannot be read, copied or shipped. Everything here is therefore Simitone's own wording -
    /// short, factual lines naming who made what - rather than a reproduction of Maxis' roll.
    /// </para>
    ///
    /// <para>
    /// Every claim in the table is sourced from files already in the repository, not from
    /// memory: the Maxis/EA wording follows Simitone's own README (which requires a legitimate
    /// copy and credits Icons8 for the eyedropper icon) and FreeSO's README/LICENSE, the
    /// remesh lines come from <c>FSO.Remeshes-main/README.md</c>, and the FreeSO/Simitone
    /// author lines use the project URL the READMEs are written around
    /// (<c>github.com/riperiperi/...</c>).
    /// </para>
    ///
    /// <para>
    /// This type is deliberately inert: no IO, no content lookups, no graphics device, no
    /// statics beyond the frozen table itself, so the whole thing is exercised by the headless
    /// test suite. The panel that renders it (<c>UICreditsPanel</c>) is the only part that needs
    /// a game to run.
    /// </para>
    ///
    /// <para>
    /// <b>Why this prose is not in the string table.</b> Everything else Simitone shows goes
    /// through <see cref="SimitoneStrings"/>, and the panel's own three captions do too. This
    /// body does not, and that is a decision rather than an oversight. The lines are hard-wrapped
    /// by hand to a column width and the panel re-wraps them again, so a translation would have
    /// to be authored already wrapped to a width no other language shares; a <c>.cst</c> entry
    /// cannot express that, and pasting paragraphs into a table where every other entry is a
    /// button caption is a worse trade than leaving the prose English and translating the
    /// captions. A localised credits body is its own piece of work, with its own wrapping and its
    /// own place for the text to live.
    /// </para>
    /// </summary>
    public static class CreditsData
    {
        /// <summary>
        /// Upper bound on section count. A credits screen that grows past this is a sign the
        /// content wants splitting into a second screen, not a longer list.
        /// </summary>
        public const int MaxSections = 6;

        /// <summary>Upper bound on body lines in a single section.</summary>
        public const int MaxLinesPerSection = 16;

        /// <summary>
        /// Upper bound on the length of an authored line. Long lines are wrapped for display
        /// (see <see cref="Wrap"/>), so this is a readability bound on the source table, not a
        /// layout constraint.
        /// </summary>
        public const int MaxLineChars = 110;

        /// <summary>Upper bound on the whole table, in characters.</summary>
        public const int MaxTotalChars = 2400;

        /// <summary>
        /// Narrowest wrap width honoured by <see cref="Wrap"/>. A caller asking for less (a
        /// pathologically narrow panel) is served this instead, which is what stops the
        /// character-splitting loop from making no progress.
        /// </summary>
        public const int MinWrapChars = 16;

        /// <summary>
        /// Wrap width used when a caller has nothing better to offer. Roughly a 700px column of
        /// 12px body text.
        /// </summary>
        public const int DefaultWrapChars = 72;

        /// <summary>Blank rows inserted above every heading except the first section's.</summary>
        public const int BlankRowsBeforeHeadings = 1;

        /// <summary>
        /// The credit sections, in print order. Frozen at type-initialisation time; every
        /// accessor hands back the same instance.
        /// </summary>
        public static IReadOnlyList<CreditSection> Sections { get; } = BuildSections();

        /// <summary>Number of authored body lines across every section.</summary>
        /// <param name="sections">Sections to measure. Null is treated as empty.</param>
        public static int TotalLineCount(IEnumerable<CreditSection> sections)
        {
            if (sections == null) return 0;
            return sections.Where(s => s != null).Sum(s => s.Lines.Count);
        }

        /// <summary>
        /// Every section's heading followed by its body lines, as one flat list of strings.
        /// Headings are returned verbatim (no blank rows, no styling) - this is the "read the
        /// credits as plain text" view, used by tests and by anything that wants to log or
        /// compare the content without a screen.
        /// </summary>
        /// <param name="sections">Sections to flatten, in order. Null entries are skipped.</param>
        public static IReadOnlyList<string> Flatten(IEnumerable<CreditSection> sections)
        {
            var lines = new List<string>();
            if (sections == null) return new ReadOnlyCollection<string>(lines);
            foreach (var section in sections)
            {
                if (section == null) continue;
                lines.Add(section.Title ?? string.Empty);
                foreach (var line in section.Lines) lines.Add(line ?? string.Empty);
            }
            return new ReadOnlyCollection<string>(lines);
        }

        /// <summary>
        /// Lays sections out into the rows a scroll view draws: spacer rows, a heading, then
        /// each body line, with every line wrapped to <paramref name="maxChars"/> characters.
        /// </summary>
        /// <remarks>
        /// A null section or line is skipped rather than thrown on: a malformed table should
        /// show a shorter credits screen, not take the game down with it. The first section is
        /// not preceded by spacers, so the scroll view starts flush with its first heading.
        /// </remarks>
        /// <param name="sections">Sections to lay out, in order. Null or empty gives an empty result.</param>
        /// <param name="maxChars">
        /// Wrap width in characters. Clamped up to <see cref="MinWrapChars"/>; pass
        /// <see cref="DefaultWrapChars"/> when nothing better is known.
        /// </param>
        public static IReadOnlyList<CreditLine> BuildDisplayLines(IEnumerable<CreditSection> sections, int maxChars)
        {
            var rows = new List<CreditLine>();
            if (sections == null) return new ReadOnlyCollection<CreditLine>(rows);

            bool first = true;
            foreach (var section in sections)
            {
                if (section == null) continue;
                if (!first)
                {
                    for (int i = 0; i < BlankRowsBeforeHeadings; i++)
                        rows.Add(new CreditLine(string.Empty, CreditLineKind.Blank));
                }
                first = false;

                foreach (var heading in Wrap(section.Title, maxChars))
                    rows.Add(new CreditLine(heading, CreditLineKind.Heading));

                foreach (var line in section.Lines)
                {
                    if (line == null) continue;
                    foreach (var wrapped in Wrap(line, maxChars))
                        rows.Add(new CreditLine(wrapped, CreditLineKind.Body));
                }
            }
            return new ReadOnlyCollection<CreditLine>(rows);
        }

        /// <summary>
        /// Wraps one line of text to <paramref name="maxChars"/> characters, breaking on spaces
        /// and mid-word only for a word that is wider than a whole row (a long URL, say).
        /// </summary>
        /// <remarks>
        /// A word is never split across two rows to use up the last few characters of the
        /// previous one: if the word does not fit, the row is closed and the word starts the
        /// next. A null or empty input yields a single empty row rather than no rows, so callers
        /// can treat "one row per input line" as an invariant. Embedded newlines are honoured;
        /// blank lines between them are dropped, because vertical rhythm is the panel's job.
        /// No character is ever dropped, reordered or truncated: concatenating the result
        /// reproduces the input's non-whitespace characters in order.
        /// </remarks>
        /// <param name="text">The line to wrap. Null is treated as empty.</param>
        /// <param name="maxChars">
        /// Wrap width in characters, clamped up to <see cref="MinWrapChars"/>. Any value at or
        /// below zero therefore means "use the narrowest supported width" rather than looping.
        /// </param>
        public static IReadOnlyList<string> Wrap(string text, int maxChars)
        {
            var width = Math.Max(MinWrapChars, maxChars);
            var result = new List<string>();
            if (string.IsNullOrEmpty(text))
            {
                result.Add(string.Empty);
                return new ReadOnlyCollection<string>(result);
            }

            var paragraphs = text.Split('\n');
            for (int i = 0; i < paragraphs.Length; i++)
            {
                var paragraph = paragraphs[i].TrimEnd('\r');
                // A blank paragraph inside multi-line text is dropped: the panel's own spacer
                // rows handle vertical rhythm, and keeping them would double-space the list.
                if (paragraph.Length == 0 && paragraphs.Length > 1) continue;
                WrapParagraph(paragraph, width, result);
            }

            // A line that is nothing but newlines produces no rows above; give it one so the
            // "one row per authored line" invariant still holds.
            if (result.Count == 0) result.Add(string.Empty);
            return new ReadOnlyCollection<string>(result);
        }

        private static void WrapParagraph(string paragraph, int width, List<string> result)
        {
            if (paragraph.Length == 0)
            {
                result.Add(string.Empty);
                return;
            }

            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                // Runs of spaces collapse rather than producing empty rows.
                if (word.Length == 0) continue;

                if (line.Length > 0)
                {
                    // A word goes on the current row only if the whole word fits. Squeezing the
                    // first few characters of it in and continuing on the next row reads as a
                    // typo, and there is always a better place for it.
                    if (line.Length + 1 + word.Length <= width)
                    {
                        line.Append(' ').Append(word);
                        continue;
                    }
                    result.Add(line.ToString());
                    line.Clear();
                }

                var remaining = word;
                while (remaining.Length > width)
                {
                    // Only reachable for a word wider than a whole row - a URL, typically. There
                    // is no space to break on, so it has to be cut.
                    result.Add(remaining.Substring(0, width));
                    remaining = remaining.Substring(width);
                }
                line.Append(remaining);
            }
            result.Add(line.ToString());
        }

        private static IReadOnlyList<CreditSection> BuildSections()
        {
            var sections = new List<CreditSection>
            {
                new CreditSection("The Sims",
                    "The Sims and The Sims: Complete Collection are published by Electronic Arts.",
                    "The Sims (c) Maxis / Electronic Arts. All rights reserved.",
                    "The Sims is a trademark of Electronic Arts Inc.",
                    "Simitone is an unofficial fan re-implementation. It is not a Maxis or EA product,",
                    "and it is not endorsed, sponsored or supported by either company.",
                    "It requires a legitimate copy of The Sims: Complete Collection, and it ships no",
                    "Maxis or EA artwork, audio or video - every asset is read from your own",
                    "installation while the game runs."),

                new CreditSection("FreeSO",
                    "FreeSO - https://freeso.org",
                    "A full reimplementation of The Sims Online using MonoGame, and the engine",
                    "Simitone is built on: the content system, the HIT VM, the SimAntics VM, the lot",
                    "renderer and the UI framework all come from it.",
                    "The Sims Online (c) Maxis / Electronic Arts.",
                    "riperiperi - FreeSO lead developer, and the author of Simitone.",
                    "The FreeSO team and its many contributors:",
                    "https://github.com/riperiperi/FreeSO/graphs/contributors",
                    "Volcanic, the object and script editor bundled with Simitone, is FreeSO's.",
                    "FreeSO is licensed under the Mozilla Public License 2.0.",
                    "Forums, wiki, Discord and translations: https://forum.freeso.org"),

                new CreditSection("3D Meshes",
                    "Simitone builds a mesh for every object from the sprite's depth buffer.",
                    "Hand-authored remeshes replace those, and are published by many people on the",
                    "FreeSO remeshing forum: https://forum.freeso.org/forums/3d-remeshing.40/",
                    "Each remesher is credited in the pack's own metadata, not in this list."),

                new CreditSection("Simitone",
                    "Simitone - an alternative C# Windows frontend for The Sims 1, based on FreeSO.",
                    "riperiperi - Simitone's author and maintainer.",
                    "The Simitone contributors:",
                    "https://github.com/riperiperi/Simitone/graphs/contributors",
                    "Eyedropper tool icon: \"eyedropper\" by Icons8 -",
                    "https://icons8.com/icon/78728/color-dropper")
            };

            return new ReadOnlyCollection<CreditSection>(sections);
        }
    }
}
