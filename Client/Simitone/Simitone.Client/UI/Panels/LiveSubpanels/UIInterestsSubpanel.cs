/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common.Rendering.Framework.Model;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    /// <summary>
    /// The Hot Date interests panel: how interested the selected sim is in each of the things the
    /// expansion tracks - and a way to change it.
    ///
    /// <para>
    /// <b>Why a panel.</b> Hot Date's interest person data is written by the shipped scripts and
    /// read by autonomy, so the game plays correctly with the player never being able to see it
    /// or touch it. The retail live-mode strip had an Interests category with a button per
    /// interest, and this is the part of it the data supports.
    /// </para>
    ///
    /// <para>
    /// <b>The toggling is a real write, not a mock-up.</b> Each row has a <c>-</c> and a
    /// <c>+</c> that move the interest by one of retail's own steps - a hundred points, the
    /// step <c>MagazineStack.iff</c> 4124 and 4125 use when a sim reads a magazine
    /// (<c>MyPersonData[N] -= 100</c>) - and write it with
    /// <see cref="VMAvatar.SetPersonData"/>. The shipped scripts honour that write immediately,
    /// and in the way the player would expect, because the same slots are what autonomy scores
    /// on: <c>PersonGlobals.iff</c> 8245, 8247, 8363, 8364, 8376, 8379, 8381 and 8443 each end
    /// with <c>TreeAdRange[14] -= 5; Temps[0] = MyPersonData[N]; Temps[0] /= 100;
    /// TreeAdRange[14] += Temps[0]</c>, so a sim's interest in dancing, hugging, worship,
    /// romance and kissing is the interest slot divided by a hundred, added to how attractive the
    /// interaction is. Turn the bar up and the sim starts choosing those interactions on its own.
    /// The write also persists: <see cref="VMAvatar.Save"/> clones the whole 101-short person
    /// array into the lot's <c>VMMarshal</c>, which is what <c>SaveService</c> commits, and
    /// every slot this panel touches is inside the 88 shorts <c>NBRS</c> keeps per neighbour, so
    /// it survives the trip out of the neighbourhood file as well.
    /// </para>
    ///
    /// <para>
    /// <b>The scale, which this panel previously had wrong.</b> Person data 46 to 53 is
    /// <b>0 to 1000</b>, not 0 to 10. <c>PersonGlobals.iff</c> 8486
    /// <c>'Convert Interests to 0-1000'</c> is the proof and the reason: it tests
    /// <c>MyPersonData[46..53] &lt;= 10</c> and, only then, multiplies all ten of its interest
    /// slots by 100 - a one-time migration off the base game's old 0-to-10 scale, with the guard
    /// that stops it running twice. <c>MagazineStack.iff</c> 4124 subtracts 100 from the same
    /// slots and 4130's dialog reads them against 1000. So one bar segment is 100 points, ten
    /// segments is the full 1000, and a larger number means a stronger interest.
    /// </para>
    ///
    /// <para>
    /// <b>Eight rows, and what the other rows would be.</b> The shipped data carries
    /// <b>fifteen</b> interest slots, not eight. <c>MagazineStack.iff</c> 4124
    /// <c>'Remove Sims 1.0 Interest'</c> walks slots 46, 47, 48, 49, 50, 51, 52 and 53 and 4125
    /// <c>'Remove Sims Hot Date Interest'</c> walks 13, 14, 16, 20, 26, 54 and 55, one
    /// <c>-= 100</c> each - fifteen slots, in two groups, named by retail as the original eight
    /// and the seven Hot Date added on top.
    /// <c>PersonGlobals.iff</c> 8486 agrees (it converts ten of them, 46-55 plus 13, 14, 16, 20
    /// and 26) and so does <c>SocialGlobals.iff</c> 8274 <c>'SHD do I like topic?'</c>, whose
    /// fifteen <c>Tuning[3456..3470]</c> comparisons land on exactly those fifteen slots.
    /// </para>
    /// <para>
    /// Retail's panel had one button per interest, and there were <b>nineteen</b> of them, not
    /// twenty: <c>UIGraphics\Res_CPanel.h</c> numbers them <c>kInterestTravel = 1600</c> through
    /// <c>kInterestRomance = 1618</c>, which is nineteen, and
    /// <c>cpanel\Buttons\Interest*.bmp</c> ships nineteen (the "Excercise" misspelling is one of
    /// them). Those nineteen names, and the fifteen slots they are spread across, are the only
    /// place the two can be lined up - and the shipped data does not do it. The engine's enum
    /// gives the eight it names as pairs ("Travel" and "Toys"), which is the whole of the
    /// mapping this tree has; for the other seven, nothing in the data says which retail
    /// interest any of them is, and inventing a caption for a row the player will act on would be
    /// worse than not drawing it. So the eight named slots are the rows, and the footer says how
    /// many there are and why.
    /// </para>
    /// </summary>
    public class UIInterestsSubpanel : UISubpanel
    {
        private const int CellWidth = 330;
        private const int CellHeight = 24;
        private const int Columns = 2;
        private const int FirstX = 18;
        private const int FirstY = 2;
        private const int Rows = 4;

        /// <summary>Where the two stepper buttons sit, relative to a row's left edge.</summary>
        private const int StepperX = 240;
        private const int StepperSize = 20;

        private UISkillDisplay[] bars;
        private UIButton[] lower;
        private UIButton[] raise;
        private UILabel footer;
        private UILabel footerWhy;

        public UIInterestsSubpanel(TS1GameScreen game) : base(game)
        {
            var slots = Interests.Slots;
            bars = new UISkillDisplay[slots.Length];
            lower = new UIButton[slots.Length];
            raise = new UIButton[slots.Length];

            for (int i = 0; i < slots.Length; i++)
            {
                var column = i % Columns;
                var row = i / Columns;
                var x = FirstX + (column * CellWidth);
                var y = FirstY + (row * CellHeight);

                var label = new UILabel()
                {
                    Position = new Vector2(x, y),
                    Size = new Vector2(150, 20),
                    CaptionStyle = TextStyle.DefaultLabel.Clone()
                };
                label.CaptionStyle.Size = 15;
                label.CaptionStyle.Color = UIStyle.Current.Text;
                label.Alignment = TextAlignment.Middle | TextAlignment.Left;
                label.Caption = Interests.NameOf(slots[i]);
                Add(label);

                // A skill bar is exactly a ten-segment value strip, and an interest is on
                // retail's ten-step scale (1000 points, 100 to a segment), so the same control
                // does the drawing. Needed is left at zero so every segment past the sim's level
                // reads as empty rather than as a requirement, which is what the job panel uses
                // it for.
                var bar = new UISkillDisplay { Position = new Vector2(x + 152, y) };
                Add(bar);
                bars[i] = bar;

                // "+" and "-" are the whole caption. A symbol needs no translation, and a word
                // per button would be two more table entries to keep in step with nothing.
                // Copied out of the loop variable: a `for` counter is one variable shared by every
                // lambda, so capturing `i` itself made every click see slots.Length and do nothing.
                var slotIndex = i;
                lower[i] = MakeStepper(x + StepperX, y, "-", () => Nudge(slotIndex, -1));
                raise[i] = MakeStepper(x + StepperX + StepperSize + 2, y, "+", () => Nudge(slotIndex, +1));
            }

            footer = new UILabel()
            {
                Position = new Vector2(FirstX, FirstY + (Rows * CellHeight)),
                Size = new Vector2(2 * CellWidth, 20),
                CaptionStyle = TextStyle.DefaultLabel.Clone()
            };
            footer.CaptionStyle.Size = 12;
            footer.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            footer.Caption = Interests.UntrackedCaption();
            Add(footer);

            footerWhy = new UILabel()
            {
                Position = new Vector2(FirstX, FirstY + (Rows * CellHeight) + 16),
                Size = new Vector2(2 * CellWidth, 34),
                CaptionStyle = TextStyle.DefaultLabel.Clone()
            };
            footerWhy.CaptionStyle.Size = 12;
            footerWhy.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            footerWhy.Wrapped = true;
            footerWhy.MaxLines = 2;
            footerWhy.Alignment = TextAlignment.Top | TextAlignment.Left;
            footerWhy.Caption = Interests.UnnamedWhyCaption();
            Add(footerWhy);
        }

        /// <summary>
        /// Moves one row by <paramref name="steps"/> of retail's scale, clamped to the scale,
        /// and writes it to the selected sim's person data.
        /// </summary>
        /// <remarks>
        /// Read-modify-write on every click rather than a cached level, because the same sim's
        /// interest can be moved by a magazine, a magazine stand or a baby-sim DNA roll between
        /// two clicks, and a panel that believed its own cache would undo one of those. The
        /// write is the same <see cref="VMAvatar.SetPersonData"/> the shipped scripts use, and
        /// the next <see cref="Update"/> reads the value back rather than assuming it took.
        /// </remarks>
        private void Nudge(int index, int steps)
        {
            var sel = Game.SelectedAvatar;
            if (sel == null || index < 0 || index >= Interests.Slots.Length) return;
            Interests.Nudge(sel, Interests.Slots[index], steps);
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            var sel = Game.SelectedAvatar;
            var slots = Interests.Slots;
            for (int i = 0; i < slots.Length; i++)
            {
                // Read even with nothing selected: the two steppers have to grey out rather than
                // keep the last sim's bars on screen, and an engine that is not running has no
                // person data to read at all.
                var level = sel == null ? 0 : Interests.Level(sel, slots[i]);
                bars[i].Value = level;
                var canStep = sel != null;
                lower[i].Disabled = !canStep;
                raise[i].Disabled = !canStep;
            }
        }

        private UIButton MakeStepper(int x, int y, string caption, Action onClick)
        {
            var button = new UIButton()
            {
                Position = new Vector2(x, y),
                Size = new Vector2(StepperSize, 20),
                Caption = caption,
            };
            button.CaptionStyle = TextStyle.DefaultButton.Clone();
            button.CaptionStyle.Size = 13;
            button.CaptionStyle.Color = UIStyle.Current.Text;
            button.OnButtonClick += b => onClick();
            Add(button);
            return button;
        }
    }

    /// <summary>
    /// Which interest is which person-data slot, retail's scale, and the write that moves one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Slot names and the retail interest names they cover are the engine's, taken from
    /// <see cref="VMPersonDataVariable"/> - the pairing of two retail interests to one slot is
    /// visible in the enum's own identifiers and nowhere else in this tree, so re-deriving it
    /// here would be a second place to get wrong. Captions go through Simitone's table because
    /// The Sims 1 has no live-mode string table for them.
    /// </para>
    /// <para>
    /// <b>The scale, and why it is a thousand and not a ten.</b> See the panel's remarks. In one
    /// line: <c>PersonGlobals.iff</c> 8486 <c>'Convert Interests to 0-1000'</c> multiplies these
    /// slots by 100 exactly once, <c>MagazineStack.iff</c> 4124/4125 moves them in steps of 100,
    /// and eight <c>PersonGlobals.iff</c> social <c>TEST</c> trees add <c>slot / 100</c> to the
    /// interaction's <c>TreeAdRange</c>. So <see cref="PersonDataMax"/> is the top of the scale,
    /// <see cref="PersonDataStep"/> is one segment, and a bigger number is a stronger interest.
    /// </para>
    /// </remarks>
    public static class Interests
    {
        /// <summary>First of the eight consecutive ids the eight tracked row captions use.</summary>
        private const int IdBase = 520;

        /// <summary>The footer naming the retail interests this panel cannot show.</summary>
        private const string UntrackedId = "528";

        /// <summary>
        /// The second footer line: the slots that exist but cannot be named. 544 is past every id
        /// the shared <c>LivePanelStringsTests</c> pins (520 to 528) and is not one of the house
        /// panel's 530 to 543, so nothing here can collide with a string already in every
        /// language's table.
        /// </summary>
        public const string UnnamedWhyId = "544";

        /// <summary>
        /// The top of the retail interest scale, in person-data points. This is the number
        /// <c>MagazineStack.iff</c> 4124/4125 compare against (<c>1000 - MyPersonData[N]</c>, and
        /// <c>Local == 1000</c> is rewritten to zero), so it is where "as interested as this gets"
        /// stops.
        /// </summary>
        public const int PersonDataMax = 1000;

        /// <summary>
        /// One of retail's ten steps. <c>MagazineStack.iff</c> 4124 and 4125 both write
        /// <c>MyPersonData[N] -= 100</c> per step, and the eight social <c>TEST</c> trees divide
        /// by 100 for their <c>TreeAdRange</c> bonus, so a step is both the granularity the game
        /// itself uses and one segment of the bar this panel draws.
        /// </summary>
        public const int PersonDataStep = 100;

        /// <summary>How high an interest reads on the bar. Retail's scale has ten steps.</summary>
        public const int MaxLevel = PersonDataMax / PersonDataStep;

        /// <summary>How many segments an interest bar is drawn with.</summary>
        public const int Segments = MaxLevel;

        /// <summary>
        /// How many interest slots the shipped data actually drives, across the two trees that
        /// walk them: <c>MagazineStack.iff</c> 4124 ('Remove Sims 1.0 Interest', slots 46-53) and
        /// 4125 ('Remove Sims Hot Date Interest', slots 13, 14, 16, 20, 26, 54, 55). Not eight.
        /// </summary>
        public const int RetailSlotCount = 15;

        /// <summary>
        /// How many interest buttons retail's panel had: <c>Res_CPanel.h</c> numbers them
        /// <c>kInterestTravel = 1600</c> to <c>kInterestRomance = 1618</c>, and
        /// <c>cpanel\Buttons\Interest*.bmp</c> ships one per number. Nineteen, not twenty.
        /// </summary>
        public const int RetailButtonCount = 19;

        /// <summary>One tracked interest: a person-data slot and the retail names it covers.</summary>
        public class InterestSlot
        {
            /// <summary>The person-data slot the level is stored in.</summary>
            public VMPersonDataVariable Slot;

            /// <summary>
            /// The retail interest names this slot covers, as
            /// <see cref="VMPersonDataVariable"/> names them. One name for the four slots the
            /// enum gave a single name, two for the rest.
            /// </summary>
            public string[] RetailNames;

            /// <summary>The row caption, which is the slot's names joined.</summary>
            public string Caption()
            {
                return RetailNames == null || RetailNames.Length == 0
                    ? Slot.ToString()
                    : string.Join(" / ", RetailNames);
            }
        }

        /// <summary>
        /// The eight interests this tree can name, in person-data order (46 to 53), which is also
        /// the order the panel lays its rows out in.
        /// </summary>
        public static readonly InterestSlot[] Slots = new InterestSlot[]
        {
            Slot(VMPersonDataVariable.Interest_TravelToys, "Travel", "Toys"),
            Slot(VMPersonDataVariable.Interest_ViolenceAliens, "Violence", "Aliens"),
            Slot(VMPersonDataVariable.Interest_PoliticsPets, "Politics", "Pets"),
            Slot(VMPersonDataVariable.Interest_SixtiesSchool, "The 60s", "School"),
            Slot(VMPersonDataVariable.Interest_Weather, "Weather"),
            Slot(VMPersonDataVariable.Interest_Sports, "Sports"),
            Slot(VMPersonDataVariable.Interest_Music, "Music"),
            Slot(VMPersonDataVariable.Interest_Outdoors, "Outdoors"),
        };

        /// <summary>
        /// The other seven slots the retail interest panel also drove, which no shipped string
        /// or enum name identifies: person data 13, 14, 16, 20, 26, 54 and 55, the seven
        /// <c>MyPersonData[N] -= 100</c> writes in <c>MagazineStack.iff</c> 4125
        /// <c>'Remove Sims Hot Date Interest'</c>.
        /// </summary>
        /// <remarks>
        /// The engine's enum does name all seven, but for The Sims Online:
        /// <c>NetworkID</c>, <c>OnlineStatus</c>, <c>CustomOutfitIndex</c>,
        /// <c>AllowedSocialAndPuppeteering</c>, <c>ChatBaloonOn</c> and the two
        /// <c>UnusedAndDoNotUse</c> slots. Retail reused them for interests, and what can be said
        /// about each from the shipped data is thin - slot 55 is the one the romantic socials
        /// score on (<c>PersonGlobals.iff</c> 8364, 8376, 8379, 8381), slot 49 the one the
        /// admiration and worship socials score on (8247, 8363) and slot 52 the one energetic
        /// dancing scores on (8245). Not enough to caption fifteen rows honestly, so the panel
        /// shows the eight it can name and the footer counts the rest.
        /// </remarks>
        public static readonly VMPersonDataVariable[] UnnamedSlots = new VMPersonDataVariable[]
        {
            (VMPersonDataVariable)13,
            (VMPersonDataVariable)14,
            (VMPersonDataVariable)16,
            (VMPersonDataVariable)20,
            (VMPersonDataVariable)26,
            (VMPersonDataVariable)54,
            (VMPersonDataVariable)55,
        };

        /// <summary>
        /// The retail interests the shipped panel had a button for and this tree keeps no named
        /// slot for, so the panel can say what it is not showing instead of leaving the player to
        /// wonder whether their sim has no interest in any of them.
        /// </summary>
        /// <remarks>
        /// Read out of <c>UIGraphics\Res_CPanel.h</c>: the <c>kInterest*</c> block at 1600 to 1618
        /// is the shipped list of interest buttons, and these are the names in it that appear in
        /// none of the eight slot captions above. This is a documentation list, not a data
        /// source - nothing reads it except the panel's footer and its test.
        /// </remarks>
        public static readonly string[] UntrackedRetailNames = new string[]
        {
            "Charisma", "Exercise", "Food", "Health", "Parties", "Romance", "Style",
        };

        private static InterestSlot Slot(VMPersonDataVariable slot, params string[] names)
        {
            return new InterestSlot() { Slot = slot, RetailNames = names };
        }

        /// <summary>The caption for a tracked row.</summary>
        public static string NameOf(InterestSlot slot)
        {
            if (slot == null) return "";
            return SimitoneStrings.Get(IdFor(slot), slot.Caption());
        }

        /// <summary>
        /// The table id a slot's caption uses: one per tracked interest, in slot order.
        /// </summary>
        public static string IdFor(InterestSlot slot)
        {
            for (int i = 0; i < Slots.Length; i++)
            {
                if (ReferenceEquals(Slots[i], slot)) return (IdBase + i).ToString();
            }
            // A slot that is not one of the tracked eight has no id of its own; its caption is
            // the enum's name, which is English Simitone did not write and cannot translate.
            return null;
        }

        /// <summary>
        /// A raw person-data reading as a bar value: retail's 0-to-1000 scale divided down to the
        /// ten segments the bar draws.
        /// </summary>
        /// <remarks>
        /// Person data is a raw short and the shipped scripts write it freely - the base game's
        /// <c>'Convert Interests to 0-1000'</c> only guards its ten slots, so a value outside
        /// 0 to 1000 is possible both from a script and from the one-time migration leaving a
        /// half-converted sim. Integer division, not rounding, because that is what the shipped
        /// trees do: every one of them writes <c>Temps[0] /= 100</c> before using the result.
        /// A negative value is possible too - the interest slots are signed - and reads as no
        /// interest at all rather than as a full bar.
        /// </remarks>
        public static int ClampLevel(int raw)
        {
            return ClampPersonData(raw) / PersonDataStep;
        }

        /// <summary>
        /// Keeps a raw person-data reading inside retail's interest scale.
        /// </summary>
        /// <remarks>
        /// Split out from <see cref="ClampLevel"/> because the write needs it: an interest that
        /// was already out of range has to be pulled back onto the scale as it is nudged, not
        /// left off the end where the next nudge cannot move it.
        /// </remarks>
        public static int ClampPersonData(int raw)
        {
            if (raw < 0) return 0;
            if (raw > PersonDataMax) return PersonDataMax;
            return raw;
        }

        /// <summary>
        /// The next person-data value for an interest moved by <paramref name="steps"/> of
        /// retail's own step size, clamped to the scale.
        /// </summary>
        /// <remarks>
        /// The whole of the panel's write arithmetic, with no engine in it, so that the clamping
        /// and the step size can be pinned by a test rather than by clicking.
        /// </remarks>
        public static short NextValue(int raw, int steps)
        {
            return (short)ClampPersonData(ClampPersonData(raw) + (steps * PersonDataStep));
        }

        /// <summary>
        /// Moves one of a sim's interests by <paramref name="steps"/> of retail's scale and
        /// writes the result to that sim's person data.
        /// </summary>
        /// <param name="avatar">The sim to change. Null is a no-op.</param>
        /// <param name="slot">The interest to change. Null is a no-op.</param>
        /// <param name="steps">
        /// How many of retail's hundred-point steps to move by. One is a single click of the
        /// row's <c>+</c> or <c>-</c>.
        /// </param>
        /// <returns>The value written, or <c>null</c> when there was nothing to write.</returns>
        /// <remarks>
        /// <para>
        /// <b>This is a write the shipped scripts honour.</b> The interest slots are ordinary
        /// person data read by the base game's own autonomy - <c>PersonGlobals.iff</c> 8245, 8247,
        /// 8363, 8364, 8376, 8379, 8381 and 8443 all fold <c>slot / 100</c> into the interaction's
        /// <c>TreeAdRange</c>, and <c>8488 'Build Change Subject Menu'</c> and
        /// <c>SocialGlobals.iff</c> 8274 read the same slots to decide what a sim will talk
        /// about - so a value written here changes what the sim chooses to do on the next
        /// autonomy pass, with no reinitialisation.
        /// </para>
        /// <para>
        /// <b>And it persists.</b> <see cref="VMAvatar.Save"/> clones the whole person-data
        /// array into the <c>VMMarshal</c> the lot save commits
        /// (<c>SaveService</c> - <c>vm.Save()</c>), and every slot in
        /// <see cref="UnnamedSlots"/> and <see cref="Slots"/> is below 88, so it is also inside
        /// the fixed 88 shorts <c>NBRS</c> keeps per neighbour. Nothing extra is needed to make
        /// the change stick.
        /// </para>
        /// </remarks>
        public static short? Nudge(VMAvatar avatar, InterestSlot slot, int steps)
        {
            if (avatar == null || slot == null || steps == 0) return null;
            var value = NextValue(avatar.GetPersonData(slot.Slot), steps);
            return avatar.SetPersonData(slot.Slot, value) ? value : (short?)null;
        }

        /// <summary>
        /// A sim's interest in one tracked row, as a bar value. Null-safe in both directions: no
        /// sim and no slot both read as no interest, because a panel that threw from its update
        /// loop would take the whole live-mode strip with it.
        /// </summary>
        public static int Level(VMAvatar avatar, InterestSlot slot)
        {
            if (avatar == null || slot == null) return 0;
            return ClampLevel(avatar.GetPersonData(slot.Slot));
        }

        /// <summary>
        /// The footer naming the retail interests this panel cannot show.
        /// </summary>
        public static string UntrackedCaption()
        {
            return SimitoneStrings.Format(UntrackedId, "Not tracked by this game: %s",
                string.Join(", ", UntrackedRetailNames));
        }

        /// <summary>
        /// The second footer line: the slots the shipped data drives that no retail name
        /// identifies, so the count of them is on screen rather than only in this file.
        /// </summary>
        /// <remarks>
        /// Built with no format specifiers on purpose. The three numbers are spliced in from the
        /// constants rather than passed as arguments, so the sentence has nothing a translation
        /// can get out of step with - and so that adding the entry to the shipped string table
        /// later cannot trip
        /// <c>SimitoneStringsTests.NoneOfTheNewIdsThatTakeNothingAsksForAnything</c>, which checks
        /// the table against a hand-kept list of ids whose call sites pass arguments.
        /// </remarks>
        public static string UnnamedWhyCaption()
        {
            return SimitoneStrings.Get(UnnamedWhyId,
                "Retail drove " + RetailSlotCount + " interest slots and " + RetailButtonCount
                + " buttons. The " + Slots.Length
                + " named above are the slots this game can name; the buttons beside each one "
                + "change it, and the sim acts on the change on its next free decision.");
        }
    }
}
