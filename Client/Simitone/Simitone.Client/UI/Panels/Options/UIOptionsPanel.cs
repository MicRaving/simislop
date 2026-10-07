using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Common;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.Content;
using Microsoft.Xna.Framework;
using Simitone.Client.Services;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.Options
{
    /// <summary>
    /// The tab strip down the left of the Options menu, and the host for whichever page is
    /// currently open. Pages are built lazily by <see cref="SetTab"/> and thrown away when
    /// another tab is picked, so a page never has to be able to un-apply its own controls.
    /// </summary>
    public class UIOptionsPanel : UIContainer
    {
        /// <summary>
        /// The page index shown when the menu is opened and whenever a page is re-entered
        /// without one being named, so the player comes back to the page they left. Graphics
        /// is the default: it is the page that actually changes what the game looks like.
        /// </summary>
        /// <remarks>
        /// Static, which looks like mutable state outliving its screen - and here it is load-bearing
        /// in the other direction. The Options dialog is rebuilt every time it is opened (see
        /// <see cref="Removed"/>), so an instance field would be a brand new 1 on every open and
        /// "come back to the page you left" would stop being true across two openings. Static is
        /// what makes the memory survive the panel that wrote it. The price is that it survives a
        /// screen change too, which is why <see cref="SetTab"/> refuses to record a page it
        /// cannot build: a remembered index nothing can open is a permanently blank menu.
        /// </remarks>
        private static int SELECTED_PAGE = GraphicsPage;

        /// <summary>Highest tab index there is a page for.</summary>
        private const int LastPage = 2;

        private const int GeneralPage = 0, GraphicsPage = 1, SoundPage = 2;

        private const string OptionsTable = UIOptionsSubpanelBase.OptionsTable,
            GFXTitle = "7", PlayTitle = "11", SoundTitle = "9";

        /// <summary>
        /// The width the pages are laid out for. The dialog centres itself around this, so
        /// the two halves of a page line up with the tab strip and the right edge.
        /// </summary>
        public const int DefaultContentWidth = 1030;

        private readonly object settingsObject;

        public UIStencilButton GeneralTabButton { get; }
        public UIStencilButton GraphicsTabButton { get; }
        public UIStencilButton AudioTabButton { get; }
        public UIContainer SubpanelContent { get; private set; }

        // -------------------------------------------------------------------------------------
        // "Check for updates".
        //
        // Lives here, in the strip column, rather than on a page: it is not a setting (it writes
        // nothing to the ini and applies nothing), so it has no business being one of the rows a
        // page describes, and putting it on a page would mean it vanished every time the player
        // switched tabs. The version stamp beside it is the same value the checker compares
        // against, so what the player reads and what the check uses cannot disagree.
        //
        // Opt-in, deliberately: nothing calls UpdateChecker until this button is pressed. A
        // startup check would be a network request the player did not ask for, on a dial-up-era
        // 60/hour API budget, in a game that is otherwise entirely offline.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Y of the update row. It sits below the pages rather than in the tab column: once the
        /// page's 113px left edge is taken off, the column is 73px wide, and the row's caption,
        /// version stamp and status line cannot be drawn in that without spilling over the page
        /// and over each other.
        /// </summary>
        public const int UpdateRowY = 330;

        /// <summary>Width of the update button, wide enough for its caption.</summary>
        public const int UpdateButtonWidth = 220;

        /// <summary>X of the version stamp and status line, to the right of the button.</summary>
        public const int UpdateTextX = 272;

        /// <summary>Y offset of the version stamp from the row's top.</summary>
        public const int UpdateVersionOffsetY = 6;

        /// <summary>Y offset of the status line from the row's top.</summary>
        public const int UpdateStatusOffsetY = 26;

        private readonly UITwoStateButton UpdateButton;
        private readonly UILabel UpdateVersionLabel;
        private readonly UILabel UpdateStatusLabel;

        private UpdateCheckState _updateState = UpdateCheckState.Idle;
        private DateTime? _updateRequestedAt;
        private bool _updateInFlight;
        private UIMobileAlert _updateDialog;
        private bool _updateDisposed;

        /// <summary>The state the update row is in. Read by the smoke test and the row's own logic.</summary>
        public UpdateCheckState UpdateState => _updateState;

        /// <summary>
        /// Creates a new <see cref="UIOptionsPanel"/> attached to the <paramref name="SettingsObject"/> which is used
        /// to get the current state of each setting, and change the state of the setting on that instance
        /// </summary>
        /// <param name="SettingsObject"></param>
        public UIOptionsPanel(object SettingsObject)
        {
            var ui = Content.Get().CustomUI;
            var gd = GameFacade.GraphicsDevice;

            GeneralTabButton = new UIStencilButton(ui.Get("opt_general.png").Get(gd))
            {
                Tooltip = OptionsStrings.Get(PlayTitle, "Gameplay")
            };
            GeneralTabButton.OnButtonClick += (btn) => { SetTab(0); };
            Add(GeneralTabButton);

            GraphicsTabButton = new UIStencilButton(ui.Get("opt_gfx.png").Get(gd))
            {
                Tooltip = OptionsStrings.Get(GFXTitle, "Graphics")
            };
            GraphicsTabButton.OnButtonClick += (btn) => { SetTab(1); };
            Add(GraphicsTabButton);

            AudioTabButton = new UIStencilButton(ui.Get("opt_audio.png").Get(gd))
            {
                Tooltip = OptionsStrings.Get(SoundTitle, "Sound")
            };
            AudioTabButton.OnButtonClick += (btn) => { SetTab(2); };
            Add(AudioTabButton);

            settingsObject = SettingsObject;

            // ---- the update row ---------------------------------------------------------------
            UpdateButton = new UITwoStateButton(Content.Get().CustomUI.Get("blank_blue.png").Get(gd))
            {
                Position = new Vector2(40, UpdateRowY),
                Width = UpdateButtonWidth,
                Caption = UpdateCheckPresenter.ButtonCaption(UpdateCheckState.Idle),
                Tooltip = UpdateCheckPresenter.Describe(UpdateCheckState.Idle, null).Message
            };
            UpdateButton.OnButtonClick += (btn) => StartUpdateCheck();
            Add(UpdateButton);

            UpdateVersionLabel = new UILabel()
            {
                Position = new Vector2(UpdateTextX, UpdateRowY + UpdateVersionOffsetY),
                Caption = UpdateCheckPresenter.VersionText(SimitoneVersion.Current)
            };
            UpdateVersionLabel.CaptionStyle = UpdateVersionLabel.CaptionStyle.Clone();
            UpdateVersionLabel.CaptionStyle.Size = 12;
            UpdateVersionLabel.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            Add(UpdateVersionLabel);

            UpdateStatusLabel = new UILabel()
            {
                Position = new Vector2(UpdateTextX, UpdateRowY + UpdateStatusOffsetY),
                Caption = UpdateCheckPresenter.Describe(UpdateCheckState.Idle, null).StatusText
            };
            UpdateStatusLabel.CaptionStyle = UpdateStatusLabel.CaptionStyle.Clone();
            UpdateStatusLabel.CaptionStyle.Size = 12;
            UpdateStatusLabel.CaptionStyle.Color = UIStyle.Current.SecondaryText;
            Add(UpdateStatusLabel);

            //The page is deliberately not opened here. The dialog this lives in sets a tab once it
            //has a parent to measure against, which is why GameResized below has to be re-run
            //after the page is attached rather than the page being built against the default width.
        }

        private void SetTitle(string Title)
        {
            if (Parent == null) return;
            if (Parent is UIMobileDialog dlg)
                dlg.Caption = Title;
        }

        /// <summary>
        /// Sets the currently viewed tab in the tab viewer.
        /// <para/>Passing -1 will set it to the last opened page.
        /// </summary>
        /// <remarks>
        /// An index with no page behind it is refused rather than half-applied. <c>SetTab</c> is
        /// public, and the remembered index outlives the panel, so an out-of-range value would
        /// otherwise leave the strip with nothing selected and then poison the next opening -
        /// every later call with the default argument would rebuild the same blank menu.
        /// </remarks>
        /// <param name="Page"></param>
        public void SetTab(int Page = -1)
        {
            if (Page == -1)
                Page = SELECTED_PAGE;
            if (Page < GeneralPage || Page > LastPage)
            {
                SimitoneLog.Warn("UIOptionsPanel.SetTab was asked for page " + Page
                    + ", which is not one of the " + (LastPage + 1) + " tabs; the current page is left alone.");
                return;
            }
            SELECTED_PAGE = Page;

            if (SubpanelContent != null)
            {
                SubpanelContent.Visible = false;
                Children.Remove(SubpanelContent);
            }

            GraphicsTabButton.Selected = AudioTabButton.Selected = GeneralTabButton.Selected = false;

            switch (Page)
            {
                case GeneralPage:
                    SetTitle(OptionsStrings.Get(PlayTitle, "Gameplay"));
                    GeneralTabButton.Selected = true;
                    SubpanelContent = new UIGeneralOptionsSubpanel(settingsObject);
                    break;
                case GraphicsPage:
                    SetTitle(OptionsStrings.Get(GFXTitle, "Graphics"));
                    GraphicsTabButton.Selected = true;
                    SubpanelContent = new UIGraphicsOptionsSubpanel(settingsObject);
                    break;
                case SoundPage:
                    SetTitle(OptionsStrings.Get(SoundTitle, "Sound"));
                    AudioTabButton.Selected = true;
                    SubpanelContent = new UIAudioOptionsSubpanel(settingsObject);
                    break;
            }

            if (SubpanelContent == null) return;
            Add(SubpanelContent);

            // The page laid itself out while it was still unparented, so it built against the
            // default width rather than the real one. Pushing the width down rebuilds it now
            // that it has a parent to measure against.
            if (SubpanelContent is UIOptionsSubpanelBase page)
                page.ContentWidth = DefaultContentWidth;

            GameResized();
        }

        public override void GameResized()
        {
            base.GameResized();

            int left = 40;

            GeneralTabButton.Position = new Vector2(left + 0, 0);
            AudioTabButton.Position = new Vector2(left, 186);
            GraphicsTabButton.Position = new Vector2(left, 93);
            UpdateButton.Position = new Vector2(left, UpdateRowY);
            UpdateVersionLabel.Position = new Vector2(UpdateTextX, UpdateRowY + UpdateVersionOffsetY);
            UpdateStatusLabel.Position = new Vector2(UpdateTextX, UpdateRowY + UpdateStatusOffsetY);
            if (SubpanelContent != null)
                SubpanelContent.Position = new Vector2(113, 0);
        }

        // -------------------------------------------------------------------------------------
        // The check itself.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Pressed "Check for updates": asks the release feed once, off the game thread, and
        /// reports whatever comes back.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The call is genuinely off the game thread. <see cref="UpdateChecker.CheckAsync"/> is
        /// an HTTP round trip with a ten second ceiling, and awaiting it from the game thread
        /// would freeze the whole game for up to that long on a bad connection. The button is
        /// disabled for the duration, so the wait is visible rather than the game being wedged.
        /// </para>
        ///
        /// <para>
        /// <see cref="UpdateChecker.CheckAsync"/> never throws, so there is no catch here for it;
        /// the <c>try</c> is for everything around it, because a failure to *show* the answer must
        /// not escape into a fire-and-forget task, where it would be lost and take the process down
        /// with it.
        /// </para>
        /// </remarks>
        private void StartUpdateCheck()
        {
            if (!UpdateCheckPresenter.IsCheckAllowed(EffectiveState(), TimeSinceLastRequest()))
            {
                SimitoneLog.Info("Update check not started: " + (UpdateCheckPresenter.ButtonCaption(EffectiveState()))
                    + " is the current state and the debounce has not elapsed.");
                return;
            }

            SetUpdateState(UpdateCheckState.Checking, null);
            _updateRequestedAt = DateTime.UtcNow;

            // Not Task.Run: CheckAsync is already asynchronous, so its continuation is what runs
            // off the thread, and the await below is resumed on the game thread. Scheduling the
            // whole call on a thread pool thread as well would only add a second hop.
            _ = RunUpdateCheckAsync();
        }

        private async Task RunUpdateCheckAsync()
        {
            UpdateCheckResult result = null;
            try
            {
                result = await UpdateChecker.CheckAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // UpdateChecker is documented as total, so this is a backstop against a future
                // change rather than an expected path - but a dropped exception inside a
                // fire-and-forget task is an unobserved task exception, which is the worst
                // possible place for one to end up.
                SimitoneLog.Error("The update check threw, which it is not supposed to do.", ex);
            }

            try
            {
                // Back on the game thread, because everything below touches the UI.
                GameThread.NextUpdate(state => PresentUpdateResult(result));
            }
            catch (Exception ex)
            {
                SimitoneLog.Error("Could not show the result of the update check.", ex);
            }
        }

        /// <summary>
        /// Shows the outcome: a dialog for all three answers, and the row's status line updated
        /// for all of them.
        /// </summary>
        /// <remarks>
        /// "Up to date" and "could not check" share one dialog shape on purpose - they are the two
        /// answers a player can do nothing about, and neither is worth a modal that stops the
        /// game. Only a real update gets a dialog with a Download button, because that is the one
        /// answer that changes what the player might do next.
        /// </remarks>
        private void PresentUpdateResult(UpdateCheckResult result)
        {
            if (_updateDisposed) return;

            var state = UpdateCheckPresenter.StateFor(result);
            SetUpdateState(state, result);

            var shown = UpdateCheckPresenter.Describe(state, result);

            if (UIScreen.Current == null) return;

            // Only one update dialog at a time: pressing the button again while one is up would
            // otherwise stack a second copy of the same message.
            if (_updateDialog != null) UIScreen.RemoveDialog(_updateDialog);

            var buttons = shown.OffersDownload
                ? new[]
                {
                    new UIAlertButton(UIAlertButtonType.Yes,
                        (b) => { UIScreen.RemoveDialog(_updateDialog); UpdateChecker.OpenReleasePage(shown.DownloadUrl); },
                        UpdateCheckPresenter.DownloadCaption()),
                    new UIAlertButton(UIAlertButtonType.Cancel, null)
                }
                : UIAlertButton.Ok(null);

            _updateDialog = new UIMobileAlert(new UIAlertOptions()
            {
                Title = shown.Title,
                Message = shown.Message,
                Buttons = buttons
            });
            UIScreen.GlobalShowDialog(_updateDialog, true);

            SimitoneLog.Info("Update check: " + state + " (running " + result?.CurrentVersion + ").");
        }

        /// <summary>
        /// The state the row should act on: <see cref="UpdateCheckState.Checking"/> while a
        /// request is outstanding, whatever <see cref="_updateState"/> was last set to.
        /// </summary>
        private UpdateCheckState EffectiveState()
        {
            return _updateInFlight ? UpdateCheckState.Checking : _updateState;
        }

        private void SetUpdateState(UpdateCheckState state, UpdateCheckResult result)
        {
            _updateState = state;
            _updateInFlight = state == UpdateCheckState.Checking;

            var shown = UpdateCheckPresenter.Describe(state, result);
            UpdateButton.Caption = UpdateCheckPresenter.ButtonCaption(state);
            UpdateStatusLabel.Caption = shown.StatusText;
            UpdateButton.Tooltip = shown.Message;
            RefreshUpdateButtonAvailability();
        }

        /// <summary>
        /// Re-enables the button once the debounce has elapsed.
        /// </summary>
        /// <remarks>
        /// Called every frame, and the only reason it is cheap is that it returns immediately
        /// unless the answer changed. Without it the button would stay greyed out for the rest of
        /// the Options dialog's life after one check, which reads as "this only works once" -
        /// the exact complaint the debounce is supposed to avoid causing.
        /// </remarks>
        private void RefreshUpdateButtonAvailability()
        {
            if (UpdateButton == null) return;

            var allowed = UpdateCheckPresenter.IsCheckAllowed(EffectiveState(), TimeSinceLastRequest());
            if (UpdateButton.Disabled == !allowed) return;

            UpdateButton.Disabled = !allowed;
            UpdateButton.Opacity = allowed ? 1f : 0.6f;
        }

        private TimeSpan? TimeSinceLastRequest()
        {
            return _updateRequestedAt.HasValue ? DateTime.UtcNow - _updateRequestedAt.Value : (TimeSpan?)null;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            RefreshUpdateButtonAvailability();
        }

        public override void Removed()
        {
            base.Removed();
            // The Options dialog is rebuilt every time it is opened, so a check left in flight
            // would otherwise complete against a torn-down panel and build controls on a screen
            // that has moved on. Flipping this is the whole of the teardown: the completion
            // checks it and returns.
            _updateDisposed = true;
        }
    }
}
