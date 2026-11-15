using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.Utils;

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
            Add(OptTabViewer);
            OptTabViewer.SetTab();

            OKButton = new UIBigButton(true)
            {
                Caption = SimitoneStrings.Get("431", "OK")
            };
            OKButton.OnButtonClick += (elem) =>
            {
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
}
