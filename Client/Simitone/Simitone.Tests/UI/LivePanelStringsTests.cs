/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The words the three new live-mode panels put on screen, checked against the shipped
    /// string table.
    ///
    /// <para>
    /// <see cref="SimitoneStringsTests"/> checks the mechanism and pins every id to the English
    /// it replaced. That leaves the half nobody can see: whether the call sites and the table
    /// agree. A call site that asked for id 507 and carried the English "Insider" as its
    /// fallback would pass every test in that file and leave a language with a translated "Sell
    /// Out" in one place and English in the other, on the same screen.
    /// </para>
    ///
    /// <para>
    /// A test process never builds <c>GameFacade.Strings</c>, so the fallback path is all that
    /// can be reached - which is also the state a stripped install is in, and the state a build
    /// whose language directory has not been translated yet is in. No Content, no VM, no
    /// <c>GraphicsDevice</c>, and no window.
    /// </para>
    /// </summary>
    public sealed class LivePanelStringsTests
    {
        /// <summary>The shipped English table, parsed the way the game's own loader reads it.</summary>
        private static IReadOnlyDictionary<string, string> Table()
        {
            return SimitoneStrings.ParseTable(
                File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory)));
        }

        /// <summary>
        /// Every id the three new panels read, with the English each call site carries as its
        /// fallback. Read from the call sites' own constants, so a renumbering shows up as a
        /// mismatch rather than as a silently untranslated screen.
        /// </summary>
        public static IEnumerable<object[]> NewPanelStrings()
        {
            // the career page
            yield return new object[] { UICareerAlert.TitleId, "Career" };
            yield return new object[] { UICareerAlert.CloseCaptionId, "Close" };
            yield return new object[] { UICareerAlert.NoCareerId, "No career" };
            yield return new object[] { UICareerAlert.NoCareerWhyId,
                "This sim does not have a job, so there is no career track to show." };
            yield return new object[] { UICareerAlert.SalaryId, "Salary: %s" };
            yield return new object[] { UICareerAlert.HoursId, "Hours: %s - %s" };
            yield return new object[] { UICareerAlert.SkillsId, "Skills needed" };
            yield return new object[] { UICareerAlert.CurrentId, "current" };
            yield return new object[] { UICareerAlert.ReachedId, "reached" };
            yield return new object[] { UICareerAlert.AheadId, "not yet" };

            // the fame panel's readouts
            yield return new object[] { FameMeter.NobodyId, "Nobody" };
            yield return new object[] { FameMeter.StarsId, "%s stars" };
            yield return new object[] { FameMeter.PointsId, "Fame points: %s" };
            yield return new object[] { FameMeter.NextRungId, "%s to go for %s" };
            yield return new object[] { FameMeter.BestId, "Career best: %s" };
            yield return new object[] { FameMeter.ToGoId,
                "Top of the ladder - %s points to stay there" };

            // the eleven fame rungs, in Star Power order
            for (short rung = 0; rung < FameMeter.RungNameFallback.Length; rung++)
            {
                yield return new object[] { (506 + rung).ToString(), FameMeter.RungNameFallback[rung] };
            }

            // the interests panel's rows, in slot order
            for (int i = 0; i < Interests.Slots.Length; i++)
            {
                yield return new object[] { (520 + i).ToString(), Interests.Slots[i].Caption() };
            }
            yield return new object[] { "528", "Not tracked by this game: %s" };

            // the house panel
            yield return new object[] { HouseServices.ChoresTitleId, "Chores" };
            yield return new object[] { HouseServices.ChoresBodyId,
                "Chores are assigned by the family, not by the game: the household's chore list lives in the lot's own data, and nothing here can read it." };
            yield return new object[] { HouseServices.HireTitleId, "Hire a Service" };
            yield return new object[] { HouseServices.HireBodyId,
                "Hiring is the household's own business: ring the service, or use the service's object." };
            for (int i = 0; i < HouseServices.NameFallback.Length; i++)
            {
                yield return new object[] { HouseServices.NameId[i], HouseServices.NameFallback[i] };
            }
            yield return new object[] { HouseServices.AvailableId, "Can be hired" };
            yield return new object[] { HouseServices.TickerId, "Next chance in %s day(s)" };

            // the inventory panel's MagiCoin readout
            yield return new object[] { MagiCoinFormat.CaptionId, "MagiCoins: %s" };
        }

        [Theory]
        [MemberData(nameof(NewPanelStrings))]
        public void TheTableCarriesTheEnglishEachCallSiteFallsBackTo(string id, string english)
        {
            // A build with no table at all shows the call site's English, which is what these
            // strings are. A build with the table shows the table's, and a translator who only
            // filled some of it would otherwise see two different words for the same label with
            // nothing to tell them apart.
            var table = Table();

            Assert.True(table.ContainsKey(id), "Simitone's table has no entry " + id + ".");
            Assert.Equal(english, table[id]);
        }

        [Fact]
        public void EveryIdTheNewPanelsReadIsInTheTable()
        {
            // The rows above check the values; this checks there are no gaps - an id the call
            // site reads that the table does not have would fall back silently, which is the one
            // failure mode a value assertion cannot see.
            var table = Table();
            foreach (var row in NewPanelStrings())
            {
                Assert.True(table.ContainsKey((string)row[0]), "missing id " + row[0]);
            }
        }

        [Fact]
        public void TheNewPanelsDoNotDuplicateTheGamesOwnWordsIntoSimitonesTable()
        {
            // The same rule UiPanelStringsTests applies to the rest of the table: the words TS1
            // ships translations for stay in TS1's own tables, and Simitone's table is only for
            // the words Simitone added.
            var table = Table();
            foreach (var word in new[] { "Cooking", "Mechanical", "Charisma", "Logic", "Creativity", "Unemployed" })
            {
                Assert.DoesNotContain(table.Values, v => v == word);
            }
        }

        // ---- the live-mode category switcher --------------------------------------------------------

        /// <summary>
        /// The switcher's live category list. Read straight off the panel rather than by
        /// reflection: a test cannot construct a <c>UIMainPanel</c> without a content pipeline
        /// and a graphics device, so the list is a public table precisely so this can see it.
        /// </summary>
        private static List<int> LiveCategoryIds()
        {
            return UIMainPanel.LiveCategories.Select(x => x.ID).ToList();
        }

        [Fact]
        public void TheLiveModeStripOffersEightCategories()
        {
            // Five before, eight now: the House, Fame and Interests panels were added. The count
            // is pinned because the switcher lays its buttons out from the list and a category
            // added to the list without a case in the switch renders as an empty strip.
            Assert.Equal(8, UIMainPanel.LiveCategoryInterests + 1);
            Assert.Equal(8, LiveCategoryIds().Count);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void EveryLiveModeCategoryIsARealIdInTheSwitcher(int id)
        {
            Assert.Contains(id, LiveCategoryIds());
        }

        [Fact]
        public void TheLiveModeCategoryIdsAreContiguousFromZero()
        {
            // The switcher selects by id and the routing switches on it, so a gap or a duplicate
            // is a category a player can select and get nothing for.
            var ids = LiveCategoryIds();
            Assert.Equal(Enumerable.Range(0, ids.Count).ToArray(), ids.ToArray());
        }

        [Fact]
        public void TheThreeNewCategoriesAreTheOnesTheRouteNames()
        {
            // The routing switch and the constants that name it have to agree, and only the
            // constants are visible from here - a case labelled 5 that the switcher never
            // selects is a dead route that still compiles.
            Assert.Equal(5, UIMainPanel.LiveCategoryHouse);
            Assert.Equal(6, UIMainPanel.LiveCategoryFame);
            Assert.Equal(7, UIMainPanel.LiveCategoryInterests);
        }

        [Fact]
        public void TheThreeNewCategoriesHaveTheirOwnIcons()
        {
            // The switcher loads a category's icon by name out of the shipped UI art, so a
            // missing file is a category that renders blank and a broken launch. The icons are
            // Simitone's own, drawn to match the five that were already there.
            var art = Path.Combine(AppContext.BaseDirectory, "Content", "uigraphics", "live", "modes");
            foreach (var name in new[] { "live_house.png", "live_fame.png", "live_interests.png" })
            {
                Assert.True(File.Exists(Path.Combine(art, name)), name + " is not in the build output.");
            }
        }

        [Fact]
        public void TheVacationSortButtonHasItsOwnArtToo()
        {
            var art = Path.Combine(AppContext.BaseDirectory, "Content", "uigraphics", "live", "inv_vac_btn.png");
            Assert.True(File.Exists(art), "inv_vac_btn.png is not in the build output.");
        }
    }
}
