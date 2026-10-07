using System.Collections.Generic;
using System.Linq;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the exact skip rules of the neighbourhood lot-location table. These used to live
    /// inline in <c>UINeighborhoodSelectionPanel.PopulateScreen</c> and were later the site of a
    /// real crash (<c>SelectHouse</c> indexing a dictionary that deliberately omits some lots),
    /// so the skipping behaviour is load-bearing and must not drift.
    /// </summary>
    public sealed class LotLocationParserTests
    {
        [Fact]
        public void ParsesAWellFormedEntry()
        {
            Assert.True(LotLocationParser.TryParseLotLocation("12,345,678", out int n, out int x, out int y));
            Assert.Equal(12, n);
            Assert.Equal(345, x);
            Assert.Equal(678, y);
        }

        [Fact]
        public void TrimsLeadingWhitespaceOnly()
        {
            // The original code applied TrimStart() (no args) to each field. Trailing spaces are
            // tolerated by int.TryParse anyway, but leading tabs/spaces must not break the split.
            Assert.True(LotLocationParser.TryParseLotLocation("\t 7, \t9 , 10", out int n, out int x, out int y));
            Assert.Equal(7, n);
            Assert.Equal(9, x);
            Assert.Equal(10, y);
        }

        [Theory]
        [InlineData("")]
        [InlineData("1")]
        [InlineData("1,2")]
        [InlineData("1,2,")] // third field empty -> TryParse fails on y
        public void RejectsEntriesWithFewerThanThreeUsableFields(string raw)
        {
            Assert.False(LotLocationParser.TryParseLotLocation(raw, out _, out _, out _));
        }

        [Fact]
        public void RejectsNonNumericNumberAndZeroesOutputs()
        {
            Assert.False(LotLocationParser.TryParseLotLocation("abc,1,2", out int n, out int x, out int y));
            Assert.Equal(0, n);
            Assert.Equal(0, x);
            Assert.Equal(0, y);
        }

        [Theory]
        [InlineData("5,abc,2")]
        [InlineData("5,2,abc")]
        public void RejectsNonNumericCoordinates(string raw)
        {
            Assert.False(LotLocationParser.TryParseLotLocation(raw, out _, out _, out _));
        }

        [Fact]
        public void Lot99IsAlwaysRejected()
        {
            // 99 is the engine's "no lot here" sentinel and must never produce a clickable button.
            Assert.False(LotLocationParser.TryParseLotLocation("99,10,20", out _, out _, out _));
        }

        [Fact]
        public void Lot99IsRejectedBeforeCoordinatesAreParsed()
        {
            // The 99 check sits between the number parse and the x parse. A malformed 99 entry
            // must therefore still be rejected (and must not be reported as a parse failure of x).
            Assert.False(LotLocationParser.TryParseLotLocation("99,garbage,garbage", out _, out _, out _));
        }

        [Fact]
        public void NullIsRejected()
        {
            Assert.False(LotLocationParser.TryParseLotLocation(null, out _, out _, out _));
        }

        [Fact]
        public void ParseTableSkipsBadEntriesAndPreservesOrder()
        {
            var raw = new List<string>
            {
                "3,100,200",
                "not,a,lot",
                "99,5,6",
                "4,7",           // too short
                "5,300,400",
                "6,x,9",         // bad x
            };

            var parsed = LotLocationParser.ParseLotLocationTable(raw);

            Assert.Equal(new[] { 3, 5 }, parsed.Select(l => l.Number).ToArray());
            Assert.Equal(new[] { 100, 300 }, parsed.Select(l => l.X).ToArray());
            Assert.Equal(new[] { 200, 400 }, parsed.Select(l => l.Y).ToArray());
        }

        [Fact]
        public void ParseTableKeepsDuplicateLotNumbers()
        {
            // The original code assigned into a dictionary (last wins) and also appended a button
            // per entry, so duplicates must survive the parse step for the caller to decide.
            var parsed = LotLocationParser.ParseLotLocationTable(new[] { "8,1,2", "8,3,4" });

            Assert.Equal(2, parsed.Count);
            Assert.Equal(new[] { 1, 3 }, parsed.Select(l => l.X).ToArray());
        }

        [Fact]
        public void ParseTableToleratesNull()
        {
            Assert.Empty(LotLocationParser.ParseLotLocationTable(null));
        }
    }
}
