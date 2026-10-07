/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Model;
using System;

namespace Simitone.Client.UI.Controls
{
    /// <summary>
    /// An on/off switch: a pill track with a knob that sits at the left when off and the right
    /// when on, the way every settings screen a player has used draws a boolean.
    ///
    /// <para>
    /// <b>Why not the checkbox art.</b> A tick box is a two-state control too, but it reads as
    /// "include this in a set" rather than "turn this on", and every row on both the in-game
    /// settings strip and the Options pages is a thing being turned on. The switch makes the
    /// current state legible without reading a tick, which is the whole point of a settings row.
    /// </para>
    ///
    /// <para>
    /// <b>It is a <see cref="UIButton"/>.</b> <c>Selected</c>, <c>Disabled</c>, <c>Tooltip</c>,
    /// <c>Opacity</c>, <c>Position</c> and <c>OnButtonClick</c> are all the ones callers already
    /// use on the checkbox it replaces, so a call site changes by its construction line and
    /// nothing else. <c>Draw</c> is overridden, so the inherited texture is a placeholder that is
    /// never drawn - only its size is used, to size the hit area.
    /// </para>
    /// </summary>
    public class UISwitch : UIButton
    {
        /// <summary>Width of the track, in pixels.</summary>
        public const int TrackWidth = 52;

        /// <summary>Height of the track, in pixels. Also the knob's diameter plus its inset.</summary>
        public const int TrackHeight = 28;

        /// <summary>Gap between the knob and the track's edge, in pixels.</summary>
        private const int KnobInset = 3;

        /// <summary>Colour of the track when the switch is off.</summary>
        private static readonly Color OffTrack = new Color(58, 66, 78, 255);

        public UISwitch() : base(GetTrack(GameFacade.GraphicsDevice, false))
        {
            ImageStates = 1;
            Size = new Vector2(TrackWidth, TrackHeight);
        }

        public override void Draw(UISpriteBatch SBatch)
        {
            if (!Visible) return;

            var gd = GameFacade.GraphicsDevice;
            var tint = Color.White * (Disabled ? 0.5f : 1f) * Opacity;

            DrawLocalTexture(SBatch, GetTrack(gd, Selected), null, Vector2.Zero, Vector2.One, tint);

            var knobX = Selected ? (TrackWidth - TrackHeight + KnobInset) : KnobInset;
            DrawLocalTexture(SBatch, GetKnob(gd), null, new Vector2(knobX, KnobInset), Vector2.One, tint);
        }

        // ---- generated art --------------------------------------------------------------------

        private static Texture2D TrackOffTexture, TrackOnTexture, KnobTexture;
        private static GraphicsDevice ArtDevice;

        private static void EnsureArt(GraphicsDevice gd)
        {
            if (TrackOffTexture != null && ArtDevice == gd) return;
            TrackOffTexture = MakeRounded(gd, TrackWidth, TrackHeight, TrackHeight / 2, OffTrack);
            TrackOnTexture = MakeRounded(gd, TrackWidth, TrackHeight, TrackHeight / 2, UIStyle.Current.BtnActive);
            KnobTexture = MakeCircle(gd, TrackHeight - (KnobInset * 2), Color.White);
            ArtDevice = gd;
        }

        private static Texture2D GetTrack(GraphicsDevice gd, bool on)
        {
            EnsureArt(gd);
            return on ? TrackOnTexture : TrackOffTexture;
        }

        private static Texture2D GetKnob(GraphicsDevice gd)
        {
            EnsureArt(gd);
            return KnobTexture;
        }

        private static Texture2D MakeRounded(GraphicsDevice gd, int width, int height, int radius, Color color)
        {
            var data = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (InRoundedRect(x, y, width, height, radius)) data[y * width + x] = color;
                }
            }
            var tex = new Texture2D(gd, width, height);
            tex.SetData(data);
            return tex;
        }

        private static Texture2D MakeCircle(GraphicsDevice gd, int diameter, Color color)
        {
            var data = new Color[diameter * diameter];
            var r = diameter / 2f;
            for (int y = 0; y < diameter; y++)
            {
                for (int x = 0; x < diameter; x++)
                {
                    var dx = (x + 0.5f) - r;
                    var dy = (y + 0.5f) - r;
                    if ((dx * dx) + (dy * dy) <= r * r) data[y * diameter + x] = color;
                }
            }
            var tex = new Texture2D(gd, diameter, diameter);
            tex.SetData(data);
            return tex;
        }

        private static bool InRoundedRect(int x, int y, int width, int height, int radius)
        {
            var cx = Math.Clamp(x, radius, width - 1 - radius);
            var cy = Math.Clamp(y, radius, height - 1 - radius);
            var dx = x - cx;
            var dy = y - cy;
            return (dx * dx) + (dy * dy) <= radius * radius;
        }
    }
}
