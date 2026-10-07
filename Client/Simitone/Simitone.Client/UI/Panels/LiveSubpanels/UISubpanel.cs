using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UISubpanel : UICachedContainer
    {
        public TS1GameScreen Game;

        /// <summary>
        /// How tall this subpanel is, in pixels.
        /// </summary>
        /// <remarks>
        /// The live-mode strip is 128px tall, which is what the six shared controls were laid
        /// out against. A subpanel that needs a row of its own below that - the inventory's
        /// MagiCoin readout is the one today - overrides this rather than adding a child that
        /// hangs off the bottom of the strip: the strip's own background is drawn by the main
        /// panel, so anything past 128px is drawn over the lot view and needs a plate of its
        /// own, which is a decision a subclass should be making on purpose.
        /// </remarks>
        public virtual float PanelHeight => 128f;

        public UISubpanel(TS1GameScreen game) : base()
        {
            Opacity = 0;
            var screenWidth = GameFacade.Screens.CurrentUIScreen.ScreenWidth;
            Size = new Vector2(screenWidth - (342 + (game.Desktop?100:0)), PanelHeight);
            GameFacade.Screens.Tween.To(this, 0.3f, new Dictionary<string, float>() { { "Opacity", 1f } });
            Game = game;
        }

        public override void GameResized()
        {
            var screenWidth = UIScreen.Current.ScreenWidth;
            Size = new Vector2(screenWidth - (342 + (Game.Desktop ? 100 : 0)), PanelHeight);
            base.GameResized();
        }

        public virtual void Kill()
        {
            GameFacade.Screens.Tween.To(this, 0.3f, new Dictionary<string, float>() { { "Opacity", 0f } });
            GameThread.SetTimeout(() =>
            {
                Parent.Remove(this);
            }, 300);
        }
    }
}
