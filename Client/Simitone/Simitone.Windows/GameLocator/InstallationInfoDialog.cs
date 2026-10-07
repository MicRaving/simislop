using System;
using System.Drawing;
using System.Windows.Forms;

namespace Simitone.Windows.GameLocator
{
    /// <summary>
    /// Confirms the installation Simitone just settled on, and names the directories it will
    /// read from and write to.
    ///
    /// <para>
    /// Shown only when the player actually made a choice - from the
    /// <see cref="InstallationSelector"/>, or by pointing Simitone at a folder themselves.
    /// That is the moment the two facts below are worth stating, because both are invisible
    /// consequences of the choice: the Legacy Collection keeps its saves outside the game
    /// directory, so a Steam install and a boxed one do not merely differ in GameData, they
    /// read different save folders and cannot trade neighbourhoods. Simitone copies out of
    /// the source named on the dialog and writes only to its own folder, so the choice is
    /// always reversible and never damages the original install.
    /// </para>
    ///
    /// <para>
    /// Deliberately not shown when the game already knew the answer (a remembered choice, or
    /// a single unambiguous install): there is no decision there, and putting a modal dialog
    /// in front of a player who has done nothing would make every launch a two-step launch.
    /// </para>
    ///
    /// <para>
    /// Laid out with a table for the same reason as <see cref="InstallationSelector"/>: fixed
    /// pixel offsets do not survive this process's DPI handling.
    /// </para>
    /// </summary>
    internal sealed class InstallationInfoDialog : Form
    {
        public InstallationInfoDialog(string installationDescription, string gamePath, string originalSavesPath, string simitoneSavesPath)
        {
            // See InstallationSelector for why the autoscale is off and the layout is a table:
            // the game is a DPI-unaware WinForms process, so a window can come up smaller than
            // the coordinates its children were placed at.
            AutoScaleMode = AutoScaleMode.None;
            Text = "Simitone is ready";
            ClientSize = new Size(660, 460);
            MinimumSize = new Size(520, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // Details take the slack; the warning and the button bar are exactly as tall as
            // their content, so the dialog is only ever tall enough for the text it has.
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var details = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 2,
                RowCount = 0
            };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddDetail(details, "Installation", installationDescription);
            AddDetail(details, "Game files", gamePath);
            AddDetail(details, "Simitone's saves", simitoneSavesPath);
            AddDetail(details, "The Sims' own saves", originalSavesPath);

            var warning = new GroupBox
            {
                Text = "Before you start",
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8)
            };
            warning.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text =
                    "Steam and non-Steam copies of The Sims do not share save files, so the " +
                    "installations above are not interchangeable: switching to a different one " +
                    "later means starting again from that one's saves.\r\n\r\n" +
                    "Simitone copies the neighbourhood above into its own save folder and never " +
                    "writes to your The Sims installation, so your original game is left " +
                    "untouched. To use a different install, delete config.ini from your Simitone " +
                    "documents folder and start again, or pass -path\"C:\\Path\\To\\The Sims\"."
            });

            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Size = new Size(96, 30),
                Margin = new Padding(0)
            };
            okButton.Click += (s, e) => Close();

            // RightToLeft + AutoSize rather than an absolute position: the button stays hard
            // against the right edge at any DPI and form size, with no layout maths to get
            // wrong the first time somebody runs this on a 4K display.
            var buttonBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            buttonBar.Controls.Add(okButton);

            layout.Controls.Add(details, 0, 0);
            layout.Controls.Add(warning, 0, 1);
            layout.Controls.Add(buttonBar, 0, 2);

            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = okButton;
        }

        /// <summary>
        /// Appends a caption/value row. Rows are added rather than declared up front so the
        /// count can never disagree with the contents - a table with a declared row count and
        /// a loop that adds a different number of rows silently renders empty cells.
        /// </summary>
        private static void AddDetail(TableLayoutPanel details, string caption, string value)
        {
            var row = details.RowCount;
            details.RowCount++;
            details.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            details.Controls.Add(new Label
            {
                Text = caption,
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.TopLeft,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold)
            }, 0, row);

            details.Controls.Add(new Label
            {
                // A missing path is shown rather than left blank: this dialog only appears
                // after a real choice, so an empty value here means the folder went away
                // between the choice and the dialog, which the player needs to see.
                Text = string.IsNullOrEmpty(value) ? "(not found)" : value,
                Dock = DockStyle.Fill,
                AutoSize = false
            }, 1, row);
        }
    }
}
