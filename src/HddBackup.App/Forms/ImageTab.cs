using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Devices;
using HddBackup.Core.Imaging;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Work on an image file only (never touches a device): verify it or calculate its checksums,
    /// convert it to the other format, or create a smaller copy.
    /// </summary>
    internal sealed class ImageTab : UserControl, ILocalizable
    {
        private const long GiB = 1024L * 1024 * 1024;
        private const long MiB = 1024L * 1024;

        private readonly PortableSettings _settings;
        private readonly SelectedImage _selected;
        private readonly Label _imageCaption = Ui.Caption();
        private readonly TextBox _path = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Label _imageInfo = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
        private readonly Label _analysisCaption = Ui.GroupCaption();
        private readonly Label _analysis = new Label { AutoSize = true, Margin = new Padding(3, 2, 3, 2), UseMnemonic = false };
        private readonly Label _blockers = new Label { AutoSize = true, ForeColor = Color.DarkRed, Visible = false, UseMnemonic = false };
        private readonly Label _targetCaption = Ui.GroupCaption();
        private readonly RadioButton _same = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _minimum = new RadioButton { AutoSize = true };
        private readonly RadioButton _toDevice = new RadioButton { AutoSize = true };
        private readonly ComboBox _device = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top, Margin = new Padding(20, 0, 3, 3) };
        private readonly RadioButton _toCard = new RadioButton { AutoSize = true };
        private readonly NumericUpDown _cardSize = new NumericUpDown { Minimum = 1, Maximum = 2048 * 1024, Value = 8, Width = 80, Margin = new Padding(20, 0, 3, 3) };
        private readonly ComboBox _cardUnit = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60 };
        private readonly Label _formatCaption = Ui.GroupCaption();
        private readonly ComboBox _format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
        private readonly CheckBox _fast = new CheckBox { AutoSize = true };
        private readonly Label _outputCaption = Ui.Caption();
        private readonly TextBox _output = new TextBox();
        private readonly Button _browseOutput = Ui.Button();
        private readonly Label _reason = new Label { AutoSize = true, ForeColor = Color.DarkRed, Visible = false, Margin = new Padding(3, 2, 3, 2), UseMnemonic = false };
        private readonly Button _create = Ui.Button();
        private readonly Button _verify = Ui.Button();
        private readonly ToolTip _tips = new ToolTip();
        private readonly TableLayoutPanel _root;
        private readonly TableLayoutPanel _left;
        private readonly TableLayoutPanel _right;
        private readonly TableLayoutPanel _bottom;
        private IReadOnlyList<DeviceInfo> _devices = Array.Empty<DeviceInfo>();
        private bool _settingPath;
        private bool _settingOutput;
        private bool _outputEdited;
        private bool _cardSizeTouched;
        private string _shownPath = string.Empty;

        public ImageTab(PortableSettings settings, SelectedImage selected)
        {
            _settings = settings;
            _selected = selected;

            _root = Ui.TwoColumn(out _left, out _right, out _bottom);
            _imageCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(_left, _imageCaption);
            Ui.AddRow(_left, Ui.PathRow(_path, _browse));
            Ui.AddRow(_left, _imageInfo);
            Ui.AddRow(_left, _analysisCaption);
            Ui.AddRow(_left, _analysis);
            Ui.AddRow(_left, _blockers);
            Ui.AddRow(_left, new Panel(), fill: true);
            // Auto-size labels docked to the top are measured too narrow and wrap; size them explicitly.
            _imageInfo.Dock = _analysis.Dock = _blockers.Dock = DockStyle.None;

            _targetCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(_right, _targetCaption);
            Ui.AddRow(_right, _same);
            Ui.AddRow(_right, _minimum);
            Ui.AddRow(_right, _toDevice);
            Ui.AddRow(_right, _device);
            Ui.AddRow(_right, _toCard);
            var card = Ui.Flow();
            card.WrapContents = false;
            card.Controls.AddRange(new Control[] { _cardSize, _cardUnit });
            Ui.AddRow(_right, card);
            Ui.AddRow(_right, _formatCaption);
            Ui.AddRow(_right, _format);
            Ui.AddRow(_right, _fast);
            Ui.AddRow(_right, new Panel(), fill: true);

            Ui.AddRow(_bottom, _outputCaption);
            Ui.AddRow(_bottom, Ui.PathRow(_output, _browseOutput));
            Ui.AddRow(_bottom, _reason);
            _reason.Dock = DockStyle.None;
            var actions = Ui.Flow(rightToLeft: true);
            actions.WrapContents = false;
            actions.Controls.AddRange(new Control[] { _create, _verify });
            Ui.AddRow(_bottom, actions);
            Controls.Add(_root);

            _path.TextChanged += (s, e) =>
            {
                if (!_settingPath)
                {
                    _selected.SetPath(_path.Text);
                }
            };
            _output.TextChanged += (s, e) =>
            {
                if (!_settingOutput)
                {
                    _outputEdited = true;
                }

                UpdateState();
            };
            _selected.Changed += (s, e) => OnImageChanged();
            _browse.Click += (s, e) => BrowseImage();
            _browseOutput.Click += (s, e) => BrowseOutput();
            _same.CheckedChanged += (s, e) => OnTargetChanged();
            _minimum.CheckedChanged += (s, e) => OnTargetChanged();
            _toDevice.CheckedChanged += (s, e) => OnTargetChanged();
            _toCard.CheckedChanged += (s, e) => OnTargetChanged();
            _device.SelectedIndexChanged += (s, e) => OnTargetChanged();
            _cardSize.ValueChanged += (s, e) =>
            {
                _cardSizeTouched = true;
                OnTargetChanged();
            };
            _cardUnit.SelectedIndexChanged += (s, e) => OnTargetChanged();
            _format.SelectedIndexChanged += (s, e) =>
            {
                UpdateFormatTip();
                OnTargetChanged();
            };
            _fast.Checked = settings.FastCompression;
            _fast.CheckedChanged += (s, e) => _settings.FastCompression = _fast.Checked;
            _create.Click += (s, e) => Create();
            _verify.Click += (s, e) =>
            {
                if (_selected.Descriptor != null)
                {
                    StartRequested?.Invoke(this, new OperationRequest
                    {
                        Kind = OperationKind.VerifyImage,
                        ImagePath = _selected.Descriptor.Path,
                        SaveMissingChecksums = true,
                        EjectAfter = false
                    });
                }
            };

            ApplyStrings();
            OnImageChanged();
        }

        public event EventHandler<OperationRequest> StartRequested;

        private bool IsGzip => _format.SelectedIndex == 0;

        /// <summary>Devices whose size can be a shrink target (only their size is read).</summary>
        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            var chosen = _device.SelectedItem as DeviceItem;
            _devices = devices;
            _device.BeginUpdate();
            _device.Items.Clear();
            foreach (var device in devices)
            {
                _device.Items.Add(new DeviceItem(device));
            }

            _device.EndUpdate();
            int index = chosen == null ? -1 : devices.ToList().FindIndex(d => d.DevicePath == chosen.Device.DevicePath);
            if (index >= 0)
            {
                _device.SelectedIndex = index;
            }
            else
            {
                if (_device.Items.Count > 0)
                {
                    _device.SelectedIndex = 0;
                }

                if (_toDevice.Checked)
                {
                    _minimum.Checked = true; // the chosen device is gone
                }
            }

            OnTargetChanged();
        }

        public void ApplyStrings()
        {
            _imageCaption.Text = Strings.Get("Restore_ImageFile");
            _browse.Text = Strings.Get("Common_Browse");
            _analysisCaption.Text = Strings.Get("Image_AnalysisHeading");
            _targetCaption.Text = Strings.Get("Group_NewImageSize");
            _same.Text = Strings.Get("Image_SameSize");
            _minimum.Text = Strings.Get("Shrink_TargetMinimum");
            _toDevice.Text = Strings.Get("Image_TargetDevice");
            _toCard.Text = Strings.Get("Image_TargetCard");
            _formatCaption.Text = Strings.Get("Group_OutputFormat");
            Ui.Embolden(Font, _analysisCaption, _targetCaption, _formatCaption);
            RelabelCombo(_cardUnit, Strings.Get("Unit_GiB"), Strings.Get("Unit_MiB"));
            RelabelCombo(_format, Strings.Get("Format_Gzip"), Strings.Get("Format_Raw"));
            _fast.Text = Strings.Get("Option_FastCompression");
            _tips.SetToolTip(_fast, Strings.Get("Tooltip_FastCompression"));
            _outputCaption.Text = Strings.Get("Image_Output");
            _browseOutput.Text = Strings.Get("Common_Browse");
            _create.Text = Strings.Get("Image_Create");
            UpdateVerifyLabel();
            for (int i = 0; i < _device.Items.Count; i++)
            {
                _device.Items[i] = new DeviceItem(((DeviceItem)_device.Items[i]).Device); // sizes are formatted per language
            }

            UpdateFormatTip();
            UpdateImageTexts();
            UpdateState();
            FitLayout();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            FitLayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int width = _left.ClientSize.Width - _left.Padding.Horizontal - _imageInfo.Margin.Horizontal;
            if (width > 100 && _imageInfo.MaximumSize.Width != width)
            {
                _imageInfo.MaximumSize = _analysis.MaximumSize = _blockers.MaximumSize = _reason.MaximumSize = new Size(width, 0);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateState();
        }

        private static void RelabelCombo(ComboBox combo, string first, string second)
        {
            // Relabel in place: rebuilding the list would fire SelectedIndexChanged.
            if (combo.Items.Count == 2)
            {
                combo.Items[0] = first;
                combo.Items[1] = second;
            }
            else
            {
                combo.Items.Add(first);
                combo.Items.Add(second);
                combo.SelectedIndex = 0;
            }
        }

        private void FitLayout()
        {
            Ui.FitRightColumn(_root, _right);
        }

        private void UpdateFormatTip()
        {
            _tips.SetToolTip(_format, Strings.Get(IsGzip ? "Tooltip_FormatGzip" : "Tooltip_FormatRaw"));
        }

        private void OnImageChanged()
        {
            if (!string.Equals(_path.Text.Trim(), _selected.Path, StringComparison.Ordinal))
            {
                _settingPath = true;
                _path.Text = _selected.Path;
                _settingPath = false;
            }

            if (!string.Equals(_shownPath, _selected.Path, StringComparison.OrdinalIgnoreCase) && _selected.Descriptor != null)
            {
                _shownPath = _selected.Path;
                _outputEdited = false;
                _format.SelectedIndex = _selected.Descriptor.Format == ImageFormat.Gzip ? 1 : 0;
            }

            // A shrink size cannot apply once the analysis says the image cannot be shrunk.
            bool settled = _selected.AnalysisState != AnalysisState.Running;
            if (settled && ShrinkablePlan == null && !_same.Checked)
            {
                _same.Checked = true;
            }

            UpdateVerifyLabel();

            var plan = _selected.AnalysisState == AnalysisState.Done ? _selected.Analysis : null;
            if (plan != null && plan.IsShrinkable && !_cardSizeTouched)
            {
                decimal gib = Math.Ceiling((decimal)plan.MinimumImageBytes / GiB);
                _cardSize.Value = Math.Max(_cardSize.Minimum, Math.Min(_cardSize.Maximum, gib));
                _cardUnit.SelectedIndex = 0;
                _cardSizeTouched = false; // the default follows each new image until the user changes it
            }

            UpdateImageTexts();
            OnTargetChanged();
        }

        private void UpdateImageTexts()
        {
            _imageInfo.Text = ImageInfoText.For(_selected);
            _blockers.Visible = false;
            if (_selected.Descriptor == null)
            {
                _analysis.Text = string.Empty;
            }
            else if (!_selected.IsFullDevice)
            {
                _analysis.Text = Strings.Get("Image_NotFullDevice");
            }
            else if (_selected.AnalysisState == AnalysisState.Running)
            {
                _analysis.Text = Strings.Get("Shrink_Analyzing");
            }
            else if (_selected.AnalysisState == AnalysisState.Failed)
            {
                _analysis.Text = _selected.AnalysisError;
            }
            else if (_selected.AnalysisState == AnalysisState.Done)
            {
                var plan = _selected.Analysis;
                string parts = string.Join(", ", plan.Partitions.Select(p => Strings.Format("Partition_Summary", p.Number, p.FileSystem, Formatters.Bytes(p.Length))));
                bool measured = plan.MinimumImageBytes > 0; // not computed when analysis stopped at a blocker
                _analysis.Text = Strings.Format(
                    "Image_Analysis",
                    parts,
                    measured ? Formatters.Bytes(plan.UsedDataBytes) : "—",
                    measured ? Formatters.Bytes(plan.MinimumImageBytes) : "—");
                if (!plan.IsShrinkable)
                {
                    _blockers.Text = Formatters.Blockers(plan.Blockers);
                    _blockers.Visible = true;
                }
            }

            _analysisCaption.Visible = _selected.Descriptor != null;
        }

        /// <summary>Target size in bytes; null for the smallest possible image.</summary>
        private long? Target
        {
            get
            {
                if (_toDevice.Checked && _device.SelectedItem is DeviceItem item)
                {
                    return item.Device.Size;
                }

                if (_toCard.Checked)
                {
                    return (long)_cardSize.Value * (_cardUnit.SelectedIndex == 1 ? MiB : GiB);
                }

                return null;
            }
        }

        private ShrinkPlan ShrinkablePlan =>
            _selected.AnalysisState == AnalysisState.Done && _selected.Analysis.IsShrinkable ? _selected.Analysis : null;

        private void OnTargetChanged()
        {
            if (!_outputEdited)
            {
                _settingOutput = true;
                if (_selected.Descriptor == null)
                {
                    _output.Text = string.Empty;
                }
                else if (_same.Checked)
                {
                    _output.Text = ImageFileName.WithExtension(_selected.Path, IsGzip);
                }
                else
                {
                    _output.Text = _selected.IsFullDevice ? ShrinkOutputName.Default(_selected.Path, Target, IsGzip) : string.Empty;
                }

                _settingOutput = false;
            }
            else if (_output.Text.Trim().Length > 0)
            {
                // Keep the user's name but follow the chosen format.
                _settingOutput = true;
                _output.Text = ImageFileName.WithExtension(_output.Text.Trim(), IsGzip);
                _settingOutput = false;
            }

            UpdateState();
        }

        private void UpdateState()
        {
            var plan = ShrinkablePlan;
            bool loaded = _selected.Descriptor != null;
            bool shrinkable = plan != null;
            _same.Enabled = _format.Enabled = loaded;
            _fast.Enabled = loaded && IsGzip;
            _minimum.Enabled = _toCard.Enabled = shrinkable;
            _toDevice.Enabled = shrinkable && _device.Items.Count > 0;
            _device.Enabled = _toDevice.Enabled && _toDevice.Checked;
            _cardSize.Enabled = _cardUnit.Enabled = shrinkable && _toCard.Checked;
            _output.Enabled = _browseOutput.Enabled = loaded && (_same.Checked || shrinkable);

            string output = _output.Text.Trim();
            bool samePath = output.Length > 0 && SamePath(output, _selected.Path);
            bool ok;
            string reason = null;
            if (_same.Checked)
            {
                // FR-008: same size and same format would only copy the source.
                bool sameFormat = loaded && IsGzip == (_selected.Descriptor.Format == ImageFormat.Gzip);
                ok = loaded && !sameFormat && !samePath && output.Length > 0;
                if (loaded && (sameFormat || samePath))
                {
                    reason = Strings.Get("Image_SameAsSource");
                }
            }
            else
            {
                bool fits = shrinkable && (!Target.HasValue || plan.Fits(Target.Value));
                ok = fits && !samePath && output.Length > 0;
                if (shrinkable && !fits)
                {
                    reason = Strings.Get("Blocker_NotEnoughSpaceEvenAtMinimum");
                }
            }

            _reason.Text = reason ?? string.Empty;
            _reason.Visible = reason != null;
            _create.Enabled = Enabled && ok;
            _verify.Enabled = Enabled && loaded;
        }

        /// <summary>FR-010: "Calculate checksums" for an image without checksum files, otherwise "Verify image".</summary>
        private void UpdateVerifyLabel()
        {
            var image = _selected.Descriptor;
            _verify.Text = Strings.Get(image != null && !image.HasAnyChecksum ? "Image_CalculateChecksums" : "Image_Verify");
        }

        private static bool SamePath(string a, string b) => FileIdentity.IsSameFile(a, b);

        private void BrowseImage()
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
                    _selected.SetPath(dialog.FileName);
                    _settings.LastImageFolder = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void BrowseOutput()
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = Strings.Get("Dialog_ShrinkFilter"); // 1 = uncompressed, 2 = compressed
                dialog.FilterIndex = IsGzip ? 2 : 1;
                dialog.OverwritePrompt = false; // asked once in Create()
                string current = _output.Text.Trim();
                dialog.FileName = Path.GetFileName(current);
                string folder = current.Length > 0 ? Path.GetDirectoryName(current) : _settings.LastImageFolder;
                if (Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    bool gzip = dialog.FilterIndex == 2;
                    _outputEdited = true;
                    _settingOutput = true;
                    _output.Text = ImageFileName.WithExtension(dialog.FileName, gzip);
                    _settingOutput = false;
                    _format.SelectedIndex = gzip ? 0 : 1;
                    _settings.LastImageFolder = Path.GetDirectoryName(dialog.FileName);
                }
            }
        }

        private void Create()
        {
            string output = _output.Text.Trim();
            if (File.Exists(output) && MessageBox.Show(
                    this,
                    Strings.Format("Backup_ConfirmOverwrite", Path.GetFileName(output)),
                    Strings.Get("App_Name"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }

            var compression = _fast.Checked ? CompressionChoice.Fastest : CompressionChoice.Optimal;
            StartRequested?.Invoke(this, _same.Checked
                ? new OperationRequest
                {
                    Kind = OperationKind.ConvertImage,
                    ImagePath = _selected.Path,
                    OutputPath = output,
                    Compression = compression,
                    EjectAfter = false
                }
                : new OperationRequest
                {
                    Kind = OperationKind.ShrinkImage,
                    ImagePath = _selected.Path,
                    ShrinkOutputPath = output,
                    ShrinkTargetBytes = Target,
                    Compression = compression,
                    EjectAfter = false
                });
        }

        private sealed class DeviceItem
        {
            public DeviceItem(DeviceInfo device)
            {
                Device = device;
            }

            public DeviceInfo Device { get; }

            public override string ToString()
            {
                string name = string.IsNullOrEmpty(Device.Model) ? Device.DevicePath : Device.Model;
                return name + " — " + Formatters.Bytes(Device.Size);
            }
        }
    }
}
