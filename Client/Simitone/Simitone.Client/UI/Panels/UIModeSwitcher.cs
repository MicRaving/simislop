using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;

namespace Simitone.Client.UI.Panels
{
    public class UIModeSwitcher : UIContainer
    {
        public UILiveButton LiveButton;
        public UIElasticButton BuyButton;
        public UIElasticButton BuildButton;
        public UIElasticButton OptionButton;

        private UIButton[] ButtonOrder;
        public Func<UIMainPanelMode, bool> OnModeClick;
        public TS1GameScreen Game;

        public UIModeSwitcher(TS1GameScreen screen)
        {
            Game = screen;
            var ui = Content.Get().CustomUI;

            var btn = new UILiveButton(screen);
            btn.MotiveLevel = 0.5f;
            btn.Position = Vector2.Zero;
            btn.OnButtonClick += (b) => { SwitchMode(UIMainPanelMode.LIVE); };
            Add(btn);
            LiveButton = btn;

            BuildButton = new UIElasticButton(ui.Get("mode_build.png").Get(GameFacade.GraphicsDevice));
            BuildButton.Position = btn.Position;
            BuildButton.OnButtonClick += (b) => { SwitchMode(UIMainPanelMode.BUILD); };
            BuildButton.Opacity = 0;
            Add(BuildButton);

            BuyButton = new UIElasticButton(ui.Get("mode_buy.png").Get(GameFacade.GraphicsDevice));
            BuyButton.Position = btn.Position;
            BuyButton.OnButtonClick += (b) => { SwitchMode(UIMainPanelMode.BUY); };
            BuyButton.Opacity = 0;
            Add(BuyButton);

            OptionButton = new UIElasticButton(ui.Get("mode_options.png").Get(GameFacade.GraphicsDevice));
            OptionButton.Position = btn.Position;
            OptionButton.OnButtonClick += (b) => { SwitchMode(UIMainPanelMode.OPTIONS); };
            OptionButton.Opacity = 0;
            Add(OptionButton);

            ButtonOrder = new UIButton[]
            {
                LiveButton,
                BuyButton,
                BuildButton,
                OptionButton
            };
            RefreshBuildBuyIfChanged();
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            foreach (var btn in ButtonOrder)
            {
                btn.Visible = btn.Opacity > 0;
            }
            RefreshBuildBuyIfChanged();
        }

        /// <summary>
        /// The last build/buy state the buttons were set from, or null if they have not been set
        /// yet. Nullable because the constructor's <see cref="RefreshBuildBuyIfChanged"/> is the
        /// first set and the panel is compared against it from the first frame, so "unset" and
        /// "enabled" cannot be the same value.
        /// </summary>
        private bool? LastBuildBuyEnabled;

        /// <summary>
        /// Re-read the lot's build/buy state and push it to the buttons, but only when it has
        /// actually changed.
        /// </summary>
        /// <remarks>
        /// <see cref="UpdateBuildBuy"/> is also called on <c>TS1BuildBuyChange</c>, which is
        /// signalled by the two bhav global calls that take and give up the disable. That is not
        /// the only way the state moves: the lot derives it from the objects holding it, and a
        /// hold is also released by the engine when its object is removed, or dropped outright
        /// when the lot reloads. None of those run bhav, so none of them signal - the bug this
        /// was left with was a panel that stayed disabled because the event that would have
        /// re-enabled it never came. Comparing against the last known value costs one bool read
        /// per frame and touches the buttons only on a change.
        /// </remarks>
        private void RefreshBuildBuyIfChanged()
        {
            var bbEnable = Game?.vm?.Context?.Architecture?.BuildBuyEnabled ?? true;
            if (LastBuildBuyEnabled.HasValue && LastBuildBuyEnabled.Value == bbEnable) return;
            LastBuildBuyEnabled = bbEnable;
            UpdateBuildBuy();
        }

        public void UpdateBuildBuy()
        {
            var bbEnable = Game.vm.Context.Architecture.BuildBuyEnabled;
            BuyButton.Disabled = !bbEnable;
            BuildButton.Disabled = !bbEnable;
        }

        public void EndSwitch(UIMainPanelMode mode)
        {
            UIButton frontButton;
            switch (mode)
            {
                case UIMainPanelMode.BUILD:
                    frontButton = BuildButton; break;
                case UIMainPanelMode.BUY:
                    frontButton = BuyButton; break;
                case UIMainPanelMode.OPTIONS:
                    frontButton = OptionButton; break;
                default:
                    frontButton = LiveButton; break;
            }
            //become this mode
            SendToFront(frontButton);
            foreach (var button in ButtonOrder)
            {
                GameFacade.Screens.Tween.To(button, 0.5f, new Dictionary<string, float>() { { "Y", 0 } }, TweenQuad.EaseOut);
                GameFacade.Screens.Tween.To(button, 0.5f, new Dictionary<string, float>() { { "Opacity", (button != frontButton)?0f:1f } }, TweenQuad.EaseOut);
            }
        }

        public void SwitchMode(UIMainPanelMode mode)
        {
            if (OnModeClick?.Invoke(mode) ?? true)
            {
                //switching mode. show the modes.
                int i = 0;
                foreach (var button in ButtonOrder)
                {
                    if (button == LiveButton && Game?.LotControl.ActiveEntity == null) continue;
                    button.Visible = true;
                    GameFacade.Screens.Tween.To(button, 0.5f, new Dictionary<string, float>() { { "Y", (-140)*(i++) } }, TweenQuad.EaseOut);
                    GameFacade.Screens.Tween.To(button, 0.5f, new Dictionary<string, float>() { { "Opacity", 1 } }, TweenQuad.EaseOut);
                }
            }
        }
    }
}
