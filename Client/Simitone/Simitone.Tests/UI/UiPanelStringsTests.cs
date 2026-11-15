/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using Simitone.Client.UI.Panels;
using Simitone.Client.UI.Panels.Desktop;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The end of the "no table, the same words" promise for the panels whose wording was routed
    /// through Simitone's own string table in the second pass over <c>UI/**</c>.
    ///
    /// <para>
    /// <see cref="SimitoneStringsTests"/> checks the mechanism and pins every id to the English
    /// it replaced. That leaves the half nobody can see: whether the call sites and the table
    /// agree. A call site that asked for id 107 and carried the English "Years" as its fallback
    /// would pass every test in that file and leave a language with a translated "Age" in one
    /// place and English in the other, on the same screen.
    /// </para>
    ///
    /// <para>
    /// A test process never builds <c>GameFacade.Strings</c>, so everything asserted here is the
    /// fallback path - the state a stripped install is in, and the state a build whose language
    /// directory has not been translated yet is in. No Content, no VM, no
    /// <c>GraphicsDevice</c>, and no window: the panels below each own a small pure
    /// decision surface, and that is what these reach.
    /// </para>
    ///
    /// <para>
    /// The panels are private, so they are reached by reflection. That is deliberate and
    /// narrow: the alternative is to widen a panel's surface to suit a test, and the assertion
    /// that matters - that these particular words come out of the table - cannot be written
    /// against a caption assigned inside a constructor that needs a graphics device. Each lookup
    /// asserts the member exists, so a rename fails loudly rather than null-referencing.
    /// </para>
    /// </summary>
    public sealed class UiPanelStringsTests
    {
        /// <summary>The shipped English table, parsed the way the game's own loader reads it.</summary>
        private static IReadOnlyDictionary<string, string> Table()
        {
            return SimitoneStrings.ParseTable(
                System.IO.File.ReadAllText(SimitoneStrings.ResolveTablePath(AppContext.BaseDirectory)));
        }

        // ------------------------------------------------------------------ the auto-follow panel

        /// <summary>
        /// <c>UIAutoFollowPanel</c>, which is internal, and its three static helpers.
        /// </summary>
        private static Type AutoFollowPanel()
        {
            var type = typeof(UILotControl).Assembly.GetType(
                "Simitone.Client.UI.Panels.UIAutoFollowPanel");
            Assert.True(type != null, "UILotControl's auto-follow panel has moved or been renamed.");
            return type;
        }

        private static object InvokeStatic(Type owner, string name, params object[] args)
        {
            var method = owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.True(method != null, "UILotControl.UIAutoFollowPanel has no static " + name + " any more.");
            return method.Invoke(null, args);
        }

        private static string Caption(AutoFollowSubject subject, bool lotIsDowntown)
        {
            return (string)InvokeStatic(AutoFollowPanel(), "AutoFollowCaption", subject, lotIsDowntown);
        }

        private static string Status(AutoFollowAction available, AutoFollowReason reason,
            AutoFollowAction pending, bool lotIsDowntown)
        {
            return (string)InvokeStatic(AutoFollowPanel(), "AutoFollowStatus",
                available, reason, pending, lotIsDowntown);
        }

        private static string Direction(AutoFollowAction action)
        {
            return (string)InvokeStatic(AutoFollowPanel(), "DirectionName", action);
        }

        /// <summary>
        /// Every line the panel can put on screen, asserted as the English it showed before any
        /// of it went through a table - and, because a test process has no string tables at all,
        /// as the English it still shows.
        /// </summary>
        [Fact]
        public void TheAutoFollowPanelSaysTheSameWordsWithNoTableAtAll()
        {
            Assert.Null(FSO.Client.GameFacade.Strings);

            // The heading: nobody hovered, the travelling family, and each of the two lots.
            Assert.Equal("Auto-follow", Caption(null, false));
            Assert.Equal("Auto-follow", Caption(AutoFollowSubject.Ordinary, false));
            Assert.Equal("Auto-follow - Downtown", Caption(AutoFollowSubject.Ordinary, true));
            Assert.Equal("Auto-follow - travelling with you", Caption(
                new AutoFollowSubject { IsOnLot = true, InTravellingFamily = true }, false));

            // The one line with two arguments in it, quoted from the direction names.
            Assert.Equal("Already queued: Take Home. Press Invite to Downtown to change it.",
                Status(AutoFollowAction.InviteToDowntown, AutoFollowReason.PendingInOtherDirection,
                    AutoFollowAction.TakeHome, false));
            Assert.Equal("Already queued: Invite to Downtown. Press Take Home to change it.",
                Status(AutoFollowAction.TakeHome, AutoFollowReason.PendingInOtherDirection,
                    AutoFollowAction.InviteToDowntown, true));

            Assert.Equal("Requests are queued both ways - press one to pick.",
                Status(AutoFollowAction.Both, AutoFollowReason.AlreadyPending, AutoFollowAction.Both, false));

            // The one reason per refusal.
            Assert.Equal("That is you - you are already here.",
                Status(AutoFollowAction.None, AutoFollowReason.IsSelf, AutoFollowAction.None, false));
            Assert.Equal("Pets cannot be auto-followed.",
                Status(AutoFollowAction.None, AutoFollowReason.IsPet, AutoFollowAction.None, false));
            Assert.Equal("Ghosts are not travelling.",
                Status(AutoFollowAction.None, AutoFollowReason.IsGhost, AutoFollowAction.None, false));
            Assert.Equal("Only sims on this lot can be followed.",
                Status(AutoFollowAction.None, AutoFollowReason.NotOnLot, AutoFollowAction.None, false));
            Assert.Equal("Already coming along with the family.",
                Status(AutoFollowAction.None, AutoFollowReason.AlreadyTravelling, AutoFollowAction.None, false));
            Assert.Equal("This sim has a request queued both ways.",
                Status(AutoFollowAction.None, AutoFollowReason.AlreadyPending, AutoFollowAction.None, false));
            Assert.Equal("Not available.",
                Status(AutoFollowAction.None, (AutoFollowReason)999, AutoFollowAction.None, false));

            // And the two an available sim gets, one per lot.
            Assert.Equal("They are not in your family.",
                Status(AutoFollowAction.InviteToDowntown, AutoFollowReason.Allowed, AutoFollowAction.None, false));
            Assert.Equal("They are away from home.",
                Status(AutoFollowAction.TakeHome, AutoFollowReason.Allowed, AutoFollowAction.None, true));

            // The two button captions, which the line above quotes back.
            Assert.Equal("Take Home", Direction(AutoFollowAction.TakeHome));
            Assert.Equal("Invite to Downtown", Direction(AutoFollowAction.InviteToDowntown));
        }

        /// <summary>
        /// The direction names are quoted by id rather than written out, so a translation of the
        /// caption and a translation of the status line can still be the same word - which is
        /// the point of 424 and 425 being table entries of their own.
        /// </summary>
        [ContentFact]
        public void TheAutoFollowStatusLineQuotesTheSameWordsTheButtonsAreCaptionedWith()
        {
            var table = Table();
            Assert.Equal(table["424"], Direction(AutoFollowAction.TakeHome));
            Assert.Equal(table["425"], Direction(AutoFollowAction.InviteToDowntown));
        }

        // ------------------------------------------------------------------ the floor readout

        /// <summary>
        /// The two live-mode strips show the same floor side by side and read the same five ids,
        /// so they cannot be given two different words for the same number. This is the assertion
        /// that makes the shared block worth having: a translator who filled 400 to 404 sees the
        /// same answer in both places.
        /// </summary>
        [Fact]
        public void BothLiveModeStripsNameTheSameFiveFloors()
        {
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal(new[] { "1st", "2nd", "3rd", "4th", "5th" }, UIDesktopUCP.FloorNames);
            Assert.Equal(UIDesktopUCP.FloorNames, UIMainPanel.FloorNames);
        }

        [ContentFact]
        public void TheFloorNamesAreTheIdsTheTableCarries()
        {
            var table = Table();
            for (int i = 0; i < UIDesktopUCP.FloorNames.Length; i++)
            {
                var id = (400 + i).ToString();
                Assert.Equal(table[id], UIDesktopUCP.FloorNames[i]);
            }
        }

        // ------------------------------------------------------------------ what stays out

        /// <summary>
        /// The words the table is <em>not</em> allowed to answer, because a translation of them
        /// would be a translation of a different language's game rather than of Simitone's.
        ///
        /// <para>
        /// TS1's own tables carry all of these, and each is read out of the table that came with
        /// the player's copy of the game: the three radio captions, which
        /// <c>UIOptionsSubpanelBase</c> reads from table 145; the six skill names and
        /// "Unemployed", which <c>UIJobSubpanel</c> reads from table 136; the eight motive names
        /// and five personality traits, which <c>UIMotiveSubpanel</c> and
        /// <c>UIPersonalitySubpanel</c> read from tables 130 and 131; and the five personality row
        /// captions, which <c>UISimCASPanel</c> reads from table 130 as well.
        /// </para>
        /// <para>
        /// The loading screen's eight lines are the fourth case, and the interesting one: they
        /// are literals in <c>LoadText</c>, but the screen reads its <c>splashprogress</c> table
        /// first and only falls back to them, so the English is a safety net for a table that has
        /// not been translated rather than Simitone's own text.
        /// </para>
        /// </summary>
        [ContentTheory]
        [InlineData("Low")]
        [InlineData("Medium")]
        [InlineData("High")]
        [InlineData("Cooking")]
        [InlineData("Mechanical")]
        [InlineData("Charisma")]
        [InlineData("Logic")]
        [InlineData("Creativity")]
        [InlineData("Unemployed")]
        [InlineData("Hunger")]
        [InlineData("Comfort")]
        [InlineData("Hygiene")]
        [InlineData("Bladder")]
        [InlineData("Energy")]
        [InlineData("Neat")]
        [InlineData("Outgoing")]
        [InlineData("Active")]
        [InlineData("Playful")]
        [InlineData("Identifying Works...")]
        [InlineData("Reticulating Splines...")]
        [InlineData("Building Concert Hall...")]
        public void TheGamesOwnWordsAreNotDuplicatedIntoSimitonesTable(string word)
        {
            foreach (var pair in Table())
            {
                Assert.NotEqual(word, pair.Value);
            }
        }

        /// <summary>
        /// The one that is a property of the game rather than of a language: the CAS panel's
        /// "Skin Color" is Simitone's, but the codes it selects bodies with are not, and a
        /// translator who "fixed" one of them would break the body carousel rather than rename
        /// it.
        /// </summary>
        [ContentTheory]
        [InlineData("lgt")]
        [InlineData("med")]
        [InlineData("drk")]
        [InlineData("huao")]
        [InlineData("huap")]
        [InlineData("huac")]
        public void TheGamesOwnIdentifiersAreNotTableEntries(string code)
        {
            foreach (var pair in Table())
            {
                Assert.NotEqual(code, pair.Value);
            }
        }
    }
}
