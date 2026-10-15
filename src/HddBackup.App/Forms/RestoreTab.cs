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

namespace HddBackup.App.Forms
{
    /// <summary>Writes an image to a device (or partition) and verifies images.</summary>
    internal sealed class RestoreTab : UserControl, ILocalizable
    {
        private readonly PortableSettings _settings;
        private readonly Label _imageCaption = Ui.Caption();
        private readonly TextBox _path = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Label _imageInfo = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        private readonly Label _deviceCaption = Ui.Caption();
        private readonly DeviceListView _devices = new DeviceListView();
        private readonly Label _noDevices = new Label { AutoSize = true, ForeColor = System.Drawing.SystemColors.GrayText, Visible = false };
        private readonly Label _unsuitable = new Label { AutoSize = true, ForeColor = System.Drawing.Color.DarkRed, Visible = false, MaximumSize = new System.Drawing.Size(760, 0) };
        private readonly Label _partitionCaption = Ui.Caption();
        private readonly PartitionGrid _partitions = new PartitionGrid();
        private readonly CheckBox _verify = new CheckBox { AutoSize = true };
        private readonly CheckBox _eject = new CheckBox { AutoSize = true };
        private readonly Button _write = Ui.Button();
        private readonly Button _verifyImage = Ui.Button();
        private readonly Button _shrink = Ui.Button();
        private ImageDescriptor _image;
        private string _imageError;

        public RestoreTab(PortableSettings settings)
        {
            _settings = settings;
            var root = Ui.Column();
            Ui.AddRow(root, _imageCaption);
            Ui.AddRow(root, Ui.PathRow(_path, _browse));
            Ui.AddRow(root, _imageInfo);
            Ui.AddRow(root, _deviceCaption);
            Ui.AddRow(root, _devices, fill: true);
            Ui.AddRow(root, _noDevices);
            Ui.AddRow(root, _unsuitable);
            Ui.AddRow(root, _partitionCaption);
            _partitions.Dock = DockStyle.Top;
            Ui.AddRow(root, _partitions);

            var options = Ui.Flow();
            options.Controls.AddRange(new Control[] { _verify, _eject });
            Ui.AddRow(root, options);

            var actions = Ui.Flow(rightToLeft: true);
            actions.Controls.AddRange(new Control[] { _write, _verifyImage, _shrink });
            Ui.AddRow(root, actions);
            Controls.Add(root);

            _verify.Checked = settings.VerifyAfterRestore;
            _eject.Checked = settings.EjectAfter;

            _path.TextChanged += (s, e) => LoadImage();
            _browse.Click += (s, e) => Browse();
            _devices.SelectedIndexChanged += (s, e) => OnDeviceChanged();
            _partitions.SelectionChanged += (s, e) => UpdateState();
            _verify.CheckedChanged += (s, e) => _settings.VerifyAfterRestore = _verify.Checked;
            _eject.CheckedChanged += (s, e) => _settings.EjectAfter = _eject.Checked;
            _write.Click += (s, e) => StartWrite();
            _verifyImage.Click += (s, e) => StartVerify();
            _shrink.Click += (s, e) =>
            {
                if (_image != null)
                {
                    ShrinkRequested?.Invoke(this, _image.Path);
                }
            };

            ApplyStrings();
            UpdateState();
        }

        public event EventHandler<OperationRequest> StartRequested;

        /// <summary>The user wants to create a smaller copy of the selected image.</summary>
        public event EventHandler<string> ShrinkRequested;

        /// <summary>Path of the loaded full-device image, or null.</summary>
        public string FullImagePath => _image != null && _image.Kind == ImageKind.FullDevice ? _image.Path : null;

        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            // The disk the program runs from is never a restore target.
            var targets = devices.Where(d => !d.HostsExecutable).ToList();
            _devices.SetDevices(targets);
            _noDevices.Visible = targets.Count == 0;
            OnDeviceChanged();
        }

        /// <summary>Re-reads the image sidecars (e.g. after checksum files were saved).</summary>
        public void ReloadImage() => LoadImage();

