using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The career page's data, exercised through <see cref="CareerLadder"/>.
    ///
    /// <para>
    /// The job subpanel's Career button had no click handler anywhere in the tree, so the whole
    /// career track was unreachable: the data was being read, the button was being drawn, and
    /// the page that would have shown a sim's rungs, salary, hours and skill requirements could
    /// not be opened. <c>UICareerAlert</c> is that page, and this is the part of it that can be
    /// wrong without looking wrong.
    /// </para>
    ///
    /// <para>
    /// A <see cref="CARR"/> is an IFF chunk, but the fields the page needs - its name and its
    /// array of <see cref="JobLevel"/> rungs - are public and settable, so the ladder can be
    /// built here without a file, a content pipeline or a graphics device. <see cref="JobLevel"/>
    /// itself can only be constructed by decoding a chunk, so the tests build the rows the other
    /// way round - through the list overload - except for the two places that genuinely need a
    /// <see cref="JobLevel"/>, which are reached by reflection over its public fields.
    /// </para>
    /// </summary>
    public sealed class CareerLadderTests
    {
        /// <summary>
        /// A <see cref="JobLevel"/> built without a decoder: the only constructor takes an
        /// <c>IffFieldEncode</c> and runs it field by field, so the fields are written directly
        /// instead. Public fields, so this is the same object the chunk reader produces.
        /// </summary>
        private static JobLevel Level(string name, int salary, int start, int end, params int[] skillHundreds)
        {
            var type = typeof(JobLevel);
            var level = (JobLevel)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);

            type.GetField("JobName").SetValue(level, name);
            type.GetField("Salary").SetValue(level, salary);
            type.GetField("StartTime").SetValue(level, start);
            type.GetField("EndTime").SetValue(level, end);

            // MinRequired is [friends, cooking, mechanical, charisma, body, logic, creativity,
            // ...]; the skills are stored multiplied by 100.
            var required = new int[10];
            for (int i = 0; i < skillHundreds.Length && i < 6; i++)
            {
                required[i + 1] = skillHundreds[i];
            }
            type.GetField("MinRequired").SetValue(level, required);

            return level;
        }

        private static CARR Job(string name, params JobLevel[] levels)
        {
            var job = new CARR();
            typeof(CARR).GetField("Name").SetValue(job, name);
            typeof(CARR).GetField("JobLevels").SetValue(job, levels);
            return job;
        }

        // ---- the ladder ---------------------------------------------------------------------

        [Fact]
        public void TheWholeTrackIsListedInOrder()
        {
            var job = Job("Athletics",
                Level("Little Leaguer", 50, 9, 15, 100, 200, 300, 400, 500, 600),
                Level("Little League Veteran", 80, 9, 15, 200, 300, 400, 500, 600, 700),
                Level("Jocks", 120, 10, 16, 300, 400, 500, 600, 700, 800));

            var rungs = CareerLadder.Rungs(job, 1);

            Assert.Equal(3, rungs.Count);
            Assert.Equal(new[] { 0, 1, 2 }, rungs.Select(x => x.Index).ToArray());
            Assert.Equal(new[] { "Little Leaguer", "Little League Veteran", "Jocks" },
                rungs.Select(x => x.Name).ToArray());
            Assert.Equal(new[] { 50, 80, 120 }, rungs.Select(x => x.Salary).ToArray());
            Assert.Equal(new[] { 9, 9, 10 }, rungs.Select(x => x.StartTime).ToArray());
            Assert.Equal(new[] { 15, 15, 16 }, rungs.Select(x => x.EndTime).ToArray());
        }

        [Fact]
        public void SkillRequirementsAreUnpackedFromTheStoredHundreds()
        {
            // JobLevel.MinRequired stores a skill multiplied by 100 - the job subpanel divides
            // by 100 to draw its bars - so a page that showed the raw number would read "300"
            // where the player means level three.
            var job = Job("Athletics", Level("Little Leaguer", 50, 9, 15, 100, 200, 300, 400, 500, 600));

            var rungs = CareerLadder.Rungs(job, 0);

            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, rungs[0].RequiredSkills);
        }

        [Fact]
        public void AFriendsRequirementIsNotShownAsASkill()
        {
            // Index 0 of MinRequired is how many friends a rung wants, not a skill. Reading it
            // as cooking would put a nonsense number on the page.
            var level = Level("Nobody", 0, 9, 17);
            typeof(JobLevel).GetField("MinRequired").SetValue(level, new int[] { 5, 100, 0, 0, 0, 0, 0, 0, 0, 0 });

            Assert.Equal(new[] { 1, 0, 0, 0, 0, 0 }, CareerLadder.RequiredSkillsOf(level));
        }

        [Fact]
        public void SixSkillsAreAskedForBecauseTheGameHasSix()
        {
            Assert.Equal(6, CareerLadder.SkillCount);
            Assert.Equal(6, LivePanelStrings.SkillNameFallback.Length);
            Assert.Equal(6, LivePanelStrings.SkillStringIds.Length);
        }

        // ---- degenerate career chunks ---------------------------------------------------------

        [Fact]
        public void NoJobIsAnEmptyTrackRatherThanAnException()
        {
            // The Career button is live at every moment of the game, including the ones where
            // the selected sim is unemployed, so the page has to survive a null chunk.
            Assert.Empty(CareerLadder.Rungs(null, 0));
            Assert.Empty(CareerLadder.Rungs(new CARR(), 0));
        }

        [Fact]
        public void AChunkWithNoRungsIsAnEmptyTrack()
        {
            Assert.Empty(CareerLadder.Rungs(Job("Empty"), 0));
        }

        [Fact]
        public void AChunkWithNoNameSaysSo()
        {
            Assert.Equal(CareerLadder.UnknownCareerFallback, CareerLadder.CareerTitle(new CARR()));
            Assert.Equal(CareerLadder.UnknownCareerFallback, CareerLadder.CareerTitle(null));
            Assert.Equal("Athletics", CareerLadder.CareerTitle(Job("Athletics")));
        }

        [Fact]
        public void AWhitespaceNameSaysSoToo()
        {
            Assert.Equal(CareerLadder.UnknownCareerFallback, CareerLadder.CareerTitle(Job("   ")));
        }

        [Fact]
        public void AChunkWithNoSkillRequirementsReadsAsNoRequirements()
        {
            var level = Level("Nothing Needed", 0, 9, 17);
            typeof(JobLevel).GetField("MinRequired").SetValue(level, new int[] { 0 });

            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, CareerLadder.RequiredSkillsOf(level));
            Assert.Equal(new[] { 0, 0, 0, 0, 0, 0 }, CareerLadder.RequiredSkillsOf(null));
        }

        [Fact]
        public void ALevelIndexPastTheEndOfTheTrackStillListsTheTrack()
        {
            // A stale promotion slot - or one written against a longer table - must not empty
            // the page. Every rung is below the sim, so every rung reads as reached.
            var job = Job("Athletics", Level("A", 50, 9, 15), Level("B", 80, 9, 15));

            var rungs = CareerLadder.Rungs(job, 9);

            Assert.Equal(2, rungs.Count);
            Assert.All(rungs, x => Assert.Equal(CareerRungState.Reached, x.State));
        }

        // ---- where the sim is on the track ------------------------------------------------------

        [Theory]
        [InlineData(0, 0, CareerRungState.Current)]
        [InlineData(0, 1, CareerRungState.Reached)]
        [InlineData(1, 1, CareerRungState.Current)]
        [InlineData(2, 1, CareerRungState.Ahead)]
        public void ARungIsBehindOnOrAheadOfTheSim(int index, int current, CareerRungState expected)
        {
            Assert.Equal(expected, CareerLadder.StateFor(index, current));
        }

        [Fact]
        public void ALevelBelowZeroMarksEveryRungAsAhead()
        {
            // What an unemployed sim's person data looks like, and what the page shows.
            Assert.Equal(CareerRungState.Ahead, CareerLadder.StateFor(0, -1));
        }

        [Fact]
        public void TheCurrentRungIsFoundForThePageToOpenOn()
        {
            var job = Job("Athletics",
                Level("A", 50, 9, 15), Level("B", 80, 9, 15), Level("C", 120, 9, 15));

            var rungs = CareerLadder.Rungs(job, 1);

            Assert.Equal(1, CareerLadder.IndexOfState(rungs, CareerRungState.Current));
            Assert.Equal(0, CareerLadder.IndexOfState(rungs, CareerRungState.Reached));
            Assert.Equal(2, CareerLadder.IndexOfState(rungs, CareerRungState.Ahead));
        }

        [Fact]
        public void ATrackWithNoRungInTheAskedStateReportsNone()
        {
            var job = Job("Athletics", Level("A", 50, 9, 15), Level("B", 80, 9, 15));
            var rungs = CareerLadder.Rungs(job, 5);

            Assert.Equal(-1, CareerLadder.IndexOfState(rungs, CareerRungState.Current));
            Assert.Equal(-1, CareerLadder.IndexOfState(null, CareerRungState.Current));
        }

        // ---- the captions ----------------------------------------------------------------------

        [Theory]
        [InlineData(0, "12am")]
        [InlineData(1, "1am")]
        [InlineData(9, "9am")]
        [InlineData(11, "11am")]
        [InlineData(12, "12pm")]
        [InlineData(13, "1pm")]
        [InlineData(17, "5pm")]
        [InlineData(23, "11pm")]
        [InlineData(24, "12am")]
        public void AShiftHourReadsOnATwelveHourClock(int hour, string expected)
        {
            Assert.Equal(expected, CareerLadder.ShiftTime(hour));
        }

        [Fact]
        public void ANegativeShiftHourReadsAsNothingRatherThanAsAGarbageClock()
        {
            // The two hours the naive hour % 12 gets wrong - midnight and noon - are exactly the
            // ones a night shift hits, which is when a player is most likely to be reading.
            Assert.Equal("12am", CareerLadder.ShiftTime(0));
            Assert.Equal("12pm", CareerLadder.ShiftTime(12));
            Assert.Equal("", CareerLadder.ShiftTime(-1));
        }

        [Fact]
        public void EveryHourOfTheDayHasAMeaningfulCaption()
        {
            // A number from one to twelve and a suffix, for every hour a shift can hold. The
            // naive hour % 12 fails this at 0 ("0am") and at 12 ("0pm").
            for (int hour = 0; hour < 24; hour++)
            {
                var text = CareerLadder.ShiftTime(hour);
                Assert.True(text.EndsWith("am") || text.EndsWith("pm"),
                    "hour " + hour + " reads as \"" + text + "\"");
                if (hour >= 12) Assert.EndsWith("pm", text);
                else Assert.EndsWith("am", text);

                var digits = text.Substring(0, text.Length - 2);
                Assert.InRange(int.Parse(digits), 1, 12);
            }
        }

        [Fact]
        public void AShiftLineNamesBothEnds()
        {
            var rung = new CareerRungInfo() { StartTime = 9, EndTime = 17 };

            Assert.Equal("Hours: 9am - 5pm", CareerLadder.HoursLine(rung));
        }

        [Fact]
        public void ASalaryLineNamesTheNumber()
        {
            var rung = new CareerRungInfo() { Salary = 90 };

            Assert.Equal("Salary: 90", CareerLadder.SalaryLine(rung));
        }

        [Fact]
        public void ASalaryLineDoesNotLoseTheNumberWhenATranslationIsInUse()
        {
            // The English is the fallback a build with no table shows. A language that translated
            // the sentence must still be given the number, or the page shows a salary with no
            // number in it - which is the one thing the page exists to say.
            var rung = new CareerRungInfo() { Salary = 1234 };
            var line = CareerLadder.SalaryLine(rung);

            Assert.Contains("1234", line);
        }

        [Fact]
        public void ANullRungHasNoLines()
        {
            Assert.Equal("", CareerLadder.SalaryLine(null));
            Assert.Equal("", CareerLadder.HoursLine(null));
        }

        [Fact]
        public void ASkillLineNamesTheSkillAndTheNumber()
        {
            var rung = new CareerRungInfo() { RequiredSkills = new[] { 3, 0, 0, 0, 0, 0 } };

            Assert.Equal("Cooking 3", CareerLadder.SkillLine(0, rung));
        }

        [Fact]
        public void ASkillNobodyHasToHaveIsNotWrittenAsARequirementOfZero()
        {
            // "Cooking 0" reads as a requirement of nothing, which is different from "this rung
            // does not ask for cooking" - and the second is the true state for most rungs.
            var rung = new CareerRungInfo() { RequiredSkills = new[] { 0, 0, 0, 0, 0, 0 } };

            Assert.Equal("Cooking -", CareerLadder.SkillLine(0, rung));
        }

        [Fact]
        public void ASkillLineOutOfRangeIsEmpty()
        {
            Assert.Equal("", CareerLadder.SkillLine(-1, new CareerRungInfo()));
            Assert.Equal("", CareerLadder.SkillLine(6, new CareerRungInfo()));
        }

        [Fact]
        public void ASkillLineForARungWithNoRequirementArrayStillReads()
        {
            var rung = new CareerRungInfo() { RequiredSkills = null };

            Assert.Equal("Cooking -", CareerLadder.SkillLine(0, rung));
        }

        [Theory]
        [InlineData(0, "Cooking")]
        [InlineData(1, "Mechanical")]
        [InlineData(2, "Charisma")]
        [InlineData(3, "Body")]
        [InlineData(4, "Logic")]
        [InlineData(5, "Creativity")]
        public void TheSixSkillCaptionsAreTheGamesOwnWordsInBarOrder(int index, string expected)
        {
            // These are TS1's, out of live.dir's table 136, not Simitone's - the job subpanel
            // already showed them in this order and the career page has to agree with the bars
            // beside it, or a player comparing the two sees two different lists.
            Assert.Equal(expected, LivePanelStrings.SkillName(index));
        }

        [Fact]
        public void TheUnemployedWordIsTheGamesOwnToo()
        {
            Assert.Equal("Unemployed", LivePanelStrings.Unemployed());
        }

        [Fact]
        public void AListCaptionNamesTheRungAndWhereItStands()
        {
            var rungs = new List<CareerRungInfo>()
            {
                new CareerRungInfo() { Index = 0, Name = "Little Leaguer", State = CareerRungState.Reached },
                new CareerRungInfo() { Index = 1, Name = "Jocks", State = CareerRungState.Current },
                new CareerRungInfo() { Index = 2, Name = "Superstar", State = CareerRungState.Ahead },
            };

            var lines = CareerLadder.ListCaptions(rungs);

            Assert.Equal(3, lines.Count);
            Assert.Contains("Little Leaguer", lines[0]);
            Assert.Contains("reached", lines[0]);
            Assert.Contains("Jocks", lines[1]);
            Assert.Contains("current", lines[1]);
            Assert.Contains("Superstar", lines[2]);
            Assert.Contains("not yet", lines[2]);
        }

        [Fact]
        public void AListCaptionFallsBackToTheRungIndexWhenItHasNoName()
        {
            // A blank row a player can pick but cannot read is worse than a numbered one.
            var rungs = new List<CareerRungInfo>()
            {
                new CareerRungInfo() { Index = 4, Name = null, State = CareerRungState.Ahead },
            };

            Assert.Contains("4", CareerLadder.ListCaptions(rungs)[0]);
        }

        [Fact]
        public void AnEmptyRungListHasNoListCaptions()
        {
            Assert.Empty(CareerLadder.ListCaptions(null));
            Assert.Empty(CareerLadder.ListCaptions(new List<CareerRungInfo>()));
        }

        [Fact]
        public void TheThreeStatesAreThreeDifferentWords()
        {
            // They are the only thing telling a player which rungs are behind them, so two of
            // them reading the same would make the page say nothing.
            var words = new[]
            {
                CareerLadder.StateCaption(CareerRungState.Reached),
                CareerLadder.StateCaption(CareerRungState.Current),
                CareerLadder.StateCaption(CareerRungState.Ahead)
            };

            Assert.Equal(3, words.Distinct().Count());
        }

        [Fact]
        public void AnUnknownStateReadsAsAheadRatherThanAsNothing()
        {
            Assert.Equal(CareerLadder.StateCaption(CareerRungState.Ahead),
                CareerLadder.StateCaption((CareerRungState)99));
        }

        // ---- the strings themselves ------------------------------------------------------------

        [Fact]
        public void TheCareerPagesWordsComeOutOfSimitonesOwnTable()
        {
            // A test process never builds GameFacade.Strings, so this is the fallback path: the
            // state a stripped install is in, and the state a language whose table has not been
            // translated yet is in. The English is what a player sees in that state, so it is
            // pinned here rather than left to the table's own test.
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Career", SimitoneStrings.Get(UICareerAlert.TitleId, "Career"));
            Assert.Equal("Close", SimitoneStrings.Get(UICareerAlert.CloseCaptionId, "Close"));
            Assert.Equal("current", SimitoneStrings.Get(UICareerAlert.CurrentId, "current"));
            Assert.Equal("reached", SimitoneStrings.Get(UICareerAlert.ReachedId, "reached"));
            Assert.Equal("not yet", SimitoneStrings.Get(UICareerAlert.AheadId, "not yet"));
        }
    }
}
