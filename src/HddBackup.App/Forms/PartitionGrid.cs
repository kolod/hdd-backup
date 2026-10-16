using System;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Partitioning;

namespace HddBackup.App.Forms
{
    /// <summary>Partition list with an explanation when the device has no usable table.</summary>
    internal sealed class PartitionGrid : UserControl, ILocalizable
    {
        private readonly ListView _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            Dock = DockStyle.Fill
        };

        private readonly Label _message = new Label { Dock = DockStyle.Fill, AutoSize = false, Visible = false };
        private readonly ColumnHeader _number = new ColumnHeader { Width = 40 };
        private readonly ColumnHeader _name = new ColumnHeader { Width = 170 };
        private readonly ColumnHeader _fileSystem = new ColumnHeader { Width = 90 };
        private readonly ColumnHeader _size = new ColumnHeader { Width = 90, TextAlign = HorizontalAlignment.Right };
        private readonly ColumnHeader _offset = new ColumnHeader { Width = 120, TextAlign = HorizontalAlignment.Right };
        private readonly HeaderToolTip _offsetTip;
        private PartitionTable _table = PartitionTable.Empty;

        public PartitionGrid()
        {
            _list.Columns.AddRange(new[] { _number, _name, _fileSystem, _size, _offset });
            _offsetTip = new HeaderToolTip(_list, _list.Columns.IndexOf(_offset));
            _list.SelectedIndexChanged += (s, e) => SelectionChanged?.Invoke(this, EventArgs.Empty);
            _list.HandleCreated += (s, e) => DeviceListView.FitColumns(_list);
            _list.FontChanged += (s, e) => DeviceListView.FitColumns(_list);
            Controls.Add(_list);
            Controls.Add(_message);
            Height = 110;
            ApplyStrings();
        }

        public event EventHandler SelectionChanged;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _offsetTip.Dispose();
            }

            base.Dispose(disposing);
        }

        public PartitionInfo SelectedPartition =>
            _list.SelectedItems.Count == 1 ? _list.SelectedItems[0].Tag as PartitionInfo : null;

        public void SetTable(PartitionTable table)
        {
            int? selected = SelectedPartition?.Number;
            _table = table ?? PartitionTable.Empty;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var p in _table.Partitions)
            {
                var item = new ListViewItem(p.Number.ToString(LanguageService.Culture)) { Tag = p };
                item.SubItems.Add(p.Name);
                item.SubItems.Add(p.FileSystem);
                item.SubItems.Add(Formatters.Bytes(p.Length));
                item.SubItems.Add(Formatters.Offset(p.Offset));
                _list.Items.Add(item);
                item.Selected = p.Number == selected;
            }

            _list.EndUpdate();
            DeviceListView.FitColumns(_list);
            bool empty = _table.Partitions.Count == 0;
            _message.Text = _table.Style == TableStyle.Unknown
                ? Strings.Get("Partitions_Damaged")
                : Strings.Get("Partitions_NoTable");
            _message.Visible = empty;
            _list.Visible = !empty;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyStrings()
        {
            _number.Text = Strings.Get("Column_Number");
            _name.Text = Strings.Get("Column_Name");
            _fileSystem.Text = Strings.Get("Column_FileSystem");
            _size.Text = Strings.Get("Column_Size");
            _offset.Text = Strings.Get("Column_Offset");
            _offsetTip.Text = Strings.Get("Tooltip_ColumnOffset");
            SetTable(_table);
        }
    }
}
