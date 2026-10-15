using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Devices;

namespace HddBackup.App.Forms
{
    /// <summary>List of removable devices; keeps the selection across refreshes (by disk number).</summary>
    internal sealed class DeviceListView : ListView, ILocalizable
    {
        private readonly ColumnHeader _device = new ColumnHeader { Width = 220 };
        private readonly ColumnHeader _size = new ColumnHeader { Width = 80, TextAlign = HorizontalAlignment.Right };
        private readonly ColumnHeader _bus = new ColumnHeader { Width = 50 };
        private readonly ColumnHeader _volumes = new ColumnHeader { Width = 80 };
        private readonly ColumnHeader _table = new ColumnHeader { Width = 100 };
        private readonly ColumnHeader _notes = new ColumnHeader { Width = 200 };
        private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();

        public DeviceListView()
        {
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = false;
            Columns.AddRange(new[] { _device, _size, _bus, _volumes, _table, _notes });
            ApplyStrings();
        }

        public DeviceInfo SelectedDevice =>
            SelectedItems.Count == 1 ? SelectedItems[0].Tag as DeviceInfo : null;

        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            int? selected = SelectedDevice?.DiskNumber;
            _devices = devices ?? Array.Empty<DeviceInfo>();
            BeginUpdate();
            Items.Clear();
            foreach (var device in _devices)
            {
                var item = new ListViewItem(DisplayName(device)) { Tag = device };
                if (device.IsCapacityMisreported)
                {
                    item.ForeColor = System.Drawing.SystemColors.GrayText;
                }

                item.SubItems.Add(Formatters.Bytes(device.Size));
                item.SubItems.Add(Formatters.Bus(device.BusType));
                item.SubItems.Add(string.Join(" ", device.Volumes.SelectMany(v => v.MountPoints).Select(m => m.TrimEnd('\\'))));
                item.SubItems.Add(Formatters.Table(device.PartitionTable.Style));
                item.SubItems.Add(Notes(device));
                Items.Add(item);
                if (device.DiskNumber == selected)
                {
                    item.Selected = true;
                }
            }

            if (SelectedItems.Count == 0 && Items.Count == 1)
            {
                Items[0].Selected = true;
            }

            EndUpdate();
            FitColumns(this);
            if (SelectedDevice?.DiskNumber != selected)
            {
                OnSelectedIndexChanged(EventArgs.Empty);
            }
        }

        public void ApplyStrings()
        {
            _device.Text = Strings.Get("Column_Device");
            _size.Text = Strings.Get("Column_Size");
            _bus.Text = Strings.Get("Column_Bus");
            _volumes.Text = Strings.Get("Column_Volumes");
            _table.Text = Strings.Get("Column_Table");
            _notes.Text = Strings.Get("Column_Notes");
            SetDevices(_devices);
            FitColumns(this);
        }

        /// <summary>Sizes every column to fit both its header and its widest cell (no clipped text).</summary>
        public static void FitColumns(ListView list)
        {
            if (!list.IsHandleCreated)
            {
                return;
            }

            for (int i = 0; i < list.Columns.Count; i++)
            {
                list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.HeaderSize);
                int header = list.Columns[i].Width;
                if (list.Items.Count > 0)
                {
                    list.AutoResizeColumn(i, ColumnHeaderAutoResizeStyle.ColumnContent);
                    list.Columns[i].Width = Math.Max(header, list.Columns[i].Width);
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FitColumns(this);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitColumns(this);
        }

        public static string DisplayName(DeviceInfo device)
        {
            var name = string.IsNullOrEmpty(device.Model) ? Strings.Get("Device_Unnamed") : device.Model;
            return name + " (#" + device.DiskNumber + ")";
        }

        private static string Notes(DeviceInfo device)
        {
            var notes = new List<string>();
            if (device.IsWriteProtected)
            {
                notes.Add(Strings.Get("Note_WriteProtected"));
            }

            if (!device.IsRemovableMedia)
            {
                notes.Add(Strings.Get("Note_External"));
            }

            if (device.PartitionTable.ExtendsBeyondDevice)
            {
                notes.Add(Strings.Get("Note_SizeTruncated"));
            }

            if (device.HostsExecutable)
            {
                notes.Add(Strings.Get("Note_RunsFromHere"));
            }

            return string.Join(", ", notes);
        }
    }
}
