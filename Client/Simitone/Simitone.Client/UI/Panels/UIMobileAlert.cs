using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;

namespace Simitone.Client.UI.Panels
{
    public class UIMobileAlert : UIMobileDialog
    {
        private UIAlertOptions m_Options;
        private TextRendererResult m_MessageText;
        private TextStyle m_TextStyle;

        private UIImage Icon;
        private Vector2 IconSpace;

        private List<UIButton> Buttons;
        private UITextBox TextBox;

        public string ResponseText
        {
            get
            {
                return (TextBox == null) ? null : TextBox.CurrentText;
            }
            set
            {
                if (TextBox != null) TextBox.CurrentText = value;
            }
        }

        public UIMobileAlert(UIAlertOptions options) : base()
        {
            this.m_Options = options;

            m_TextStyle = TextStyle.DefaultLabel.Clone();
            m_TextStyle.Size = 19;
            m_TextStyle.Color = Color.White;

            Caption = options.Title;

            Icon = new UIImage();
            Icon.Visible = false;
            Icon.Position = new Vector2(32, 32);
            Icon.SetSize(0, 0);
            Add(Icon);

            /** Determine the size **/
            ComputeText();

            /** Add buttons **/
            Buttons = new List<UIButton>();

            foreach (var button in options.Buttons)
            {
                string buttonText = "";
                if (button.Text != null) buttonText = button.Text;
                else
                {
                    switch (button.Type)
                    {
                        case UIAlertButtonType.OK:
                            buttonText = GameFacade.Strings.GetString("142", "0");
                            break;
                        case UIAlertButtonType.Yes:
                            buttonText = GameFacade.Strings.GetString("142", "2");
                            break;
                        case UIAlertButtonType.No:
                            buttonText = GameFacade.Strings.GetString("142", "3");
                            break;
                        case UIAlertButtonType.Cancel:
                            buttonText = GameFacade.Strings.GetString("142", "1");
                            break;
                    }
                }
                var btnElem = AddButton(buttonText, button.Type, button.Handler == null);
                Buttons.Add(btnElem);
                if (button.Handler != null) btnElem.OnButtonClick += button.Handler;
            }

            if (options.TextEntry)
            {
                TextBox = new UITextBox()
                {
                    MaxChars = options.MaxChars,
                    // the dialog's own text style, so the typed text is the same size and colour
                    // as the message it is answering
                    TextStyle = m_TextStyle.Clone(),
                    FlashOnEmpty = true, // as in TS1, draw attention to a field that needs a response
                    FrameColor = UIStyle.Current.TextBoxSelectionFrameColor
                };
                this.Add(TextBox);

                // the text box is placed from the message's own extent, which is only known once
                // the message has been measured - and it has not been at this point if SetIcon
                // has not run yet, so lay it out now and again whenever the text is recomputed
                ComputeText();
            }

            /** Position buttons **/
            RefreshSize();
        }

        public override void GameResized()
        {
            base.GameResized();
            RefreshSize();
        }

        public void RefreshSize()
        {
            var h = m_Options.Height;

            h = Math.Max(h, Math.Max((int)IconSpace.Y - 25, m_MessageText == null ? 0 : m_MessageText.BoundingBox.Height) + 105);

            // ComputeText hangs the text box off the bottom of the message, so the dialog has to
            // be tall enough to hold it before the buttons are laid out against the height.
            if (TextBoxBottom > 0) h = Math.Max(h, TextBoxBottom + TextBoxGap);

            if (Buttons.Count > 0)
            {
                h += 175;
            }
            else
            {
                h += 32;
            }

            h = ResetButtons(h, true);

            if (Height != h)
            {
                SetHeight(h);
            }
            //update bg with height
        }

        private int ResetButtons(int h, bool setY)
        {
            var btnX = Width/2;
            var btnY = h - 125;
            var totalBtnWidth = Buttons.Sum(x => x.Width);
            int runningWidth = 0;
            int start = 0;
            int i = 0;
            for (i=0; i<Buttons.Count; i++)
            {
                var btn = Buttons[i];
                
                if (runningWidth == 0 || (Width-50) - runningWidth > btn.Width)
                {
                    btn.X = btnX + runningWidth;
                } else
                {
                    btnY += 120;
                    h += 120;
                    //center buttons
                    runningWidth -= 25;
                    for (int j=start; j<i; j++)
                    {
                        Buttons[j].X -= runningWidth / 2;
                    }

                    runningWidth = 0;
                    start = i;
                    btn.X = btnX + runningWidth;
                }
                runningWidth += (int)btn.Width + 25;
                if (setY) btn.Y = btnY;
            }
            runningWidth -= 25;
            for (int j = start; j < i; j++)
            {
                Buttons[j].X -= runningWidth / 2;
            }
            return h;
        }

        private float TargetIX;

