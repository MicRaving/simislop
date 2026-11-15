using FSO.Client.UI.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;
using FSO.Client;
using FSO.Client.UI.Framework;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.UI.Model;

namespace Simitone.Client.UI.Controls
{
    /// <summary>
    /// A drop shadow drawn from a single texture, offset from the element it belongs to.
    ///
    /// <para>
    /// One texture, one flat colour and one offset: that is all the shadow art Simitone
    /// ships (a soft radial blob), and it is enough to lift a control off the flat panel
    /// behind it. The offset is a direction plus a depth rather than a fixed point, so a
    /// caller can pick the light direction once and reuse it.
    /// </para>
    /// </summary>
    public class UIShadow
    {
        public UIShadow(Texture2D shadowTexture, float shadowOpacity = .5f, Color? shadowColor = default, float shadowDepth = 2f, Vector2? shadowDirection = default, Vector2? shadowSize = default)
        {
            ShadowTexture = shadowTexture;
            ShadowOpacity = shadowOpacity;
            ShadowColor = shadowColor ?? Color.Black;
            ShadowDepth = shadowDepth;
            ShadowDirection = shadowDirection ?? new Vector2(1, 1);
            ShadowDirection.Normalize();
            ShadowSize = shadowSize ?? new Vector2(shadowTexture.Width, shadowTexture.Height);
        }

        public Texture2D ShadowTexture { get; set; }
        public float ShadowOpacity { get; set; }
        public Color ShadowColor { get; set; }
        public float ShadowDepth { get; set; }
        public Vector2 ShadowDirection { get; set; }
        public Vector2 ShadowSize { get; set; }

        /// <summary>
        /// Draws the shadow into <paramref name="VisualParent"/>'s local space, offset from
        /// <paramref name="Origin"/> - which is the element's own top-left in that space.
        ///
        /// <para>
        /// <see cref="ShadowSize"/> is a size, not a scale, so it is converted against the
        /// texture's own dimensions here; the default (the texture's own size) comes out as
        /// a scale of one, i.e. drawn unscaled. <see cref="ShadowColor"/> and
        /// <see cref="ShadowOpacity"/> are applied as a tint, which is what turns the white
        /// shadow blob into a shadow.
        /// </para>
        /// </summary>
        public void Draw(UIElement VisualParent, Vector2 Origin, SpriteBatch Batch)
        {
            if (ShadowTexture == null) return;

            var scale = new Vector2(
                ShadowTexture.Width == 0 ? 1 : ShadowSize.X / ShadowTexture.Width,
                ShadowTexture.Height == 0 ? 1 : ShadowSize.Y / ShadowTexture.Height);

            VisualParent.DrawLocalTexture(Batch, ShadowTexture, null,
                Origin + (ShadowDirection * ShadowDepth), scale, ShadowColor * ShadowOpacity);
        }
    }

    public class UIElasticButton : UIButton
    {
        public UIElasticButton(Texture2D tex) : base (tex)
        {
            ImageStates = 1;

            if (ClickHandler != null)
            {
                ClickHandler.Region.Width = Texture.Width;
                ClickHandler.Region.Height = Texture.Height;
                ClickHandler.Region.X = Texture.Width / -2;
                ClickHandler.Region.Y = Texture.Height / -2;
            }
        }

        private int LastState;
        private int DownTime = 5;

        /// <summary>
        /// Optional shadow drawn behind the button. Scaled with the button, so it stretches
        /// rather than slides during the elastic press.
        /// </summary>
        public UIShadow ElementShadow { get; set; }

