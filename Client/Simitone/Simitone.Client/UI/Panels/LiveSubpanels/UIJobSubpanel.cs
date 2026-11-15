using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Simitone.Client.UI.Screens;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Controls;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.Client;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIJobSubpanel : UISubpanel
    {
        /// <summary>
        /// The live-mode career strings, in <c>live.dir</c>. The skill captions are ids 1..6 in bar
        /// order and "unemployed" is id 7; they are shared with the career page, which is why
        /// they live in <see cref="LivePanelStrings"/> rather than being a private array here.
        /// </summary>
        private const string STRTable = LivePanelStrings.CareerTable;

        private static readonly string[] SkillNameFallback = LivePanelStrings.SkillNameFallback;

        private const string UnemployedFallback = LivePanelStrings.UnemployedFallback;

        public UILabel PerformanceTitle;
        public UIMotiveBar PerformanceBar;
        public UILabel JobTitle;
        public UILabel SalaryTitle;
        public UITwoStateButton CareerButton;

        private JobLevel LastJobLevel;

        private UISkillDisplay[] Skills;

        private VMPersonDataVariable[] SkillInd = new VMPersonDataVariable[]
        {
            VMPersonDataVariable.CookingSkill,
            VMPersonDataVariable.MechanicalSkill,
            VMPersonDataVariable.CharismaSkill,
            VMPersonDataVariable.BodySkill,
            VMPersonDataVariable.LogicSkill,
            VMPersonDataVariable.CreativitySkill
        };

        /// <summary>
        /// Reads a string out of <see cref="STRTable"/>, falling back to English.
        /// Delegated to <see cref="LivePanelStrings"/> so the career page reads the same six
        /// skill captions out of the same table rather than keeping its own copy of the rule.
        /// </summary>
        private static string Str(string id, string fallback)
        {
            return LivePanelStrings.Get(id, fallback);
        }

        public UIJobSubpanel(TS1GameScreen game) : base(game)
        {
            PerformanceTitle = new UILabel();
            // "Performance" is the one word on this panel TS1 has no string for, so it is id 460
            // of Simitone's own table; the six skill captions and "Unemployed" below come out of
            // the game's live-mode table 136 and stay there.
            PerformanceTitle.Caption = SimitoneStrings.Get("460", "Performance");
            PerformanceTitle.Position = new Vector2(79, 16);
            InitLabel(PerformanceTitle);

            PerformanceBar = new UIMotiveBar();
            PerformanceBar.Position = new Vector2(79, 41);
            DynamicOverlay.Add(PerformanceBar);

            JobTitle = new UILabel();
            JobTitle.Caption = "Subway Musician";
            JobTitle.Position = new Vector2(18, 71);
            InitLabel(JobTitle);

            SalaryTitle = new UILabel();
            SalaryTitle.Caption = "Salary: §90";
            SalaryTitle.Position = new Vector2(18, 94);
            InitLabel(SalaryTitle);
            SalaryTitle.CaptionStyle.Color = UIStyle.Current.BtnActive;

            CareerButton = new UITwoStateButton(Content.Get().CustomUI.Get("blank_blue.png").Get(GameFacade.GraphicsDevice));
            CareerButton.Position = new Vector2(20, 15);
            // The retail career page. The button existed with no handler anywhere in the tree,
            // so the whole career ladder was unreachable; the data it needs (the per-career CARR
            // chunk and the level the sim is on) is the same job data this panel already reads.
            CareerButton.OnButtonClick += (b) => OpenCareerPage();
            CareerButton.Tooltip = SimitoneStrings.Get(UICareerAlert.TitleId, "Career");
            Add(CareerButton);

            Skills = new UISkillDisplay[6];
            for (int i=0; i<6; i++)
            {
                Skills[i] = new UISkillDisplay();
                Skills[i].Position = new Vector2(334 + (i%3)*140, 35 + 60*(i/3));
                Add(Skills[i]);

                var name = new UILabel();
                //string id 1 is the first bar, which is Skills[0] - the ids count from one.
                name.Caption = Str((i + 1).ToString(), SkillNameFallback[i]);
                name.Position = new Vector2(332 + (i % 3) * 140, 11 + 60 * (i / 3));
                InitLabel(name);
            }
        }

        public int LastPerformance;

        /// <summary>
        /// Opens the career page for the selected sim.
        /// </summary>
        /// <remarks>
        /// The job and the level are read here rather than taken from the panel's own cached
        /// state, so the page is correct even on the first frame after the selection changes -
        /// and so that an unemployed sim, whose job is the "no job" entry, still gets a page
        /// that says so rather than a button that does nothing.
        /// </remarks>
        private void OpenCareerPage()
        {
            var sel = Game.SelectedAvatar;
            var job = (CARR)null;
            var level = 0;

            if (sel != null)
            {
                var type = sel.GetPersonData(VMPersonDataVariable.JobType);
                var promotion = sel.GetPersonData(VMPersonDataVariable.JobPromotionLevel);
                job = Content.Get().Jobs.GetJob((ushort)type);
                if (job?.JobLevels != null && promotion < job.JobLevels.Length) level = promotion;
            }

            UICareerAlert.Show(job, level, job != null);
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            if (Opacity < 1)
            {
                if (DynamicOverlay.GetChildren().Count > 0)
                {
                    DynamicOverlay.Remove(PerformanceBar);
                    Add(PerformanceBar);
                }
            } else
            {
                if (DynamicOverlay.GetChildren().Count == 0)
                {
                    Remove(PerformanceBar);
                    DynamicOverlay.Add(PerformanceBar);
                }
            }
            var sel = Game.SelectedAvatar;
            if (sel == null) return;
            var type = sel.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.JobType);
            var level = sel.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.JobPromotionLevel);
            var performance = sel.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.JobPerformance);

            var job = Content.Get().Jobs.GetJob((ushort)type);
            if (job == null)
            {
                if (LastPerformance != -200)
                {
                    JobTitle.Caption = Str("7", UnemployedFallback);
                    SalaryTitle.Caption = "";
                    PerformanceBar.Visible = false;
                    PerformanceTitle.Visible = false;

                    LastJobLevel = null;
                    LastPerformance = -200;
                }
            }
            else
            {
                // person data is a raw short the scripts write freely, so the level is clamped
                // onto the track instead of trusted (OpenCareerPage guards the same index)
                var levels = job.JobLevels;
                var myLevel = levels[Math.Max(0, Math.Min((int)level, levels.Length - 1))];
                if (myLevel != LastJobLevel)
                {
                    JobTitle.Caption = myLevel.JobName;
                    // id 461 takes three arguments - the salary, then the shift's start and end -
                    // so the sentence can be composed rather than concatenated. The English at the
                    // call site is written with the same order, and carries no specifier, so with
                    // no table at all this is the string it has always been.
                    var salary = myLevel.Salary.ToString();
                    var start = CareerLadder.ShiftTime(myLevel.StartTime);
                    var end = CareerLadder.ShiftTime(myLevel.EndTime);
                    SalaryTitle.Caption = SimitoneStrings.Format("461",
                        "Salary: §" + salary + " (" + start + "-" + end + ")",
                        salary, start, end);

                    LastJobLevel = myLevel;
                }

                if (LastPerformance != performance)
                {
                    PerformanceBar.Visible = true;
                    PerformanceTitle.Visible = true;
                    PerformanceBar.MotiveValue = performance;
                    LastPerformance = performance;
                }
                var needed = CareerLadder.RequiredSkillsOf(myLevel);
                for (int i = 0; i < 6; i++)
                    Skills[i].Needed = needed[i];
            }

            for (int i = 0; i < 6; i++)
            {
                Skills[i].Value = sel.GetPersonData(SkillInd[i]) / 100;
            }
        }

        private void InitLabel(UILabel label)
        {
            label.CaptionStyle = label.CaptionStyle.Clone();
            label.CaptionStyle.Color = UIStyle.Current.Text;
            label.CaptionStyle.Size = 15;
            Add(label);
        }
    }
}
