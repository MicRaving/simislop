/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// Simitone's credits screen: a centred, scrollable panel of
    /// <see cref="CreditsData"/> sections with a Close button.
    ///
    /// <para>
    /// FreeSO's own credits screen (<c>FSO.Client.UI.Screens.Credits</c>) cannot be reused here
    /// for the same reason <c>UISettingsPanel</c> cannot reuse FreeSO's options dialog: it is
    /// built by rendering <c>credits.uis</c> out of the TSO content tree, which Simitone does not
    /// ship, so <c>UIContainer.RenderScript</c> throws a <c>FileNotFoundException</c> looking for
    /// it. It is also a <c>GameScreen</c> push, not a panel you can hang off the live mode UI.
    /// This panel is therefore Simitone-styled, like the rest of Simitone's own UI.
    /// </para>
    ///
    /// <para>
    /// The text is Simitone's own (see <see cref="CreditsData"/> for why it cannot be the game's
    /// <c>Credits.iff</c>), and the panel needs no new artwork: the plate behind it and the Close
    /// button are rasterised at runtime with <c>TextureGenerator</c>, exactly as
    /// <c>UISettingsPanel</c> does for its Apply/Revert buttons, and the type is the standard UI
    /// font. Nothing here touches a <c>GraphicsDevice</c> outside the running game, so the whole
    /// content side stays headless-testable.
    /// </para>
    ///
    /// <para>
    /// Show it with <see cref="Show(TS1GameScreen)"/> - typically straight out of an options
    /// entry's click handler - which puts it up as a modal dialog. Scrolling is the mouse wheel
    /// over the text, or a click-drag on it; the drag threshold is
    /// <see cref="TouchInput.ScrollThreshold"/>, the same DPI-aware one the lot's touch helper
    /// and <c>UITouchScroll</c> use, so a click on the text is not mistaken for a scroll.
    /// </para>
    ///
    /// <para>
    /// The three strings the panel spells out itself (title, scroll hint, Close) come from
    /// Simitone's own string table through <see cref="SimitoneStrings.Get(string, string)"/>,
    /// with the English kept as the second argument. Nothing about the panel depends on the
    /// table being there, which is what lets it keep working in a build whose
    /// <c>Content/UI/uitext</c> folder never made it out of the package. The credits
    /// <i>body</i> is the exception and deliberately is not a string: it is
    /// <see cref="CreditsData"/>'s frozen prose, which is wrapped to the column width and
    /// would need re-flowing per language.
    /// </para>
    /// </summary>
    public class UICreditsPanel : UIContainer
    {
        #region Layout constants

        /// <summary>Gap kept between the panel and the edge of the screen.</summary>
        private const int ScreenMargin = 24;

        /// <summary>Narrowest the panel gets before the screen runs out of room for it.</summary>
        private const int MinPanelWidth = 460;

        /// <summary>Widest the panel gets, however large the screen is.</summary>
        private const int MaxPanelWidth = 780;

        /// <summary>Shortest the panel gets before the screen runs out of room for it.</summary>
        private const int MinPanelHeight = 260;

        /// <summary>Tallest the panel gets, however tall the screen is.</summary>
        private const int MaxPanelHeight = 640;

        /// <summary>Height of the title strip.</summary>
        private const int HeaderHeight = 28;

        /// <summary>Height of the Close button strip.</summary>
        private const int FooterHeight = 58;

        /// <summary>Padding between the panel edge and its text.</summary>
        private const int TextInset = 16;

        /// <summary>Width of the scroll indicator.</summary>
        private const int ScrollBarWidth = 6;

        /// <summary>Gap between the text column and the scroll indicator.</summary>
        private const int ScrollBarGap = 10;

        private const int TitleFontSize = 19;
        private const int HeadingFontSize = 15;
        private const int BodyFontSize = 12;
        private const int HintFontSize = 10;

        /// <summary>How far one wheel notch scrolls, in pixels.</summary>
        private const float WheelPixelsPerNotch = 45f;

        /// <summary>One wheel notch's worth of <c>MouseState.ScrollWheelValue</c> movement.</summary>
        private const int WheelNotch = 120;

        /// <summary>Seconds the panel takes to fade in or out.</summary>
        private const float FadeSeconds = 0.2f;

        /// <summary>Milliseconds <see cref="Close"/> waits before unhooking the dialog.</summary>
        private const int CloseDelay = 220;

        /// <summary>
        /// Sample used to work out how many characters of the body font fit the text column.
        /// Weighted towards the lowercase letters the credits are actually written in, since a
        /// sample of capitals and punctuation would under-fill the column.
        /// </summary>
        private const string WidthSample = "abcdefghijklmnopqrstuvwxyz ,.'-:()/ ";

        /// <summary>
        /// Safety factor on the character budget, because a width measured from one sample is an
        /// average and individual words are wider than average. Erring narrow just means the odd
        /// line is a little short of the right edge.
        /// </summary>
        private const float WidthSafety = 0.94f;

        #endregion

        /// <summary>The live mode screen the credits were opened from.</summary>
        public TS1GameScreen Game;

        /// <summary>Raised once, after the panel has faded out and left the dialog list.</summary>
        public event Action<UICreditsPanel> Closed;

        private static UICreditsPanel current;

        /// <summary>
        /// The credits panel currently on screen, or null. Opening the credits twice in a row
        /// brings the existing one forward instead of stacking a second copy.
        /// </summary>
        public static UICreditsPanel Current
        {
            get { return current; }
        }

        private readonly UIDiagonalStripe backdrop;
        private readonly UIDiagonalStripe plate;
        private readonly UILabel titleLabel;
        private readonly UILabel hintLabel;
        private readonly UIContainer viewport;
        private readonly ScrollBarIndicator scrollBar;
        private readonly UIButton closeButton;
        private readonly List<UIElement> fadeTargets = new List<UIElement>();

        private readonly List<UILabel> rowLabels = new List<UILabel>();
        private readonly List<float> rowTops = new List<float>();
        private readonly List<float> rowHeights = new List<float>();

        private readonly TextStyle headingStyle;
        private readonly TextStyle bodyStyle;

        private float viewportWidth;
        private float contentHeight;
        private float scroll;
        private bool closing;

        private int lastWheel;
        private bool wheelInvalid;

        private UIMouseEventRef dragRegion;
        private int dragMouseId = -1;
        private Point dragAnchor;
        private float dragStartScroll;
        private bool dragging;

        private GameThreadTimeout closeTimeout;

        private static Texture2D closePlate;

        /// <summary>
        /// Builds the panel and lays it out against the current UI screen. Nothing is on screen
        /// until <see cref="Show()"/> is called.
        /// </summary>
        /// <param name="game">
        /// The live mode screen the credits were opened from. Kept for the caller; the panel
        /// itself only needs the current UI screen's size, which it reads on every relayout.
        /// </param>
        public UICreditsPanel(TS1GameScreen game)
        {
            Game = game;

            headingStyle = TextStyle.DefaultLabel.Clone();
            headingStyle.Size = HeadingFontSize;
            headingStyle.Color = UIStyle.Current.ActiveSelection;

            bodyStyle = TextStyle.DefaultLabel.Clone();
            bodyStyle.Size = BodyFontSize;
            bodyStyle.Color = UIStyle.Current.Text;

            // Paint order is child order: the backdrop, then the plate, then the text on top of
            // both. The backdrop is a child rather than a sibling because a panel only knows how
            // to fade its own children - see the Fade remarks.
            backdrop = new UIDiagonalStripe(new Point(0, 0), UIDiagonalStripeSide.LEFT, new Color(0, 70, 140) * 0.33f);
            Add(backdrop);
            fadeTargets.Add(backdrop);

            plate = new UIDiagonalStripe(new Point(0, 0), UIDiagonalStripeSide.RIGHT, UIStyle.Current.Bg);
            Add(plate);
            fadeTargets.Add(plate);

            viewport = new UIContainer();
            Add(viewport);
            fadeTargets.Add(viewport);

            scrollBar = new ScrollBarIndicator();
            Add(scrollBar);
            fadeTargets.Add(scrollBar);

            titleLabel = new UILabel();
            titleLabel.Caption = SimitoneStrings.Get("1", "Credits");
            titleLabel.CaptionStyle = TextStyle.DefaultLabel.Clone();
            titleLabel.CaptionStyle.Size = TitleFontSize;
            titleLabel.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            titleLabel.Alignment = TextAlignment.Middle | TextAlignment.Left;
            Add(titleLabel);
            fadeTargets.Add(titleLabel);

            hintLabel = new UILabel();
            hintLabel.Caption = SimitoneStrings.Get("3", "Scroll with the mouse wheel, or click and drag the text.");
            hintLabel.CaptionStyle = TextStyle.DefaultLabel.Clone();
            hintLabel.CaptionStyle.Size = HintFontSize;
            hintLabel.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            hintLabel.Alignment = TextAlignment.Middle | TextAlignment.Right;
            Add(hintLabel);
            fadeTargets.Add(hintLabel);

            closeButton = new UIButton(GetClosePlate(GameFacade.GraphicsDevice));
            closeButton.ImageStates = 1;
            closeButton.CaptionStyle = closeButton.CaptionStyle.Clone();
            closeButton.CaptionStyle.Size = 13;
            closeButton.CaptionStyle.Color = UIStyle.Current.BtnTxt;
            closeButton.CaptionStyle.SelectedColor = UIStyle.Current.BtnTxt;
            closeButton.Caption = SimitoneStrings.Get("2", "Close");
            closeButton.AutoMargins = 10;
            closeButton.OnButtonClick += (b) => Close();
            Add(closeButton);
            fadeTargets.Add(closeButton);

            // Drag-to-scroll has to be listened for on the text column, and the column's size is
            // only known once the first layout pass has run, so the region is patched in Relayout.
            dragRegion = ListenForMouse(new Rectangle(0, 0, 1, 1), OnViewportMouse);

            Relayout();
            Fade = 0f;
        }

        /// <summary>
        /// Builds the credits panel and shows it modally, the way an options entry should open
        /// it. If it is already open, the existing panel is brought forward and returned instead
        /// of a second copy being stacked on top of it.
        /// </summary>
        /// <param name="game">The live mode screen the credits were opened from.</param>
        /// <returns>The panel now on screen.</returns>
        public static UICreditsPanel Show(TS1GameScreen game)
        {
            // A panel that is still parented is genuinely on screen, so bring it forward. One
            // with no parent was torn down without going through Close (a screen change, say),
            // so it is stale and gets replaced.
            if (current != null && current.Parent != null)
            {
                if (current.Parent is UIContainer container) container.SendToFront(current);
                return current;
            }

            var panel = new UICreditsPanel(game);
            panel.Show();
            return panel;
        }

        /// <summary>
        /// Puts the panel up as a modal dialog and fades it in. Safe to call on a panel that is
        /// already showing: it just fades in again. A caller that adds the panel to its own
        /// container instead gets the fade without the dialog registration, because a parent has
        /// already been set by then.
        /// </summary>
        public void Show()
        {
            // Re-opening during the close fade has to cancel the removal Close queued, otherwise
            // the panel fades back in and is then yanked off the screen anyway.
            closeTimeout?.Clear();
            closeTimeout = null;
            if (Parent == null) UIScreen.GlobalShowDialog(this, true);
            current = this;
            closing = false;
            GameFacade.Screens.Tween.To(this, FadeSeconds,
                new Dictionary<string, float>() { { "Fade", 1f } }, TweenQuad.EaseOut);
        }

        /// <summary>
        /// Fades the panel out and takes it off the dialog list, raising <see cref="Closed"/>.
        /// Calling it twice does nothing the second time.
        /// </summary>
        public void Close()
        {
            if (closing) return;
            closing = true;
            dragMouseId = -1;
            dragging = false;
            GameFacade.Screens.Tween.To(this, FadeSeconds,
                new Dictionary<string, float>() { { "Fade", 0f } }, TweenQuad.EaseIn);
            closeTimeout = GameThread.SetTimeout(() =>
            {
                closeTimeout = null;
                UIScreen.RemoveDialog(this);
                if (Parent != null) Parent.Remove(this);
                if (ReferenceEquals(current, this)) current = null;
                Closed?.Invoke(this);
            }, CloseDelay);
        }

        private float _fade;

        /// <summary>
        /// Panel opacity, 0 to 1. Tweened by <see cref="Show"/> and <see cref="Close"/>; exposed
        /// because the tween sets properties by name. The framework has no parent-to-child
        /// opacity propagation, so this pushes the value onto the parts the panel is made of.
        /// </summary>
        public float Fade
        {
            get { return _fade; }
            set
            {
                _fade = Math.Max(0f, Math.Min(1f, value));
                foreach (var target in fadeTargets) target.Opacity = _fade;
                // The rows are rebuilt on every relayout, so they are faded here rather than
                // accumulated in fadeTargets, which would keep every discarded row alive.
                foreach (var row in rowLabels) row.Opacity = _fade;
            }
        }

        /// <summary>Current scroll offset, in pixels from the top of the text column.</summary>
        public float ScrollOffset
        {
            get { return scroll; }
        }

        public override void GameResized()
        {
            base.GameResized();
            Relayout();
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (closing) return;

            // Rows are only moved when the offset or the box actually changes, and both of those
            // update them at the point of change - this is not a per-frame relayout.
            UpdateWheelScroll(state);
            UpdateDragScroll(state);
        }

        public override Rectangle GetBounds()
        {
            return new Rectangle(0, 0, (int)Size.X, (int)Size.Y);
        }

        /// <summary>
        /// Recomputes the panel box, the text column and the rows inside it. Called on
        /// construction and on every resize, because the box is derived from the screen.
        /// </summary>
        private void Relayout()
        {
            var screen = GameFacade.Screens.CurrentUIScreen;
            int screenWidth = screen == null ? 800 : screen.ScreenWidth;
            int screenHeight = screen == null ? 600 : screen.ScreenHeight;

            float width = Math.Min(MaxPanelWidth, Math.Max(MinPanelWidth, screenWidth - (2 * ScreenMargin)));
            width = Math.Min(width, Math.Max(80, screenWidth - 8));
            float height = Math.Min(MaxPanelHeight, Math.Max(MinPanelHeight, screenHeight - (2 * ScreenMargin)));
            height = Math.Min(height, Math.Max(80, screenHeight - 8));

            Size = new Vector2(width, height);
            Position = new Vector2((screenWidth - width) / 2f, (screenHeight - height) / 2f);

            plate.BodySize = new Point((int)width, (int)height);

            // The backdrop is anchored to the screen, not to the panel box: it sits at the
            // panel's negated position and covers everything.
            backdrop.Position = -Position;
            backdrop.BodySize = new Point(screenWidth, screenHeight);

            titleLabel.Position = new Vector2(TextInset, 2);
            titleLabel.Size = new Vector2(width - (2 * TextInset), TitleFontSize);

            float footerY = height - FooterHeight;
            hintLabel.Position = new Vector2(TextInset, footerY);
            hintLabel.Size = new Vector2(width - (2 * TextInset), HintFontSize);

            closeButton.Position = new Vector2((width - closeButton.Width) / 2f, footerY + 14);

            viewport.Position = new Vector2(TextInset, HeaderHeight);
            viewportWidth = Math.Max(40, width - (2 * TextInset) - ScrollBarWidth - ScrollBarGap);
            viewport.Size = new Vector2(viewportWidth, Math.Max(20, footerY - HeaderHeight));

            scrollBar.Position = new Vector2(width - TextInset - ScrollBarWidth, HeaderHeight);
            scrollBar.Size = new Vector2(ScrollBarWidth, Math.Max(20, footerY - HeaderHeight));

            if (dragRegion != null)
            {
                dragRegion.Region = new Rectangle((int)viewport.X, (int)viewport.Y,
                    (int)viewportWidth, (int)viewport.Size.Y);
            }

            BuildRows();
            ClampScroll();
        }

        /// <summary>
        /// Lays the credit text out into one label per row, wrapped to the width of the text
        /// column.
        /// </summary>
        /// <remarks>
        /// The wrap width is a character budget rather than a pixel one, because
        /// <see cref="CreditsData"/> is a pure data helper that must stay headless. The budget is
        /// derived from the body font's measured average character width (see
        /// <see cref="WidthSample"/>), which is close enough for a column of English prose; a
        /// line that comes out marginally narrow costs a couple of pixels of right margin, and a
        /// line that came out marginally wide would be culled by the panel's own right edge
        /// rather than spill over the scroll indicator.
        /// </remarks>
        private void BuildRows()
        {
            ClearRows();

            int wrapChars = CharsPerLine(viewportWidth, bodyStyle);
            var rows = CreditsData.BuildDisplayLines(CreditsData.Sections, wrapChars);

            float y = 0;
            foreach (var row in rows)
            {
                var label = new UILabel();
                label.CaptionStyle = (row.IsHeading ? headingStyle : bodyStyle).Clone();
                label.CaptionStyle.Size = row.IsHeading ? HeadingFontSize : BodyFontSize;
                label.Caption = row.Text;
                label.Alignment = TextAlignment.Top | TextAlignment.Left;
                // The width is set so the label measures against the text column, and the height is
                // left at one: the row's own height comes from the font's line height, which is
                // what the scroll arithmetic below is in.
                label.Size = new Vector2(viewportWidth, 1);
                label.Position = new Vector2(0, y);
                label.Opacity = Fade;
                viewport.Add(label);

                float height = label.CaptionStyle.LineHeight;
                rowLabels.Add(label);
                rowTops.Add(y);
                rowHeights.Add(height);
                y += height;
            }
            contentHeight = y;
            UpdateRows();
        }

        /// <summary>
        /// Detaches the current rows from the viewport and forgets them, so a relayout does not
        /// leave a discarded set of labels parented (and updated) behind.
        /// </summary>
        private void ClearRows()
        {
            foreach (var label in rowLabels) viewport.Remove(label);
            rowLabels.Clear();
            rowTops.Clear();
            rowHeights.Clear();
            contentHeight = 0;
        }

        /// <summary>
        /// How many characters of <paramref name="style"/> fit in <paramref name="width"/>
        /// pixels, from the font's own measured average character width. Falls back to
        /// <see cref="CreditsData.DefaultWrapChars"/> if the font measures as unmeasurable.
        /// </summary>
        private static int CharsPerLine(float width, TextStyle style)
        {
            float measured = style.MeasureString(WidthSample).X;
            if (measured <= 0) return CreditsData.DefaultWrapChars;
            float perChar = (measured / WidthSample.Length) / WidthSafety;
            if (perChar <= 0) return CreditsData.DefaultWrapChars;
            return Math.Max(CreditsData.MinWrapChars, (int)(width / perChar));
        }

        private void UpdateWheelScroll(UpdateState state)
        {
            // A wheel delta accumulated while the window was not focused is not a gesture, it is
            // a jump, so the first focused frame re-baselines instead of scrolling by it. Same
            // guard UILotControlTouchHelper uses for the camera.
            if (!state.WindowFocused)
            {
                wheelInvalid = true;
                return;
            }
            if (wheelInvalid)
            {
                lastWheel = state.MouseState.ScrollWheelValue;
                wheelInvalid = false;
            }

            int wheel = state.MouseState.ScrollWheelValue;
            if (wheel == lastWheel) return;
            int delta = wheel - lastWheel;
            lastWheel = wheel;
            if (delta == 0) return;
            if (!HitTestArea(state.MouseState, ViewportBounds(), true)) return;

            ScrollBy((delta / (float)WheelNotch) * WheelPixelsPerNotch);
        }

        private void OnViewportMouse(UIMouseEventType type, UpdateState state)
        {
            switch (type)
            {
                case UIMouseEventType.MouseDown:
                    dragMouseId = state.CurrentMouseID;
                    dragAnchor = state.MouseState.Position;
                    dragStartScroll = scroll;
                    dragging = false;
                    break;
                case UIMouseEventType.MouseUp:
                    dragMouseId = -1;
                    dragging = false;
                    break;
            }
        }

        private void UpdateDragScroll(UpdateState state)
        {
            if (dragMouseId == -1) return;

            var mouse = state.MouseStates.FirstOrDefault(x => x.ID == dragMouseId);
            if (mouse == null)
            {
                // Released outside the window, or the device went away.
                dragMouseId = -1;
                dragging = false;
                return;
            }

            var delta = mouse.MouseState.Position - dragAnchor;
            if (!dragging)
            {
                if (Math.Abs(delta.Y) <= TouchInput.ScrollThreshold(FSOEnvironment.DPIScaleFactor)) return;
                dragging = true;
            }
            if (dragging) ScrollTo(dragStartScroll - delta.Y);
        }

        private Rectangle ViewportBounds()
        {
            return new Rectangle((int)viewport.X, (int)viewport.Y,
                (int)viewportWidth, (int)viewport.Size.Y);
        }

        private void ScrollBy(float amount)
        {
            ScrollTo(scroll + amount);
        }

        private void ScrollTo(float value)
        {
            scroll = value;
            ClampScroll();
            UpdateRows();
        }

        private void ClampScroll()
        {
            float maxScroll = Math.Max(0, contentHeight - viewport.Size.Y);
            scroll = Math.Max(0, Math.Min(maxScroll, scroll));
        }

        /// <summary>
        /// Moves the rows to their scrolled positions and hides the ones outside the text
        /// column. The panel is a plain container rather than a cached one, so this culling is
        /// what stops a scrolled row drawing over the title and the Close button.
        /// </summary>
        private void UpdateRows()
        {
            float viewHeight = viewport.Size.Y;
            bool scrollable = contentHeight > viewHeight;
            for (int i = 0; i < rowLabels.Count; i++)
            {
                float top = rowTops[i] - scroll;
                var label = rowLabels[i];
                label.Y = top;
                label.Visible = top < viewHeight && (top + rowHeights[i]) > 0;
            }

            scrollBar.Visible = scrollable;
            if (scrollable)
            {
                float maxScroll = Math.Max(1, contentHeight - viewHeight);
                scrollBar.Fraction = scroll / maxScroll;
                scrollBar.ThumbFraction = Math.Max(0.08f, viewHeight / contentHeight);
            }
        }

        /// <summary>
        /// The plate behind the Close button. Generated once and shared, like the Apply/Revert
        /// plates in <c>UISettingsPanel</c>, because <c>TextureGenerator</c> builds a fresh
        /// texture on every call.
        /// </summary>
        private static Texture2D GetClosePlate(GraphicsDevice gd)
        {
            if (closePlate == null)
                closePlate = TextureGenerator.GenerateRoundedRectangle(gd, new Color(120, 140, 170), 24, 40, 6);
            return closePlate;
        }

        /// <summary>
        /// A plain two-tone vertical indicator for the scroll position: a full-height track and a
        /// thumb whose size and offset track the visible fraction of the text. Drawn with the
        /// shared white pixel, so it costs no artwork.
        /// </summary>
        private class ScrollBarIndicator : UIElement
        {
            /// <summary>Where the top of the thumb sits, 0 (top) to 1 (bottom).</summary>
            public float Fraction;

            /// <summary>How much of the track the thumb covers, 0 to 1.</summary>
            public float ThumbFraction = 0.2f;

            public override void Draw(UISpriteBatch batch)
            {
                if (!Visible) return;
                var white = TextureGenerator.GetPxWhite(batch.GraphicsDevice);
                float trackHeight = Size.Y;
                float thumbHeight = Math.Max(12, trackHeight * Math.Max(0.05f, Math.Min(1f, ThumbFraction)));

                DrawLocalTexture(batch, white, null, Vector2.Zero, new Vector2(Size.X, trackHeight),
                    new Color(0, 0, 0, 90));
                float top = (trackHeight - thumbHeight) * Math.Max(0f, Math.Min(1f, Fraction));
                DrawLocalTexture(batch, white, null, new Vector2(0, top), new Vector2(Size.X, thumbHeight),
                    UIStyle.Current.SecondaryText);
            }

            public override Rectangle GetBounds()
            {
                return new Rectangle(0, 0, (int)Size.X, (int)Size.Y);
            }
        }
    }
}
