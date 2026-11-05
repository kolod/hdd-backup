using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Browsing;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Every item of an extraction that was skipped, renamed or failed, with the reason (FR-018);
    /// the list can be copied to the clipboard.
    /// </summary>
    internal sealed class ExtractSummaryDialog : Form
    {
        private readonly ExtractionSummary _summary;

        public ExtractSummaryDialog(ExtractionSummary summary)
        {
            _summary = summary;
            Text = Strings.Get("Extract_DetailsTitle");
            Icon = AppIcon.Icon;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 440);
            MinimumSize = new Size(520, 300);

            var root = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new Label
            {
                AutoSize = true,
                UseMnemonic = false,
                Margin = new Padding(0, 0, 0, 8),
                Text = Strings.Format(
                    "Result_Extracted",
                    summary.FilesExtracted.ToString("N0", LanguageService.Culture),
                    summary.DirectoriesCreated.ToString("N0", LanguageService.Culture),
                    summary.DestinationFolder)
            };
            root.Controls.Add(header, 0, 0);

            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false
            };
            list.Columns.Add(Strings.Get("Extract_ColKind"), 90);
            list.Columns.Add(Strings.Get("Extract_ColPath"), 280);
            list.Columns.Add(Strings.Get("Extract_ColName"), 150);
            list.Columns.Add(Strings.Get("Extract_ColReason"), 280);
            foreach (var issue in Ordered(summary))
            {
                var item = new ListViewItem(Formatters.ExtractKind(issue.Kind));
                item.SubItems.Add(issue.Path);
                item.SubItems.Add(issue.ResultName);
                item.SubItems.Add(Formatters.ExtractIssue(issue));
                if (issue.Kind == ExtractionIssueKind.Failed)
                {
                    item.ForeColor = Color.DarkRed;
                }

                list.Items.Add(item);
            }

            root.Controls.Add(list, 0, 1);

            var copy = Ui.Button();
            copy.Text = Strings.Get("Extract_Copy");
            copy.Click += (s, e) => CopyList();
            var close = Ui.Button();
            close.Text = Strings.Get("Common_Close");
            close.DialogResult = DialogResult.OK;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 8, 0, 0);
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);
            root.Controls.Add(buttons, 0, 2);

            Controls.Add(root);
            AcceptButton = close;
            CancelButton = close;
        }

        private static System.Collections.Generic.IEnumerable<ExtractionIssue> Ordered(ExtractionSummary summary)
        {
            // Failures first, then renames, then skipped items; the order of the tree within each.
            return summary.Issues.Select((issue, index) => new { issue, index })
                .OrderBy(x => x.issue.Kind == ExtractionIssueKind.Failed ? 0 : x.issue.Kind == ExtractionIssueKind.Renamed ? 1 : 2)
                .ThenBy(x => x.index)
                .Select(x => x.issue);
        }

        private void CopyList()
        {
            var text = new StringBuilder();
            foreach (var issue in Ordered(_summary))
            {
                text.Append(Formatters.ExtractKind(issue.Kind)).Append('\t')
                    .Append(issue.Path).Append('\t')
                    .Append(issue.ResultName).Append('\t')
                    .AppendLine(Formatters.ExtractIssue(issue));
            }

            try
            {
                Clipboard.SetText(text.ToString());
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard busy; the list is still on screen.
            }
        }
    }
}
