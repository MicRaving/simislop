/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels.EODs
{
    /// <summary>
    /// The chrome Simitone draws around an EOD panel: what it is called, and a button that ends
    /// the interaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FSO's own EOD windows come with all of this and do not need it twice: <c>UILiveMode</c>
    /// puts the panel inside a frame image, gives the frame its size from
    /// <c>EODLiveModeOpt</c>, and hangs a close button, an expand button, a timer and a tip line
    /// off the side. Simitone has no live-mode frame - <c>EODHost</c> puts the panel on the lot
    /// itself - so without this the panel is a set of floating controls with no title and no way
    /// out of the window other than Escape, and the sim's interaction thread stays blocked behind
    /// it.
    /// </para>
    /// <para>
    /// The buttons are built with no texture at all, so <c>UIButton</c> falls back to
    /// <c>StandardButton</c> - The Sims 1 dialog button, which every supported install has. The
    /// one thing this deliberately does not do is put a picture behind the panel: the retail
    /// layouts carry their own artwork, and ours do not, and a stretched grey rectangle would
    /// only hide that. A title and a close button are honest; a fake window frame is not.
    /// </para>
    /// </remarks>
    public sealed class EODPanelFrame : UIContainer
    {
        /// <summary>Height of the title strip, in pixels.</summary>
        public const int TitleHeight = 22;

        /// <summary>Gap between the panel and the edge of its title strip.</summary>
        public const int Padding = 6;

        /// <summary>
        /// Id in Simitone's own string table for the close button's caption, and one for its
        /// tooltip. Hand written rather than generated, and deliberately below
        /// <c>EODLayoutStrings.FirstId</c> so the two blocks cannot collide.
        /// </summary>
        public const string CaptionStringId = "990";

        /// <summary>See <see cref="CaptionStringId"/>.</summary>
        public const string TooltipStringId = "991";

        private readonly UILabel Title;
        private readonly UIButton Close;

        /// <summary>Id of the title label, so a caller (or a test) can find it.</summary>
        public const string TitleId = "SimitoneEODTitle";

        /// <summary>Id of the close button, matching <see cref="TitleId"/>.</summary>
        public const string CloseId = "SimitoneEODClose";

        /// <summary>The panel this frame is currently wrapped around, or null.</summary>
        public UIContainer Wrapped { get; private set; }

        /// <summary>Whether the frame is showing anything.</summary>
        public bool Showing { get { return Visible; } }

        public EODPanelFrame()
        {
            Title = new UILabel
            {
                ID = TitleId,
                Alignment = TextAlignment.Left,
                CaptionStyle = TextStyle.DefaultTitle,
                Position = new Vector2(Padding, 2)
            };
            Add(Title);

            Close = new UIButton
            {
                ID = CloseId,
                Caption = SimitoneStrings.Get(CaptionStringId, "Close"),
                Tooltip = SimitoneStrings.Get(TooltipStringId, "Close this minigame and end the interaction")
            };
            Close.OnButtonClick += OnCloseClicked;
            Add(Close);

            Visible = false;
        }

        /// <summary>
        /// Wraps a panel: sizes the frame to the panel, names it, and shows both. Passing null
        /// hides the frame and forgets the panel, which is what happens when the plugin leaves.
        /// </summary>
        public void Wrap(UIContainer panel, string title)
        {
            Wrapped = panel;
            if (panel == null)
            {
                Visible = false;
                return;
            }

            var extent = Extent(panel);
            Title.Caption = title ?? string.Empty;
            Title.Size = new Vector2(Math.Max(0f, extent.X - Padding * 2 - Close.Size.X - 8), TitleHeight - 4);

            //The close button is deliberately outside the extent rather than inside it: it is
            //chrome, so it must not change where the panel ends up, or the two would chase each
            //other on a resize.
            Close.Position = new Vector2(Math.Max(0f, extent.X - Close.Size.X - Padding), 2);
            Size = new Vector2(extent.X, extent.Y + TitleHeight);
            Visible = true;
        }

        private void OnCloseClicked(UIElement button)
        {
            if (OnCloseRequested != null) OnCloseRequested();
        }

        /// <summary>
        /// Raised when the player presses the close button. The host turns it into the same
        /// "close" message the panel's own close button sends, so the plugin is told the
        /// interaction is over rather than being left with a window that vanished.
        /// </summary>
        public event Action OnCloseRequested;

        /// <summary>
        /// Re-lays the frame out around its panel. Called on a resize, because a <c>.uis</c>
        /// panel is authored at absolute coordinates and nothing else in the tree would move it.
        /// </summary>
        public void Reflow()
        {
            if (Wrapped != null) Wrap(Wrapped, Title.Caption);
        }

        /// <summary>
        /// How big a panel actually is, in pixels.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>UIContainer.Size</c> is inherited from <c>UIElement.Size</c>, whose base
        /// implementation returns <c>Vector2.Zero</c> and whose setter does nothing. It is not
        /// a bounding box and it is not maintained as one, so anything that needs to know how
        /// large a panel is has to walk it - which is what this does.
        /// </para>
        /// <para>
        /// Nested containers are walked too, because several panels put their sub-panels (card
        /// hands, the maze's wall and cell containers) inside the panel rather than beside it,
        /// and their contents are what the player sees. The depth limit is not paranoia about
        /// a cycle: a panel that contains itself would otherwise be an infinite loop in the
        /// middle of a lot load.
        /// </para>
        /// <para>
        /// Children with no size of their own - an image with no texture, which is most of what
        /// these layouts contain - contribute their position and nothing else, so a panel whose
        /// only content is pictures still gets a frame around the right area.
        /// </para>
        /// </remarks>
        public static Vector2 Extent(UIContainer panel, int depth = 0)
        {
            if (panel == null) return Vector2.Zero;

            var widest = 0f;
            var tallest = 0f;
            var children = panel.GetChildren();
            if (children == null) return Vector2.Zero;

            foreach (var child in children)
            {
                if (child == null) continue;
                var position = child.Position;
                var size = child.Size;
                var right = position.X + Math.Max(0f, size.X);
                var bottom = position.Y + Math.Max(0f, size.Y);
                if (right > widest) widest = right;
                if (bottom > tallest) tallest = bottom;

                var nested = child as UIContainer;
                if (nested != null && depth < 4)
                {
                    var inner = Extent(nested, depth + 1);
                    var innerRight = position.X + inner.X;
                    var innerBottom = position.Y + inner.Y;
                    if (innerRight > widest) widest = innerRight;
                    if (innerBottom > tallest) tallest = innerBottom;
                }
            }

            return new Vector2(widest, tallest);
        }
    }
}
