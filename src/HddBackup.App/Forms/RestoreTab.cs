using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>Writes an image to a device (or partition) and verifies images.</summary>
    internal sealed class RestoreTab : UserControl, ILocalizable
    {
        private readonly PortableSettings _settings;
        private readonly SelectedImage _selected;
        private readonly Label _imageCaption = Ui.Caption();
        private readonly TextBox _path = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Label _imageInfo = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        private readonly Label _sizeWarning = new Label { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed, Visible = false, Margin = new Padding(3, 2, 3, 2), UseMnemonic = false };
        private readonly Label _deviceCaption = Ui.Caption();
        private readonly DeviceListView _devices = new DeviceListView();
        private readonly Label _noDevices = new Label { AutoSize = true, ForeColor = System.Drawing.SystemColors.GrayText, Visible = false };
        private readonly Label _unsuitable = new Label { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed, Visible = false };
        private readonly Label _partitionCaption = Ui.Caption();
        private readonly PartitionGrid _partitions = new PartitionGrid();
        private readonly Label _afterCaption = Ui.GroupCaption();
        private readonly CheckBox _verify = new CheckBox { AutoSize = true };
        private readonly CheckBox _eject = new CheckBox { AutoSize = true };
        private readonly Button _write = Ui.Button();
        private readonly TableLayoutPanel _root;
        private readonly TableLayoutPanel _right;
        private readonly TableLayoutPanel _bottom;
        private bool _settingPath;

        public RestoreTab(PortableSettings settings, SelectedImage selected)
        {
            _settings = settings;
            _selected = selected;
            _root = Ui.TwoColumn(out var left, out _right, out var bottom);
            _deviceCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(left, _deviceCaption);
            Ui.AddRow(left, _devices, fill: true);
            Ui.AddRow(left, _noDevices);
            Ui.AddRow(left, _unsuitable);
            Ui.AddRow(left, _partitionCaption);
            _partitions.Dock = DockStyle.Top;
            Ui.AddRow(left, _partitions);

            _afterCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(_right, _afterCaption);
            Ui.AddRow(_right, _verify);
            Ui.AddRow(_right, _eject);
            Ui.AddRow(_right, new Panel(), fill: true);

            Ui.AddRow(bottom, _imageCaption);
            Ui.AddRow(bottom, Ui.PathRow(_path, _browse));
            Ui.AddRow(bottom, _imageInfo);
            Ui.AddRow(bottom, _sizeWarning);
            // Docked auto-size labels are measured too narrow and wrap; their width is set in OnLayout.
            _imageInfo.Dock = _sizeWarning.Dock = DockStyle.None;
            _bottom = bottom;
            var actions = Ui.Flow(rightToLeft: true);
            actions.WrapContents = false; // measured at a narrow width it would wrap and reserve a second row
            actions.Controls.Add(_write);
            Ui.AddRow(bottom, actions);
            Controls.Add(_root);

            _verify.Checked = settings.VerifyAfterRestore;
            _eject.Checked = settings.EjectAfter;

            _path.TextChanged += (s, e) =>
            {
                if (!_settingPath)
                {
                    _selected.SetPath(_path.Text);
                }
            };
            _selected.Changed += (s, e) => OnImageChanged();
            _browse.Click += (s, e) => Browse();
            _devices.SelectedIndexChanged += (s, e) => OnDeviceChanged();
            _partitions.SelectionChanged += (s, e) => UpdateState();
            _verify.CheckedChanged += (s, e) => _settings.VerifyAfterRestore = _verify.Checked;
            _eject.CheckedChanged += (s, e) => _settings.EjectAfter = _eject.Checked;
            _write.Click += (s, e) => StartWrite();

            ApplyStrings();
            UpdateState();
        }

        public event EventHandler<OperationRequest> StartRequested;

        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            // The disk the program runs from is never a restore target.
            var targets = devices.Where(d => !d.HostsExecutable).ToList();
            _devices.SetDevices(targets);
            _noDevices.Visible = targets.Count == 0;
            OnDeviceChanged();
        }


        public void ApplyStrings()
        {
            _imageCaption.Text = Strings.Get("Restore_ImageFile");
            _browse.Text = Strings.Get("Common_Browse");
            _deviceCaption.Text = Strings.Get("Restore_TargetDevice");
            _noDevices.Text = Strings.Get("Devices_None");
            _unsuitable.Text = Strings.Get("Device_Unsuitable");
            _partitionCaption.Text = Strings.Get("Restore_TargetPartition");
            _afterCaption.Text = Strings.Get("Group_AfterWriting");
            Ui.Embolden(Font, _afterCaption);
            _verify.Text = Strings.Get("Option_VerifyAfterRestore");
            _eject.Text = Strings.Get("Option_Eject");
            _write.Text = Strings.Get("Restore_Write");
            _devices.ApplyStrings();
            _partitions.ApplyStrings();
            UpdateImageInfo();
            FitLayout();
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

            int infoWidth = _bottom.ClientSize.Width - _bottom.Padding.Horizontal - _imageInfo.Margin.Horizontal;
            if (infoWidth > 100 && _imageInfo.MaximumSize.Width != infoWidth)
            {
                _imageInfo.MaximumSize = _sizeWarning.MaximumSize = new System.Drawing.Size(infoWidth, 0);
            }
        }

        private ImageDescriptor Image => _selected.Descriptor;

        private bool IsPartitionImage => Image != null && Image.Kind == ImageKind.Partition;

        private void Browse()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Filter = Strings.Get("Dialog_ImageFilter_Open");
                dialog.CheckFileExists = true;
                if (Directory.Exists(_settings.LastImageFolder))
                {
                    dialog.InitialDirectory = _settings.LastImageFolder;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _path.Text = dialog.FileName;
                    _settings.LastImageFolder = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void OnImageChanged()
        {
            if (!string.Equals(_path.Text.Trim(), _selected.Path, StringComparison.Ordinal))
            {
                _settingPath = true;
                _path.Text = _selected.Path;
                _settingPath = false;
            }

            UpdateImageInfo();
            UpdateState();
        }

        private void UpdateImageInfo()
        {
            _imageInfo.Text = ImageInfoText.For(_selected);
            UpdateSizeWarning();
        }

        /// <summary>FR-018: warn as soon as the selected image does not fit the selected device.</summary>
        private void UpdateSizeWarning()
        {
            var device = _devices.SelectedDevice;
            if (device == null || !_selected.IsFullDevice)
            {
                _sizeWarning.Visible = false;
                return;
            }

            var result = ImageFit.Evaluate(
                Image.Format == ImageFormat.Raw,
                Image.FileSize,
                _selected.RequiredDeviceBytes,
                _selected.AnalysisState == AnalysisState.Done ? _selected.Analysis : null,
                _selected.AnalysisState == AnalysisState.Failed,
                device.Size);
            switch (result)
            {
                case ImageFitResult.Pending:
                    _sizeWarning.Text = Strings.Get("Restore_TooLarge");
                    break;
                case ImageFitResult.ShrinkToFit:
                    _sizeWarning.Text = Strings.Get(ImageFit.RequiresRepair(_selected.Analysis) ? "Restore_TooLargeRepair" : "Restore_TooLargeShrink");
                    break;
                case ImageFitResult.CannotFit:
                    var plan = _selected.Analysis;
                    string reasons = plan == null
                        ? _selected.AnalysisError
                        : plan.CanShrink(true) ? Strings.Get("Blocker_NotEnoughSpaceEvenAtMinimum") : Formatters.Blockers(plan.Blockers);
                    _sizeWarning.Text = Strings.Format("Restore_TooLargeCannot", reasons);
                    break;
            }

            _sizeWarning.Visible = result != ImageFitResult.Fits;
        }

        private void OnDeviceChanged()
        {
            _partitions.SetTable(_devices.SelectedDevice?.PartitionTable);
            UpdateSizeWarning();
            UpdateState();
        }

        private void UpdateState()
        {
            _partitionCaption.Visible = IsPartitionImage;
            _partitions.Visible = IsPartitionImage;
            bool unsuitable = _devices.SelectedDevice?.IsCapacityMisreported == true;
            _unsuitable.Visible = unsuitable;
            bool targetOk = _devices.SelectedDevice != null
                && !unsuitable
                && (!IsPartitionImage || _partitions.SelectedPartition != null);
            _write.Enabled = Enabled && Image != null && targetOk;
        }

        private void StartWrite()
        {
            if (Image == null || _devices.SelectedDevice == null)
            {
                return;
            }

            StartRequested?.Invoke(this, new OperationRequest
            {
                Kind = IsPartitionImage ? OperationKind.RestorePartition : OperationKind.RestoreDevice,
                Device = _devices.SelectedDevice,
                Partition = IsPartitionImage ? _partitions.SelectedPartition : null,
                ImagePath = Image.Path,
                VerifyAfter = _verify.Checked,
                EjectAfter = _eject.Checked
            });
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateState();
        }
    }
}