        public void SetIcon(Texture2D img, int width, int height)
        {
            if (img.Height < 4) return;
            Icon.Texture = img;

            float scale = Math.Min(3, Math.Min((float)height / (float)img.Height, (float)width / (float)img.Width));
            if (scale * img.Height + 20 < height) height = (int)(scale * img.Height + 20);
            IconSpace = new Vector2(width + 30, height);
            Icon.SetSize(img.Width * scale, img.Height * scale);
            Icon.Position = new Vector2(50, 110) + new Vector2(width / 2 - (img.Width * scale / 2), height / 2 - (img.Height * scale / 2));
            TargetIX = Icon.Position.X;

            ComputeText();
            RefreshSize();
        }

        /// <summary>
        /// Map of buttons attached to this message box.
        /// </summary>
        public Dictionary<UIAlertButtonType, UIButton> ButtonMap = new Dictionary<UIAlertButtonType, UIButton>();

        /// <summary>
        /// Adds a button to this message box.
        /// </summary>
        /// <param name="label">Label of the button.</param>
        /// <param name="type">Type of the button to be added.</param>
        /// <param name="InternalHandler">Should the button's click be handled internally?</param>
        /// <returns></returns>
        private UIButton AddButton(string label, UIAlertButtonType type, bool InternalHandler)
        {
            var btn = new UIBigButton(type == UIAlertButtonType.OK || type == UIAlertButtonType.Yes);
            btn.Visible = false;
            btn.Caption = label;
            if (btn.Width < 275) btn.Width = 275;

            if (InternalHandler)
                btn.OnButtonClick += new ButtonClickDelegate(x =>
                {
                    HandleClose();
                });

            ButtonMap.Add(type, btn);

            this.Add(btn);
            return btn;
        }

        private void HandleClose()
        {
            Close();
        }

        private bool m_TextDirty = false;
        public override void CalculateMatrix()
        {
            base.CalculateMatrix();
            m_TextDirty = true;
        }

        /// <summary>
        /// Gap kept between the bottom of the message and the top of the text box, and again
        /// below the text box so the buttons are not sitting on it.
        /// </summary>
        private const int TextBoxGap = 10;

        /// <summary>
        /// Where the text box ends, worked out by <see cref="ComputeText"/>. Zero when there is
        /// no text box. Read by <see cref="RefreshSize"/> to grow the dialog to fit it.
        /// </summary>
        private int TextBoxBottom = 0;

        private void ComputeText()
        {
            var margin = (IconSpace.X > 0) ? 50 : 80;
            var maxWidth = Width - margin * 2;
            int topLeft = 105;

            m_MessageText = TextRenderer.ComputeText(m_Options.Message, new TextRendererOptions
            {
                Alignment = TextAlignment.Left | TextAlignment.Top,
                MaxWidth = maxWidth,
                Position = new Microsoft.Xna.Framework.Vector2(margin, topLeft),
                Scale = _Scale,
                TextStyle = m_TextStyle,
                WordWrap = true,
                TopLeftIconSpace = IconSpace
            }, this);

            TextBoxBottom = 0;

            // The text box goes directly under the message rather than at a fixed distance from
            // the bottom of the dialog, so a one line prompt and a four line prompt both get a
            // field that looks like it belongs to the text above it.
            if (m_Options.TextEntry && TextBox != null)
            {
                int lineHeight = (int)(m_TextStyle.MeasureString("B").Y * _Scale.Y);
                int msgBottom = m_MessageText.BoundingBox.Bottom;
                if (m_MessageText.BoundingBox.Height <= 0)
                {
                    // TextRenderer's bounding box runs from the top of the first line to the top
                    // of the last, so a single line reports a height of zero and there is no
                    // line count to recover it from (TextRendererResult.Lines is never filled
                    // in). One line is the only case that can land here, so the bottom is
                    // exactly one line down.
                    msgBottom += lineHeight;
                }

                int y = msgBottom + TextBoxGap;
                // as wide as the widest line the message actually wrapped to, so the field
                // lines up with the text it belongs to, but never wider than the dialog
                int w = Math.Max(Math.Min(maxWidth, m_MessageText.MaxWidth), 200);

                TextBox.X = IconSpace.X + margin;
                TextBox.Y = y;
                TextBox.SetSize(w, 50);
                TextBoxBottom = y + 50;
            }

            m_TextDirty = false;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            var off = ((Closing) ? 1 : -1) * (1 - InterpolatedAnimation) * Width;
            var newIX = TargetIX + off;
            if (Icon.X != newIX)
            {
                Icon.Visible = true;
                Icon.X = newIX;
                ResetButtons(Height, false);
                foreach (var btn in Buttons)
                {
                    btn.X += off;
                    btn.Visible = true;
                }
            }
        }

        public override void Draw(UISpriteBatch batch)
        {
            m_TextStyle.Color = UIStyle.Current.DialogText * InterpolatedAnimation;
            base.Draw(batch);

            if (m_TextDirty)
            {
                int previousTextBoxBottom = TextBoxBottom;
                ComputeText();
                // A resize can re-wrap the message onto a different number of lines, which moves
                // the text box. Let the dialog grow to match instead of clipping it. This stops
                // recursing because re-measuring at the new height gives the same line count.
                if (TextBoxBottom != previousTextBoxBottom) RefreshSize();
            }

            TextRenderer.DrawText(m_MessageText.DrawingCommands, this, batch);
        }
    }
}
