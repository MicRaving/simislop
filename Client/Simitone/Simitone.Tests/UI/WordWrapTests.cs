/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using System.Linq;
using System.Text;
using FSO.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="WordWrapper"/> - where the line breaks in a wrapped caption go.
    ///
    /// <para>
    /// This is the algorithm behind <c>UIUtils.WordWrap</c>, split out so it can be measured with
    /// a delegate instead of a font. The UI adapter truncates the measurement to an int before
    /// comparing it to the width, so the fake measure here does too, and the two agree exactly.
    /// </para>
    ///
    /// <para>
    /// Two rules are being locked in.
    /// </para>
    /// <list type="number">
    /// <item><b>No text is ever lost.</b> The old implementation, on finding a word that did not
    /// fit, cleared the line buffer and looked for a prefix of that word that did. When it found
    /// one it split the word, which was right. When it did not - and it could not, if the box
    /// was narrower than a single glyph - it fell out of the loop having added nothing, emitted
    /// the resulting <i>empty</i> line, and left the word in place. So it did not silently delete
    /// the word; it emitted an empty line for it and then did it again, forever, appending to
    /// <c>result.Lines</c> and <c>positions</c> without bound. The reachable route is
    /// <c>UILabel.NumLines</c>, which wraps at <c>m_Size.Width</c> and so asks for a zero-width
    /// box whenever <c>Size</c> has not been set.</item>
    /// <item><b>The reported width is the width of what was emitted.</b> The old code updated
    /// <c>maxWidth</c> from the string that was <em>about to overflow</em>, and not at all in the
    /// truncation branch, so <c>result.MaxWidth</c> was neither the widest line drawn nor a bound
    /// on it. <c>UILayer.DrawTooltip</c> uses it as the tooltip box width and
    /// <c>UICustomTooltip</c> as its background width, so a caption wider than the box it was
    /// measured against produced a box too small for its own text.</item>
    /// </list>
    ///
    /// <para>
    /// Greedy wrapping of ordinary text is asserted against literals, so that fixing the two rules
    /// above cannot quietly change the look of every caption in the game.
    /// </para>
    /// </summary>
    public sealed class WordWrapTests
    {
        private const int CharWidth = 10;

        /// <summary>10px per character, truncated to an int - the same shape as a TextStyle.</summary>
        private static float Measure(string text) => (int)(text.Length * CharWidth);

        /// <summary>An ellipsis truncation, so the maxLines branch is exercised for real.</summary>
        private static string Truncate(string text, int width)
        {
            if (Measure(text) <= width) { return text; }
            var text2 = text;
            while (text2.Length > 0)
            {
                text2 = text2.Substring(0, text2.Length - 1);
                if (Measure(text2 + "...") <= width) { return text2 + "..."; }
            }
            return text2;
        }

        private static UIWordWrapOutput Wrap(string text, int width, int maxLines = int.MaxValue)
        {
            return WordWrapper.Wrap(text, width, Measure, maxLines, Truncate, lineHeight: 12);
        }

        // ---- greedy wrapping, unchanged ---------------------------------------------------------

        [Fact]
        public void TextThatFitsStaysOnOneLine()
        {
            var result = Wrap("the quick brown", 200);

            Assert.Equal(new[] { "the quick brown" }, result.Lines);
            Assert.Equal(150, result.MaxWidth);
        }

        [Fact]
        public void TheLastWordThatFitsIsKeptOnTheLine()
        {
            // 9 characters is 90px, which fits in 90.
            var result = Wrap("the quick brown fox", 90);

            Assert.Equal(new[] { "the quick", "brown fox" }, result.Lines);
        }

        [Fact]
        public void AWordThatDoesNotFitStartsTheNextLine()
        {
            // "aaaa" is 40 and "aaaa bbbb" is 90, which does not fit in 80.
            var result = Wrap("aaaa bbbb", 80);

            Assert.Equal(new[] { "aaaa", "bbbb" }, result.Lines);
        }

        [Fact]
        public void WordsAreJoinedWithSingleSpaces()
        {
            // The split points come from ' ', not from the join, so internal runs of spaces
            // survive as they did before.
            var result = Wrap("a  b", 1000);

            Assert.Equal(new[] { "a  b" }, result.Lines);
        }

        [Fact]
        public void NewlinesStartNewLines()
        {
            // 90px is nine characters. "three four" is ten, so the third word cannot join it.
            var result = Wrap("one two\nthree four", 90);

            Assert.Equal(new[] { "one two", "three", "four" }, result.Lines);
        }

        [Fact]
        public void AnEmptySourceLineProducesAnEmptyOutputLine()
        {
            // Unchanged: "".Split(' ') is one empty word, so an empty line in the input is one
            // empty line of output, and the label draws one blank row for it.
            var result = Wrap("a\n\nb", 1000);

            Assert.Equal(new[] { "a", "", "b" }, result.Lines);
        }

        // ---- a word wider than the box ------------------------------------------------------------

        [Fact]
        public void AWordWiderThanTheBoxIsSplitNotDropped()
        {
            // 20 characters, 200px, in a 50px box: five characters to a line.
            var result = Wrap("supercalifragilistic", 50);

            Assert.Equal(new[] { "super", "calif", "ragil", "istic" }, result.Lines);
        }

        [Fact]
        public void NothingIsLostAcrossEveryBoxWidth()
        {
            // The property that matters, checked over a range of widths including the degenerate
            // ones: the letters of the input all survive, in order, once the wrapping spaces and
            // the splits' newlines are removed.
            const string source = "aaaaaaaaaaaaaaaaaaaaaaaaa bb cc";
            var expected = source.Replace(" ", "");

            for (var width = 0; width <= 320; width += 7)
            {
                var result = Wrap(source, width);
                var joined = string.Concat(result.Lines).Replace(" ", "");
                Assert.Equal(expected, joined);
            }
        }

        [Fact]
        public void ABoxTooNarrowForOneCharacterStillTerminates()
        {
            // The infinite loop. width 0 cannot fit even a single glyph, so the old code emitted
            // an empty line, removed nothing, and went round again for ever, growing result.Lines
            // and result.Positions without bound. The word is now emitted whole and unclamped,
            // because losing it is worse than overflowing it.
            var result = Wrap("abc", 0);

            Assert.Equal(new[] { "abc" }, result.Lines);
            Assert.Equal(30, result.MaxWidth);
        }

        [Fact]
        public void AZeroWidthBoxStillEmitsOneLinePerWordRatherThanSpinning()
        {
            var result = Wrap("one two three", 0);

            Assert.Equal(new[] { "one", "two", "three" }, result.Lines);
            Assert.Equal(3, result.Positions.Count);
        }

        [Fact]
        public void ANullTextIsOneEmptyLineNotAnException()
        {
            var result = Wrap(null, 100);

            Assert.Single(result.Lines);
            Assert.Equal(string.Empty, result.Lines[0]);
        }

        // ---- the reported width ------------------------------------------------------------------

        [Fact]
        public void MaxWidthIsTheWidestEmittedLine()
        {
            // Lines measure 70, 50, 30. The old code reported the width of the string that
            // overflowed - "brown fox" at 90 - which is wider than anything it drew.
            var result = Wrap("aaa bbb ccc ddd eee", 70);

            Assert.Equal(new[] { "aaa bbb", "ccc ddd", "eee" }, result.Lines);
            Assert.Equal(70, result.MaxWidth);
        }

        [Fact]
        public void MaxWidthAccountsForTheTruncatedLastLine()
        {
            // The truncation branch never updated maxWidth at all, so a caption clamped to the box
            // reported the width of whatever fitted before it and produced a box too small for
            // the text it was about to draw.
            var result = Wrap("aaa bbb ccc ddd", 70, maxLines: 1);

            Assert.Single(result.Lines);
            Assert.Equal(70, result.MaxWidth);
            Assert.True(Measure(result.Lines[0]) <= 70);
        }

        [Fact]
        public void HeightIsLinesTimesLineHeight()
        {
            Assert.Equal(3 * 12, Wrap("aaa bbb ccc ddd eee", 70).Height);
        }

        // ---- maxLines ----------------------------------------------------------------------------

        [Fact]
        public void MaxLinesCapsTheOutput()
        {
            // The last permitted line is built greedily and then truncated as a whole, so it
            // reaches one word past the fill and loses its tail. That is what the old code did
            // and it is what the caller is told to draw, so it is unchanged here.
            var result = Wrap("aaa bbb ccc ddd eee", 70, maxLines: 2);

            Assert.Equal(2, result.Lines.Count);
            Assert.Equal("aaa bbb", result.Lines[0]);
            Assert.Equal("ccc ...", result.Lines[1]);
        }

        [Fact]
        public void TheLastPermittedLineIsTruncatedRatherThanWrapped()
        {
            // The last line runs until something stops fitting and is then shortened.
            var result = Wrap("aaa bbb ccc", 70, maxLines: 1);

            Assert.Single(result.Lines);
            Assert.True(Measure(result.Lines[0]) <= 70);
        }

        // ---- positions ----------------------------------------------------------------------------

        [Fact]
        public void PositionsAreOffsetsIntoTheOriginalText()
        {
            // Written but never read anywhere in the tree, so this fixes what it means rather
            // than locking an existing contract: the index into the source text at which each
            // line's content begins. For an ordinary wrap the line is exactly the source from
            // that offset.
            var source = "aaa bbb ccc ddd";
            var result = Wrap(source, 70);

            Assert.Equal(new[] { "aaa bbb", "ccc ddd" }, result.Lines);
            Assert.Equal(2, result.Positions.Count);
            Assert.Equal(0, result.Positions[0]);
            Assert.Equal(8, result.Positions[1]);
            // Each line matches the source character for character from its offset.
            foreach (var i in System.Linq.Enumerable.Range(0, result.Lines.Count))
            {
                Assert.Equal(result.Lines[i],
                    source.Substring(result.Positions[i], result.Lines[i].Length));
            }
        }

        [Fact]
        public void PositionsSurviveAHardSplitAndANewline()
        {
            // A 25-character word in a 50px box is split into five-character pieces, and the
            // source has a newline in the middle. The invariant is the one that actually holds
            // for every case: each line's content matches the source character for character
            // from that offset. A whole line does because nothing was dropped in front of it; a
            // split piece does because the pieces are consecutive runs of one word with no
            // separator between them.
            var source = "aaaaaaaaaaaaaaaaaaaaaaaaa bb\ncc ddd";
            var result = Wrap(source, 50);

            Assert.Equal(result.Lines.Count, result.Positions.Count);
            Assert.Equal(0, result.Positions[0]);
            for (var i = 0; i < result.Positions.Count; i++)
            {
                var pos = result.Positions[i];
                Assert.InRange(pos, 0, source.Length - 1);
                if (i > 0)
                {
                    Assert.True(result.Positions[i] > result.Positions[i - 1]);
                }
                var firstWord = result.Lines[i].Split(' ')[0];
                Assert.Equal(firstWord, source.Substring(pos, firstWord.Length));
            }

            // Five pieces of five, then "bb", then the two words after the newline.
            Assert.Equal(new[] { 0, 5, 10, 15, 20, 26, 29, 32 }, result.Positions);
        }
    }
}
