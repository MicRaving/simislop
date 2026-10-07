using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIMotiveSubpanel : UISubpanel
    {
        /// <summary>
        /// The eight motives shown, in the order they are laid out (four across, two rows).
        ///
        /// <para>
        /// These used to be positional: <c>MotiveDisplays[0]</c> was hunger and the label came
        /// from the stock client's own table by index. The index was the only thing tying a bar to
        /// its caption, so <c>UpdateMotives</c> and the constructor had to agree by hand. Naming
        /// them makes both sides of that agreement explicit and lets the captions come out of the
        /// TS1 table, where the names are the ones the game itself uses.
        /// </para>
        /// </summary>
        public enum UIMotiveElement
        {
            Hunger,
            Comfort,
            Hygiene,
            Bladder,
            Energy,
            Fun,
            Social,
            Room
        }

        /// <summary>
        /// Caption ids in <c>live.dir</c>'s motive table, keyed by motive. The ids are not in bar
        /// order: the game groups them by how urgently the motive drains, which is why this is a
        /// lookup rather than an index offset.
        /// </summary>
        private static readonly Dictionary<UIMotiveElement, string> MotiveStringIds = new Dictionary<UIMotiveElement, string>
        {
            { UIMotiveElement.Hunger, "1" },
            { UIMotiveElement.Comfort, "5" },
            { UIMotiveElement.Hygiene, "9" },
            { UIMotiveElement.Bladder, "13" },
            { UIMotiveElement.Energy, "3" },
            { UIMotiveElement.Fun, "7" },
            { UIMotiveElement.Social, "11" },
            { UIMotiveElement.Room, "15" },
        };

        /// <summary>English captions, used when the table has not been translated.</summary>
        private static readonly Dictionary<UIMotiveElement, string> MotiveNameFallback = new Dictionary<UIMotiveElement, string>
        {
            { UIMotiveElement.Hunger, "Hunger" },
            { UIMotiveElement.Comfort, "Comfort" },
            { UIMotiveElement.Hygiene, "Hygiene" },
            { UIMotiveElement.Bladder, "Bladder" },
            { UIMotiveElement.Energy, "Energy" },
            { UIMotiveElement.Fun, "Fun" },
            { UIMotiveElement.Social, "Social" },
            { UIMotiveElement.Room, "Room" },
        };

        public UIMotiveBar[] MotiveDisplays;

        /// <summary>The bar for one motive.</summary>
        public UIMotiveBar GetMotiveDisplay(UIMotiveElement Motive)
        {
            var index = Math.Max(Math.Min((int)Motive, MotiveDisplays.Length - 1), 0);
            return MotiveDisplays[index];
        }

        public UIMotiveSubpanel(TS1GameScreen game) : base (game)
        {
            MotiveDisplays = new UIMotiveBar[Enum.GetValues(typeof(UIMotiveElement)).Length];
            for (int i = 0; i < MotiveDisplays.Length; i++)
            {
                var motive = (UIMotiveElement)i;
                var d = new UIMotiveBar();
                d.Position = new Vector2(17 + (i%4)*180, 36+(i/4) * 60);
                Add(d);

                MotiveDisplays[i] = d;

                var l = new UILabel();
                l.CaptionStyle = l.CaptionStyle.Clone();
                l.CaptionStyle.Size = 15;
                l.CaptionStyle.Color = UIStyle.Current.Text;
                l.Alignment = FSO.Client.UI.Framework.TextAlignment.Bottom;
                l.Size = new Vector2(1);
                l.Position = new Vector2(17 + (i % 4) * 180, 30 + (i / 4) * 60);
                l.Caption = MotiveCaption(motive);
                Add(l);
            }
        }

        /// <summary>
        /// The caption for one motive, out of the TS1 live-mode string table and falling back to
        /// the English name when the table has no entry for it.
        /// </summary>
        private static string MotiveCaption(UIMotiveElement motive)
        {
            var value = GameFacade.Strings.GetString("live", "130", MotiveStringIds[motive]);
            return (value == "***MISSING***" || string.IsNullOrEmpty(value)) ? MotiveNameFallback[motive] : value;
        }

        public override void Update(UpdateState state)
        {
            UpdateMotives();
            base.Update(state);
            if (Opacity < 1)
            {
                if (DynamicOverlay.GetChildren().Count > 0)
                {
                    foreach (var m in MotiveDisplays)
                    {
                        DynamicOverlay.Remove(m);
                        Add(m);
                    }
                }
                Invalidate();
            } else
            {
                if (DynamicOverlay.GetChildren().Count == 0)
                {
                    foreach (var m in MotiveDisplays)
                    {
                        Remove(m);
                        DynamicOverlay.Add(m);
                        Invalidate();
                    }
                }
            }
        }

        private void UpdateMotives()
        {
            if (Game.SelectedAvatar == null) return;
            GetMotiveDisplay(UIMotiveElement.Hunger).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Hunger);
            GetMotiveDisplay(UIMotiveElement.Comfort).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Comfort);
            GetMotiveDisplay(UIMotiveElement.Hygiene).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Hygiene);
            GetMotiveDisplay(UIMotiveElement.Bladder).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Bladder);
            GetMotiveDisplay(UIMotiveElement.Energy).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Energy);
            GetMotiveDisplay(UIMotiveElement.Fun).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Fun);
            GetMotiveDisplay(UIMotiveElement.Social).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Social);
            GetMotiveDisplay(UIMotiveElement.Room).MotiveValue = Game.SelectedAvatar.GetMotiveData(VMMotive.Room);
        }
    }
}
