/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    /// <summary>
    /// The career page the job subpanel's Career button opens: the sim's job, the level it is on,
    /// and every rung of the track between "nobody" and the top - salary, shift hours and the six
    /// skills each rung asks for, with the rungs already climbed marked as such.
    ///
    /// <para>
    /// <b>What this replaced.</b> The button existed and had no click handler anywhere in the
    /// tree, so the page was unreachable. The data for it was always there -
    /// <c>GameData/work.iff</c> is read by <c>TS1JobProvider</c> into a <see cref="CARR"/> per
    /// career, each with its own array of <see cref="JobLevel"/> rungs - so the page is built
    /// from the same chunk the job subpanel already reads its own title and salary from, and
    /// there is nothing new to load.
    /// </para>
    ///
    /// <para>
    /// <b>What this is not.</b> It is read-only. Retail's page also let a player edit the days
    /// and hours a sim works, which is a different feature entirely: the work schedule lives in
    /// the lot's own globals rather than in the neighbourhood chunk, and no part of this tree
    /// exposes a setter for it. Rather than offer a control that does nothing, the page says
    /// what it can and stops.
    /// </para>
    /// </summary>
    public class UICareerAlert : UIMobileDialog
    {
        /// <summary>
        /// The words this page is made of, as ids 480 to 489 of Simitone's own table.
        /// </summary>
        /// <remarks>
        /// The six skill captions are TS1's own (table 136, through
        /// <see cref="LivePanelStrings"/>) and stay there; everything else here is Simitone's.
        /// </remarks>
        public const string TitleId = "480",
            CloseCaptionId = "481",
            NoCareerId = "482",
            NoCareerWhyId = "483",
            SalaryId = "484",
            HoursId = "485",
            SkillsId = "486",
            CurrentId = "487",
            ReachedId = "488",
            AheadId = "489";

        /// <summary>The career page's contents, exposed for the same reason the other dialogs expose theirs.</summary>
        public UICareerPanel CareerPanel { get; private set; }

        /// <summary>
        /// The career page currently up, if any.
        /// </summary>
        /// <remarks>
        /// Static because the subpanel that opens it is itself destroyed and rebuilt on every
        /// category switch, a mode switch and a language change: a field on the subpanel would
        /// lose track of a page the player left open while looking at something else, and the
        /// next press would stack a second copy. <see cref="Removed"/> clears it on every way
        /// the dialog can leave the screen, so this cannot hold a page that is gone.
        /// </remarks>
        private static UICareerAlert open;

        /// <summary>
        /// The career page, or the state it was asked for when there is no job.
        /// </summary>
        /// <remarks>
        /// A page that is already up is closed first, so pressing Career twice shows one page
        /// rather than two stacked modals - the same rule the Options dialog follows.
        /// </remarks>
        public static void Show(CARR job, int currentLevel, bool hasJob)
        {
            open?.Close();
            open = new UICareerAlert(job, currentLevel, hasJob);
            UIScreen.GlobalShowDialog(open, true);
        }

        public override void Removed()
        {
            base.Removed();
            if (ReferenceEquals(open, this)) open = null;
        }

        public UICareerAlert(CARR job, int currentLevel, bool hasJob)
        {
            Caption = SimitoneStrings.Get(TitleId, "Career");
            SetHeight(UICareerPanel.DialogHeight);
            CareerPanel = new UICareerPanel(job, currentLevel, hasJob);
            UpdatePlacement();
            CareerPanel.OnClose += () => Close();
            Add(CareerPanel);
        }

        /// <summary>
        /// Centres the panel on the dialog. Pulled out of the constructor so
        /// <see cref="GameResized"/> can re-run it, in the same way the phone book's panel does.
        /// </summary>
        private void UpdatePlacement()
        {
            CareerPanel.Position = new Vector2((Width - UICareerPanel.PanelWidth) / 2, 110);
        }

        public override void GameResized()
        {
            base.GameResized();
            UpdatePlacement();
        }
    }

    /// <summary>
    /// The contents of the career page: the rungs of the track on the left, the picked rung's
    /// details on the right.
    /// </summary>
    /// <remarks>
    /// Positions are absolute and re-applied on a window resize, because the panel is a fixed
    /// width that the dialog centres rather than one the screen lays out.
    /// </remarks>
    public class UICareerPanel : UIContainer
    {
        /// <summary>Fixed content width; the dialog centres this.</summary>
        public const int PanelWidth = 640;

        /// <summary>Height the dialog is told to reserve for this panel.</summary>
        public const int DialogHeight = 560;

        private const int ListX = 0;
        private const int ListY = 44;
        private const int ListWidth = 300;
        private const int ListHeight = 380;

        private const int DetailX = 340;
        private const int DetailWidth = PanelWidth - DetailX;

        private UITouchStringList rungList;
        private UILabel nameLabel;
        private UILabel stateLabel;
        private UILabel salaryLabel;
        private UILabel hoursLabel;
        private UILabel skillsHeader;
        private UILabel[] skillLabels;
        private UILabel emptyLabel;
        private UIBigButton closeButton;

        private readonly IReadOnlyList<CareerRungInfo> rungs;
        private int selected = -1;

        /// <summary>Raised by the close button, so the owning dialog can close itself.</summary>
        public event Action OnClose;

        public UICareerPanel(CARR job, int currentLevel, bool hasJob)
        {
            rungs = hasJob ? CareerLadder.Rungs(job, currentLevel) : new List<CareerRungInfo>();

            var title = MakeLabel(0, 0, PanelWidth, 30, 22);
            title.Caption = hasJob
                ? CareerLadder.CareerTitle(job)
                : LivePanelStrings.Unemployed();
            Add(title);

            if (rungs.Count == 0)
            {
                // No job, or a job whose chunk could not be read. The button still has to do
                // something, and "there is no track to show" is the honest answer rather than
                // an empty list the player cannot read anything into.
                emptyLabel = MakeLabel(ListX, ListY, PanelWidth - ListX, 60, 15);
                emptyLabel.Wrapped = true;
                emptyLabel.Caption = SimitoneStrings.Get(UICareerAlert.NoCareerWhyId,
                    "This sim does not have a job, so there is no career track to show.");
                Add(emptyLabel);
            }
            else
            {
                rungList = new UITouchStringList();
                rungList.Size = new Vector2(ListWidth, ListHeight);
                rungList.Position = new Vector2(ListX, ListY);
                rungList.BackingList = CareerLadder.ListCaptions(rungs);
                rungList.OnSelectionChange += OnRungPicked;
                Add(rungList);

                nameLabel = MakeLabel(DetailX, ListY, DetailWidth, 34, 21);
                stateLabel = MakeLabel(DetailX, ListY + 34, DetailWidth, 26, 15);
                salaryLabel = MakeLabel(DetailX, ListY + 64, DetailWidth, 26, 15);
                hoursLabel = MakeLabel(DetailX, ListY + 90, DetailWidth, 26, 15);
                skillsHeader = MakeLabel(DetailX, ListY + 124, DetailWidth, 26, 15);
                Add(nameLabel);
                Add(stateLabel);
                Add(salaryLabel);
                Add(hoursLabel);
                Add(skillsHeader);

                skillLabels = new UILabel[CareerLadder.SkillCount];
                for (int i = 0; i < skillLabels.Length; i++)
                {
                    skillLabels[i] = MakeLabel(DetailX + 20, ListY + 152 + (i * 26), DetailWidth - 20, 24, 15);
                    Add(skillLabels[i]);
                }
            }

            closeButton = new UIBigButton(false) { Width = 275 };
            closeButton.Caption = SimitoneStrings.Get(UICareerAlert.CloseCaptionId, "Close");
            closeButton.OnButtonClick += (btn) => Close();
            Add(closeButton);

            // Open on the rung the sim is actually on, so the page lands on the answer the
            // player asked for rather than on the bottom of the ladder.
            var start = CareerLadder.IndexOfState(rungs, CareerRungState.Current);
            if (start < 0) start = 0;
            if (rungList != null) rungList.ScrollElem.SelectItem(start);
            ShowRung(start);

            PlaceControls();
        }

        private void OnRungPicked(int row)
        {
            ShowRung(row);
        }

        private void ShowRung(int index)
        {
            if (rungs.Count == 0) return;
            if (index < 0 || index >= rungs.Count)
            {
                // -1 is "nothing picked in the list", which is a state the page has to survive:
                // the list can be clicked empty by a drag, and the detail column must not index
                // off the end of the ladder when it is.
                selected = -1;
                ClearDetail();
                return;
            }

            selected = index;
            var rung = rungs[index];
            nameLabel.Caption = rung.Name ?? "";
            stateLabel.Caption = CareerLadder.StateCaption(rung.State);
            salaryLabel.Caption = CareerLadder.SalaryLine(rung);
            hoursLabel.Caption = CareerLadder.HoursLine(rung);
            skillsHeader.Caption = SimitoneStrings.Get(UICareerAlert.SkillsId, "Skills needed");
            for (int i = 0; i < skillLabels.Length; i++)
            {
                skillLabels[i].Caption = CareerLadder.SkillLine(i, rung);
            }
        }

        private void ClearDetail()
        {
            nameLabel.Caption = "";
            stateLabel.Caption = "";
            salaryLabel.Caption = "";
            hoursLabel.Caption = "";
            skillsHeader.Caption = "";
            for (int i = 0; i < skillLabels.Length; i++) skillLabels[i].Caption = "";
        }

        private UILabel MakeLabel(int x, int y, int width, int height, int size)
        {
            // UILabel hands out the shared default style, so it has to be cloned before it is
            // restyled or every other label in the game inherits this size.
            var label = new UILabel()
            {
                Position = new Vector2(x, y),
                Size = new Vector2(width, height),
                CaptionStyle = TextStyle.DefaultLabel.Clone(),
            };
            label.CaptionStyle.Size = size;
            label.CaptionStyle.Color = UIStyle.Current.Text;
            return label;
        }

        private void Close()
        {
            OnClose?.Invoke();
        }

        private void PlaceControls()
        {
            closeButton.Position = new Vector2(DetailX, ListY + ListHeight + 20);
        }

        public override void GameResized()
        {
            base.GameResized();
            PlaceControls();
        }
    }

    /// <summary>How a rung of a career track stands relative to the sim's current level.</summary>
    public enum CareerRungState
    {
        /// <summary>Below the sim: they have been here.</summary>
        Reached,

        /// <summary>The rung the sim is on now.</summary>
        Current,

        /// <summary>Above the sim: they have not been here yet.</summary>
        Ahead,
    }

    /// <summary>One rung of a career track, flattened out of a <see cref="JobLevel"/>.</summary>
    public class CareerRungInfo
    {
        /// <summary>Index of the rung in <see cref="CARR.JobLevels"/>.</summary>
        public int Index;

        /// <summary>The rung's own name, e.g. "Subway Musician". Null when the chunk had none.</summary>
        public string Name;

        /// <summary>Daily salary in simoleons.</summary>
        public int Salary;

        /// <summary>Hour of the day the shift starts, 0..23.</summary>
        public int StartTime;

        /// <summary>Hour of the day the shift ends, 0..23.</summary>
        public int EndTime;

        /// <summary>The six skill requirements, cooking first, in the bar's order.</summary>
        public int[] RequiredSkills = new int[CareerLadder.SkillCount];

        /// <summary>Where this rung stands relative to the sim.</summary>
        public CareerRungState State;
    }

    /// <summary>
    /// The career track, flattened into rows a panel can draw.
    ///
    /// <para>
    /// All of it is pure. A <see cref="CARR"/> is an IFF chunk, but the parts this needs - its
    /// name and its array of rungs - are public fields, so a test can build one by hand and
    /// check the mapping, the ordering, the hour formatting and the reached/current/ahead
    /// marking without opening a file, a content pipeline or a graphics device.
    /// </para>
    /// </summary>
    public static class CareerLadder
    {
        /// <summary>How many skill requirements a rung carries.</summary>
        public const int SkillCount = 6;

        /// <summary>English for a career chunk with no name of its own.</summary>
        public const string UnknownCareerFallback = "(unknown career)";

        /// <summary>
        /// The whole track as rows, marked against the sim's current level.
        /// </summary>
        /// <remarks>
        /// A null chunk, a null rung array or a level index that is not in the array all give an
        /// empty list rather than an exception: the career page is opened from a button that is
        /// live at every moment of the game, including the ones where there is no job at all.
        /// </remarks>
        public static IReadOnlyList<CareerRungInfo> Rungs(CARR job, int currentLevel)
        {
            var result = new List<CareerRungInfo>();
            var levels = job?.JobLevels;
            if (levels == null) return result;

            for (int i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                result.Add(new CareerRungInfo()
                {
                    Index = i,
                    Name = level?.JobName,
                    Salary = level?.Salary ?? 0,
                    StartTime = level?.StartTime ?? 0,
                    EndTime = level?.EndTime ?? 0,
                    RequiredSkills = RequiredSkillsOf(level),
                    State = StateFor(i, currentLevel),
                });
            }
            return result;
        }

        /// <summary>
        /// The six skill requirements of a rung, cooking first.
        /// </summary>
        /// <remarks>
        /// <c>JobLevel.MinRequired</c> is ten entries: index 0 is the number of friends the rung
        /// asks for and 1..6 are the skills. The skills are stored multiplied by 100 - the job
        /// subpanel divides them by 100 to draw its bars - so the division happens here rather
        /// than in the panel, and a short array reads as "no requirement" for the rungs that
        /// name skills the track does not use.
        /// </remarks>
        public static int[] RequiredSkillsOf(JobLevel level)
        {
            var skills = new int[SkillCount];
            var required = level?.MinRequired;
            if (required == null) return skills;
            for (int i = 0; i < SkillCount; i++)
            {
                var index = i + 1;
                skills[i] = required.Length > index ? required[index] / 100 : 0;
            }
            return skills;
        }

        /// <summary>
        /// Whether the rung at <paramref name="index"/> is behind, on, or ahead of the sim.
        /// </summary>
        /// <remarks>
        /// A level index outside the ladder - which is what an unemployed sim has, and what a
        /// stale person-data slot can hold - marks every rung as ahead, so a page opened for a
        /// sim who is not on this track shows the track rather than an empty list.
        /// </remarks>
        public static CareerRungState StateFor(int index, int currentLevel)
        {
            if (index < currentLevel) return CareerRungState.Reached;
            if (index == currentLevel) return CareerRungState.Current;
            return CareerRungState.Ahead;
        }

        /// <summary>
        /// The first rung in one of the three states, or -1 when the track has none.
        /// </summary>
        public static int IndexOfState(IReadOnlyList<CareerRungInfo> rungs, CareerRungState state)
        {
            if (rungs == null) return -1;
            for (int i = 0; i < rungs.Count; i++)
            {
                if (rungs[i].State == state) return i;
            }
            return -1;
        }

        /// <summary>
        /// The career track's own name, or the English stand-in when the chunk has none.
        /// </summary>
        public static string CareerTitle(CARR job)
        {
            var name = job?.Name;
            return string.IsNullOrWhiteSpace(name) ? UnknownCareerFallback : name;
        }

        /// <summary>
        /// The one-word list caption for a rung: its name, and where it stands.
        /// </summary>
        /// <remarks>
        /// The state is a separate word rather than a colour or a tick, because a list row that
        /// only differs by tint is a list a colourblind player cannot read, and a tick is a
        /// glyph the panel's font may not have.
        /// </remarks>
        public static List<string> ListCaptions(IReadOnlyList<CareerRungInfo> rungs)
        {
            var lines = new List<string>();
            if (rungs == null) return lines;
            for (int i = 0; i < rungs.Count; i++)
            {
                var name = string.IsNullOrWhiteSpace(rungs[i].Name) ? rungs[i].Index.ToString(CultureInfo.InvariantCulture) : rungs[i].Name;
                lines.Add(name + " - " + StateCaption(rungs[i].State));
            }
            return lines;
        }

        /// <summary>
        /// The word a state is shown as, in Simitone's own words.
        /// </summary>
        public static string StateCaption(CareerRungState state)
        {
            switch (state)
            {
                case CareerRungState.Current:
                    return SimitoneStrings.Get(UICareerAlert.CurrentId, "current");
                case CareerRungState.Reached:
                    return SimitoneStrings.Get(UICareerAlert.ReachedId, "reached");
                default:
                    return SimitoneStrings.Get(UICareerAlert.AheadId, "not yet");
            }
        }

        /// <summary>
        /// A rung's salary line.
        /// </summary>
        public static string SalaryLine(CareerRungInfo rung)
        {
            if (rung == null) return "";
            return SimitoneStrings.Format(UICareerAlert.SalaryId, "Salary: %s",
                rung.Salary.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A rung's shift line, e.g. "Hours: 9am - 5pm".
        /// </summary>
        public static string HoursLine(CareerRungInfo rung)
        {
            if (rung == null) return "";
            return SimitoneStrings.Format(UICareerAlert.HoursId, "Hours: %s - %s",
                ShiftTime(rung.StartTime), ShiftTime(rung.EndTime));
        }

        /// <summary>
        /// One skill's requirement, e.g. "Cooking 3". A rung that asks for nothing shows
        /// "Cooking -" rather than a bare zero, so an empty requirement is not read as a
        /// requirement of zero skill points.
        /// </summary>
        public static string SkillLine(int skillIndex, CareerRungInfo rung)
        {
            var name = LivePanelStrings.SkillName(skillIndex);
            if (name.Length == 0) return "";
            var needed = rung?.RequiredSkills != null && skillIndex < rung.RequiredSkills.Length
                ? rung.RequiredSkills[skillIndex]
                : 0;
            return needed > 0
                ? name + " " + needed.ToString(CultureInfo.InvariantCulture)
                : name + " -";
        }

        /// <summary>
        /// A shift hour as the game writes it, on a 12 hour clock.
        /// </summary>
        /// <remarks>
        /// Midnight and noon are the two the naive conversion gets wrong - hour 0 becomes
        /// "0am" and hour 12 becomes "12pm" instead of "12am" - and a night shift is exactly
        /// when a player is most likely to be reading this page. A retail shift can also be
        /// stored as 24 to mean midnight at the end of the day, so the hour wraps.
        /// </remarks>
        public static string ShiftTime(int hour)
        {
            if (hour < 0) return "";
            var wrapped = hour % 24;
            var display = wrapped % 12;
            if (display == 0) display = 12;
            return display.ToString(CultureInfo.InvariantCulture) + (wrapped < 12 ? "am" : "pm");
        }
    }
}
