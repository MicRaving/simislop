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

namespace Simitone.Client.UI.Controls
{
    /// <summary>
    /// Implemented by controls that can carry a <see cref="UIShadow"/>, so generic code (a
    /// list of buttons, a neighbour-lot card) can drop a shadow behind any of them without
    /// knowing the concrete type.
    /// </summary>
    public interface IUIShadowElement
    {
        UIShadow ElementShadow { get; }
    }

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

    public class UIElasticButton : UIButton, IUIShadowElement
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
            if (ElementShadow != null)
                ElementShadow.Draw(this, new Vector2(Texture.Width, Texture.Height) / -2, SBatch);
            DrawLocalTexture(SBatch, Texture, null, new Vector2(Texture.Width, Texture.Height) / -2, Vector2.One, Color.White * (Disabled?0.5f:1f));
        }
    }
}
