using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Simitone.Windows.GameLocator
{
    /// <summary>
    /// Asks which of several detected The Sims 1 installations to use.
    ///
    /// <para>
    /// This exists because "more than one" is a normal state for a PC and not a failure. A
    /// player can have a boxed copy on one drive and the Steam Legacy Collection on another,
    /// or a leftover copy in a folder they stopped using years ago. The old locator took the
    /// first one its probe chain found and booted; the Legacy Collection in particular moves
    /// its save data out of the game directory entirely, so picking the wrong one does not
    /// just mean the wrong GameData - it means no neighbourhood to start from.
    /// </para>
    ///
    /// <para>
    /// The list is in discovery precedence order (the same order
    /// <see cref="GameInstallLadder"/> consults), so the pre-selected first row is what the
    /// game would have used on its own, and the hint says so.
    /// </para>
    ///
    /// <para>
    /// Laid out with a table rather than at fixed pixel offsets, because the game is a
    /// DPI-unaware WinForms process on a desktop that may not be at 100%: a window can end up
    /// noticeably smaller than the coordinates its children were placed at, and a hard-coded
    /// layout then clips the instruction text, pushes the location column off the edge and
    /// drops the buttons out of sight. Proportional rows survive that; the fixed offsets
    /// these two dialogs used to have did not.
    /// </para>
    /// </summary>
    internal sealed class InstallationSelector : Form
    {
        /// <summary>
        /// Floor for the location column when a path is too long to show in full. Narrower than
        /// this and the paths stop being distinguishable from each other, which is the one
        /// thing the column is for.
        /// </summary>
        private const int MinimumLocationColumnWidth = 200;

        private readonly IReadOnlyList<SimsInstallCandidate> installations;
        private readonly ListView installList;
        private readonly Button okButton;

        /// <summary>
        /// The install the player picked, or null if they cancelled or picked nothing.
        /// </summary>
        public SimsInstallCandidate SelectedInstallation { get; private set; }

        public InstallationSelector(IReadOnlyList<SimsInstallCandidate> installations)
        {
            this.installations = installations ?? Array.Empty<SimsInstallCandidate>();

            AutoScaleMode = AutoScaleMode.None;
            Text = "Select your The Sims installation";
            ClientSize = new Size(760, 420);
            MinimumSize = new Size(520, 340);
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
                RowCount = 4,
                Padding = new Padding(12)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            // The list takes the slack; the rest are exactly as tall as their text, so the
            // dialog is never taller than it needs to be and never shorter than its words.
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var instruction = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = "More than one copy of The Sims was found on this computer. Choose the one you play:" +
                       " Simitone reads your first neighbourhood from it and copies it into its own save folder."
            };

            installList = new ListView
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 6, 0, 6),
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false
            };
            installList.Columns.Add("Installation", 300);
            installList.Columns.Add("Location", 380);
            foreach (var installation in this.installations)
            {
                var row = new ListViewItem(installation.Description);
                row.SubItems.Add(installation.Path);
                // Tag is what the handler maps back to; the selected row index is not usable
                // here, because a ListView row can also be current without being selected.
                row.Tag = installation;
                installList.Items.Add(row);
            }

            // Details view neither wraps nor ellipsises, so fixed column widths would clip a
            // long Steam library path - the one row where seeing the whole thing matters most,
            // since several of them are under steamapps\common\. Size to content here, and
            // BalanceColumns() below then makes the location column take up whatever is left.
            installList.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            if (installList.Items.Count > 0)
            {
                installList.Items[0].Selected = true;
                installList.Items[0].EnsureVisible();
            }
            installList.DoubleClick += (s, e) => AcceptSelection();

            var hint = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Text = this.installations.Count > 0
                    ? "The highlighted entry is the one Simitone would pick on its own."
                    : "Nothing was found. Cancel to quit, or start Simitone with -path\"C:\\Path\\To\\The Sims\"."
            };

            okButton = new Button
            {
                Text = "Use this",
                DialogResult = DialogResult.OK,
                Size = new Size(96, 30),
                Enabled = installList.Items.Count > 0
            };
            okButton.Click += (s, e) => AcceptSelection();

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(96, 30)
            };

            // RightToLeft + AutoSize rather than absolute positions: the buttons stay hard
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
            buttonBar.Controls.Add(cancelButton);
            buttonBar.Controls.Add(okButton);

            layout.Controls.Add(instruction, 0, 0);
            layout.Controls.Add(installList, 0, 1);
            layout.Controls.Add(hint, 0, 2);
            layout.Controls.Add(buttonBar, 0, 3);

            Controls.Add(layout);

            // Subscribed last on purpose: the handler touches okButton, and the initial
            // SelectedIndex above is raised while the list is being filled, which is before
            // the button exists.
            installList.SelectedIndexChanged += (s, e) => okButton.Enabled = SelectedInstallationFromList() != null;

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        /// <summary>
        /// Makes the location column take up the width the list actually has, or give space
        /// back if the content needs more than that.
        /// </summary>
        /// <remarks>
        /// Deferred to <see cref="OnLoad"/> because the list has no client width until its
        /// window handle exists - the control is docked, so its size is only known once the
        /// form has been laid out. OnLoad runs before the first paint, so the adjustment is
        /// never visible as the columns jumping under the player's cursor.
        /// </remarks>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            var available = installList.ClientSize.Width;
            if (available <= 0) return;

            var slack = available - installList.Columns[0].Width - installList.Columns[1].Width;
            if (slack > 0)
            {
                installList.Columns[1].Width += slack;
            }
            else if (slack < 0)
            {
                // Never narrow the paths past the point where two of them stop being
                // tellable apart; a horizontal scrollbar for a path that will not fit is
                // better than a list of identical-looking rows.
                installList.Columns[1].Width = Math.Max(MinimumLocationColumnWidth, installList.Columns[1].Width + slack);
            }
        }

        /// <summary>
        /// Applies the highlighted row. Also wired to double-click, which has to close the
        /// dialog: the Accept button's DialogResult only fires on a click, so a double-click
        /// would otherwise leave the form open with a selection made.
        /// </summary>
        private void AcceptSelection()
        {
            var selected = SelectedInstallationFromList();
            if (selected == null)
            {
                MessageBox.Show(this, "Please select an installation to use.", "No selection",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SelectedInstallation = selected;
            DialogResult = DialogResult.OK;
            Close();
        }

        private SimsInstallCandidate SelectedInstallationFromList()
        {
            if (installList.SelectedItems.Count != 1) return null;
            return installList.SelectedItems[0].Tag as SimsInstallCandidate;
        }
    }
}
