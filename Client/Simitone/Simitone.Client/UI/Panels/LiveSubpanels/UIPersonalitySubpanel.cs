using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Simitone.Client.UI.Screens;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Controls;
using FSO.Client;
using FSO.Client.UI.Controls;
using Microsoft.Xna.Framework;
using FSO.Content;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics;
using FSO.SimAntics.Model;
using FSO.Common.Rendering.Framework.Model;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIPersonalitySubpanel : UISubpanel
    {
        /// <summary>
        /// The personality description table in <c>live.dir</c>. It holds seven strings per
        /// trait in three strength bands, laid out consecutively, so trait <c>n</c> owns ids
        /// <c>1 + n*7 .. 7 + n*7</c> - see <see cref="InitDescriptions"/>.
        /// </summary>
        private const string STRFile = "live", STRTable = "131";

        /// <summary>
        /// The five personality points, in the order the game stores them. Named for the same
        /// reason as <see cref="UIMotiveSubpanel.UIMotiveElement"/>: the description lookup is
        /// keyed off them, and a bare <c>for (i = 0; i &lt; 5; i++)</c> cannot say which trait a
        /// string belongs to.
        /// </summary>
        private enum UIPersonalityBarElement
        {
            Neat,
            Outgoing,
            Active,
            Playful,
            Nice,
        }

        /// <summary>
        /// One trait's wording. <see cref="PlainName"/> is the axis name (the row's caption, and the
        /// last-resort title if the table has no titles); the rest are the three title bands and
        /// the three description bands.
        /// </summary>
        private record UIPersonalityDescriptor(string PlainName, string TitleLow, string TitleNormal, string TitleHigh, string CaptionLow, string CaptionNormal, string CaptionHigh);

        /// <summary>English trait names, in bar order - the fallback when the table is untranslated.</summary>
        private static readonly string[] TraitNameFallback = { "Neat", "Outgoing", "Active", "Playful", "Nice" };

        /// <summary>Strings stored per trait in the table; the id stride between traits.</summary>
        private const int DescriptionsPerTrait = 7;

        /// <summary>Where the description block starts, leaving room for the three columns of bars.</summary>
        private const float DescriptionLeft = 450f;

        private VMPersonDataVariable[] SkillInd = new VMPersonDataVariable[]
        {
            VMPersonDataVariable.NeatPersonality,
            VMPersonDataVariable.OutgoingPersonality,
            VMPersonDataVariable.ActivePersonality,
            VMPersonDataVariable.PlayfulPersonality,
            VMPersonDataVariable.NicePersonality
        };

        private readonly Dictionary<UIPersonalityBarElement, UIPersonalityDescriptor> descriptions =
            new Dictionary<UIPersonalityBarElement, UIPersonalityDescriptor>();

        private UILabel titleLabel;
        private UILabel captionLabel;
        private UISkillDisplay[] PersonalityTraits;

        /// <summary>
        /// The sim the bars and description were last filled in for. A personality point does not
        /// move during play the way a motive does, so re-reading five of them and re-picking five
        /// description bands every frame is wasted work; this makes it happen when the selected
        /// sim changes instead.
        /// </summary>
        private VMAvatar lastSelectedAvatar;

        public UIPersonalitySubpanel(TS1GameScreen game) : base(game)
        {
            int leftMargin = 20;
            int topMargin = 11, topMarginBar = 35;

            //**initialize personality descriptions from STR
            InitDescriptions();

            PersonalityTraits = new UISkillDisplay[Enum.GetValues(typeof(UIPersonalityBarElement)).Length];
            for (int i=0; i<PersonalityTraits.Length; i++)
            {
                var trait = (UIPersonalityBarElement)i;

                PersonalityTraits[i] = new UISkillDisplay();
                PersonalityTraits[i].Position = new Vector2(leftMargin + 2 + (i%3)*140, topMarginBar + 60*(i/3));
                Add(PersonalityTraits[i]);

                var name = new UILabel();
                name.Caption = descriptions[trait].PlainName;
                name.Position = new Vector2(leftMargin + (i % 3) * 140, topMargin + 60 * (i / 3));
                InitLabel(name);
            }

            //The right hand side describes the trait: a one line title over a wrapped paragraph.
            //It is placed to the right of the three columns of bars rather than at a fixed pixel
            //width, so it still fits a narrow window.
            titleLabel = new UILabel
            {
                Position = new Vector2(DescriptionLeft, topMargin)
            };
            InitLabel(titleLabel);
            Add(titleLabel);

            captionLabel = new UILabel
            {
                Position = new Vector2(DescriptionLeft, topMargin + 20),
                Size = new Vector2(Math.Max(240f, Size.X - DescriptionLeft - 10f), 200),
                Wrapped = true,
                Alignment = FSO.Client.UI.Framework.TextAlignment.Left
            };
            InitLabel(captionLabel);
            Add(captionLabel);
        }

        /// <summary>
        /// Reads one trait's six description strings out of the string table and pairs them with
        /// its English name.
        /// </summary>
        /// <remarks>
        /// The seven strings of a trait are stored consecutively, so trait <c>n</c> owns ids
        /// <c>1 + n*7</c> to <c>7 + n*7</c>. Reading them as a run per trait (rather than one id
        /// per band) is how the game stores them, and it lets a translation word all three bands
        /// of a trait together.
        /// </remarks>
        private void InitDescriptions()
        {
            descriptions.Clear();

            for (int i = 0; i < Enum.GetValues(typeof(UIPersonalityBarElement)).Length; i++)
            {
                int field = 0;
                string GET() => Str((1 + field++ + (i * DescriptionsPerTrait)).ToString());
                descriptions.Add((UIPersonalityBarElement)i,
                    new UIPersonalityDescriptor(TraitNameFallback[i], GET(), GET(), GET(), GET(), GET(), GET()));
            }
        }

        /// <summary>
        /// Reads a string out of the personality table, or null when the table has nothing for it.
        /// </summary>
        /// <remarks>
        /// The lookup answers <c>***MISSING***</c> for an unknown id rather than failing, so that
        /// sentinel is treated as "not translated" rather than being shown to the player.
        /// </remarks>
        private static string Str(string id)
        {
            var value = GameFacade.Strings.GetString(STRFile, STRTable, id);
            return (value == "***MISSING***" || string.IsNullOrEmpty(value)) ? null : value;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            var sel = Game.SelectedAvatar;
            if (sel == null)
            {
                //nothing selected: forget who we were describing, so that selecting a sim later -
                //including the same one - refills the panel instead of looking like nothing changed.
                lastSelectedAvatar = null;
                return;
            }
            if (lastSelectedAvatar == sel) return;
            lastSelectedAvatar = sel;

            for (int i = 0; i < PersonalityTraits.Length; i++)
            {
                int value = sel.GetPersonData(SkillInd[i]) / 100;
                PersonalityTraits[i].Value = value;
                SetPersonalityDescription((UIPersonalityBarElement)i, value);
            }
        }

        /// <summary>
        /// Writes the title and paragraph for one trait, at the strength its value falls into.
        /// </summary>
        /// <remarks>
        /// A personality point runs 0..10 and is shown in three bands, so the band is the value
        /// divided by three after clamping: 0-2 low, 3-6 normal, 7-10 high.
        /// </remarks>
        private void SetPersonalityDescription(UIPersonalityBarElement personality, int value)
        {
            UIPersonalityDescriptor desc = descriptions[personality];
            int traitStrength = Math.Max(Math.Min(value, 10), 0) / 3; // 0,1,2

            titleLabel.Caption = (traitStrength switch
            {
                0 => desc.TitleLow,
                1 => desc.TitleNormal,
                _ => desc.TitleHigh
            }) ?? desc.PlainName;

            string caption = (traitStrength switch
            {
                0 => desc.CaptionLow,
                1 => desc.CaptionNormal,
                _ => desc.CaptionHigh
            });
            captionLabel.Caption = caption ?? "";
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
