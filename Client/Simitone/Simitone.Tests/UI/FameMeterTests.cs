using FSO.SimAntics.Utils;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The Superstar fame meter, exercised through <see cref="FameMeter"/>.
    ///
    /// <para>
    /// The fame ladder is complete on the engine side - <see cref="VMTS1FameHelper"/> holds the
    /// ten half-star rungs and the promotion and demotion calls that walk them - but the only
    /// thing in the client that mentioned fame at all was the relationship panel's famous
    /// friends filter, which is a list of <em>other</em> sims. A sim could earn every point the
    /// expansion has to give and never find out what they were worth. This panel is that
    /// readout.
    /// </para>
    ///
    /// <para>
    /// Star Power is counted in <b>half stars</b>, so the arithmetic that goes wrong most easily
    /// here is the rendering of a half: 5 is two and a half stars, not two, and 6 is three, not
    /// two and a bit. That is what most of these are about. Everything here is pure static code.
    /// </para>
    /// </summary>
    public sealed class FameMeterTests
    {
        // ---- stars out of half stars ---------------------------------------------------------

        [Theory]
        [InlineData((short)0, 0, false)]
        [InlineData((short)1, 0, true)]
        [InlineData((short)2, 1, false)]
        [InlineData((short)3, 1, true)]
        [InlineData((short)4, 2, false)]
        [InlineData((short)5, 2, true)]
        [InlineData((short)6, 3, false)]
        [InlineData((short)9, 4, true)]
        [InlineData((short)10, 5, false)]
        public void StarPowerBecomesWholeStarsAndAHalf(short starPower, int whole, bool half)
        {
            Assert.Equal(whole, FameMeter.FilledStars(starPower));
            Assert.Equal(half, FameMeter.HasHalfStar(starPower));
        }

        [Fact]
        public void NoStarPowerEverDrawsMoreThanFiveStars()
        {
            // VMTS1FameHelper clamps to ten half stars, and the retail rating is five stars.
            // A person-data slot the shipped scripts overwrote must not draw a sixth.
            Assert.Equal(FameMeter.StarCount, FameMeter.FilledStars(short.MaxValue));
            Assert.Equal(FameMeter.StarCount, FameMeter.FilledStars(10));
            Assert.False(FameMeter.HasHalfStar(short.MaxValue));
        }

        [Fact]
        public void NegativeStarPowerReadsAsNobody()
        {
            Assert.Equal(0, FameMeter.FilledStars(-4));
            Assert.False(FameMeter.HasHalfStar(-4));
        }

        // ---- the rating in words ---------------------------------------------------------------

        [Theory]
        [InlineData((short)0, "0")]
        [InlineData((short)1, "0.5")]
        [InlineData((short)2, "1")]
        [InlineData((short)3, "1.5")]
        [InlineData((short)9, "4.5")]
        [InlineData((short)10, "5")]
        public void HalfStarsAreWrittenOutRatherThanTruncated(short starPower, string expected)
        {
            // Truncating here would tell a two-and-a-half-star celebrity they are a two-star
            // one, and would make 5 half stars and 4 half stars read identically.
            Assert.Equal(expected, FameMeter.HalfStarsText(starPower));
        }

        [Fact]
        public void NobodyIsNamedRatherThanCountedAsZeroStars()
        {
            // What the retail panel showed at the bottom of the ladder, and the only rung whose
            // name is not one of the fame-track labels.
            Assert.Equal("Nobody", FameMeter.RatingCaption(0));
            Assert.Equal("2.5 stars", FameMeter.RatingCaption(5));
            Assert.Equal("5 stars", FameMeter.RatingCaption(10));
        }

        [Fact]
        public void ARatingAlwaysCarriesTheNumber()
        {
            for (short starPower = 0; starPower <= 10; starPower++)
            {
                var caption = FameMeter.RatingCaption(starPower);
                if (starPower == 0)
                {
                    Assert.Equal("Nobody", caption);
                    continue;
                }
                Assert.Contains(FameMeter.HalfStarsText(starPower), caption);
            }
        }

        // ---- the rung names ----------------------------------------------------------------------

        [Fact]
        public void EveryHalfStarRungHasItsOwnName()
        {
            // The eleven rungs are what make the meter a ladder rather than a number. If two of
            // them shared a name, a player could not tell which half of a star they were on.
            var names = FameMeter.RungNameFallback.ToList();
            Assert.Equal(VMTS1FameHelper.MaxStarPower + 1, names.Count);
            Assert.Equal(names.Count, names.Distinct().Count());
        }

        [Fact]
        public void EachRungNameLinesUpWithTheLadderItNames()
        {
            // VMTS1FameHelper's own comments name each rung; the panel's list is those names in
            // Star Power order. A rung out of step with the ladder it describes is a rung that
            // lies about the sim's standing.
            Assert.Equal("Nobody", FameMeter.RungNameFallback[0]);
            Assert.Equal("Superstar", FameMeter.RungNameFallback[VMTS1FameHelper.MaxStarPower]);
            Assert.Equal("Celebrity", FameMeter.RungNameFallback[9]);
        }

        [Fact]
        public void AClampedStarPowerNamesTheRungItClampsTo()
        {
            Assert.Equal(FameMeter.RungCaption(0), FameMeter.RungCaption(-9));
            Assert.Equal(FameMeter.RungCaption(10), FameMeter.RungCaption(9999));
        }

        // ---- the readouts -------------------------------------------------------------------------

        [Fact]
        public void ThePointTotalIsTheNumberTheScriptsMove()
        {
            Assert.Equal("Fame points: 78", FameMeter.PointsCaption(78));
        }

        [Fact]
        public void TheCareerBestIsTheWatermarkNotTheCurrentRating()
        {
            // The watermark only ever goes up, which is what lets Studio Town keep listing a sim
            // who has since been beaten back down a rung or two. Showing the current rating
            // there would make the "best" line jump about.
            Assert.Equal("Career best: 2.5", FameMeter.BestCaption(5));
            Assert.Equal("Career best: 0", FameMeter.BestCaption(0));
        }

        // ---- the climb to the next rung -------------------------------------------------------------

        [Fact]
        public void TheProgressLineNamesTheNextRungAndHowFarOffItIs()
        {
            // On 2 half stars (1 star, 9 points) with 12 points: the next rung is 21.
            var line = FameMeter.ProgressCaption(12, 2);

            Assert.Contains("9", line);
            Assert.Contains(FameMeter.RungCaption(3), line);
        }

        [Fact]
        public void ASimOnARungItHasAlreadyPaidForIsAtTheBottomOfTheBar()
        {
            // Exactly the threshold: the promotion call has not run yet, so the panel shows the
            // bar empty rather than claiming a promotion that has not happened.
            Assert.Equal(0f, FameMeter.Progress(VMTS1FameHelper.FamePointsFor(2), 2), 4);
        }

        [Fact]
        public void ASimHalfwayToTheNextRungIsHalfWayUpTheBar()
        {
            // 9 points is the bottom of star 1, 21 is the bottom of star 1.5: halfway is 15.
            Assert.Equal(0.5f, FameMeter.Progress(15, 2), 3);
        }

        [Fact]
        public void ASimJustShortOfTheNextRungIsNearlyThere()
        {
            // On 4 half stars (2 stars, 35 points), the next rung is 52: seventeen points away,
            // eight of them spent.
            Assert.Equal(8f / 17f, FameMeter.Progress(43, 4), 3);
        }

        [Fact]
        public void ASimWithMorePointsThanTheNextRungIsNotOverOne()
        {
            // The panel reports the star power the sim is on, not the points they happen to
            // hold. A bar past its end would say the promotion is further along than the game
            // thinks it is.
            var over = FameMeter.Progress(1000, 2);
            Assert.InRange(over, 0f, 1f);
        }

        [Fact]
        public void ASimWithFewerPointsThanItsCurrentRungIsNotBelowZero()
        {
            // The scripts move the point total and the star power separately, and a sim can be
            // demoted on the star power before the points catch up.
            Assert.Equal(0f, FameMeter.Progress(0, 5), 4);
        }

        [Fact]
        public void ASimOnTheTopRungHasNothingLeftToClimb()
        {
            // Progress reads as full, not as a division by the gap above the last rung, which
            // does not exist.
            Assert.Equal(1f, FameMeter.Progress(900, VMTS1FameHelper.MaxStarPower), 4);
            Assert.Equal(1f, FameMeter.Progress(0, VMTS1FameHelper.MaxStarPower), 4);
        }

        [Fact]
        public void AClampedStarPowerStillProducesAProgressLine()
        {
            foreach (short starPower in new short[] { -5, 0, 5, 10, 40 })
            {
                var line = FameMeter.ProgressCaption(100, starPower);
                Assert.False(string.IsNullOrWhiteSpace(line));
                Assert.InRange(FameMeter.Progress(100, starPower), 0f, 1f);
            }
        }

        [Fact]
        public void ATopOfTheLadderSaysSoInsteadOfPromisingANextRung()
        {
            // The top rung is five stars because past 900 points there is nothing left to be
            // promoted into, so a line promising a sixth would be a lie the ladder itself
            // already rules out.
            var line = FameMeter.ProgressCaption(1200, VMTS1FameHelper.MaxStarPower);

            Assert.Contains("1200", line);
            Assert.DoesNotContain(" to go for ", line);
        }

        // ---- the strings themselves -------------------------------------------------------------------

        [Fact]
        public void TheFamePanelsWordsComeOutOfSimitonesOwnTable()
        {
            // A test process never builds GameFacade.Strings, so this is the fallback path: the
            // state a stripped install is in, and the state a language whose table has not been
            // translated yet is in.
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Nobody", SimitoneStrings.Get(FameMeter.NobodyId, "Nobody"));
            Assert.Equal("2.5 stars", SimitoneStrings.Format(FameMeter.StarsId, "%s stars", "2.5"));
            Assert.Equal("Fame points: 12", SimitoneStrings.Format(FameMeter.PointsId, "Fame points: %s", "12"));
            Assert.Equal("Nobody", SimitoneStrings.Get("506", "Nobody"));
            Assert.Equal("Superstar", SimitoneStrings.Get("516", "Superstar"));
        }

        [Fact]
        public void TheElevenRungNamesHaveElevenConsecutiveIds()
        {
            // The rung a sim is on is derived from Star Power, so the id has to be derivable the
            // same way - eleven ids counting up from one base, with nothing else in between.
            for (short starPower = 0; starPower <= VMTS1FameHelper.MaxStarPower; starPower++)
            {
                var id = (506 + starPower).ToString();
                Assert.Equal(FameMeter.RungNameFallback[starPower], SimitoneStrings.Get(id, FameMeter.RungNameFallback[starPower]));
            }
        }
    }
}
