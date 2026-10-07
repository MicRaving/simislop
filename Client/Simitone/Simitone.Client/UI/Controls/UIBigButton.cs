using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Controls
{
    /// <summary>
    /// The large confirm/dismiss plate used by the dialog-style panels (the Options dialog's
    /// OK button, for instance).
    ///
    /// <para>
    /// Desktop and touch builds get different artwork and different type sizes. The two art
    /// pairs are not interchangeable: the desktop plate is 485x48 and is drawn with a 14pt
    /// caption, the touch plate is 600x100 with a 37pt one, so using the touch plate on
    /// desktop produced an oversized button with a caption too big to fit inside it.
    /// </para>
    /// </summary>
    public class UIBigButton : UIButton
    {
        /// <summary>Width the caption was last fitted to, so the fit is only redone on change.</summary>
        private float _mySize = 0;

        /// <summary>
        /// The elided caption to draw instead of <see cref="UIButton.Caption"/> when the real
        /// one does not fit. Empty when the caption fits.
        /// </summary>
        private string _ghostText = string.Empty;

        public UIBigButton(bool green) : base()
        {
            // UIButton's parameterless ctor hands us the shared standard button texture, which
            // is then replaced below. Reading the screen is safe here: a dialog is only ever
            // built while one is current, and the null-coalesce keeps a headless construct from
            // throwing if that ever stops being true.
            bool Desktop = (UIScreen.Current as TS1GameScreen)?.Desktop ?? true;
            string texRef = Desktop ? (green ? "greenbutton.png" : "button.png") : (green ? "greenbutton.mobile.png" : "button.mobile.png");

            CaptionStyle = CaptionStyle.Clone();
            CaptionStyle.Size = Desktop ? 14 : 37;
            CaptionStyle.Color = green ? UIStyle.Current.GreenBtnTxt : UIStyle.Current.BtnTxt;
            CaptionStyle.DisabledColor = UIStyle.Current.BtnDisable;
            CaptionStyle.HighlightedColor = green ? UIStyle.Current.BtnPrimaryHighlight : UIStyle.Current.BtnNormalHighlight;
            Texture = Content.Get().CustomUI.Get(texRef).Get(GameFacade.GraphicsDevice);
        }

        /// <summary>
        /// Works out the elided caption for the current width, if the caption needs it.
        /// </summary>
        /// <remarks>
        /// The button is a three-slice, so <see cref="UIButton.Width"/> is the drawn width and
        /// can be set by anything from a tween to a resize; the result is cached against it
        /// rather than recomputed on every frame.
        /// </remarks>
        private void SetGhostText()
        {
            _ghostText = string.Empty;
            if (string.IsNullOrEmpty(Caption)) return;

            var desiredSize = CaptionStyle.MeasureString(Caption);
            if (desiredSize.X <= Width) return;

            //truncate
            float overage = desiredSize.X - Width;
            float charSize = desiredSize.X / Caption.Length;
            if (charSize <= 0) return;
            int trimChars = 4 + (int)(overage / charSize); // 3 '.' plus truncating error correction
            int totalChars = Math.Max(0, Caption.Length - trimChars);
            _ghostText = Caption.Substring(0, totalChars) + "...";
        }

        public override void Draw(UISpriteBatch SBatch)
        {
            if (_mySize != Width) // on size changed invalidate size changed
            {
                SetGhostText();
                _mySize = Width;
            }

            // draw the fitted caption, but leave the real one on the button: it is what the
            // click handler and any owner reading Caption sees, and restoring it after the
            // base draw keeps the elision invisible to everything except the pixels.
            string caption = Caption;
            if (!string.IsNullOrWhiteSpace(_ghostText))
                Caption = _ghostText;

            base.Draw(SBatch);

            Caption = caption;
        }
    }
}