        /// <summary>
        /// Whether this button is the currently active view. The active button is drawn with a
        /// bright ring behind it, so a column of view buttons says which view is on screen
        /// rather than only which ones the player could travel to.
        /// </summary>
        /// <remarks>
        /// A separate flag rather than <see cref="UIButton.Selected"/> because these buttons are
        /// built with a single image state (<see cref="ImageStates"/> is 1), so <c>Selected</c>
        /// picks a frame that does not exist and draws nothing different. The ring is generated
        /// rather than shipped: Simitone has no selection-ring art, and a ring has to scale with
        /// whatever button it marks.
        /// </remarks>
        public bool Active { get; set; }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (CurrentFrame != LastState)
            {
                if (CurrentFrame == 2)
                {
                    if (LastState == 1)
                    {
                        //elsatically snap back to normal scale (pressed)
                        ScaleX = ScaleY = 0.75f;
                        GameFacade.Screens.Tween.To(this, 0.75f, new Dictionary<string, float>() { { "ScaleX", 1.0f }, { "ScaleY", 1.0f } }, TweenElastic.EaseOut);
                    }
                    else
                    {
                        //hover (get slightly bigger)
                        GameFacade.Screens.Tween.To(this, 0.3f, new Dictionary<string, float>() { { "ScaleX", 1.1f }, { "ScaleY", 1.1f } }, TweenQuad.EaseOut);
                    }
                }
                else if (CurrentFrame == 0)
                {
                        //return to normal size
                        GameFacade.Screens.Tween.To(this, 0.3f, new Dictionary<string, float>() { { "ScaleX", 1.0f }, { "ScaleY", 1.0f } }, TweenQuad.EaseOut);
                } else if (CurrentFrame == 1)
                {
                    //down (get smaller)
                    DownTime = 0;
                    GameFacade.Screens.Tween.To(this, 0.2f, new Dictionary<string, float>() { { "ScaleX", 0.8f }, { "ScaleY", 0.8f } }, TweenQuad.EaseOut);
                } else
                {
                    ScaleX = ScaleY = 1; //disabled
                }
                LastState = CurrentFrame;
            }
            if (CurrentFrame != 1 && DownTime < 5) DownTime++;
        }

        public override void Draw(UISpriteBatch SBatch)
        {
            if (!Visible) return;
            if (Active && Texture != null)
            {
                var ring = GetActiveRing(GameFacade.GraphicsDevice);
                var pad = 6f;
                var scale = new Vector2(
                    (Texture.Width + pad * 2) / ring.Width,
                    (Texture.Height + pad * 2) / ring.Height);
                DrawLocalTexture(SBatch, ring, null,
                    new Vector2(-Texture.Width / 2f - pad, -Texture.Height / 2f - pad),
                    scale, UIStyle.Current.ActiveSelection);
            }
            if (ElementShadow != null)
                ElementShadow.Draw(this, new Vector2(Texture.Width, Texture.Height) / -2, SBatch);
            DrawLocalTexture(SBatch, Texture, null, new Vector2(Texture.Width, Texture.Height) / -2, Vector2.One, Color.White * (Disabled?0.5f:1f));
        }

        // ---- the active ring ------------------------------------------------------------------

        private static Texture2D ActiveRing;
        private static GraphicsDevice ActiveRingDevice;

        private static Texture2D GetActiveRing(GraphicsDevice gd)
        {
            if (ActiveRing == null || ActiveRingDevice != gd)
            {
                ActiveRing = BuildRing(gd, 96, 3);
                ActiveRingDevice = gd;
            }
            return ActiveRing;
        }

        /// <summary>
        /// A rounded-rectangle outline: a filled rounded rect with a smaller one punched out of
        /// the middle. Pure pixel arithmetic, so the ring is the same on every machine and there
        /// is no new asset to ship.
        /// </summary>
        private static Texture2D BuildRing(GraphicsDevice gd, int size, int thickness)
        {
            var data = new Color[size * size];
            var radius = size / 4;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!InRoundedRect(x, y, size, 0, radius)) continue;
                    if (InRoundedRect(x, y, size, thickness, radius - thickness)) continue;
                    data[y * size + x] = Color.White;
                }
            }
            var tex = new Texture2D(gd, size, size);
            tex.SetData(data);
            return tex;
        }

        /// <summary>Whether a pixel is inside a rounded rect inset by <paramref name="inset"/>.</summary>
        private static bool InRoundedRect(int x, int y, int size, int inset, int radius)
        {
            if (radius < 0) return false;
            var min = inset;
            var max = size - 1 - inset;
            if (x < min || x > max || y < min || y > max) return false;
            var cx = Math.Clamp(x, min + radius, max - radius);
            var cy = Math.Clamp(y, min + radius, max - radius);
            var dx = x - cx;
            var dy = y - cy;
            return (dx * dx + dy * dy) <= radius * radius;
        }
    }
}
