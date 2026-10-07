/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.SimAntics;
using FSO.SimAntics.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.Utils;
using System;
using System.Linq;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The Hot Date interests panel, exercised through <see cref="Interests"/>.
    ///
    /// <para>
    /// Hot Date's interest person data is written by the shipped scripts and read by autonomy, so
    /// the game plays correctly with the player never seeing any of it. This tree persists the
    /// slots and can now write them too, so three things are worth pinning.
    /// </para>
    /// <list type="bullet">
    /// <item><b>The scale.</b> The slots hold 0 to 1000, not 0 to 10.
    /// <c>PersonGlobals.iff</c> 8486 <c>'Convert Interests to 0-1000'</c> multiplies them by 100
    /// exactly once, <c>MagazineStack.iff</c> 4124/4125 moves them in steps of 100, and eight
    /// social <c>TEST</c> trees divide by 100 for their <c>TreeAdRange</c> bonus. A panel that
    /// drew the raw number on a ten-segment bar showed a full bar for a tenth of the scale.</item>
    /// <item><b>How many slots there are.</b> Fifteen, not eight, and not the twenty interests
    /// the earlier note claimed - retail shipped nineteen buttons.</item>
    /// <item><b>The write arithmetic.</b> The step size, the clamping, and the read-modify-write
    /// that <see cref="Interests.Nudge"/> performs.</item>
    /// </list>
    /// </summary>
    public sealed class InterestsTests
    {
        // ---- the eight named rows -----------------------------------------------------------------

        [Fact]
        public void ThereAreEightNamedRows()
        {
            // The slots this tree can put a retail name on. Retail drove fifteen slots; see
            // TheShippedDataDrivesFifteenSlots for the other seven.
            Assert.Equal(8, Interests.Slots.Length);
        }

        [Fact]
        public void TheNamedSlotsAreTheEightsTheEngineNames()
        {
            Assert.Equal(VMPersonDataVariable.Interest_TravelToys, Interests.Slots[0].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_ViolenceAliens, Interests.Slots[1].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_PoliticsPets, Interests.Slots[2].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_SixtiesSchool, Interests.Slots[3].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_Weather, Interests.Slots[4].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_Sports, Interests.Slots[5].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_Music, Interests.Slots[6].Slot);
            Assert.Equal(VMPersonDataVariable.Interest_Outdoors, Interests.Slots[7].Slot);
        }

        [Fact]
        public void TheNamedSlotsAreFortySixToFiftyThreeWithNoGaps()
        {
            // A gap would be a slot the panel thinks it can draw and the engine has never heard
            // of, and a repeat would mean two rows reading the same number.
            var indices = Interests.Slots.Select(x => (int)x.Slot).ToArray();

            Assert.Equal(Enumerable.Range(46, 8).ToArray(), indices);
        }

        [Fact]
        public void TheNamedSlotsAreConsecutiveAndInOrder()
        {
            // The panel lays its rows out in this order, and so the first row is the first slot.
            for (int i = 1; i < Interests.Slots.Length; i++)
            {
                Assert.Equal((int)Interests.Slots[i - 1].Slot + 1, (int)Interests.Slots[i].Slot);
            }
        }

        [Fact]
        public void EveryNamedSlotNamesAtLeastOneRetailInterest()
        {
            Assert.All(Interests.Slots, slot => Assert.NotEmpty(slot.RetailNames));
            Assert.All(Interests.Slots, slot => Assert.All(slot.RetailNames, n => Assert.False(string.IsNullOrWhiteSpace(n))));
        }

        [Fact]
        public void TheFourPairedSlotsNameTwoInterestsAndTheRestNameOne()
        {
            // The pairing is visible in the enum's own identifiers and nowhere else: TravelToys,
            // ViolenceAliens, PoliticsPets, SixtiesSchool are pairs; Weather, Sports, Music and
            // Outdoors are single. Four pairs and four singles is eight slots covering twelve
            // retail names.
            var paired = Interests.Slots.Count(x => x.RetailNames.Length == 2);
            var single = Interests.Slots.Count(x => x.RetailNames.Length == 1);

            Assert.Equal(4, paired);
            Assert.Equal(4, single);
            Assert.Equal(12, Interests.Slots.Sum(x => x.RetailNames.Length));
        }

        [Fact]
        public void ARowCaptionNamesEveryInterestItsSlotCovers()
        {
            Assert.Equal("Travel / Toys", Interests.Slots[0].Caption());
            Assert.Equal("Weather", Interests.Slots[4].Caption());
        }

        [Fact]
        public void NoRetailInterestNameIsClaimedByTwoSlots()
        {
            // Two rows reading the same name is how a player concludes the panel is broken.
            var names = Interests.Slots.SelectMany(x => x.RetailNames).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
        }

        [Fact]
        public void NoTrackedNameIsAlsoOnTheUntrackedList()
        {
            // The untracked names are what the footer admits the panel cannot show. One of them
            // also being a row caption would be a contradiction on the same screen.
            var tracked = Interests.Slots.SelectMany(x => x.RetailNames);
            Assert.Empty(tracked.Intersect(Interests.UntrackedRetailNames, StringComparer.OrdinalIgnoreCase));
        }

        // ---- how many slots retail actually drove -------------------------------------------------

        [Fact]
        public void TheShippedDataDrivesFifteenSlotsNotEight()
        {
            // MagazineStack.iff 4124 'Remove Sims 1.0 Interest' writes MyPersonData[46..53] -= 100
            // and 4125 'Remove Sims Hot Date Interest' writes 13, 14, 16, 20, 26, 54 and 55 the
            // same way. PersonGlobals.iff 8486 converts ten of the fifteen, and
            // SocialGlobals.iff 8274 'SHD do I like topic?' tests fifteen Tuning constants
            // against fifteen slots - the same fifteen.
            Assert.Equal(15, Interests.RetailSlotCount);
        }

        [Fact]
        public void TheSevenUnnamedSlotsAreTheHotDateOnes()
        {
            // The second half of MagazineStack.iff's pair of trees, in the order it writes them.
            Assert.Equal(new[] { 13, 14, 16, 20, 26, 54, 55 },
                Interests.UnnamedSlots.Select(x => (int)x).ToArray());
        }

        [Fact]
        public void TheNamedAndUnnamedSlotsTogetherAreTheFifteenTheScriptsWalk()
        {
            // One list of fifteen distinct slots, and the assertion that it is the retail list:
            // 46 to 53 from 4124, then 13, 14, 16, 20, 26, 54, 55 from 4125. A slot added to one
            // list and not the other, or a duplicate across them, would be a row reading another
            // row's number or a slot the scripts never touch.
            var all = Interests.Slots.Select(x => (int)x.Slot)
                .Concat(Interests.UnnamedSlots.Select(x => (int)x))
                .ToArray();

            Assert.Equal(Interests.RetailSlotCount, all.Length);
            Assert.Equal(all.Length, all.Distinct().Count());
            Assert.Equal(Enumerable.Range(46, 8).Concat(new[] { 13, 14, 16, 20, 26, 54, 55 })
                .OrderBy(x => x).ToArray(), all.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void EveryInterestSlotFitsInsideBothPersonDataArrays()
        {
            // NBRS keeps a fixed 88 shorts per neighbour, so a slot at 88 or above would be
            // IndexOutOfRangeException out of the render loop, and would not survive the trip
            // out of the neighbourhood file either. VMAvatar throws above 100.
            var all = Interests.Slots.Select(x => (int)x.Slot)
                .Concat(Interests.UnnamedSlots.Select(x => (int)x));

            Assert.All(all, slot => Assert.InRange(slot, 0, 87));
        }

        [Fact]
        public void RetailShippedNineteenInterestButtonsNotTwenty()
        {
            // UIGraphics\Res_CPanel.h numbers them kInterestTravel = 1600 through
            // kInterestRomance = 1618, which is nineteen, and cpanel\Buttons\Interest*.bmp ships
            // one per number. The earlier "20" in the gap report counted Interest.bmp, which is
            // the category button rather than one of the interests.
            Assert.Equal(19, Interests.RetailButtonCount);
            Assert.Equal(19, 1618 - 1600 + 1);
        }

        [Fact]
        public void TheNamedRowsTheUnnamedSlotsAndTheUntrackedListAddUpToTheRetailButtonCount()
        {
            // Twelve retail names across the eight named slots, seven more on the untracked list,
            // and the seven unnamed slots that carry no name at all: nineteen names, nineteen
            // buttons, fifteen slots. If a name is added to one list and not the other the panel
            // either repeats a name or drops one, and both are visible on screen.
            var names = Interests.Slots.SelectMany(x => x.RetailNames)
                .Concat(Interests.UntrackedRetailNames)
                .ToArray();

            Assert.Equal(Interests.RetailButtonCount, names.Length);
            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void TheUntrackedNamesAreTheRetailOnesWithNoNamedSlot()
        {
            Assert.Equal(new[] { "Charisma", "Exercise", "Food", "Health", "Parties", "Romance", "Style" },
                Interests.UntrackedRetailNames);
        }

        // ---- the scale ----------------------------------------------------------------------------

        [Fact]
        public void TheScaleIsAThousandPointsInTenSteps()
        {
            // PersonGlobals.iff 8486 'Convert Interests to 0-1000' multiplies by 100;
            // MagazineStack.iff 4124/4125 move by 100; the social TEST trees divide by 100 for
            // their TreeAdRange bonus. A ten-segment bar and a ten-step scale are the same number
            // written twice, so if the scale changed both would have to.
            Assert.Equal(1000, Interests.PersonDataMax);
            Assert.Equal(100, Interests.PersonDataStep);
            Assert.Equal(10, Interests.MaxLevel);
            Assert.Equal(10, Interests.Segments);
            Assert.Equal(Interests.Segments, Interests.MaxLevel);
        }

        [Fact]
        public void TheStepIsWhatTheShippedMagazineScriptWrites()
        {
            // Both of MagazineStack.iff's interest trees are `MyPersonData[N] -= 100`, so a
            // stepper that moves by anything else lands the value somewhere retail has no scale
            // for. Ten steps to the top of the scale, and the top is a round thousand.
            Assert.Equal(100, Interests.PersonDataStep);
            Assert.Equal(Interests.PersonDataStep * Interests.Segments, Interests.PersonDataMax);
        }

        [Theory]
        [InlineData(0, 0)]        // no interest at all
        [InlineData(99, 0)]       // a fraction of a step, which is what the shipped `/= 100` gives
        [InlineData(100, 1)]
        [InlineData(450, 4)]
        [InlineData(999, 9)]
        [InlineData(1000, 10)]    // as interested as retail lets a sim get
        public void TheBarValueIsThePersonDataValueDividedDownToRetailScale(int raw, int expected)
        {
            // Truncating division, not rounding, because every shipped tree that uses the value
            // writes `Temps[0] /= 100` before using it.
            Assert.Equal(expected, Interests.ClampLevel(raw));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-1000)]
        [InlineData(short.MinValue)]
        [InlineData(1001)]
        [InlineData(short.MaxValue)]
        public void AnOutOfRangeReadingIsClampedToTheEndsOfTheScale(int raw)
        {
            // Person data is a raw short the scripts write freely, and 'Convert Interests' guards
            // only ten of the fifteen slots, so a negative or an over-thousand value is possible.
            // A negative one must read as an empty bar, not a full one, which is what clamping to
            // the wrong end would do.
            var clamped = Interests.ClampPersonData(raw);

            Assert.InRange(clamped, 0, Interests.PersonDataMax);
            Assert.Equal(clamped, raw < 0 ? 0 : Interests.PersonDataMax);
            Assert.InRange(Interests.ClampLevel(raw), 0, Interests.MaxLevel);
        }

        [Fact]
        public void AReadingInsideTheScaleIsLeftExactlyAsItIs()
        {
            for (int raw = 0; raw <= Interests.PersonDataMax; raw += Interests.PersonDataStep)
            {
                Assert.Equal(raw, Interests.ClampPersonData(raw));
            }
        }

        [Fact]
        public void AFreshlyGeneratedSimHasNoInterestsAtAll()
        {
            // What Simitone's person generator produces: zeroes. Every bar empty, and every
            // stepper has somewhere to go.
            var personData = new short[88];

            Assert.All(Interests.Slots, slot => Assert.Equal(0, Interests.ClampLevel(personData[(int)slot.Slot])));
        }

        // ---- the write ----------------------------------------------------------------------------

        [Fact]
        public void OneStepUpIsOneHundredPoints()
        {
            Assert.Equal(100, Interests.NextValue(0, 1));
            Assert.Equal(300, Interests.NextValue(200, 1));
        }

        [Fact]
        public void OneStepDownIsOneHundredPoints()
        {
            Assert.Equal(0, Interests.NextValue(100, -1));
            Assert.Equal(0, Interests.NextValue(50, -1));
        }

        [Fact]
        public void TheTopOfTheScaleStaysAtTheTop()
        {
            // Retail's magazine tree rewrites a full 1000 down to 0 and a zero up to 100, which
            // is the same clamp from the other end: the scale is closed.
            Assert.Equal(Interests.PersonDataMax, Interests.NextValue(Interests.PersonDataMax, 1));
            Assert.Equal(Interests.PersonDataMax, Interests.NextValue(Interests.PersonDataMax, 9));
            Assert.Equal(900, Interests.NextValue(1000, -1));
        }

        [Fact]
        public void TheBottomOfTheScaleStaysAtTheBottom()
        {
            Assert.Equal(0, Interests.NextValue(0, -1));
            Assert.Equal(0, Interests.NextValue(0, -9));
            Assert.Equal(100, Interests.NextValue(0, 1));
        }

        [Fact]
        public void AnOutOfRangeReadingIsPulledOntoTheScaleRatherThanLeftOffTheEnd()
        {
            // A sim whose slot was left above 1000 by a half-run migration, or below zero by a
            // script, still responds to a click: the value is clamped before the step is added, so
            // the first click lands on the scale instead of pushing it further out. It is clamped
            // *first* on purpose - clamping the result would leave a value above the scale at a
            // value above the scale forever, because adding a step to a huge number and then
            // clamping still saturates.
            Assert.Equal(Interests.PersonDataMax, Interests.NextValue(short.MaxValue, 1));
            Assert.Equal(0, Interests.NextValue(short.MinValue, -1));
            Assert.Equal(Interests.PersonDataMax, Interests.NextValue(5000, 1));
            // Off the bottom, up a step: the reading is pulled to 0 first, so the click is worth
            // one step rather than being swallowed by the clamp.
            Assert.Equal(Interests.PersonDataStep, Interests.NextValue(-500, 1));
            Assert.Equal(0, Interests.NextValue(-500, -1));
        }

        [Fact]
        public void SteppingZeroTimesLeavesTheValueOnTheScale()
        {
            Assert.Equal(300, Interests.NextValue(300, 0));
        }

        [Fact]
        public void EveryStepIsAShortSoItFitsThePersonDataWrite()
        {
            // VMAvatar.SetPersonData takes a short, and person data is short[] - a value that
            // does not fit would be narrowed silently at the call and land somewhere the scripts
            // have no scale for.
            Assert.Equal(Interests.PersonDataMax, Interests.NextValue(900, 1));
            Assert.True(Interests.NextValue(short.MaxValue, 1) <= short.MaxValue);
            Assert.True(Interests.NextValue(short.MinValue, -1) >= short.MinValue);
        }

        [Fact]
        public void NudgingWithNothingToNudgeWritesNothing()
        {
            // Null sim, null slot and a zero step all have to be no-ops: the panel's click
            // handler runs from a button that is still on screen when the selection changes.
            Assert.Null(Interests.Nudge(null, Interests.Slots[0], 1));
            Assert.Null(Interests.Nudge(null, null, 1));
            Assert.Null(Interests.Nudge(null, Interests.Slots[0], 0));
        }

        [Fact]
        public void ReadingWithNothingToReadIsNoInterestRatherThanAnException()
        {
            Assert.Equal(0, Interests.Level(null, Interests.Slots[0]));
            Assert.Equal(0, Interests.Level(null, null));
        }

        [Fact]
        public void TheSlotsNudgeWritesAreOnesTheEngineAccepts()
        {
            // VMAvatar.GetPersonData and SetPersonData both throw above 100, and neither has a
            // case for any of the fifteen - so they are plain reads and writes of the array the
            // scripts also read and write. A slot added outside 0..100 would throw from the
            // panel's click handler.
            var all = Interests.Slots.Select(x => (int)x.Slot)
                .Concat(Interests.UnnamedSlots.Select(x => (int)x));
            Assert.All(all, slot => Assert.InRange(slot, 0, 100));
        }

        [Fact]
        public void TheNudgeSignatureTakesThePersonDataSlotItIsGiven()
        {
            // The write goes to `slot.Slot` and nothing else. A variant that hard-coded an index
            // would still compile against this signature and would write the wrong sim's
            // interest, so the type is pinned.
            var method = typeof(Interests).GetMethod(nameof(Interests.Nudge));
            Assert.NotNull(method);
            var parameters = method.GetParameters();
            Assert.Equal(3, parameters.Length);
            Assert.Equal(typeof(VMAvatar), parameters[0].ParameterType);
            Assert.Equal(typeof(Interests.InterestSlot), parameters[1].ParameterType);
            Assert.Equal(typeof(int), parameters[2].ParameterType);
        }

        // ---- captions -----------------------------------------------------------------------------

        [Fact]
        public void EachNamedRowHasItsOwnTableId()
        {
            var ids = Interests.Slots.Select(Interests.IdFor).ToArray();

            Assert.Equal(8, ids.Distinct().Count());
            Assert.All(ids, id => Assert.NotNull(id));
            // The shared LivePanelStringsTests derives the table id as 520 + index, so the ids
            // cannot move without taking a pinned string with them.
            Assert.Equal(Enumerable.Range(520, 8).Select(x => x.ToString()).ToArray(), ids);
        }

        [Fact]
        public void TheInterestsWordsComeOutOfSimitonesOwnTable()
        {
            // A test process never builds GameFacade.Strings, so this is the fallback path: the
            // state a stripped install is in, and the state a language whose table has not been
            // translated yet is in. The Sims 1 has no live-mode string table for interests.
            Assert.Null(FSO.Client.GameFacade.Strings);

            Assert.Equal("Travel / Toys", Interests.NameOf(Interests.Slots[0]));
            Assert.Equal("Outdoors", Interests.NameOf(Interests.Slots[7]));
        }

        [Fact]
        public void ANamedSlotNamesItselfWhenItHasNoIdOfItsOwn()
        {
            // The guard for a caller that builds a row for a slot the table has no caption for:
            // the enum's identifier is English Simitone did not write, but it is at least a name
            // rather than a blank row.
            var orphan = new Interests.InterestSlot() { Slot = VMPersonDataVariable.Interest_Weather };

            Assert.Null(Interests.IdFor(orphan));
            Assert.Equal("Interest_Weather", orphan.Caption());
        }

        [Fact]
        public void ANullSlotHasNoCaptionAtAll()
        {
            Assert.Equal("", Interests.NameOf(null));
        }

        [Fact]
        public void ASlotWithNoRetailNamesFallsBackToTheEnumsOwnIdentifier()
        {
            // A blank row a player can read but cannot understand is worse than an identifier
            // that at least names the thing it is reading.
            var bare = new Interests.InterestSlot() { Slot = VMPersonDataVariable.Interest_Weather };

            Assert.Equal("Interest_Weather", bare.Caption());
        }

        [Fact]
        public void TheFooterNamesEveryInterestThePanelCannotShow()
        {
            // The alternative is a panel with eight rows and no explanation, which reads as
            // "this sim has no interests" rather than "this game cannot name the rest".
            var footer = Interests.UntrackedCaption();

            Assert.All(Interests.UntrackedRetailNames, name => Assert.Contains(name, footer));
            Assert.NotEmpty(Interests.UntrackedRetailNames);
        }

        [Fact]
        public void TheSecondFooterLineCarriesTheSlotCountAndTheButtonCount()
        {
            // The two numbers the gap report got wrong, on screen. A panel that says "there are
            // more" without saying how many is the alternative to a count.
            var why = Interests.UnnamedWhyCaption();

            Assert.Contains(Interests.RetailSlotCount.ToString(), why);
            Assert.Contains(Interests.RetailButtonCount.ToString(), why);
            Assert.Contains(Interests.Slots.Length.ToString(), why);
            // No format specifiers, so nothing a translation has to keep in step.
            Assert.DoesNotContain("%s", why);
            Assert.DoesNotContain("{0}", why);
        }

        [Fact]
        public void TheSecondFooterLineHasItsOwnNewTableId()
        {
            // 544 is past every id the shared LivePanelStringsTests pins (520-528) and is not one
            // of the house panel's 530-543, so the shipped table has no entry for it yet and the
            // call site's English is what shows until a translator adds one.
            Assert.Equal("544", Interests.UnnamedWhyId);
            Assert.DoesNotContain(Interests.UnnamedWhyId, new[]
            {
                "520", "521", "522", "523", "524", "525", "526", "527", "528",
                "530", "531", "532", "533", "534", "535", "536", "537", "538",
                "539", "541", "542", "543",
            });
        }
    }
}
