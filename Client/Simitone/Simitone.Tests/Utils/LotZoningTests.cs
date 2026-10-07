using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the pure rules behind the lot panel's <b>Rezone</b> button - see
    /// <see cref="LotZoning"/>.
    ///
    /// <para>
    /// There is one reader of this data in the whole program:
    /// <c>TS1NeighborhoodProvider.InitSpecific</c>, which splits every line of
    /// <c>&lt;UserData&gt;/LotZoning.iff</c>'s STR chunk 1 on the comma and stores
    /// <c>split[1] == " community" ? 1 : 0</c>. Every rule tested here is that reader's behaviour
    /// restated, because a rezone the loader disagrees with is not a rezone - it is a button that
    /// lies to the player and forgets what it said on the next launch.
    /// </para>
    ///
    /// <para>
    /// The shape of the shipped file is the other half of it, and it is why three of these rules
    /// exist at all. <see cref="ShippedChunk"/> reproduces it: ten blocks of a hundred lines, where
    /// the first fifty are one copy of the zoning (lots 1-10 and 50-80 and 90-98), line 50 of each
    /// block is a resident GUID, lines 51-59 are the camera coordinates of the ten original lots,
    /// and lines 60-99 are a <em>second</em> copy of most of that zoning - the copy the provider
    /// actually ends up with, because it assigns in file order and the last line wins. It also
    /// carries Maxis' hand-edited "90,residential" at index 91 of each block, whose missing
    /// delimiter space is the only spelling quirk in the whole file.
    /// </para>
    ///
    /// <para>
    /// So the line a rezone has to change is not the first line that mentions the lot, and for the
    /// ten original lots there is no line it may change at all. Both of those are pinned below by
    /// index, against a chunk built from the real file's lines, so a change to the lookup rules
    /// shows up as a failed index rather than as a silent behaviour change in a playtest.
    /// </para>
    ///
    /// <para>
    /// The last group writes a real STR chunk out and reads it back, which is the only part of
    /// this that touches the file format at all: it pins that a rezone leaves the other 999 lines
    /// - and the comments Maxis stored beside them - exactly as they were.
    /// </para>
    /// </summary>
    public sealed class LotZoningTests
    {
        // ------------------------------------------------------------- the shipped file's shape

        /// <summary>Lines per block in the shipped <c>LotZoning.iff</c>.</summary>
        private const int BlockSize = 100;

        /// <summary>Blocks in the shipped file, so 1000 lines.</summary>
        private const int BlockCount = 10;

        /// <summary>
        /// Where the last block starts - and since the provider takes the last line about a lot,
        /// this is the block every rezone happens in. Rewriting an earlier block's copy of the same
        /// lot would achieve nothing at all.
        /// </summary>
        private const int LastBlock = (BlockCount - 1) * BlockSize;

        /// <summary>
        /// The whole shipped zoning chunk, 1000 lines, as the game shipped it.
        /// </summary>
        /// <remarks>
        /// Taken verbatim from a real <c>UserData\LotZoning.iff</c> - one block's worth of lines,
        /// repeated ten times, which is what the file is. Every hazard the rules have to survive is
        /// in here: the shadowed originals, the camera coordinates, the GUID line, the duplicate
        /// copy, and the spaceless lot 90 line.
        /// </remarks>
        private static readonly string[] Block = new string[]
        {
            //0-9: the ten original lots
            "1, residential", "2, residential", "3, residential", "4, residential", "5, residential",
            "6, residential", "7, residential", "8, residential", "9, residential", "10, residential",
            //10-49: the first copy of the unleashed block. Lot 54 reads as residential from here and
            //as community from the second copy further down - the last-line-wins trap.
            "50, residential", "51, residential", "52, residential", "53, residential", "54, residential",
            "55, residential", "56, residential", "57, residential", "58, community", "59, residential",
            "60, residential", "61, community", "62, residential", "63, residential", "64, residential",
            "65, residential", "66, residential", "67, residential", "68, residential", "69, residential",
            "70, community", "71, community", "72, community", "73, community", "74, community",
            "75, community", "76, residential", "77, residential", "78, residential", "79, residential",
            "80, residential", "90, residential", "91, residential", "92, residential", "93, community",
            "94, community", "95, community", "96, community", "97, community", "98, community",
            //50: a resident GUID, which is not a line about a lot at all
            "xa37dd45ffe100bfffcc9753aabac325f07cb3fa231144fe2e33ae4783feead2b8a73ff021fac326df0ef9753"
            + "ab9cdf6573ddff0312fab0b0ff39779eaff312x",
            //51-59: the camera coordinates of lots 2-10. The provider reads every one of these as a
            //residential statement about its lot, which is what shadows lots 2-10.
            "2, 636, 102", "3, 359, 230", "4, 209, 305", "5, 522, 381", "6, 105, 482",
            "7, 376, 454", "8, 672, 477", "9, 277, 555", "10, 527, 548",
            //60-99: the copy the game reads. Note the duplicated "79, residential" at 89 and 90, and
            //Maxis' "90,residential" with no delimiter space at 91.
            "50, residential", "51, residential", "52, residential", "53, residential", "54, community",
            "55, residential", "56, residential", "57, residential", "58, residential", "59, residential",
            "60, residential", "61, residential", "62, residential", "63, residential", "64, residential",
            "65, residential", "66, residential", "67, residential", "68, residential", "69, residential",
            "70, residential", "71, residential", "72, residential", "73, residential", "74, residential",
            "75, residential", "76, residential", "77, residential", "78, residential", "79, residential",
            "79, residential", "90,residential", "91, residential", "92, residential", "93, community",
            "94, community", "95, community", "96, community", "97, community", "98, community",
        };

        /// <summary>The shipped chunk, rebuilt so a test can hand out its own copy.</summary>
        private static string[] ShippedChunk()
        {
            var lines = new string[BlockSize * BlockCount];
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = Block[i % BlockSize];
            }
            return lines;
        }

        // ------------------------------------------------------------- writing the token

        [Fact]
        public void ACommunityZoneIsWrittenWithTheLeadingSpaceTheProviderComparesAgainst()
        {
            //the whole format, in one assertion. Drop the space, capitalise the C, or spell it any
            //other way and the provider reads the lot as residential instead - silently, and for as
            //long as the file says so.
            Assert.Equal(" community", LotZoning.CommunityToken);
            Assert.Equal(" residential", LotZoning.ResidentialToken);
        }

        [Fact]
        public void TheTokenIsTheNameWithTheDelimiterSpace()
        {
            //spelled out so the two constants cannot drift apart: the token is the provider's
            //literal, the name is what a line says once the space is gone.
            Assert.Equal(LotZoning.CommunityName, LotZoning.CommunityToken.TrimStart());
            Assert.Equal(LotZoning.ResidentialName, LotZoning.ResidentialToken.TrimStart());
        }

        [Theory]
        [InlineData(0, " residential")]
        [InlineData(1, " community")]
        public void EachZoneHasOneToken(short zone, string expected)
        {
            Assert.Equal(expected, LotZoning.ZoneToken(zone));
        }

        [Theory]
        [InlineData(0, true)]
        [InlineData(1, true)]
        [InlineData(2, false)]     //the lot panel's studio display value - never written
        [InlineData(-1, false)]
        [InlineData(3, false)]
        public void OnlyTheTwoFileZonesAreKnown(short zone, bool known)
        {
            Assert.Equal(known, LotZoning.IsKnownZone(zone));
        }

        [Theory]
        [InlineData(1, 0, "1, residential")]
        [InlineData(22, 1, "22, community")]
        [InlineData(98, 1, "98, community")]
        public void AFormattedLineIsTheLotThenTheToken(int lot, short zone, string expected)
        {
            Assert.Equal(expected, LotZoning.Format(lot, zone));
        }

        [Fact]
        public void AFormattedLineReadsBackAsWhatItSays()
        {
            //the round trip the write depends on: what we put in the file is what the provider will
            //read back out of it.
            foreach (var lot in new[] { 1, 2, 50, 54, 79, 90, 98 })
            {
                foreach (short zone in new short[] { LotZoning.ZoneResidential, LotZoning.ZoneCommunity })
                {
                    var line = LotZoning.Format(lot, zone);
                    Assert.True(LotZoning.TryParse(line, out var readLot, out var readZone), line);
                    Assert.Equal(lot, readLot);
                    Assert.Equal(zone, readZone);
                }
            }
        }

        // ------------------------------------------------------------- reading a line

        [Fact]
        public void ACommunityLineIsReadAsCommunity()
        {
            Assert.True(LotZoning.TryParse("22, community", out var lot, out var zone));
            Assert.Equal(22, lot);
            Assert.Equal(LotZoning.ZoneCommunity, zone);
        }

        [Fact]
        public void AResidentialLineIsReadAsResidential()
        {
            Assert.True(LotZoning.TryParse("21, residential", out var lot, out var zone));
            Assert.Equal(21, lot);
            Assert.Equal(LotZoning.ZoneResidential, zone);
        }

        [Theory]
        [InlineData("22,community")]      //no delimiter space
        [InlineData("22, Community")]     //capitalised
        [InlineData("22,  community")]    //the delimiter space, then another
        [InlineData("22,COMMUNITY")]
        [InlineData("22, community ")]    //trailing space
        public void ACommunityLineTheProviderWouldNotRecogniseReadsAsResidential(string line)
        {
            //pinned as the provider's behaviour, not as a rule we would like. A rezone that wrote
            //any of these would look right in the file and do nothing in the game.
            Assert.True(LotZoning.TryParse(line, out var lot, out var zone));
            Assert.Equal(22, lot);
            Assert.Equal(LotZoning.ZoneResidential, zone);
        }

        [Fact]
        public void MaxisHandEditedLineIsStillAReadableResidentialLine()
        {
            //the one line in the shipped file with no delimiter space, at index 91 of every block.
            Assert.True(LotZoning.TryParse("90,residential", out var lot, out var zone));
            Assert.Equal(90, lot);
            Assert.Equal(LotZoning.ZoneResidential, zone);
        }

        [Fact]
        public void ACameraCoordinateLineIsReadAsAResidentialStatementAboutItsLot()
        {
            //three fields, and the provider only ever looks at the second one. This is not a
            //mistake of ours to fix - it is what the game does, and it is why the ten original
            //lots cannot be rezoned.
            Assert.True(LotZoning.TryParse("2, 636, 102", out var lot, out var zone));
            Assert.Equal(2, lot);
            Assert.Equal(LotZoning.ZoneResidential, zone);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("residential")]          //no comma: the provider would throw
        [InlineData("21")]                   //ditto
        [InlineData("twenty-one, residential")]
        [InlineData("21.5, residential")]
        [InlineData("99999, residential")]   //out of short range: the provider would throw
        [InlineData("xa37dd45ffe100bfffcc9753aabac325f07")]
        public void ALineTheProviderWouldThrowOnIsNotReadable(string line)
        {
            //the shipped GUID line is the real case: 128 hex characters, no comma.
            Assert.False(LotZoning.TryParse(line, out _, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("residential")]
        [InlineData("21")]
        [InlineData("not a lot, community")]
        public void AMalformedLineIsNeverAZoningEntry(string line)
        {
            //"leave the line alone if you cannot understand it" - a garbage line must not be
            //something a rezone is willing to overwrite.
            Assert.False(LotZoning.IsZoningEntry(line), line ?? "(null)");
        }

        // ------------------------------------------------------------- what counts as a zoning line

        [Theory]
        [InlineData("21, residential")]
        [InlineData("21, community")]
        [InlineData("90,residential")]  //Maxis' spelling, accepted
        [InlineData("90, community")]
        public void AZoneNameMakesALineAZoningEntry(string line)
        {
            Assert.True(LotZoning.IsZoningEntry(line));
        }

        [Theory]
        [InlineData("2, 636, 102")]     //camera coordinates: three fields
        [InlineData("2, 636")]          //and not a zone name either
        [InlineData("2,  the park")]
        [InlineData("90,residentialx")]
        [InlineData("90,")]
        public void SomethingElseDoesNot(string line)
        {
            Assert.False(LotZoning.IsZoningEntry(line));
        }

        [Theory]
        [InlineData(" residential", true)]
        [InlineData(" community", true)]
        [InlineData("residential", true)]     //Maxis' line, which has no delimiter space
        [InlineData("community", true)]
        [InlineData("\tcommunity", true)]
        [InlineData(" 636", false)]
        [InlineData("Community", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void AZoneNameIsRecognisedHoweverItIsSpaced(string field, bool isName)
        {
            Assert.Equal(isName, LotZoning.IsZoneName(field));
        }

        // ------------------------------------------------------------- finding the line

        [Fact]
        public void TheLineTheGameReadsIsTheOneFound()
        {
            //lot 54 appears twice in every block: "54, residential" at 14 and "54, community" at
            //64. The provider assigns in order, so the block 9 copy at 964 is the lot's zone and
            //is the line a rezone has to change. Picking 14 - or 914, or any other block - would
            //produce a rezone the very next load undoes.
            var lines = ShippedChunk();

            Assert.True(LotZoning.TryGetZoningEntry(lines, 54, out var index, out var zone));
            Assert.Equal(964, index);
            Assert.Equal(LotZoning.ZoneCommunity, zone);
            Assert.Equal(zone, LotZoning.EffectiveZone(lines, 54));
        }

        [Fact]
        public void ACameraCoordinateShadowsItsLot()
        {
            //lot 2's last zoning line is block 9's copy at 901, and the camera coordinate line for
            //lot 2 is at 951 - after it. The provider reads the coordinate, so the lot's zone is
            //residential whatever the zoning line says, and a rezone of the zoning line would not
            //survive the next launch.
            var lines = ShippedChunk();

            Assert.True(LotZoning.TryGetZoningEntry(lines, 2, out var index, out var zone));
            Assert.Equal(901, index);
            Assert.Equal(LotZoning.ZoneResidential, zone);
            Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(lines, 2));
        }

        [Fact]
        public void TheLastZoningEntryWinsWhenALotHasTwoInABlock()
        {
            //lots 79 and 90 are the two the shipped file states twice, for no reason a player could
            //guess. The later line is the one the provider holds, so it is the later one that is
            //found.
            Assert.True(LotZoning.TryGetZoningEntry(new[] { "79, community", "79, residential" },
                79, out var index, out var zone));
            Assert.Equal(1, index);
            Assert.Equal(LotZoning.ZoneResidential, zone);

            var lines = ShippedChunk();
            Assert.True(LotZoning.TryGetZoningEntry(lines, 79, out var copy, out var copyZone));
            Assert.Equal(LastBlock + 90, copy);      //the duplicated 79, not the 89 one
            Assert.Equal(copyZone, LotZoning.EffectiveZone(lines, 79));
        }

        [Fact]
        public void TheSpacelessLineIsTheOneFoundForLotNinety()
        {
            //Maxis wrote "90,residential" without the delimiter space, and it is the last line
            //about lot 90. It states a zone, so it is the line to rewrite - not a reason to tell
            //the player lot 90 cannot be rezoned.
            var lines = ShippedChunk();

            Assert.True(LotZoning.TryGetZoningEntry(lines, 90, out var index, out var zone));
            Assert.Equal(LastBlock + 91, index);
            Assert.Equal(LotZoning.ZoneResidential, zone);
        }

        [Fact]
        public void ALotTheFileNeverMentionsHasNoEntryAndNoZone()
        {
            //residential lot 11, the Downtown, Vacation, Studio and Magic lots: not in the file at
            //all. The lot panel still opens them, on the fallback zone.
            var lines = ShippedChunk();

            foreach (var lot in new[] { 11, 25, 42, 85, 99, 100, 0, -1 })
            {
                Assert.False(LotZoning.TryGetZoningEntry(lines, lot, out var index, out _), lot.ToString());
                Assert.Equal(-1, index);
                Assert.Null(LotZoning.EffectiveZone(lines, lot));
            }
        }

        [Fact]
        public void NoLinesAtAllMeansNoLotIsRezonable()
        {
            //a chunk that failed to decode, or an empty one, must not throw: the service turns this
            //into "the lot has no zoning entry" and moves on.
            Assert.False(LotZoning.TryGetZoningEntry(new string[0], 50, out var index, out _));
            Assert.Equal(-1, index);
            Assert.Null(LotZoning.EffectiveZone(new string[0], 50));
            Assert.Equal(LotZoning.RezoneDecision.NoZoningEntry,
                LotZoning.Rezoned(new string[0], 50, LotZoning.ZoneCommunity, false).Decision);
        }

        // ------------------------------------------------------------- the panel's fallback

        [Theory]
        [InlineData(81, LotZoning.ZoneStudio)]
        [InlineData(85, LotZoning.ZoneStudio)]
        [InlineData(89, LotZoning.ZoneStudio)]
        [InlineData(1, LotZoning.ZoneCommunity)]
        [InlineData(80, LotZoning.ZoneCommunity)]
        [InlineData(90, LotZoning.ZoneCommunity)]
        [InlineData(99, LotZoning.ZoneCommunity)]
        public void AStudioLotIsTheOnlyOneWithItsOwnFallback(int houseId, short expected)
        {
            //unchanged behaviour, moved out of the lot panel so the rezone button and the caption
            //cannot read the lot's zone two different ways.
            Assert.Equal(expected, LotZoning.DefaultZoneForLot(houseId));
        }

        [Fact]
        public void TheFallbackIsAlwaysANonResidentialValue()
        {
            //the lot panel's primary button is disabled for any value above zero, so a lot with no
            //entry in the file is presented as one that cannot be moved into. That is what it has
            //always done; the fallback exists to keep the two asking the same question.
            for (int houseId = 1; houseId <= 100; houseId++)
            {
                Assert.True(LotZoning.DefaultZoneForLot(houseId) > LotZoning.ZoneResidential);
            }
        }

        // ------------------------------------------------------------- the decision

        [Fact]
        public void AnEmptyResidentialLotIsRezonedByRewritingItsLine()
        {
            var lines = ShippedChunk();
            Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(lines, 50));

            var plan = LotZoning.Rezoned(lines, 50, LotZoning.ZoneCommunity, false);

            Assert.Equal(LotZoning.RezoneDecision.Write, plan.Decision);
            Assert.True(plan.WritesFile);
            Assert.Equal(LastBlock + 60, plan.Index);
            Assert.Equal("50, community", plan.Line);
            Assert.Equal(LotZoning.ZoneCommunity, plan.Zone);
            Assert.Equal(LotZoning.ZoneCommunity, LotZoning.EffectiveZone(plan.ApplyTo(lines), 50));
        }

        [Fact]
        public void ACommunityLotIsRezonedBackToResidential()
        {
            //lot 54 is one of the community lots the shipped file ships with.
            var lines = ShippedChunk();
            Assert.Equal(LotZoning.ZoneCommunity, LotZoning.EffectiveZone(lines, 54));

            var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);

            Assert.Equal(LotZoning.RezoneDecision.Write, plan.Decision);
            Assert.Equal("54, residential", plan.Line);
            Assert.Equal(LotZoning.ZoneResidential, plan.Zone);
            Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(plan.ApplyTo(lines), 54));
        }

        [Fact]
        public void TheSpacelessLineIsNormalisedByTheRewrite()
        {
            //rezoning lot 90 to community replaces "90,residential" with a spelled-the-normal-way
            //"90, community" - which is the point: the line is now one the provider will read as
            //community, and the hand edit is gone as a side effect.
            var lines = ShippedChunk();

            var plan = LotZoning.Rezoned(lines, 90, LotZoning.ZoneCommunity, false);

            Assert.Equal(LotZoning.RezoneDecision.Write, plan.Decision);
            Assert.Equal("90, community", plan.Line);
            Assert.Equal(LotZoning.ZoneCommunity, LotZoning.EffectiveZone(plan.ApplyTo(lines), 90));
        }

        [Fact]
        public void AnOccupiedLotIsRefusedBeforeTheFileIsEvenConsulted()
        {
            //the rule: a lot is zoned before anyone moves in, so a household is evicted first. It
            //is also the only refusal that must not depend on the file - the same answer for a lot
            //the file has never heard of, and for a zone the file could not hold.
            var lines = ShippedChunk();

            Assert.Equal(LotZoning.RezoneDecision.Occupied,
                LotZoning.Rezoned(lines, 50, LotZoning.ZoneCommunity, true).Decision);
            Assert.Equal(LotZoning.RezoneDecision.Occupied,
                LotZoning.Rezoned(lines, 25, LotZoning.ZoneCommunity, true).Decision);
            Assert.Equal(LotZoning.RezoneDecision.Occupied,
                LotZoning.Rezoned(lines, 50, 7, true).Decision);
            Assert.Equal(LotZoning.RezoneDecision.Occupied,
                LotZoning.Rezoned(lines, 2, LotZoning.ZoneCommunity, true).Decision);
        }

        [Fact]
        public void AZoneTheFileCannotHoldIsRefused()
        {
            var lines = ShippedChunk();

            foreach (short zone in new short[] { LotZoning.ZoneStudio, 2, 7, -1 })
            {
                Assert.Equal(LotZoning.RezoneDecision.UnknownZone,
                    LotZoning.Rezoned(lines, 50, zone, false).Decision);
            }
        }

        [Fact]
        public void ALotWithNoZoningLineIsRefusedRatherThanAppended()
        {
            //Downtown 25, vacation 42, studio 85, magic 99: the file says nothing about them, and
            //the STR is a fixed 1000-line, index-addressed table. Appending would be inventing a
            //line the game has no way to expect.
            var lines = ShippedChunk();

            foreach (var lot in new[] { 11, 25, 42, 85, 99, 100 })
            {
                var plan = LotZoning.Rezoned(lines, lot, LotZoning.ZoneCommunity, false);
                Assert.Equal(LotZoning.RezoneDecision.NoZoningEntry, plan.Decision);
                Assert.Null(plan.ApplyTo(lines));
            }
        }

        [Fact]
        public void ALotWhoseZoneIsDecidedByALaterLineIsRefused()
        {
            //lots 2-10 have a camera coordinate line after their last zoning line, and the provider
            //reads the coordinate. Rewriting the zoning line would be undone on the next launch, so
            //the button refuses and says why rather than writing something that cannot stick.
            var lines = ShippedChunk();

            for (var lot = 2; lot <= 10; lot++)
            {
                var plan = LotZoning.Rezoned(lines, lot, LotZoning.ZoneCommunity, false);
                Assert.Equal(LotZoning.RezoneDecision.ShadowedEntry, plan.Decision);
                Assert.Null(plan.ApplyTo(lines));
            }
        }

        [Fact]
        public void ExactlyTheLotsTheFileCanDecideAreRezonable()
        {
            //the whole set of outcomes the shipped file produces, stated once. A change to the
            //rules that widened or narrowed this - accepting the coordinate lines, appending a line
            //for an unzoned lot - would show up here rather than in a playtest.
            var lines = ShippedChunk();
            var outcomes = new List<string>();
            for (int lot = 1; lot <= 100; lot++)
            {
                var plan = LotZoning.Rezoned(lines, lot, LotZoning.ZoneCommunity, false);
                if (plan.WritesFile || plan.Decision == LotZoning.RezoneDecision.Unchanged) continue;
                outcomes.Add(lot + ":" + plan.Decision);
            }

            Assert.Equal(
                "2:ShadowedEntry,3:ShadowedEntry,4:ShadowedEntry,5:ShadowedEntry,6:ShadowedEntry,"
                + "7:ShadowedEntry,8:ShadowedEntry,9:ShadowedEntry,10:ShadowedEntry,"
                + "11:NoZoningEntry,12:NoZoningEntry,13:NoZoningEntry,14:NoZoningEntry,"
                + "15:NoZoningEntry,16:NoZoningEntry,17:NoZoningEntry,18:NoZoningEntry,"
                + "19:NoZoningEntry,20:NoZoningEntry,21:NoZoningEntry,22:NoZoningEntry,"
                + "23:NoZoningEntry,24:NoZoningEntry,25:NoZoningEntry,26:NoZoningEntry,"
                + "27:NoZoningEntry,28:NoZoningEntry,29:NoZoningEntry,30:NoZoningEntry,"
                + "31:NoZoningEntry,32:NoZoningEntry,33:NoZoningEntry,34:NoZoningEntry,"
                + "35:NoZoningEntry,36:NoZoningEntry,37:NoZoningEntry,38:NoZoningEntry,"
                + "39:NoZoningEntry,40:NoZoningEntry,41:NoZoningEntry,42:NoZoningEntry,"
                + "43:NoZoningEntry,44:NoZoningEntry,45:NoZoningEntry,46:NoZoningEntry,"
                + "47:NoZoningEntry,48:NoZoningEntry,49:NoZoningEntry,81:NoZoningEntry,"
                + "82:NoZoningEntry,83:NoZoningEntry,84:NoZoningEntry,85:NoZoningEntry,"
                + "86:NoZoningEntry,87:NoZoningEntry,88:NoZoningEntry,89:NoZoningEntry,"
                + "99:NoZoningEntry,100:NoZoningEntry",
                string.Join(",", outcomes));
        }

        [Fact]
        public void RezoningToTheZoneTheLotAlreadyHasChangesNothing()
        {
            var lines = ShippedChunk();

            var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneCommunity, false);

            Assert.Equal(LotZoning.RezoneDecision.Unchanged, plan.Decision);
            Assert.False(plan.WritesFile);
            Assert.Equal(LotZoning.ZoneCommunity, plan.Zone);
            Assert.Null(plan.ApplyTo(lines));
        }

        [Fact]
        public void UnchangedIsDecidedAgainstTheLineNotTheDictionary()
        {
            //lot 54's first copy says residential and its second says community, and the provider
            //holds community. Asking for community is a no-op even though there is a line in the
            //file that spells "residential" - so the decision is made on the line that is read.
            var lines = new[] { "54, residential", "54, community" };

            Assert.Equal(LotZoning.RezoneDecision.Unchanged,
                LotZoning.Rezoned(lines, 54, LotZoning.ZoneCommunity, false).Decision);
            Assert.Equal(LotZoning.RezoneDecision.Write,
                LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false).Decision);
        }

        [Theory]
        [InlineData(LotZoning.RezoneDecision.Occupied)]
        [InlineData(LotZoning.RezoneDecision.NoZoningEntry)]
        [InlineData(LotZoning.RezoneDecision.ShadowedEntry)]
        [InlineData(LotZoning.RezoneDecision.UnknownZone)]
        [InlineData(LotZoning.RezoneDecision.Unchanged)]
        public void APlanThatDoesNotWriteCarriesNoLineToApply(LotZoning.RezoneDecision decision)
        {
            //each of the four refusals and the no-op, reached on a one-line chunk, so the assertion
            //is about the outcome rather than about which lot produced it.
            var lines = new[] { "50, residential" };
            short zone = LotZoning.ZoneCommunity;
            bool occupied = false;
            switch (decision)
            {
                case LotZoning.RezoneDecision.Occupied: occupied = true; break;
                case LotZoning.RezoneDecision.NoZoningEntry: lines = new[] { "60, residential" }; break;
                case LotZoning.RezoneDecision.ShadowedEntry: lines = new[] { "50, residential", "50, 636, 102" }; break;
                case LotZoning.RezoneDecision.UnknownZone: zone = 9; break;
                case LotZoning.RezoneDecision.Unchanged: lines = new[] { "50, community" }; break;
            }

            var plan = LotZoning.Rezoned(lines, 50, zone, occupied);

            Assert.Equal(decision, plan.Decision);
            Assert.False(plan.WritesFile);
            Assert.Null(plan.Line);
            Assert.Null(plan.ApplyTo(lines));
        }

        // ------------------------------------------------------------- what a rewrite preserves

        [Fact]
        public void ARewriteChangesExactlyOneLineOfAThousand()
        {
            //the shipped file is 1000 lines of which 110 are not zoning at all, and the same lot is
            //stated twice in every block. A rewrite that touched anything but its own line would be
            //changing the neighbourhood's map data, or a lot the game does not read.
            var lines = ShippedChunk();
            var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);

            var rewritten = plan.ApplyTo(lines);

            Assert.NotNull(rewritten);
            Assert.Equal(lines.Length, rewritten.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == plan.Index)
                {
                    Assert.Equal(plan.Line, rewritten[i]);
                }
                else
                {
                    //reference equality, not just equality: the untouched lines are the very same
                    //strings, so nothing could have normalised, trimmed or rebuilt them
                    Assert.Same(lines[i], rewritten[i]);
                }
            }
        }

        [Fact]
        public void ARewriteLeavesEveryCameraCoordinateAndGuidLineAlone()
        {
            var lines = ShippedChunk();
            var plan = LotZoning.Rezoned(lines, 50, LotZoning.ZoneCommunity, false);

            var rewritten = plan.ApplyTo(lines);

            Assert.Equal(
                lines.Where((l, i) => i % BlockSize == 50).ToArray(),
                rewritten.Where((l, i) => i % BlockSize == 50).ToArray());
            Assert.Equal(
                lines.Where((l, i) => i % BlockSize >= 51 && i % BlockSize <= 59).ToArray(),
                rewritten.Where((l, i) => i % BlockSize >= 51 && i % BlockSize <= 59).ToArray());
        }

        [Fact]
        public void ARewriteLeavesEveryOtherLineAboutTheSameLotAlone()
        {
            //lot 54 is stated twice in every one of the ten blocks - once as residential and once
            //as community - and only the second of the last block is what the provider reads. The
            //other nineteen are that lot's other, unread, statements, and an edit to any of them
            //would be a change to the neighbourhood that nobody asked for.
            var lines = ShippedChunk();
            var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);

            var rewritten = plan.ApplyTo(lines);

            var others = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!LotZoning.TryParse(lines[i], out var lot, out _)) continue;
                if (lot != 54) continue;
                if (i == plan.Index) continue;
                others++;
                Assert.Same(lines[i], rewritten[i]);
            }
            Assert.Equal(BlockCount * 2 - 1, others);
        }

        [Fact]
        public void ARewriteLeavesTheBlocksBeforeTheLastOneAlone()
        {
            //the provider takes the last line about a lot, so only the last block's copy is
            //worthwriting. Rewriting all ten would be a bigger change than a rezone is; rewriting an
            //earlier one would achieve nothing.
            var lines = ShippedChunk();
            var plan = LotZoning.Rezoned(lines, 70, LotZoning.ZoneCommunity, false);

            var rewritten = plan.ApplyTo(lines);

            Assert.Equal(LotZoning.RezoneDecision.Write, plan.Decision);
            Assert.Equal(LastBlock + 80, plan.Index);
            for (int block = 0; block < BlockCount - 1; block++)
            {
                Assert.Equal("70, residential", rewritten[block * BlockSize + 80]);
            }
            Assert.Equal("70, community", rewritten[plan.Index]);
        }

        [Fact]
        public void ApplyingAPlanToTheWrongLinesIsRefused()
        {
            //the plan names one index; a chunk that is not the one it was made for is a bug in the
            //caller, and silently writing line -1 of it would be a corrupted file rather than a
            //wrong one.
            var plan = LotZoning.Rezoned(ShippedChunk(), 50, LotZoning.ZoneCommunity, false);

            Assert.Throws<ArgumentNullException>(() => plan.ApplyTo(null));
            Assert.Throws<ArgumentOutOfRangeException>(() => plan.ApplyTo(new[] { "50, residential" }));
        }

        // ------------------------------------------------------------- the chunk on disk

        [Fact]
        public void ARewrittenChunkReadsBackWithEveryOtherLineIntact()
        {
            //the file the service actually writes. A real STR chunk, written out and read back the
            //way the next launch will read it: one line changed, the other 999 identical, and the
            //comments Maxis stored beside them (the "Sim Lane" on every one) still there.
            var lines = ShippedChunk();
            var iff = ZoningIff(lines, "Sim Lane");
            Assert.Equal(lines, ReadLines(iff.Get<STR>(1)));

            var plan = LotZoning.Rezoned(lines, 54, LotZoning.ZoneResidential, false);
            SetChangedLines(iff.Get<STR>(1), lines, plan);

            var after = RoundTrip(iff);
            var reloaded = ReadLines(after.Get<STR>(1));

            Assert.Equal(lines.Length, reloaded.Length);
            Assert.Equal(plan.Line, reloaded[plan.Index]);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i != plan.Index) Assert.Equal(lines[i], reloaded[i]);
            }
            //and the reloaded file says what the session was told
            Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(reloaded, 54));
            //including the comment on the line that changed
            Assert.Equal("Sim Lane", after.Get<STR>(1).GetComment(plan.Index));
        }

        [Fact]
        public void EveryLineSurvivesAWriteEvenOnTheOneTheProviderCannotRead()
        {
            //the GUID line is 128 characters of hex with no comma. It is the line a hand-rolled
            //writer would truncate or drop, and it is read by nobody - but it is read by
            //nobody only as long as it is still there, and the provider throws on a file where one
            //of these lines has been mangled into something with a comma in it.
            var lines = ShippedChunk();
            var iff = ZoningIff(lines, "Sim Lane");
            var plan = LotZoning.Rezoned(lines, 93, LotZoning.ZoneResidential, false);

            SetChangedLines(iff.Get<STR>(1), lines, plan);
            var reloaded = ReadLines(RoundTrip(iff).Get<STR>(1));

            for (int block = 0; block < BlockCount; block++)
            {
                Assert.Equal(lines[50], reloaded[block * BlockSize + 50]);
            }
            Assert.Equal(LotZoning.ZoneResidential, LotZoning.EffectiveZone(reloaded, 93));
        }

        [Fact]
        public void ALineWithNoCommentSurvivesTheWriteToo()
        {
            //index 91 of every block - the spaceless lot 90 line - is the one line the shipped file
            //stores without a comment, so the write cannot assume there is one.
            var lines = ShippedChunk();
            var iff = ZoningIff(lines, "Sim Lane");
            var str = iff.Get<STR>(1);
            str.GetStringEntry(LastBlock + 91).Comment = string.Empty;

            var plan = LotZoning.Rezoned(lines, 90, LotZoning.ZoneCommunity, false);
            SetChangedLines(str, lines, plan);

            var after = RoundTrip(iff);
            var reloaded = ReadLines(after.Get<STR>(1));

            Assert.Equal("90, community", reloaded[LastBlock + 91]);
            Assert.Equal(string.Empty, after.Get<STR>(1).GetComment(LastBlock + 91));
            //the block 9 copy of lot 90 in the first copy of the table (index 41) keeps the spaced
            //spelling, because it is not the line the game reads
            Assert.Equal("90, residential", reloaded[LastBlock + 41]);
        }

        /// <summary>
        /// A zoning file as it comes off disk: one STR chunk, id 1, the label the shipped file
        /// carries, and a comment on every line.
        /// </summary>
        /// <remarks>
        /// All twenty language sets are present, empty but not null, because that is what a
        /// decoded chunk holds and <c>STR.Write</c> writes a fixed twenty of them - a chunk with
        /// fewer would come back from the write unreadable.
        /// </remarks>
        private static IffFile ZoningIff(string[] lines, string comment)
        {
            var items = new STRItem[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                items[i] = new STRItem(lines[i])
                {
                    LanguageCode = (byte)STRLangCode.EnglishUS,
                    Comment = comment,
                };
            }

            //marked processed because these are the payloads the game would decode out of raw
            //bytes; there is no stream behind them for IffFile to read them from.
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

        /// <summary>
        /// The write half of the service: only the lines the plan actually changed are handed back
        /// to the chunk, so a line that came back null from the read is never written and no other
        /// line is touched.
        /// </summary>
        private static void SetChangedLines(STR str, string[] lines, LotZoning.RezonePlan plan)
        {
            var rewritten = plan.ApplyTo(lines);
            for (int i = 0; i < rewritten.Length; i++)
            {
                if (!string.Equals(rewritten[i], lines[i], StringComparison.Ordinal))
                {
                    str.SetString(i, rewritten[i]);
                }
            }
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
