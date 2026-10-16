using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Devices;
using HddBackup.Core.Imaging;

namespace HddBackup.App.Forms
{
    /// <summary>Backup of a whole device or one partition to .img / .img.gz.</summary>
    internal sealed class BackupTab : UserControl, ILocalizable
    {
        private readonly PortableSettings _settings;
        private readonly Label _deviceCaption = Ui.Caption();
        private readonly DeviceListView _devices = new DeviceListView();
        private readonly Label _noDevices = new Label { AutoSize = true, ForeColor = System.Drawing.SystemColors.GrayText, Visible = false };
        private readonly Label _unsuitable = new Label { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed, Visible = false };
        private readonly CheckBox _allowIncomplete = new CheckBox { AutoSize = true, Visible = false, ForeColor = System.Drawing.Color.DarkRed };
        private readonly RadioButton _whole = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _partition = new RadioButton { AutoSize = true };
        private readonly PartitionGrid _partitions = new PartitionGrid { Visible = false };
        private readonly Label _imageCaption = Ui.Caption();
        private readonly TextBox _path = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Label _scopeCaption = Ui.GroupCaption();
        private readonly Label _formatCaption = Ui.GroupCaption();
        private readonly Label _afterCaption = Ui.GroupCaption();
        private readonly ComboBox _format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        private readonly CheckBox _fast = new CheckBox { AutoSize = true };
        private readonly CheckBox _verify = new CheckBox { AutoSize = true };
        private readonly CheckBox _eject = new CheckBox { AutoSize = true };
        private readonly Button _start = Ui.Button();
        private readonly ToolTip _tips = new ToolTip();
        private readonly TableLayoutPanel _root;
        private readonly TableLayoutPanel _right;

        public BackupTab(PortableSettings settings)
        {
            _settings = settings;
            _root = Ui.TwoColumn(out var left, out _right, out var bottom);
            Ui.AddRow(left, _deviceCaption);
            Ui.AddRow(left, _devices, fill: true);
            Ui.AddRow(left, _noDevices);
            Ui.AddRow(left, _unsuitable);
            Ui.AddRow(left, _allowIncomplete);
            _partitions.Dock = DockStyle.Top;
            Ui.AddRow(left, _partitions);

            _deviceCaption.Margin = new Padding(3, 0, 3, 2);
            _scopeCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(_right, _scopeCaption);
            Ui.AddRow(_right, _whole);
            Ui.AddRow(_right, _partition);
            Ui.AddRow(_right, _formatCaption);
            Ui.AddRow(_right, _format);
            Ui.AddRow(_right, _fast);
            Ui.AddRow(_right, _afterCaption);
            Ui.AddRow(_right, _verify);
            Ui.AddRow(_right, _eject);
            Ui.AddRow(_right, new Panel(), fill: true);

            Ui.AddRow(bottom, _imageCaption);
            Ui.AddRow(bottom, Ui.PathRow(_path, _browse));
            var actions = Ui.Flow(rightToLeft: true);
            actions.WrapContents = false; // measured at a narrow width it would wrap and reserve a second row
            actions.Controls.Add(_start);
            Ui.AddRow(bottom, actions);
            Controls.Add(_root);

            _fast.Checked = settings.FastCompression;
            _verify.Checked = settings.VerifyAfterBackup;
            _eject.Checked = settings.EjectAfter;

            _devices.SelectedIndexChanged += (s, e) => OnDeviceChanged();
            _whole.CheckedChanged += (s, e) => UpdateState();
            _partitions.SelectionChanged += (s, e) => UpdateState();
            _path.TextChanged += (s, e) => OnPathChanged();
            _format.SelectedIndexChanged += (s, e) => OnFormatChanged();
            _browse.Click += (s, e) => Browse();
            _start.Click += (s, e) => Start();
            _fast.CheckedChanged += (s, e) => _settings.FastCompression = _fast.Checked;
            _verify.CheckedChanged += (s, e) => _settings.VerifyAfterBackup = _verify.Checked;
            _eject.CheckedChanged += (s, e) => _settings.EjectAfter = _eject.Checked;
            _allowIncomplete.CheckedChanged += (s, e) => UpdateState();

            ApplyStrings();
            _format.SelectedIndex = 0;
        }

        public event EventHandler<OperationRequest> StartRequested;

        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            _devices.SetDevices(devices);
            _noDevices.Visible = devices.Count == 0;
            OnDeviceChanged();
        }

        public void ApplyStrings()
        {
            _deviceCaption.Text = Strings.Get("Backup_SourceDevice");
            _noDevices.Text = Strings.Get("Devices_None");
            _unsuitable.Text = Strings.Get("Device_Unsuitable");
            _allowIncomplete.Text = Strings.Get("Option_AllowIncomplete");
            _whole.Text = Strings.Get("Backup_ScopeWhole");
            _partition.Text = Strings.Get("Backup_ScopePartition");
            _imageCaption.Text = Strings.Get("Backup_ImageFile");
            _browse.Text = Strings.Get("Common_Browse");
            _scopeCaption.Text = Strings.Get("Group_BackupScope");
            _formatCaption.Text = Strings.Get("Group_Format");
            _afterCaption.Text = Strings.Get("Group_AfterBackup");
            Ui.Embolden(Font, _scopeCaption, _formatCaption, _afterCaption);
            // Relabel in place: rebuilding the list would fire SelectedIndexChanged and rewrite the path.
            if (_format.Items.Count == 2)
            {
                _format.Items[0] = Strings.Get("Format_Gzip");
                _format.Items[1] = Strings.Get("Format_Raw");
            }
            else
            {
                _format.Items.Add(Strings.Get("Format_Gzip"));
                _format.Items.Add(Strings.Get("Format_Raw"));
                _format.SelectedIndex = 0;
            }
            _fast.Text = Strings.Get("Option_FastCompression");
            _verify.Text = Strings.Get("Option_VerifyAfterBackup");
            _eject.Text = Strings.Get("Option_Eject");
            _start.Text = Strings.Get("Backup_Start");
            _devices.ApplyStrings();
            _partitions.ApplyStrings();
            _tips.SetToolTip(_whole, Strings.Get("Tooltip_ScopeWhole"));
            _tips.SetToolTip(_fast, Strings.Get("Tooltip_FastCompression"));
            _tips.SetToolTip(_allowIncomplete, Strings.Get("Tooltip_AllowIncomplete"));
            UpdateFormatTip();
            FitLayout();
        }

