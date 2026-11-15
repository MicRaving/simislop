/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Framework.Parser;
using FSO.Client.UI.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework.IO;
using FSO.Client.Utils;
using FSO.SimAntics;
using FSO.Common.Utils;
using FSO.Client.UI.Controls;
using Simitone.Client.UI.Panels;
using FSO.Client;
using FSO.HIT;
using FSO.Content;

namespace Simitone.Client.UI.Controls
{
    /// <summary>
    /// Used to display an interaction. Will eventually have all features like the timer, big huge red x support for cancel etc.
    /// </summary>
    public class UIInteraction : UIContainer
    {
        public Texture2D Icon;
        private UITooltipHandler m_TooltipHandler;
        private Texture2D Background;
        private Texture2D Overlay;
        private Texture2D Rim;
        private bool Active;
        private UIMouseEventRef ClickHandler;
        public event ButtonClickDelegate OnMouseEvent;
        public UIIQTrackEntry ParentEntry;

        public float OverlayScale;
        public float RimScale;
        public bool Dead;

        /// <summary>
        /// Fade the red cancel X in over the entry. Idempotent: the caller
        /// (<c>UIIQTrackEntry.MarkCancelled</c>, driven by the engine's <c>NotifyIdle</c>) already
        /// guards against asking twice, and the guard here is what makes that safe from any other
        /// caller too - re-running the tween from <see cref="OverlayScale"/> 2 would restart the
        /// fade-in every frame and pulse.
        /// </summary>
        public void SetCancelled()
        {
            if (Overlay != null) return;
            OverlayScale = 2f;
            Overlay = Content.Get().CustomUI.Get("int_cancel.png").Get(GameFacade.GraphicsDevice);
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "OverlayScale", 1f }}, TweenQuad.EaseOut);
        }

        public void SetActive(bool active)
        {
            if (active)
            {
                Rim = Content.Get().CustomUI.Get("int_big_sel.png").Get(GameFacade.GraphicsDevice);
                Background = Content.Get().CustomUI.Get("int_big_bg.png").Get(GameFacade.GraphicsDevice);
                if (!Active)
                {
                    ClickHandler.Region = new Rectangle(-55, -55, 110, 110);
                    ScaleX = ScaleY = 0.5f;
                    GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "ScaleX", 1f }, { "ScaleY", 1f } }, TweenQuad.EaseOut);

                    RimScale = 2f;
                    GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "RimScale", 1f } }, TweenQuad.EaseOut);
                }
            }
            else
            {
                Rim = null;
                Background = Content.Get().CustomUI.Get("int_small_bg.png").Get(GameFacade.GraphicsDevice);
                if (Active)
                {
                    ClickHandler.Region = new Rectangle(-23, -23, 46, 46);
                    ScaleX = ScaleY = 2f;
                    GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "ScaleX", 1f }, { "ScaleY", 1f } }, TweenQuad.EaseOut);
                }
            }
            this.Active = active;
        }

        public UIInteraction(bool Active)
        {
            ClickHandler = ListenForMouse(new Rectangle(-23, -23, 46, 46), new UIMouseEvent(MouseEvt));
            SetActive(Active);
            m_TooltipHandler = UIUtils.GiveTooltip(this);
        }


        public void Kill()
        {
            GameFacade.Screens.Tween.To(this, 0.3f, new Dictionary<string, float>() { { "Y", -60f } }, TweenQuad.EaseIn);
            GameThread.SetTimeout(() => Parent.Remove(this), 300);
            Dead = true;
        }

        private void MouseEvt(UIMouseEventType type, UpdateState state)
        {
            if (Dead) return;
            if (type == UIMouseEventType.MouseDown) OnMouseEvent(this); //pass to parents to handle
        }

        public override void Draw(UISpriteBatch batch)
        {
            base.Draw(batch);
            DrawLocalTexture(batch, Background, null, new Vector2(-Background.Width, -Background.Height)/2, Vector2.One, new Color(104, 164, 184, 255));
            var iconSize = (Active) ? 74f : 37f;
            if (Icon != null)
            {
                if (Icon.Width/(float)Icon.Height < 1.1f)
                {
                    DrawLocalTexture(batch, Icon, new Rectangle(0, 0, Icon.Width, Icon.Height), new Vector2(iconSize/-2, iconSize / -2), new Vector2(iconSize / Icon.Width, iconSize / Icon.Height));
                }
                else DrawLocalTexture(batch, Icon, new Rectangle(0, 0, Icon.Width / 2, Icon.Height), new Vector2(iconSize / -2, iconSize / -2), new Vector2(iconSize / Icon.Height, iconSize / Icon.Height));
            }
            if (Overlay != null) DrawLocalTexture(batch, Overlay, null, new Vector2(Overlay.Width, Overlay.Height) * (OverlayScale/-2), new Vector2(OverlayScale), Color.White * (2 - OverlayScale));
            if (Rim != null) DrawLocalTexture(batch, Rim, null, new Vector2(Rim.Width, Rim.Height) * (RimScale / -2), new Vector2(RimScale), Color.Yellow * (2-RimScale));
        }

        public override Rectangle GetBounds()
        {
            return new Rectangle(0, 0, ClickHandler.Region.Width, ClickHandler.Region.Height);
        }
    }
}
