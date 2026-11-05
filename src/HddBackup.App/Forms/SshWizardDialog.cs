using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// The SSH check: Server, Settings, Accounts, Result. It only shows what <see cref="SshCheck"/> found;
    /// nothing here changes the image, and advice is words, not actions (contracts/ui.md).
    /// </summary>
    internal sealed class SshWizardDialog : Form
    {
        private static readonly Color Green = Color.FromArgb(0x1E, 0x7B, 0x34);
        private static readonly Color Red = Color.FromArgb(0xB0, 0x20, 0x20);
        private static readonly Color Amber = Color.FromArgb(0x9A, 0x6A, 0x00);
        private static readonly Color Blue = Color.FromArgb(0x2B, 0x5C, 0x9E);

        private readonly SshReport _report;
        private readonly Label[] _steps = new Label[4];
        private readonly Panel[] _pages = new Panel[4];
        private readonly Button _back = Ui.Button();
        private readonly Button _next = Ui.Button();
        private readonly Button _close = Ui.Button();
        private readonly CheckBox _showSystem = new CheckBox { AutoSize = true };
        private readonly ListView _accounts = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false
        };
        private readonly NoteListPanel _server = new NoteListPanel();
        private readonly NoteListPanel _settings = new NoteListPanel();
        private readonly Panel _verdictBanner = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12) };
        private readonly Label _verdictText = new Label { AutoSize = true, UseMnemonic = false };
        private readonly Label _whyCaption = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 12, 0, 4) };
        private readonly NoteListPanel _why = new NoteListPanel { Dock = DockStyle.Fill };
        private readonly Label _noteCaption = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 12, 0, 4) };
        private readonly NoteListPanel _notes = new NoteListPanel { Dock = DockStyle.Fill };
        private readonly Label _warning = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Color.DarkRed, Margin = new Padding(0, 0, 0, 6) };
        private readonly Label _title = new Label { AutoSize = true, UseMnemonic = false };
        private readonly Button _copy = Ui.Button();
        private readonly Button _save = Ui.Button();
        private readonly Label _reportStatus = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(8, 8, 0, 0) };
        private readonly PortableSettings _portable;
        private int _step;

        public SshWizardDialog(SshReport report, PortableSettings settings = null)
        {
            _report = report;
            _portable = settings;
            Text = Strings.Format("Ssh_Title", report.Source?.DisplayName ?? string.Empty);
            Icon = AppIcon.Icon;
            Font = UiFont.For(LanguageService.Current);
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 560);
            MinimumSize = new Size(560, 420);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, Margin = new Padding(0) };
            _title.Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold);
            _title.Text = Text;
            header.Controls.Add(_title);
            ShowWarning();
            if (_warning.Text.Length > 0)
            {
                header.Controls.Add(_warning);
            }

            root.Controls.Add(header, 0, 0);

            var stepRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 8), WrapContents = true };
            for (int i = 0; i < 4; i++)
            {
                _steps[i] = new Label
                {
                    AutoSize = true,
                    UseMnemonic = false,
                    Margin = new Padding(0, 0, 14, 0),
                    Text = (i + 1) + "  " + Strings.Get("Ssh_Step" + (i + 1))
                };
                stepRow.Controls.Add(_steps[i]);
            }

            root.Controls.Add(stepRow, 0, 1);

            var host = new Panel { Dock = DockStyle.Fill };
            for (int i = 0; i < 4; i++)
            {
                _pages[i] = new Panel { Dock = DockStyle.Fill, Visible = false };
                host.Controls.Add(_pages[i]);
            }

            _pages[0].Controls.Add(_server);
            _pages[1].Controls.Add(_settings);
            BuildAccountsPage(_pages[2]);
            BuildResultPage(_pages[3]);
            root.Controls.Add(host, 0, 2);

            _back.Text = Strings.Get("Ssh_Back");
            _next.Text = Strings.Get("Ssh_Next");
            _close.Text = Strings.Get("Common_Close");
            _close.DialogResult = DialogResult.Cancel;
            _back.Click += (s, e) => Go(_step - 1);
            _next.Click += (s, e) => Go(_step + 1);
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 8, 0, 0);
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_next);
            buttons.Controls.Add(_back);
            root.Controls.Add(buttons, 0, 3);

            Controls.Add(root);
            CancelButton = _close;

            Fill();
            Go(report.Verdict.Code == VerdictCode.NotALinuxRoot ? 3 : 0);
        }

        private void BuildAccountsPage(Panel page)
        {
            _accounts.Columns.Add(Strings.Get("Ssh_ColAccount"), 130);
            _accounts.Columns.Add(Strings.Get("Ssh_ColShell"), 120);
            _accounts.Columns.Add(Strings.Get("Ssh_ColPassword"), 90);
            _accounts.Columns.Add(Strings.Get("Ssh_ColKeys"), 70);
            _accounts.Columns.Add(Strings.Get("Ssh_ColCanLogIn"), 300);
            _accounts.ShowItemToolTips = true;
            _accounts.Resize += (s, e) => FitLastColumn();
            _showSystem.Text = Strings.Get("Ssh_ShowSystemAccounts");
            _showSystem.Dock = DockStyle.Bottom;
            _showSystem.CheckedChanged += (s, e) => FillAccounts();
            page.Controls.Add(_accounts);
            page.Controls.Add(_showSystem);
        }

        private void BuildResultPage(Panel page)
        {
            _verdictText.Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold);
            _verdictBanner.Controls.Add(_verdictText);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            _whyCaption.Font = new Font(Font, FontStyle.Bold);
            _noteCaption.Font = new Font(Font, FontStyle.Bold);
            _whyCaption.Text = Strings.Get("Ssh_Why");
            _noteCaption.Text = Strings.Get("Ssh_CouldNotCheck");
            body.Controls.Add(_whyCaption, 0, 0);
            body.Controls.Add(_why, 0, 1);
            body.Controls.Add(_noteCaption, 0, 2);
            body.Controls.Add(_notes, 0, 3);
            page.Controls.Add(body);
            page.Controls.Add(_verdictBanner);

            _copy.Text = Strings.Get("Report_Copy");
            _save.Text = Strings.Get("Report_Save");
            _copy.Click += (s, e) => ShowReportStatus(ReportActions.Copy(ReportText(), out bool ok), ok);
            _save.Click += (s, e) =>
            {
                string message = ReportActions.Save(ReportText(), "ssh-check_" + (_report.Source?.DisplayName ?? "report"), _portable, this, out bool ok);
                if (message != null)
                {
                    ShowReportStatus(message, ok);
                }
            };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
            actions.Controls.AddRange(new Control[] { _copy, _save, _reportStatus });
            page.Controls.Add(actions);
        }

        private string ReportText() => ReportBuilder.Ssh(_report, new ReportTexts(), DateTime.Now);

        private void ShowReportStatus(string message, bool ok)
        {
            _reportStatus.Text = message;
            _reportStatus.ForeColor = ok ? SystemColors.ControlText : Color.DarkRed;
        }

        private void ShowWarning()
        {
            var state = _report.Source?.State;
            if (state == null || state.Condition == FileSystemCondition.Clean)
            {
                return;
            }

            _warning.Text = Strings.Get(state.Condition == FileSystemCondition.Damaged ? "Files_StateDamaged" : "Files_StateNeedsRepair");
        }

        private void Fill()
        {
            _server.SetItems(_report.Findings.Where(f => f.Page == 1).Select(ToItem));
            _settings.SetItems(_report.Findings.Where(f => f.Page == 2).Select(ToItem));
            FillAccounts();
            FillResult();
        }

        private static NoteItem ToItem(Finding f)
        {
            return new NoteItem(
                DiagnosticTexts.Glyph(f.Status),
                StatusColor(f.Status),
                DiagnosticTexts.Finding(f),
                DiagnosticTexts.EvidenceText(f.Evidence));
        }

        private static Color StatusColor(FindingStatus status)
        {
            switch (status)
            {
                case FindingStatus.Ok:
                    return Green;
                case FindingStatus.Problem:
                    return Red;
                case FindingStatus.Unknown:
                    return Amber;
                default:
                    return Blue;
            }
        }

        private void FillAccounts()
        {
            _accounts.BeginUpdate();
            _accounts.Items.Clear();
            foreach (var a in _report.Accounts.Where(x => _showSystem.Checked || !x.IsSystemAccount))
            {
                var item = new ListViewItem(a.Name);
                item.SubItems.Add(a.Shell);
                item.SubItems.Add(DiagnosticTexts.Password(a.Password));
                item.SubItems.Add(DiagnosticTexts.Keys(a));
                item.SubItems.Add(DiagnosticTexts.CanLogIn(a));
                item.ForeColor = a.CanLogIn == LoginAnswer.No ? SystemColors.GrayText : SystemColors.WindowText;
                item.ToolTipText = item.SubItems[4].Text;
                _accounts.Items.Add(item);
            }

            _accounts.EndUpdate();
            FitLastColumn();
        }

        /// <summary>The "can log in" column takes the width that is left, so its reason is not cut off.</summary>
        private void FitLastColumn()
        {
            if (_accounts.Columns.Count < 5)
            {
                return;
            }

            int others = 0;
            for (int i = 0; i < 4; i++)
            {
                others += _accounts.Columns[i].Width;
            }

            _accounts.Columns[4].Width = Math.Max(300, _accounts.ClientSize.Width - others - 2);
        }

        private void FillResult()
        {
            var v = _report.Verdict;
            _verdictText.Text = DiagnosticTexts.Verdict(v);
            Color back;
            Color fore;
            string glyph;
            switch (v.Code)
            {
                case VerdictCode.Works:
                    back = Color.FromArgb(0xDF, 0xF0, 0xD8);
                    fore = Color.FromArgb(0x1E, 0x4D, 0x2B);
                    glyph = DiagnosticTexts.Glyph(FindingStatus.Ok);
                    break;
                case VerdictCode.NotEnabledBootFlagPossible:
                case VerdictCode.Undetermined:
                    back = Color.FromArgb(0xFC, 0xF3, 0xCF);
                    fore = Color.FromArgb(0x6B, 0x4A, 0x00);
                    glyph = DiagnosticTexts.Glyph(FindingStatus.Unknown);
                    break;
                case VerdictCode.NotALinuxRoot:
                    back = Color.FromArgb(0xE6, 0xEE, 0xF8);
                    fore = Color.FromArgb(0x1F, 0x3F, 0x6B);
                    glyph = DiagnosticTexts.Glyph(FindingStatus.Info);
                    break;
                default:
                    back = Color.FromArgb(0xF6, 0xD9, 0xD9);
                    fore = Color.FromArgb(0x7A, 0x14, 0x14);
                    glyph = DiagnosticTexts.Glyph(FindingStatus.Problem);
                    break;
            }

            _verdictBanner.BackColor = back;
            _verdictText.ForeColor = fore;
            _verdictText.Text = glyph + "  " + _verdictText.Text;
            _verdictText.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 80), 0);

            var why = v.Reasons.Select(ToItem).ToList();
            _whyCaption.Visible = _why.Visible = why.Count > 0;
            _why.SetItems(why);

            var notes = v.Notes.Select(ToItem).ToList();
            var read = _report.ReadNotes;
            if (read != null)
            {
                notes.AddRange(read.TruncatedFiles.Select(p => new NoteItem(DiagnosticTexts.Glyph(FindingStatus.Unknown), Amber, Strings.Format("Diag_N_Truncated", p))));
                notes.AddRange(read.UnreadableFiles.Select(p => new NoteItem(DiagnosticTexts.Glyph(FindingStatus.Unknown), Amber, Strings.Format("Diag_N_Unreadable", p))));
            }

            _noteCaption.Visible = _notes.Visible = notes.Count > 0;
            _notes.SetItems(notes);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _verdictText.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 80), 0);
        }

        private void Go(int step)
        {
            bool notLinux = _report.Verdict.Code == VerdictCode.NotALinuxRoot;
            _step = Math.Max(0, Math.Min(3, step));
            for (int i = 0; i < 4; i++)
            {
                _pages[i].Visible = i == _step;
                _steps[i].Font = new Font(Font, i == _step ? FontStyle.Bold : FontStyle.Regular);
                _steps[i].Visible = !notLinux;
            }

            _copy.Visible = _save.Visible = _reportStatus.Visible = !notLinux;
            _back.Enabled = !notLinux && _step > 0;
            _next.Enabled = !notLinux && _step < 3;
            _back.Visible = _next.Visible = !notLinux;
            AcceptButton = _next.Enabled ? _next : _close;
        }
    }
}