        public void ApplyStrings()
        {
            _imageCaption.Text = Strings.Get("Restore_ImageFile");
            _browse.Text = Strings.Get("Common_Browse");
            _deviceCaption.Text = Strings.Get("Restore_TargetDevice");
            _noDevices.Text = Strings.Get("Devices_None");
            _unsuitable.Text = Strings.Get("Device_Unsuitable");
            _partitionCaption.Text = Strings.Get("Restore_TargetPartition");
            _verify.Text = Strings.Get("Option_VerifyAfterRestore");
            _eject.Text = Strings.Get("Option_Eject");
            _write.Text = Strings.Get("Restore_Write");
            _verifyImage.Text = Strings.Get("Restore_VerifyImage");
            _shrink.Text = Strings.Get("Restore_Shrink");
            _devices.ApplyStrings();
            _partitions.ApplyStrings();
            UpdateImageInfo();
        }

        private bool IsPartitionImage => _image != null && _image.Kind == ImageKind.Partition;

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

        private void LoadImage()
        {
            _image = null;
            _imageError = null;
            var path = _path.Text.Trim();
            if (path.Length > 0)
            {
                try
                {
                    _image = ImageDescriptor.Load(path);
                }
                catch (ImagingException ex)
                {
                    _imageError = Formatters.Error(ex.Code);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    _imageError = ex.Message;
                }
            }

            UpdateImageInfo();
            UpdateState();
        }

        private void UpdateImageInfo()
        {
            if (_imageError != null)
            {
                _imageInfo.Text = Strings.Format("Restore_ImageError", _imageError);
                return;
            }

            if (_image == null)
            {
                _imageInfo.Text = Strings.Get("Restore_NoImage");
                return;
            }

            string format = Strings.Get(_image.Format == ImageFormat.Gzip ? "ImageFormat_Gzip" : "ImageFormat_Raw");
            string size = _image.DataSize.HasValue ? Formatters.Bytes(_image.DataSize.Value) : Strings.Get("Restore_SizeUnknown");
            string kind = _image.Kind == ImageKind.Partition
                ? Strings.Format("ImageKind_Partition", _image.PartitionMetadata.PartitionNumber, _image.PartitionMetadata.FileSystem)
                : Strings.Get("ImageKind_Full");

            var sidecars = new List<string>();
            if (_image.FileChecksum != null)
            {
                sidecars.Add(Path.GetFileName(_image.FileChecksum.Path));
            }

            if (_image.ContentChecksum != null && !ReferenceEquals(_image.ContentChecksum, _image.FileChecksum))
            {
                sidecars.Add(Path.GetFileName(_image.ContentChecksum.Path));
            }

            string checksums = sidecars.Count > 0
                ? Strings.Format("Restore_ChecksumsFound", string.Join(", ", sidecars))
                : Strings.Get("Restore_ChecksumsNone");
            _imageInfo.Text = Strings.Format("Restore_ImageInfo", format, size, kind) + Environment.NewLine + checksums;
        }

        private void OnDeviceChanged()
        {
            _partitions.SetTable(_devices.SelectedDevice?.PartitionTable);
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
            _write.Enabled = Enabled && _image != null && targetOk;
            _verifyImage.Enabled = Enabled && _image != null;
            _shrink.Enabled = Enabled && _image != null && _image.Kind == ImageKind.FullDevice;
        }

        private void StartWrite()
        {
            if (_image == null || _devices.SelectedDevice == null)
            {
                return;
            }

            StartRequested?.Invoke(this, new OperationRequest
            {
                Kind = IsPartitionImage ? OperationKind.RestorePartition : OperationKind.RestoreDevice,
                Device = _devices.SelectedDevice,
                Partition = IsPartitionImage ? _partitions.SelectedPartition : null,
                ImagePath = _image.Path,
                VerifyAfter = _verify.Checked,
                EjectAfter = _eject.Checked
            });
        }

        private void StartVerify()
        {
            if (_image == null)
            {
                return;
            }

            StartRequested?.Invoke(this, new OperationRequest { Kind = OperationKind.VerifyImage, ImagePath = _image.Path, EjectAfter = false });
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateState();
        }
    }
}
