using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;
using System;
using System.IO;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Pins the format the rezone actually writes, which is not the format a test process defaults
    /// to.
    ///
    /// <para>
    /// <c>STR.Write</c> picks between two encodings from a process-global,
    /// <c>IffFile.TargetTS1</c>, which <c>Content</c> sets from the engine mode. Simitone runs
    /// <c>FSOEngineMode.TS1</c>, so the flag is <c>true</c> in the game and the chunk is written in
    /// the TS1 encoding (format -3) - the same one the shipped <c>LotZoning.iff</c> uses. A test
    /// process has the flag at its default, <c>false</c>, and would otherwise only ever exercise the
    /// TSO encoding (format -4), which is not what the player gets.
    /// </para>
    ///
    /// <para>
    /// The two encodings are not interchangeable in one respect that matters here: the TS1 one
    /// writes a language code and a comment beside every line and pads the chunk with 0xA3 bytes,
    /// the TSO one writes a per-language set count. A file written in the wrong one still reads
    /// back through the same provider, so nothing would look broken - which is exactly why the
    /// round trip is asserted here rather than left to a playtest.
    /// </para>
    ///
    /// <para>
    /// Non-parallel because the flag is global: see <see cref="NonParallelCollection"/>. The flag
    /// is restored in a finally so a failure here cannot leave the rest of the suite writing
    /// chunks in the other encoding.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class LotZoningWriteFormatTests
    {
        [Fact]
        public void ARezoneRewritesTheFileInTheEncodingTheGameUses()
        {
            var original = IffFile.TargetTS1;
            try
            {
                IffFile.TargetTS1 = true; //what Content.TS1 is in Simitone

                //one line of each kind the shipped chunk holds: a residential lot, a community lot,
                //a lot with a camera coordinate shadowing it, and Maxis' spaceless line.
                var lines = new[] { "50, residential", "54, community", "2, residential", "2, 636, 102", "90,residential" };
                var iff = ZoningIff(lines);

                var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);
                Assert.Equal(LotZoning.RezoneDecision.Write, plan.Decision);
                iff.Get<STR>(1).SetString(plan.Index, plan.Line);

                var reloaded = ReadLines(RoundTrip(iff).Get<STR>(1));

                Assert.Equal(5, reloaded.Length);
                Assert.Equal("54, residential", reloaded[1]);
                Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(reloaded, 54));
                //the coordinate and the spaceless line came through the TS1 encoding untouched, and
                //the coordinate still shadows lot 2
                Assert.Equal("2, 636, 102", reloaded[3]);
                Assert.Equal("90,residential", reloaded[4]);
                Assert.Equal(LotZoning.RezoneDecision.ShadowedEntry,
                    LotZoning.Rezoned(reloaded, 2, LotZoning.ZoneCommunity, false).Decision);
            }
            finally
            {
                IffFile.TargetTS1 = original;
            }
        }

        [Fact]
        public void TheSameChunkRoundTripsInEitherEncoding()
        {
            //the decision must not depend on the encoding: the same plan, the same line, the same
            //effective zone - whichever way the file is written.
            var original = IffFile.TargetTS1;
            try
            {
                var lines = new[] { "50, residential", "54, community" };
                foreach (var target in new[] { false, true })
                {
                    IffFile.TargetTS1 = target;

                    var iff = ZoningIff(lines);
                    var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);
                    iff.Get<STR>(1).SetString(plan.Index, plan.Line);

                    var reloaded = ReadLines(RoundTrip(iff).Get<STR>(1));

                    Assert.Equal("54, residential", reloaded[1]);
                    Assert.Equal(lines[0], reloaded[0]);
                    Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(reloaded, 54));
                }
            }
            finally
            {
                IffFile.TargetTS1 = original;
            }
        }

        private static IffFile ZoningIff(string[] lines)
        {
            var items = new STRItem[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                items[i] = new STRItem(lines[i])
                {
                    LanguageCode = (byte)STRLangCode.EnglishUS,
                    Comment = "Sim Lane",
                };
            }

            var str = new STR();
            str.LanguageSets[0].Strings = items;

            var iff = new IffFile();
            iff.AddChunk(new STR
            {
                ChunkID = 1,
                ChunkLabel = "Lot Zoning Info",
                ChunkProcessed = true,
                LanguageSets = str.LanguageSets,
            });
            return iff;
        }

        private static IffFile RoundTrip(IffFile iff)
        {
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                iff.Write(stream);
                bytes = stream.ToArray();
            }

            var read = new IffFile();
            using (var stream = new MemoryStream(bytes))
            {
                read.Read(stream);
            }
            return read;
        }

        private static string[] ReadLines(STR str)
        {
            var lines = new string[str.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = str.GetString(i);
            }
            return lines;
        }
    }
}
