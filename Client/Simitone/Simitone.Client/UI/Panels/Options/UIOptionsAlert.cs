using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Files.Formats.IFF.Chunks;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Runtime.CompilerServices;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The Options menu, as a dialog: a title that tracks the open tab, the tabbed page
    /// strip, and a single OK to dismiss it.
    ///
    /// <para>
    /// Settings are applied as they are touched, not on OK - the page writes straight
    /// through to the settings object and saves it (see
    /// <see cref="UIOptionsSubpanelBase.OnSettingsChanged"/>). OK only closes.
    /// </para>
    /// </summary>
    public class UIOptionsAlert : UIMobileDialog
    {
        public UIOptionsPanel OptTabViewer;

        public UIBigButton OKButton { get; }

        public event Action<short> OnResult;

        /// <summary>
        /// Creates a new <see cref="UIOptionsAlert"/> that will display the current state of each setting on the
        /// <paramref name="SettingsObjectInstance"/> provided.
        /// <para/>Should be of type <see cref="GlobalSettings"/> for now.
        /// </summary>
        /// <param name="SettingsObjectInstance"></param>
        public UIOptionsAlert(object SettingsObjectInstance) : base()
        {
            //set title text to default value, selected tab page changes title
            //ids 430 and 431 of Simitone's own table: TS1 has no string for the frame this
            //dialog is drawn in, only for the pages inside it (which UIOptionsPanel reads out
            //of table 145 through OptionsStrings). The English stays as the second argument.
            string title = SimitoneStrings.Get("430", "Options");

            Caption = title;
            // 520 rather than 400. The left column below the tab strip is the only place a page-
            // independent control can go (see UIOptionsPanel's update row), and at 400 the strip
            // alone filled it. The pages lay out from the top, so the extra 120px is unused space
            // on all three of them and costs nothing there.
            SetHeight(520);

            OptTabViewer = new UIOptionsPanel(SettingsObjectInstance);
            //GfxPanel.OnResult += (res) => { OnResult?.Invoke(res); Close(); };
            Add(OptTabViewer);
            OptTabViewer.SetTab();

            OKButton = new UIBigButton(true)
            {
                Caption = SimitoneStrings.Get("431", "OK")
            };
            OKButton.OnButtonClick += (elem) =>
            {
                //OnResult?.Invoke(GfxPanel.GetSelectedOption());
                Close();
            };
            Add(OKButton);

            GameResized();
        }

        public override void GameResized()
        {
            base.GameResized();
            OptTabViewer.Position = new Vector2((Width - UIOptionsPanel.DefaultContentWidth) / 2, 90);
            OKButton.Position = new Vector2((Width / 2) - (OKButton.Width / 2), Height - 10 - OKButton.Size.Y);
        }
    }

    /// <summary>
    /// The three pieces of <see cref="TS1GameScreen"/> the Options menu needs, written as
    /// extension methods.
    ///
    /// <para>
    /// In the upstream change these are ordinary members of <see cref="TS1GameScreen"/>. They
    /// live here instead because that file belongs to a different piece of work, and an
    /// extension method is picked up by every existing call site without touching it: when
    /// the real members land on the screen, C# prefers the instance method and these simply
    /// stop being candidates. Nothing has to be re-pointed when that happens, and there is
    /// no duplicate-member conflict to resolve.
    /// </para>
    /// </summary>
    internal static class TS1GameScreenOptionsExtensions
    {
        /// <summary>
        /// The Options dialog currently up, if any. Tracked so opening the menu twice
        /// re-opens it rather than stacking a second copy on top of the first.
        /// </summary>
        private static UIOptionsAlert _openOptionsDialog;

        /// <summary>
        /// Opens the Options menu.
        /// </summary>
        public static void ShowOptionsDialog(this TS1GameScreen game)
        {
            if (_openOptionsDialog != null)
                UIScreen.RemoveDialog(_openOptionsDialog);

            _openOptionsDialog = new UIOptionsAlert(GlobalSettings.Default);
            UIScreen.GlobalShowDialog(_openOptionsDialog, true);
        }

        /// <summary>
        /// Switches the game language behind a transition wipe.
        ///
        /// <para>
        /// The strings are reloaded by <see cref="SimitoneGame.ChangeLanguage"/>; the wipe is
        /// there so the swap is not a visible pop, and because a lot of the UI text is only
        /// read once when a panel is built.
        /// </para>
        ///
        /// <para>
        /// The upstream change draws the language icon from CustomUI here, which needs a new
        /// <c>UITransDialog</c> factory that takes an arbitrary texture. That file is not
        /// part of this change, so the stock "normal" transition art is used instead - the
        /// wipe itself is the same.
        /// </para>
        /// </summary>
        public static void RuntimeChangeLanguage(this TS1GameScreen game, STRLangCode NewLanguage)
        {
            new UITransDialog("normal", () => (GameFacade.Game as SimitoneGame)?.ChangeLanguage(NewLanguage));
        }

        /// <summary>
        /// The gameplay speed that was in force before <see cref="LockSimSpeed"/> froze it,
        /// so leaving a paused mode can put it back rather than forcing pause.
        /// </summary>
        private sealed class SpeedLock
        {
            public int Underlying;
        }

        private static readonly ConditionalWeakTable<TS1GameScreen, SpeedLock> SpeedLocks = new();

        /// <summary>
        /// If leaving a mode that is forcing paused gameplay speed, resets the gameplay speed to the one set before <see cref="LockSimSpeed"/>
        /// </summary>
        internal static void UnlockSimSpeed(this TS1GameScreen game)
        {
            var vm = game.vm;
            if (vm == null) return;
            if (vm.SpeedMultiplier != GameplaySpeed.Disabled) return; // unlocked already

            var restore = SpeedLocks.TryGetValue(game, out var held) ? held.Underlying : GameplaySpeed.Disabled;
            vm.SpeedMultiplier = restore;
            if (vm.SpeedMultiplier < 0) // oops? - nothing worth restoring
                vm.SpeedMultiplier = GameplaySpeed.ReverseRemap[GameplaySpeed.Play]; // force speed 1
            vm.ResetTickAlign();
        }

        /// <summary>
        /// If the gameplay is not already locked (<see cref="GameplaySpeed.Disabled"/>), saves the current gameplay speed and disables changing gameplay speed.
        /// </summary>
        internal static void LockSimSpeed(this TS1GameScreen game)
        {
            var vm = game.vm;
            if (vm == null) return;
            if (vm.SpeedMultiplier == GameplaySpeed.Disabled) return; // locked already

            var held = SpeedLocks.GetOrCreateValue(game);
            held.Underlying = vm.SpeedMultiplier;
            vm.SpeedMultiplier = GameplaySpeed.Disabled;
        }
    }
}
