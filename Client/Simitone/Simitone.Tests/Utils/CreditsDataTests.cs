using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Pins the shape of the credits table and the behaviour of the pure layout helpers the
    /// credits panel builds its rows from.
    ///
    /// <para>
    /// The content itself is a legal/attribution obligation rather than logic, so what is worth
    /// testing is that it stays <i>well formed</i>: every section has a heading and at least one
    /// line, no heading is repeated, no line runs away with the layout, and the whole thing stays
    /// inside a size a scrollable panel can actually show. A duplicate heading or an empty
    /// section is the kind of thing that survives review by accident and then ships.
    /// </para>
    ///
    /// <para>
    /// Nothing here touches a graphics device, content or the filesystem: <see cref="CreditsData"/>
    /// is a frozen string table plus string math, which is what makes the credits screen testable
    /// at all - the panel around it needs a live game.
    /// </para>
    /// </summary>
    public sealed class CreditsDataTests
    {
        [Fact]
        public void CreditsScreenHasSections()
        {
            Assert.NotEmpty(CreditsData.Sections);
        }

        [Fact]
        public void EverySectionHasATitleAndAtLeastOneLine()
        {
            foreach (var section in CreditsData.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title));
                Assert.NotEmpty(section.Lines);
            }
        }

        [Fact]
        public void NoLineIsNullOrBlank()
        {
            foreach (var section in CreditsData.Sections)
            {
                foreach (var line in section.Lines)
                {
                    Assert.False(line == null);
                    Assert.False(string.IsNullOrWhiteSpace(line));
                }
            }
        }

        [Fact]
        public void SectionTitlesAreUnique()
        {
            var titles = CreditsData.Sections.Select(x => x.Title).ToList();
            Assert.Equal(titles.Count, titles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void LineTextIsUniqueWithinTheWholeTable()
        {
            var lines = CreditsData.Flatten(CreditsData.Sections);
            Assert.Equal(lines.Count, lines.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void TheTableCoversTheSimsFreeSoAndSimitone()
        {
            // The three attributions the credits screen exists for. Losing any one of them is a
            // regression a test should shout about, whatever the sections end up being called.
            var flat = string.Join("\n", CreditsData.Flatten(CreditsData.Sections));
            Assert.Contains("The Sims", flat, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("FreeSO", flat, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Simitone", flat, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSimsSectionCreditsMaxisAndElectronicArts()
        {
            var sims = Section("The Sims");
            Assert.Contains("Maxis", string.Join("\n", sims.Lines), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Electronic Arts", string.Join("\n", sims.Lines), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSimsSectionStatesTheUnofficialFanPosition()
        {
            // Simitone requires a legitimate copy and ships no EA assets, so the credits have to
            // say both of those things rather than leave the impression it is an EA product.
            var sims = string.Join("\n", Section("The Sims").Lines).ToLowerInvariant();
            Assert.Contains("unofficial", sims);
            Assert.Contains("legitimate copy", sims);
        }

        [Fact]
        public void TheFreeSoSectionNamesTheProjectAndItsLicence()
        {
            var freeso = string.Join("\n", Section("FreeSO").Lines);
            Assert.Contains("freeso.org", freeso, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Mozilla Public License", freeso, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheSimitoneSectionNamesTheAuthor()
        {
            Assert.Contains("riperiperi", string.Join("\n", Section("Simitone").Lines), StringComparison.Ordinal);
        }

        [Fact]
        public void EverySectionIsReadableOnOneLine()
        {
            // Authored lines are hard-wrapped by hand at roughly this width, which is what keeps
            // the panel's character-budget wrap from having to do anything clever. Anything longer
            // and a row can run past the right edge of the text column on a narrow screen.
            foreach (var section in CreditsData.Sections)
            {
                foreach (var line in section.Lines)
                {
                    Assert.InRange(line.Length, 1, 84);
                }
            }
        }

        [Fact]
        public void SectionTitlesAreShortEnoughToHeadALine()
        {
            foreach (var section in CreditsData.Sections)
            {
                Assert.InRange(section.Title.Length, 1, 24);
            }
        }

        [Fact]
        public void TheTableStaysInsideItsSizeBounds()
        {
            Assert.InRange(CreditsData.Sections.Count, 1, CreditsData.MaxSections);

            foreach (var section in CreditsData.Sections)
            {
                Assert.InRange(section.Lines.Count, 1, CreditsData.MaxLinesPerSection);
                foreach (var line in section.Lines)
                {
                    Assert.InRange(line.Length, 1, CreditsData.MaxLineChars);
                }
            }

            int totalChars = CreditsData.Sections.Sum(x => x.Title.Length + x.Lines.Sum(l => l.Length));
            Assert.InRange(totalChars, 1, CreditsData.MaxTotalChars);
        }

        [Fact]
        public void SectionOrderIsPreserved()
        {
            // The Sims has to come first (it is the game being credited) and Simitone last (it is
            // the thing doing the crediting).
            var titles = CreditsData.Sections.Select(x => x.Title).ToList();
            Assert.Equal("The Sims", titles[0]);
            Assert.Equal("Simitone", titles[titles.Count - 1]);
        }

        [Fact]
        public void SectionsAreTheSameFrozenInstanceEveryTime()
        {
            Assert.Same(CreditsData.Sections, CreditsData.Sections);
        }

        [Fact]
        public void FlattenPutsEveryHeadingAndLineInOrder()
        {
            var flat = CreditsData.Flatten(CreditsData.Sections);
            int expected = CreditsData.Sections.Count + CreditsData.TotalLineCount(CreditsData.Sections);
            Assert.Equal(expected, flat.Count);

            int index = 0;
            foreach (var section in CreditsData.Sections)
            {
                Assert.Equal(section.Title, flat[index++]);
                foreach (var line in section.Lines) Assert.Equal(line, flat[index++]);
            }
            Assert.Equal(flat.Count, index);
        }

        [Fact]
        public void FlattenOfNothingIsEmpty()
        {
            Assert.Empty(CreditsData.Flatten(null));
            Assert.Empty(CreditsData.Flatten(new List<CreditSection>()));
        }

        [Fact]
        public void FlattenSkipsNullSectionsRatherThanThrowing()
        {
            var sections = new List<CreditSection> { null, new CreditSection("Only", "line") };
            Assert.Equal(new[] { "Only", "line" }, CreditsData.Flatten(sections).ToArray());
        }

        [Fact]
        public void TotalLineCountIgnoresNullSections()
        {
            var sections = new List<CreditSection>
            {
                new CreditSection("One", "a", "b"),
                null,
                new CreditSection("Two", "c")
            };
            Assert.Equal(3, CreditsData.TotalLineCount(sections));
            Assert.Equal(0, CreditsData.TotalLineCount(null));
        }

        [Fact]
        public void ANullLineListBecomesAnEmptySection()
        {
            var section = new CreditSection("Empty", (IEnumerable<string>)null);
            Assert.Empty(section.Lines);
        }

        [Fact]
        public void ANullLineBecomesAnEmptyString()
        {
            // Stored, not dropped: a line count that changes when someone nulls a line would be a
            // confusing thing to debug.
            var section = new CreditSection("Section", new string[] { "kept", null });
            Assert.Equal(2, section.Lines.Count);
            Assert.Equal(string.Empty, section.Lines[1]);
        }

        [Fact]
        public void TheSectionsCannotBeMutatedThroughTheReturnedList()
        {
            // The panel holds on to CreditsData.Sections for the life of the screen, so nothing
            // a caller can reach may be writable.
            var lines = CreditsData.Sections[0].Lines as IList<string>;
            Assert.NotNull(lines);
            Assert.True(lines.IsReadOnly);
            var sections = CreditsData.Sections as IList<CreditSection>;
            Assert.NotNull(sections);
            Assert.True(sections.IsReadOnly);
        }

        #region Display line layout

        [Fact]
        public void DisplayLinesCarryEveryRowInOrder()
        {
            var rows = CreditsData.BuildDisplayLines(CreditsData.Sections, CreditsData.DefaultWrapChars);
            var expected = CreditsData.BuildDisplayLines(CreditsData.Sections, CreditsData.DefaultWrapChars);
            Assert.Equal(expected.Count, rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                Assert.Equal(expected[i].Text, rows[i].Text);
                Assert.Equal(expected[i].Kind, rows[i].Kind);
            }
        }

        [Fact]
        public void TheFirstHeadingIsFlushAndLaterOnesAreSpaced()
        {
            var rows = CreditsData.BuildDisplayLines(CreditsData.Sections, CreditsData.DefaultWrapChars).ToList();

            int headingCount = rows.Count(x => x.IsHeading);
            Assert.Equal(CreditsData.Sections.Count, headingCount);
            Assert.Equal(CreditLineKind.Heading, rows[0].Kind);
            Assert.Equal(CreditsData.Sections[0].Title, rows[0].Text);

            // Every heading after the first is preceded by exactly one blank row.
            for (int i = 1; i < rows.Count; i++)
            {
                if (rows[i].Kind != CreditLineKind.Heading) continue;
                Assert.Equal(CreditLineKind.Blank, rows[i - 1].Kind);
            }
        }

        [Fact]
        public void EverySectionContributesAHeadingAndItsLines()
        {
            // Wrapped wide enough that no authored line has to break, so each one shows up as a
            // row exactly as written. The wrapping itself is covered further down.
            var rows = CreditsData.BuildDisplayLines(CreditsData.Sections, 200).ToList();
            foreach (var section in CreditsData.Sections)
            {
                Assert.Contains(rows, x => x.Kind == CreditLineKind.Heading && x.Text == section.Title);
                foreach (var line in section.Lines)
                {
                    Assert.Contains(rows, x => x.Kind == CreditLineKind.Body && x.Text == line);
                }
            }
        }

        [Fact]
        public void ANarrowerWrapProducesMoreRows()
        {
            var wide = CreditsData.BuildDisplayLines(CreditsData.Sections, CreditsData.DefaultWrapChars);
            var narrow = CreditsData.BuildDisplayLines(CreditsData.Sections, CreditsData.MinWrapChars);
            Assert.True(narrow.Count > wide.Count);
            Assert.True(narrow.Count(x => x.IsHeading) == wide.Count(x => x.IsHeading));
        }

        [Fact]
        public void DisplayLinesOfNothingAreEmpty()
        {
            Assert.Empty(CreditsData.BuildDisplayLines(null, 40));
            Assert.Empty(CreditsData.BuildDisplayLines(new List<CreditSection>(), 40));
        }

        [Fact]
        public void DisplayLinesSkipNullSectionsAndKeepAnEmptyLineAsABlankRow()
        {
            // CreditSection normalises a null line to an empty string, so a skipped line still
            // costs a row rather than silently closing up the gap the layout measured.
            var sections = new List<CreditSection>
            {
                null,
                new CreditSection("Real", new[] { "one", null, "two" })
            };
            var rows = CreditsData.BuildDisplayLines(sections, 40).ToList();
            Assert.Equal(4, rows.Count);
            Assert.Equal("Real", rows[0].Text);
            Assert.Equal("one", rows[1].Text);
            Assert.Equal(CreditLineKind.Body, rows[2].Kind);
            Assert.Equal(string.Empty, rows[2].Text);
            Assert.Equal("two", rows[3].Text);
        }

        [Fact]
        public void ABlankRowCarriesNoText()
        {
            var rows = CreditsData.BuildDisplayLines(CreditsData.Sections, 40)
                .Where(x => x.Kind == CreditLineKind.Blank)
                .ToList();
            Assert.NotEmpty(rows);
            Assert.All(rows, x => Assert.Equal(string.Empty, x.Text));
        }

        [Fact]
        public void CreditLineKindReportsHeadings()
        {
            Assert.True(new CreditLine("x", CreditLineKind.Heading).IsHeading);
            Assert.False(new CreditLine("x", CreditLineKind.Body).IsHeading);
            Assert.False(new CreditLine("x", CreditLineKind.Blank).IsHeading);
            Assert.Equal(string.Empty, new CreditLine(null, CreditLineKind.Body).Text);
        }

        #endregion

        #region Wrapping

        [Fact]
        public void ShortTextIsNotWrapped()
        {
            Assert.Equal(new[] { "short" }, CreditsData.Wrap("short", 40).ToArray());
        }

        [Fact]
        public void WrappingBreaksOnSpaces()
        {
            // A word that does not fit starts the next row rather than being split to fill this
            // one - "fox" is not chopped to "fo" / "x".
            var wrapped = CreditsData.Wrap("the quick brown fox jumps", 16).ToList();
            Assert.Equal(new[] { "the quick brown", "fox jumps" }, wrapped.ToArray());
        }

        [Fact]
        public void AnExactlyFittingLineIsNotBroken()
        {
            Assert.Equal(new[] { "one two three" }, CreditsData.Wrap("one two three", 13).ToList());
        }

        [Fact]
        public void AWordLongerThanTheWidthIsSplit()
        {
            // A URL is the real case: there is no space to break on, so it has to be cut.
            var wrapped = CreditsData.Wrap("abcdefghijklmnopqrst", 16).ToList();
            Assert.Equal(new[] { "abcdefghijklmnop", "qrst" }, wrapped.ToArray());
        }

        [Fact]
        public void ALongWordAfterAWordIsSplitNotDropped()
        {
            var wrapped = CreditsData.Wrap("go abcdefghijklmnopqrst", 16).ToList();
            Assert.Equal(new[] { "go", "abcdefghijklmnop", "qrst" }, wrapped.ToArray());
        }

        [Fact]
        public void AWidthBelowTheMinimumIsWidenedToIt()
        {
            // Clamping up is what stops the character-splitting loop from making no progress;
            // the caller gets a very short row rather than a hang.
            Assert.Equal(new[] { "one two" }, CreditsData.Wrap("one two", 1).ToArray());
        }

        [Fact]
        public void EmptyAndNullTextProduceOneEmptyRow()
        {
            // One row per authored line, always - callers rely on this to keep row indices and
            // labels lined up.
            Assert.Equal(new[] { "" }, CreditsData.Wrap(null, 40).ToArray());
            Assert.Equal(new[] { "" }, CreditsData.Wrap("", 40).ToArray());
            Assert.Equal(new[] { "" }, CreditsData.Wrap("\n", 40).ToArray());
        }

        [Fact]
        public void EmbeddedNewlinesEachGetARow()
        {
            Assert.Equal(new[] { "one", "two" }, CreditsData.Wrap("one\ntwo", 40).ToArray());
        }

        [Fact]
        public void RunsOfSpacesDoNotProduceEmptyRows()
        {
            Assert.Equal(new[] { "a b" }, CreditsData.Wrap("a    b", 40).ToArray());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void AnImpossibleWidthFallsBackToTheNarrowestSupported(int width)
        {
            // A zero or negative width must not hang the character-splitting loop, and must not
            // return nothing: the panel would lose the row.
            var wrapped = CreditsData.Wrap("hello there", width).ToList();
            Assert.NotEmpty(wrapped);
            Assert.All(wrapped, x => Assert.True(x.Length <= CreditsData.MinWrapChars));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void EveryAuthoredLineSurvivesWrappingIntact(int width)
        {
            // No character may be dropped, reordered or duplicated at any width the panel could
            // ask for - that is the invariant the whole layout rests on.
            foreach (var section in CreditsData.Sections)
            {
                foreach (var text in new[] { section.Title }.Concat(section.Lines))
                {
                    var wrapped = CreditsData.Wrap(text, width);
                    string input = StripWhitespace(text);
                    string output = StripWhitespace(string.Concat(wrapped));
                    Assert.Equal(input, output);
                }
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        public void NoWrappedRowIsLongerThanTheWidth(int width)
        {
            var usable = Math.Max(CreditsData.MinWrapChars, width);
            foreach (var section in CreditsData.Sections)
            {
                foreach (var text in new[] { section.Title }.Concat(section.Lines))
                {
                    Assert.All(CreditsData.Wrap(text, width), x => Assert.True(x.Length <= usable));
                }
            }
        }

        #endregion

        private static CreditSection Section(string title)
        {
            var section = CreditsData.Sections.FirstOrDefault(x => x.Title == title);
            Assert.True(section != null, "no credits section titled " + title);
            return section;
        }

        private static string StripWhitespace(string input)
        {
            return new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        }
    }
}
