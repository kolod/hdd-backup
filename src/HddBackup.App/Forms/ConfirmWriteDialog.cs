using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.Core.Imaging;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Last stop before a device is overwritten. Cancel is the default button, and Write is enabled
    /// only after a short delay (and, for external disks, an explicit checkbox).
    /// </summary>
    internal sealed class ConfirmWriteDialog : Form
    {
        private const int DelaySeconds = 2;

        private readonly Button _write = Ui.Button();
        private readonly Button _cancel = Ui.Button();
        private readonly CheckBox _external = new CheckBox { AutoSize = true, Visible = false };
        private readonly Timer _timer = new Timer { Interval = 1000 };
        private int _countdown = DelaySeconds;

        /// <param name="imageBytes">Size of an image that does not exist yet (shrink and write); null = file size.</param>
        /// <param name="imageName">Display name for such an image.</param>
        public ConfirmWriteDialog(OperationRequest request, IReadOnlyCollection<ValidationWarning> warnings, long? imageBytes = null, string imageName = null)
        {
            Text = Strings.Get("Confirm_Title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };
            var warning = new Label
            {
                AutoSize = true,
                ForeColor = Color.DarkRed,
                Font = new Font(Font, FontStyle.Bold),
                Text = Strings.Get("Confirm_Warning"),
                MaximumSize = new Size(560, 0)
            };
            root.Controls.Add(warning);

            var device = request.Device;
            var details = new List<string>
            {
                Strings.Format("Confirm_Device", DeviceListView.DisplayName(device)),
                Strings.Format("Confirm_Capacity", Formatters.Bytes(device.Size)),
                Strings.Format("Confirm_Bus", Formatters.Bus(device.BusType)),
                Strings.Format("Confirm_Volumes", Volumes(device)),
                Strings.Format("Confirm_Partitions", Partitions(device))
            };
            if (request.Kind == OperationKind.RestorePartition && request.Partition != null)
            {
                details.Add(Strings.Format(
                    "Confirm_PartitionTarget",
                    Strings.Format("Partition_Summary", request.Partition.Number, request.Partition.FileSystem, Formatters.Bytes(request.Partition.Length))));
            }

            if (imageBytes.HasValue)
            {
                details.Add(Strings.Format("Confirm_Image", imageName ?? Path.GetFileName(request.ImagePath), Formatters.Bytes(imageBytes.Value)));
            }
            else
            {
                var image = new FileInfo(request.ImagePath);
                details.Add(Strings.Format("Confirm_Image", image.Name, Formatters.Bytes(image.Length)));
            }
            root.Controls.Add(new Label { AutoSize = true, Text = string.Join(Environment.NewLine, details), MaximumSize = new Size(560, 0), Margin = new Padding(3, 8, 3, 8) });

            var notes = warnings
                .Where(w => w != ValidationWarning.ExternalDisk)
                .Select(w => Strings.Get("Warning_" + w))
                .ToList();
            if (notes.Count > 0)
            {
                root.Controls.Add(new Label
                {
                    AutoSize = true,
                    Text = string.Join(Environment.NewLine, notes.Select(n => "• " + n)),
                    MaximumSize = new Size(560, 0),
                    BackColor = Color.FromArgb(252, 248, 227),
                    Padding = new Padding(6)
                });
            }

            _external.Text = Strings.Get("Confirm_ExternalCheck");
            _external.Visible = !device.IsRemovableMedia;
            _external.MaximumSize = new Size(560, 0);
            _external.CheckedChanged += (s, e) => UpdateWrite();
            root.Controls.Add(_external);

            var buttons = Ui.Flow(rightToLeft: true);
            buttons.Dock = DockStyle.Fill;
            _cancel.Text = Strings.Get("Common_Cancel");
            _cancel.DialogResult = DialogResult.Cancel;
            _write.DialogResult = DialogResult.OK;
            _write.ForeColor = Color.DarkRed;
            buttons.Controls.Add(_cancel);
            buttons.Controls.Add(_write);
            root.Controls.Add(buttons);
            Controls.Add(root);

            AcceptButton = _cancel;
            CancelButton = _cancel;
            ActiveControl = _cancel;

            _timer.Tick += (s, e) =>
            {
                _countdown--;
                if (_countdown <= 0)
                {
                    _timer.Stop();
                }

                UpdateWrite();
            };
            UpdateWrite();
            _timer.Start();
        }

        /// <summary>True when the user ticked the external-disk confirmation.</summary>
        public bool ExternalDiskConfirmed => _external.Checked;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void UpdateWrite()
        {
            _write.Text = _countdown > 0 ? Strings.Format("Confirm_WriteCountdown", _countdown) : Strings.Get("Confirm_Write");
            _write.Enabled = _countdown <= 0 && (!_external.Visible || _external.Checked);
        }

        private static string Volumes(Core.Devices.DeviceInfo device)
        {
            var mounts = device.Volumes.SelectMany(v => v.MountPoints).Select(m => m.TrimEnd('\\')).ToList();
            return mounts.Count > 0 ? string.Join(" ", mounts) : Strings.Get("Common_None");
        }

        private static string Partitions(Core.Devices.DeviceInfo device)
        {
            var parts = device.PartitionTable.Partitions
                .Select(p => Strings.Format("Partition_Summary", p.Number, p.FileSystem, Formatters.Bytes(p.Length)))
                .ToList();
            return parts.Count > 0
                ? Formatters.Table(device.PartitionTable.Style) + ": " + string.Join("; ", parts)
                : Formatters.Table(device.PartitionTable.Style);
        }
    }
}