        private void UpdateFormatTip()
        {
            _tips.SetToolTip(_format, Strings.Get(IsGzip ? "Tooltip_FormatGzip" : "Tooltip_FormatRaw"));
        }

        private void FitLayout()
        {
            Ui.FitRightColumn(_root, _right);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_devices.Width > 100 && _unsuitable.MaximumSize.Width != _devices.Width)
            {
                _unsuitable.MaximumSize = new System.Drawing.Size(_devices.Width, 0);
            }
        }

        private bool IsGzip => _format.SelectedIndex != 1;

        private void OnDeviceChanged()
        {
            _allowIncomplete.Checked = false; // the override is a per-device, per-session decision
            _partitions.SetTable(_devices.SelectedDevice?.PartitionTable);
            UpdateState();
        }

        private void OnPathChanged()
        {
            var path = _path.Text.Trim();
            if (path.EndsWith(ImageFileName.Gzip, StringComparison.OrdinalIgnoreCase) && _format.SelectedIndex != 0)
            {
                _format.SelectedIndex = 0;
            }
            else if (path.EndsWith(ImageFileName.Raw, StringComparison.OrdinalIgnoreCase) && _format.SelectedIndex != 1)
            {
                _format.SelectedIndex = 1;
            }

            UpdateState();
        }

        private void OnFormatChanged()
        {
            var path = _path.Text.Trim();
            if (path.Length > 0)
            {
                _path.Text = ImageFileName.WithExtension(path, IsGzip);
            }

            UpdateFormatTip();
            UpdateState();
        }

        private void UpdateState()
        {
            _partitions.Visible = _partition.Checked;
            _fast.Enabled = IsGzip;
            bool unsuitable = _devices.SelectedDevice?.IsCapacityMisreported == true;
            _unsuitable.Visible = unsuitable;
            _allowIncomplete.Visible = unsuitable;
            bool ready = _devices.SelectedDevice != null
                && (!unsuitable || _allowIncomplete.Checked)
                && _path.Text.Trim().Length > 0
                && (_whole.Checked || _partitions.SelectedPartition != null);
            _start.Enabled = ready && Enabled;
        }

        private void Browse()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = Strings.Get("Dialog_ImageFilter_Save");
                dialog.FilterIndex = IsGzip ? 1 : 2;
                dialog.AddExtension = false;
                dialog.OverwritePrompt = false; // asked once in Start(), together with the checksum files
                dialog.FileName = Path.GetFileName(_path.Text.Trim()).Length > 0 ? Path.GetFileName(_path.Text.Trim()) : DefaultFileName();
                if (Directory.Exists(_settings.LastImageFolder))
                {
                    dialog.InitialDirectory = _settings.LastImageFolder;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    bool gzip = dialog.FilterIndex == 1;
                    _format.SelectedIndex = gzip ? 0 : 1;
                    _path.Text = ImageFileName.WithExtension(dialog.FileName, gzip);
                    _settings.LastImageFolder = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void Start()
        {
            var device = _devices.SelectedDevice;
            if (device == null)
            {
                return;
            }

            var path = ImageFileName.WithExtension(_path.Text.Trim(), IsGzip);
            _path.Text = path;
            if (File.Exists(path) && !ConfirmOverwrite(path))
            {
                return;
            }

            StartRequested?.Invoke(this, new OperationRequest
            {
                Kind = _whole.Checked ? OperationKind.BackupDevice : OperationKind.BackupPartition,
                Device = device,
                Partition = _whole.Checked ? null : _partitions.SelectedPartition,
                ImagePath = path,
                VerifyAfter = _verify.Checked,
                EjectAfter = _eject.Checked,
                Compression = _fast.Checked ? CompressionChoice.Fastest : CompressionChoice.Optimal,
                AllowIncompleteBackup = device.IsCapacityMisreported && _allowIncomplete.Checked
            });
        }

        private bool ConfirmOverwrite(string path)
        {
            return MessageBox.Show(
                this,
                Strings.Format("Backup_ConfirmOverwrite", Path.GetFileName(path)),
                Strings.Get("App_Name"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        private string DefaultFileName()
        {
            var device = _devices.SelectedDevice;
            var model = device == null || string.IsNullOrEmpty(device.Model) ? Strings.Get("Backup_DefaultName") : device.Model;
            var safe = new string(model.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
            var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var incomplete = device != null && device.IsCapacityMisreported ? "_incomplete" : string.Empty;
            return safe + "_" + date + incomplete + (IsGzip ? ImageFileName.Gzip : ImageFileName.Raw);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateState();
        }
    }
}
