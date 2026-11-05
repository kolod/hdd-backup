using System;
using System.Collections;
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
    /// Everything that starts by itself and every scheduled task of the opened system, as one filterable list
    /// (contracts/ui.md). It shows what <see cref="StartupInventory"/> found; nothing here changes the image.
    /// </summary>
    internal sealed class StartupTasksWindow : Form
    {
        private readonly StartupResult _result;
        private readonly ComboBox _kind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
        private readonly TextBox _search = new TextBox { Width = 220 };
        private readonly CheckBox _onlyActive = new CheckBox { AutoSize = true, Checked = true };
        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = true,
            ShowItemToolTips = true
        };
        private readonly Label _counts = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 2) };
        private readonly Label _empty = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 8, 0, 8) };
        private readonly Label _notes = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
        private readonly Label _verbatim = new Label { AutoSize = true, UseMnemonic = false, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };
        private readonly Label _warning = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Color.DarkRed, Margin = new Padding(0, 0, 0, 6) };
        private readonly Button _close = Ui.Button();
        private readonly FlowLayoutPanel _bar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
        private readonly Button _copy = Ui.Button();
        private readonly Button _save = Ui.Button();
        private readonly Label _reportStatus = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 0, 0, 2) };
        private readonly PortableSettings _settings;
        private int _sortColumn = -1;
        private bool _sortDescending;

        public StartupTasksWindow(StartupResult result, PortableSettings settings = null)
        {
            _result = result;
            _settings = settings;
            Text = Strings.Format("Startup_Title", result.Source?.DisplayName ?? string.Empty);
            Icon = AppIcon.Icon;
            Font = UiFont.For(LanguageService.Current);
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1040, 600);
            MinimumSize = new Size(720, 420);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _warning.Visible = false;
            ShowWarning();
            var bar = _bar;
            var filterCaption = new Label { Text = Strings.Get("Startup_Filter") + ":", AutoSize = true, Margin = new Padding(0, 6, 4, 0) };
            var searchCaption = new Label { Text = Strings.Get("Startup_Search") + ":", AutoSize = true, Margin = new Padding(12, 6, 4, 0) };
            _onlyActive.Text = Strings.Get("Startup_OnlyActive");
            _onlyActive.Margin = new Padding(12, 5, 0, 0);
            bar.Controls.AddRange(new Control[] { filterCaption, _kind, searchCaption, _search, _onlyActive });
            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            head.Controls.Add(_warning);
            root.Controls.Add(head, 0, 0);
            root.Controls.Add(bar, 0, 1);

            var listHost = new Panel { Dock = DockStyle.Fill };
            listHost.Controls.Add(_list);
            _empty.Dock = DockStyle.Top;
            listHost.Controls.Add(_empty);
            _empty.Visible = false;
            root.Controls.Add(listHost, 0, 2);

            var below = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
            below.Controls.Add(_counts);
            below.Controls.Add(_notes);
            below.Controls.Add(_verbatim);
            root.Controls.Add(below, 0, 3);

            _close.Text = Strings.Get("Common_Close");
            _close.DialogResult = DialogResult.Cancel;
            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 4, 0, 0);
            _copy.Text = Strings.Get("Report_Copy");
            _save.Text = Strings.Get("Report_Save");
            _copy.Click += (s, e) => ShowReportStatus(ReportActions.Copy(ReportText(), out bool ok), ok);
            _save.Click += (s, e) =>
            {
                string message = ReportActions.Save(ReportText(), "startup-tasks_" + (_result.Source?.DisplayName ?? "report"), _settings, this, out bool ok);
                if (message != null)
                {
                    ShowReportStatus(message, ok);
                }
            };
            buttons.Controls.Add(_close);
            buttons.Controls.Add(_save);
            buttons.Controls.Add(_copy);
            below.Controls.Add(_reportStatus);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);
            CancelButton = _close;

            _list.Columns.Add(Strings.Get("Startup_ColKind"), 100);
            _list.Columns.Add(Strings.Get("Startup_ColName"), 170);
            _list.Columns.Add(Strings.Get("Startup_ColRuns"), 340);
            _list.Columns.Add(Strings.Get("Startup_ColUser"), 70);
            _list.Columns.Add(Strings.Get("Startup_ColSource"), 190);
            _list.Columns.Add(Strings.Get("Startup_ColStatus"), 170);
            _list.ColumnClick += (s, e) => SortBy(e.Column);
            _list.Resize += (s, e) => FitColumns();

            foreach (string key in new[] { "Startup_All", "Startup_KindService", "Startup_KindTimer", "Startup_KindBoot", "Startup_KindDesktop", "Startup_KindCron" })
            {
                _kind.Items.Add(Strings.Get(key));
            }

            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => Fill();
            _search.TextChanged += (s, e) => Fill();
            _onlyActive.CheckedChanged += (s, e) => Fill();
            _verbatim.Text = Strings.Get("Startup_Verbatim");
            Fill();
        }

        /// <summary>The entries that pass the filters, in the order they are shown.</summary>
        internal IReadOnlyList<StartupEntry> VisibleEntries { get; private set; } = Array.Empty<StartupEntry>();

        private string ReportText() => ReportBuilder.Startup(_result, VisibleEntries, _counts.Text, new ReportTexts(), DateTime.Now);

        private void ShowReportStatus(string message, bool ok)
        {
            _reportStatus.Text = message;
            _reportStatus.ForeColor = ok ? SystemColors.ControlText : Color.DarkRed;
        }

        private void ShowWarning()
        {
            var state = _result.Source?.State;
            if (state == null || state.Condition == FileSystemCondition.Clean)
            {
                return;
            }

            _warning.Text = Strings.Get(state.Condition == FileSystemCondition.Damaged ? "Files_StateDamaged" : "Files_StateNeedsRepair");
            _warning.Visible = true;
        }

        private bool NotLinux => _result.System != null && !_result.System.IsLinux;

        private static bool InKind(StartupEntry e, int filter)
        {
            switch (filter)
            {
                case 1:
                    return e.Kind == StartupKind.Service || e.Kind == StartupKind.Socket || e.Kind == StartupKind.UserService;
                case 2:
                    return e.Kind == StartupKind.Timer;
                case 3:
                    return e.Kind == StartupKind.BootScript;
                case 4:
                    return e.Kind == StartupKind.Desktop;
                case 5:
                    return e.Kind == StartupKind.Cron;
                default:
                    return true;
            }
        }

        private void Fill()
        {
            if (NotLinux)
            {
                foreach (Control c in _bar.Controls)
                {
                    c.Visible = false;
                }

                _verbatim.Visible = false;
                _copy.Visible = _save.Visible = false;
                _list.Visible = false;
                _empty.Text = Strings.Get("Ssh_NotLinux");
                _empty.Visible = true;
                _counts.Text = string.Empty;
                return;
            }

            string search = _search.Text.Trim();
            var baseSet = _result.Entries.Where(e => !_onlyActive.Checked || e.Active).ToList();
            var visible = baseSet
                .Where(e => InKind(e, _kind.SelectedIndex))
                .Where(e => search.Length == 0 || Matches(e, search))
                .ToList();
            VisibleEntries = visible;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in visible)
            {
                var item = new ListViewItem(DiagnosticTexts.KindName(e.Kind)) { Tag = e };
                item.SubItems.Add(e.Name);
                item.SubItems.Add(DiagnosticTexts.Runs(e));
                item.SubItems.Add(e.User);
                item.SubItems.Add(e.Source);
                item.SubItems.Add(DiagnosticTexts.Status(e));
                item.ToolTipText = DiagnosticTexts.Tooltip(e);
                if (!e.Active)
                {
                    item.ForeColor = SystemColors.GrayText;
                }

                _list.Items.Add(item);
            }

            Sort();
            _list.EndUpdate();
            FitColumns();

            bool filtered = _kind.SelectedIndex != 0 || search.Length > 0;
            _counts.Text = filtered
                ? Strings.Format("Startup_CountsFiltered", visible.Count.ToString("N0", LanguageService.Culture), baseSet.Count.ToString("N0", LanguageService.Culture))
                : Strings.Format("Startup_Counts", visible.Count.ToString("N0", LanguageService.Culture), _result.InactiveCount.ToString("N0", LanguageService.Culture));

            var notes = _result.Notes.Select(DiagnosticTexts.Note).ToList();
            var read = _result.ReadNotes;
            if (read != null)
            {
                notes.AddRange(read.TruncatedFiles.Select(p => Strings.Format("Diag_N_Truncated", p)));
                notes.AddRange(read.UnreadableFiles.Select(p => Strings.Format("Diag_N_Unreadable", p)));
            }

            _notes.Text = string.Join("\n", notes);
            _notes.Visible = notes.Count > 0;
            _empty.Text = Strings.Get("Startup_Empty");
            _empty.Visible = _result.Entries.Count == 0;
            _list.Visible = !_empty.Visible;
        }

        private static bool Matches(StartupEntry e, string text)
        {
            return Contains(e.Name, text) || Contains(e.Command, text) || Contains(e.User, text) || Contains(e.Source, text) || Contains(DiagnosticTexts.When(e), text);
        }

        private static bool Contains(string haystack, string needle)
        {
            return haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void FitColumns()
        {
            if (_list.Columns.Count < 6)
            {
                return;
            }

            int others = 0;
            foreach (int i in new[] { 0, 1, 3, 4, 5 })
            {
                others += _list.Columns[i].Width;
            }

            _list.Columns[2].Width = Math.Max(240, _list.ClientSize.Width - others - 2);
        }

        private void SortBy(int column)
        {
            if (column == _sortColumn)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortColumn = column;
                _sortDescending = false;
            }

            _list.BeginUpdate();
            Sort();
            _list.EndUpdate();
        }

        private void Sort()
        {
            if (_sortColumn < 0)
            {
                return; // the order of the result: active first, then by kind and name
            }

            _list.ListViewItemSorter = new ItemComparer(_sortColumn, _sortDescending);
            _list.Sort();
            _list.ListViewItemSorter = null;
        }

        private sealed class ItemComparer : IComparer
        {
            private readonly int _column;
            private readonly bool _descending;

            public ItemComparer(int column, bool descending)
            {
                _column = column;
                _descending = descending;
            }

            public int Compare(object x, object y)
            {
                var a = (ListViewItem)x;
                var b = (ListViewItem)y;
                int result;
                if (_column == 0)
                {
                    result = ((int)((StartupEntry)a.Tag).Kind).CompareTo((int)((StartupEntry)b.Tag).Kind);
                }
                else
                {
                    result = string.Compare(a.SubItems[_column].Text, b.SubItems[_column].Text, StringComparison.CurrentCultureIgnoreCase);
                }

                return _descending ? -result : result;
            }
        }
    }
}
