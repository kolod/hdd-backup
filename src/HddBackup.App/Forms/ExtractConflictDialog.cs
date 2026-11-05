using System.Drawing;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Browsing;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Asked once before anything is written: how many existing files an extraction would replace
    /// (FR-015). Nothing is replaced without this choice; Cancel is the default.
    /// </summary>
    internal sealed class ExtractConflictDialog : Form
    {
        public ExtractConflictDialog(int conflicts)
        {
            Text = Strings.Get("Files_ExtractTitle");
            Icon = AppIcon.Icon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(480, 0),
                UseMnemonic = false,
                Text = Strings.Format("Extract_Conflicts", conflicts.ToString("N0", LanguageService.Culture))
            });

            var replace = Ui.Button();
            replace.Text = Strings.Get("Extract_Replace");
            replace.Click += (s, e) => Choose(ConflictPolicy.Replace);
            var skip = Ui.Button();
            skip.Text = Strings.Get("Extract_SkipExisting");
            skip.Click += (s, e) => Choose(ConflictPolicy.Skip);
            var cancel = Ui.Button();
            cancel.Text = Strings.Get("Common_Cancel");
            cancel.DialogResult = DialogResult.Cancel;

            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 12, 0, 0);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(skip);
            buttons.Controls.Add(replace);
            root.Controls.Add(buttons);
            Controls.Add(root);
            AcceptButton = cancel;
            CancelButton = cancel;
        }

        /// <summary>The user's choice; null when the dialog was cancelled.</summary>
        public ConflictPolicy? Choice { get; private set; }

        private void Choose(ConflictPolicy policy)
        {
            Choice = policy;
            DialogResult = DialogResult.OK;
        }
    }
}
