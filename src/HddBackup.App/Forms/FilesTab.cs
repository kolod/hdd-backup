using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Settings;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Browse the ext4 file systems of an image (and, later, a device) read-only: partition
    /// choice, a lazily expanded tree, a viewer for the selected file and extraction.
    /// </summary>
    internal sealed partial class FilesTab : UserControl, ILocalizable
    {
        private static readonly object LoadingTag = new object();

        private readonly PortableSettings _settings;
        private readonly SelectedImage _selected;
        private readonly IDeviceAccess _access;
        private readonly Button _extract = Ui.Button();
        private readonly Button _tools = Ui.Button();
        private readonly ContextMenuStrip _toolsMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _menuSsh = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _menuStartup = new ToolStripMenuItem();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _menuExtract = new ToolStripMenuItem();
        private readonly Label _sourceCaption = Ui.Caption();
        private readonly RadioButton _modeImage = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _modeDevice = new RadioButton { AutoSize = true };
        private readonly ComboBox _device = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Enabled = false };
        private readonly TextBox _path = new TextBox();
        private readonly Button _browse = Ui.Button();
        private readonly Label _partitionCaption = Ui.Caption();
        private readonly ComboBox _partition = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
        private readonly Label _state = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 2), UseMnemonic = false };
        private readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill };
        private readonly TreeView _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
        private readonly Panel _rightPanel = new Panel { Dock = DockStyle.Fill };
        private readonly Label _headerPath = new Label { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Dock = DockStyle.Top };
        private readonly Label _headerDetail = new Label { AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Dock = DockStyle.Top };
        private readonly TextBox _viewer = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            MaxLength = int.MaxValue,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window
        };
        private readonly RadioButton _viewAuto = new RadioButton { AutoSize = true, Checked = true };
        private readonly RadioButton _viewText = new RadioButton { AutoSize = true };
        private readonly RadioButton _viewHex = new RadioButton { AutoSize = true };
        private readonly Label _viewNote = new Label { AutoSize = true, UseMnemonic = false, Margin = new Padding(12, 6, 3, 3) };
        private readonly Label _status = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 2), UseMnemonic = false };
        private readonly ToolTip _tips = new ToolTip();
        private readonly TableLayoutPanel _root = Ui.Column();

        private BrowseSource _source;
        private Ext4FileSystemView _view;
        private string _openedPath;
        private DeviceInfo _openedDevice;
        private bool _populatingDevices;
        private int _generation;
        private bool _dirty;
        private bool _settingPath;
        private bool _splitterPlaced;
        private bool _planning;
        private bool _toolRunning;
        private CancellationTokenSource _toolCts;
        private FsEntry _shownEntry;
        private FilePreview _shownPreview;
        private string _viewerError;
        private CancellationTokenSource _previewCts;
        private Font _fontBase;
        private Font _boldFont;
        private Font _monoFont;
        private Func<string> _stateText = () => string.Empty;
        private Func<string> _statusText = () => string.Empty;
        private Color _stateColor = SystemColors.ControlText;
        private Color _statusColor = SystemColors.ControlText;

        public FilesTab(PortableSettings settings, SelectedImage selected, IDeviceAccess access)
        {
            _settings = settings;
            _selected = selected;
            _access = access;

            _sourceCaption.Margin = new Padding(3, 0, 3, 2);
            Ui.AddRow(_root, _sourceCaption);
            var sourceRows = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            sourceRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            sourceRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sourceRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _modeImage.Margin = _modeDevice.Margin = new Padding(3, 6, 12, 3);
            _path.Dock = DockStyle.Fill;
            _path.Margin = new Padding(3, 5, 3, 3);
            _device.Dock = DockStyle.Fill;
            _device.Margin = new Padding(3, 3, 3, 3);
            sourceRows.Controls.Add(_modeImage, 0, 0);
            sourceRows.Controls.Add(_path, 1, 0);
            sourceRows.Controls.Add(_browse, 2, 0);
            sourceRows.Controls.Add(_modeDevice, 0, 1);
            sourceRows.Controls.Add(_device, 1, 1);
            sourceRows.SetColumnSpan(_device, 2);
            Ui.AddRow(_root, sourceRows);
            var partitionRow = Ui.Flow();
            partitionRow.WrapContents = false;
            _partitionCaption.Margin = new Padding(3, 8, 8, 2);
            partitionRow.Controls.AddRange(new Control[] { _partitionCaption, _partition });
            Ui.AddRow(_root, partitionRow);
            Ui.AddRow(_root, _state);
            _state.Dock = DockStyle.None;
            _split.Panel1.Controls.Add(_tree);
            _split.Panel2.Controls.Add(_rightPanel);
            Ui.AddRow(_root, _split, fill: true);
            var bottom = new TableLayoutPanel { ColumnCount = 6, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int c = 0; c < 5; c++)
            {
                bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            }

            bottom.Controls.Add(_status, 0, 0);
            bottom.Controls.Add(_discard, 1, 0);
            bottom.Controls.Add(_save, 2, 0);
            bottom.Controls.Add(_edit, 3, 0);
            bottom.Controls.Add(_tools, 4, 0);
            bottom.Controls.Add(_extract, 5, 0);
            Ui.AddRow(_root, bottom);
            _status.Dock = DockStyle.None;
            _menu.Items.Add(_menuExtract);
            _tree.ContextMenuStrip = _menu;
            Controls.Add(_root);

            _tree.ImageList = FilesTreeIcons.Create(DeviceDpi / 96f);
            // The width is not known yet; the splitter is placed when the control first gets its size.
            _split.SizeChanged += (s, e) => PlaceSplitter();

            var viewerRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8, 0, 0, 0) };
            viewerRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            viewerRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewerRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            viewerRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            viewerRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var modes = Ui.Flow();
            modes.WrapContents = false;
            modes.Controls.AddRange(new Control[] { _viewAuto, _viewText, _viewHex, _viewNote, _apply });
            viewerRows.Controls.Add(_headerPath, 0, 0);
            viewerRows.Controls.Add(_headerDetail, 0, 1);
            viewerRows.Controls.Add(_viewer, 0, 2);
            viewerRows.Controls.Add(modes, 0, 3);
            _rightPanel.Controls.Add(viewerRows);
            foreach (var radio in new[] { _viewAuto, _viewText, _viewHex })
            {
                radio.CheckedChanged += (s, e) =>
                {
                    if (((RadioButton)s).Checked)
                    {
                        ShowEntry(_tree.SelectedNode);
                    }
                };
            }

            _path.TextChanged += (s, e) =>
            {
                if (!_settingPath)
                {
                    _selected.SetPath(_path.Text);
                }
            };
            _selected.Changed += (s, e) => OnSelectedChanged();
            _modeImage.CheckedChanged += (s, e) => OnModeChanged();
            _device.SelectedIndexChanged += (s, e) =>
            {
                if (!_populatingDevices && DeviceMode)
                {
                    OpenSelectedDevice();
                }
            };
            _browse.Click += (s, e) => BrowseImage();
            _partition.SelectedIndexChanged += (s, e) => OpenSelectedPartition();
            _tree.BeforeExpand += OnBeforeExpand;
            _tree.AfterSelect += (s, e) =>
            {
                UpdateExtractEnabled();
                ShowEntry(e.Node);
            };
            _tree.NodeMouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    _tree.SelectedNode = e.Node; // the context menu acts on the node under the pointer
                }
            };
            _extract.Click += (s, e) => ExtractSelected();
            _tools.Click += (s, e) => OnToolsClick();
            _toolsMenu.Items.Add(_menuSsh);
            _toolsMenu.Items.Add(_menuStartup);
            _menuStartup.Click += (s, e) => RunTool(
                (reader, source) => StartupInventory.Run(reader, source, CancellationToken.None),
                (result, source) =>
                {
                    ShowTool(new StartupTasksWindow(result, _settings));
                });
            _menuSsh.Click += (s, e) => RunTool(
                (reader, source) => SshCheck.Run(reader, source, CancellationToken.None),
                (report, source) =>
                {
                    ShowTool(new SshWizardDialog(report, _settings));
                });
            _menuExtract.Click += (s, e) => ExtractSelected();
            SizeChanged += (s, e) => FitLabels();

            InitEdit();
            ApplyStrings();
            OnSelectedChanged();
        }

        /// <summary>Raised to run an extraction through the shared operation panel.</summary>
        public event EventHandler<OperationRequest> StartRequested;

        public void ApplyStrings()
        {
            _sourceCaption.Text = Strings.Get("Files_Source") + ":";
            _modeImage.Text = Strings.Get("Files_SourceImage");
            _modeDevice.Text = Strings.Get("Files_SourceDevice");
            _browse.Text = Strings.Get("Common_Browse");
            _partitionCaption.Text = Strings.Get("Files_Partition") + ":";
            _tips.SetToolTip(_state, Strings.Get("Tooltip_Files_State"));
            _extract.Text = Strings.Get("Files_Extract");
            _menuExtract.Text = Strings.Get("Files_Extract");
            _tips.SetToolTip(_extract, Strings.Get("Tooltip_Files_Extract"));
            RenderToolsButton();
            ApplyEditStrings();
            _menuSsh.Text = Strings.Get("Tools_Ssh");
            _menuStartup.Text = Strings.Get("Tools_Startup");
            _viewAuto.Text = Strings.Get("Files_ViewAuto");
            _viewText.Text = Strings.Get("Files_ViewText");
            _viewHex.Text = Strings.Get("Files_ViewHex");
            _tips.SetToolTip(_viewHex, Strings.Get("Tooltip_Files_ViewHex"));
            RenderViewer();
            RenderState();
            RenderStatus();
            foreach (PartitionItem item in _partition.Items)
            {
                item.Refresh();
            }

            _partition.Refresh();
            FitLabels();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && _dirty)
            {
                _dirty = false;
                OpenSource();
            }
        }

        private bool DeviceMode => _modeDevice.Checked;

        /// <summary>Opens the chosen image or device now, or when the tab is first shown.</summary>
        private void OpenSource()
        {
            if (DeviceMode)
            {
                OpenSelectedDevice();
            }
            else
            {
                OpenSelectedImage();
            }
        }

        private void OnModeChanged()
        {
            _path.Enabled = _browse.Enabled = !DeviceMode;
            _device.Enabled = DeviceMode;
            _openedPath = null;
            _openedDevice = null;
            CloseSource();
            _generation++;
            SetState(() => string.Empty, SystemColors.ControlText);
            SetStatus(() => string.Empty, false);
            if (!Visible)
            {
                _dirty = true;
                return;
            }

            OpenSource();
        }

        /// <summary>The removable devices (same list as the other tabs); a device that vanished closes the file system.</summary>
        public void SetDevices(IReadOnlyList<DeviceInfo> devices)
        {
            var chosen = (_device.SelectedItem as DeviceItem)?.Device;
            _populatingDevices = true;
            try
            {
                _device.BeginUpdate();
                _device.Items.Clear();
                foreach (var device in devices)
                {
                    _device.Items.Add(new DeviceItem(device));
                }

                _device.EndUpdate();
                int index = chosen == null ? -1 : devices.ToList().FindIndex(d => d.DevicePath == chosen.DevicePath);
                _device.SelectedIndex = index >= 0 ? index : (_device.Items.Count > 0 ? 0 : -1);
            }
            finally
            {
                _populatingDevices = false;
            }

            if (!DeviceMode)
            {
                return;
            }

            var opened = _openedDevice;
            if (opened != null && !devices.Any(d => d.DevicePath == opened.DevicePath))
            {
                // The device was pulled out: nothing it showed can be trusted any more.
                _generation++;
                CloseSource();
                _openedDevice = null;
                SetState(() => string.Empty, SystemColors.ControlText);
                SetStatus(() => Strings.Format("Files_ReadError", Formatters.Error(ErrorCode.DeviceRemoved)), true);
            }

            var selected = (_device.SelectedItem as DeviceItem)?.Device;
            if (selected != null && _source == null && _openedDevice == null && Visible)
            {
                OpenSelectedDevice();
            }
            else if (selected == null && _openedDevice == null && _source == null)
            {
                SetState(() => Strings.Get("Devices_None"), SystemColors.ControlText);
            }
        }

        private async void OpenSelectedDevice()
        {
            var item = _device.SelectedItem as DeviceItem;
            CloseSource();
            int generation = ++_generation;
            _openedDevice = item?.Device;
            if (item == null)
            {
                SetState(() => Strings.Get("Devices_None"), SystemColors.ControlText);
                SetStatus(() => string.Empty, false);
                return;
            }

            SetState(() => string.Empty, SystemColors.ControlText);
            SetStatus(() => Strings.Get("Files_Opening"), false);
            var device = item.Device;
            BrowseSource source;
            try
            {
                source = await Task.Run(() => BrowseSource.OpenDevice(_access, device));
            }
            catch (Exception ex) when (ex is ImagingException || ex is IOException || ex is UnauthorizedAccessException)
            {
                if (generation == _generation && !IsDisposed)
                {
                    _openedDevice = null;
                    ShowReadError(ex);
                }

                return;
            }

            if (generation != _generation || IsDisposed)
            {
                source.Dispose();
                return;
            }

            AttachSource(source);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _generation++;
                CloseSource();
                _previewCts?.Dispose();
                _boldFont?.Dispose();
                _monoFont?.Dispose();
                _tips.Dispose();
                _tree.ImageList?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnSelectedChanged()
        {
            if (!string.Equals(_path.Text, _selected.Path, StringComparison.Ordinal))
            {
                _settingPath = true;
                _path.Text = _selected.Path;
                _settingPath = false;
            }

            if (DeviceMode || string.Equals(_selected.Path, _openedPath, StringComparison.OrdinalIgnoreCase))
            {
                return; // another tab changed the shared image; it is used when the image source is chosen
            }

            if (!Visible)
            {
                _dirty = true;
                return;
            }

            OpenSelectedImage();
        }

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

        private async void OpenSelectedImage()
        {
            string path = _selected.Path;
            _openedPath = path;
            CloseSource();
            int generation = ++_generation;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                SetState(() => string.Empty, SystemColors.ControlText);
                SetStatus(() => string.Empty, false);
                return;
            }

            SetStatus(() => Strings.Get("Files_Opening"), false);
            BrowseSource source;
            try
            {
                source = await Task.Run(() => BrowseSource.OpenImage(path));
            }
            catch (ImagingException ex)
            {
                if (generation == _generation && !IsDisposed)
                {
                    ShowReadError(ex);
                }

                return;
            }

            if (generation != _generation || IsDisposed)
            {
                source.Dispose();
                return;
            }

            AttachSource(source);
        }

        private void AttachSource(BrowseSource source)
        {
            _source = source;
            SetStatus(() => string.Empty, false);
            if (source.Problem == BrowseProblem.NeedsConversion)
            {
                SetState(() => Strings.Get("Files_NeedsConversion"), Color.DarkRed);
                return;
            }

            var ext4 = source.Partitions.Where(p => p.FileSystem == "ext4").ToList();
            if (ext4.Count == 0)
            {
                string detail = source.ProblemDetail.Length == 0 ? "–" : source.ProblemDetail;
                SetState(() => Strings.Format("Files_NoExt4", detail), Color.DarkRed);
                return;
            }

            _partition.BeginUpdate();
            _partition.Items.Clear();
            foreach (var p in ext4)
            {
                _partition.Items.Add(new PartitionItem(p));
            }

            _partition.EndUpdate();
            int first = ext4.FindIndex(p => p.IsBrowsable);
            _partition.SelectedIndex = first >= 0 ? first : 0; // raises SelectedIndexChanged
        }

        private async void OpenSelectedPartition()
        {
            var item = _partition.SelectedItem as PartitionItem;
            if (item == null || _source == null)
            {
                return;
            }

            CloseView();
            int generation = ++_generation;
            var partition = item.Partition;
            if (!partition.IsBrowsable)
            {
                SetState(() => Strings.Format("Files_Unsupported", partition.Unsupported), Color.DarkRed);
                SetStatus(() => string.Empty, false);
                return;
            }

            SetStatus(() => Strings.Get("Files_Opening"), false);
            var source = _source;
            Ext4FileSystemView view;
            try
            {
                view = await Task.Run(() => source.OpenFileSystem(partition));
            }
            catch (Exception ex) when (ex is ImagingException || ex is ObjectDisposedException || ex is IOException)
            {
                if (generation == _generation && !IsDisposed)
                {
                    ShowReadError(ex);
                }

                return;
            }

            if (generation != _generation || IsDisposed)
            {
                view.Dispose();
                return;
            }

            _view = view;
            ShowPartitionState(partition);
            RenderCounts();
            var root = CreateNode(view.Root);
            root.Text = "/";
            _tree.Nodes.Add(root);
            _tree.SelectedNode = root;
            root.Expand();
            RestoreEditIfWaiting();
        }

        private static Font MonospaceFont(Font like)
        {
            bool consolas = System.Drawing.FontFamily.Families.Any(f => f.Name == "Consolas");
            return consolas ? new Font("Consolas", like.Size) : new Font(System.Drawing.FontFamily.GenericMonospace, like.Size);
        }

        private PreviewMode SelectedMode()
        {
            return _viewHex.Checked ? PreviewMode.Hex : _viewText.Checked ? PreviewMode.Text : PreviewMode.Auto;
        }

        /// <summary>
        /// Describes the selected entry and renders its content on a worker thread; a newer
        /// selection cancels the previous read, so only the latest one is shown.
        /// </summary>
        private async void ShowEntry(TreeNode node)
        {
            _previewCts?.Cancel();
            var cts = new CancellationTokenSource();
            _previewCts = cts;
            var entry = node?.Tag as FsEntry;
            var view = Tree;
            _viewerError = null;
            if (entry == null || view == null)
            {
                _shownEntry = null;
                _shownPreview = null;
                RenderViewer();
                return;
            }

            _shownEntry = entry;
            _shownPreview = null;
            RenderViewer();
            var mode = SelectedMode();
            FilePreview preview = null;
            try
            {
                await Task.Run(
                    () =>
                    {
                        view.Describe(entry);
                        preview = FilePreview.Create(view, entry, mode, cts.Token);
                    },
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is ImagingException || ex is ObjectDisposedException || ex is IOException)
            {
                if (cts.IsCancellationRequested || IsDisposed || !ReferenceEquals(view, Tree))
                {
                    return;
                }

                _viewerError = Strings.Format("Files_ReadError", ex is ImagingException ie ? ie.Detail : ex.Message);
                RenderViewer();
                return;
            }

            if (cts.IsCancellationRequested || IsDisposed || !ReferenceEquals(view, Tree))
            {
                return;
            }

            _shownPreview = preview;
            if (entry.Unreadable != null && node.TreeView != null)
            {
                node.ImageKey = FilesTreeIcons.Unreadable;
                node.SelectedImageKey = FilesTreeIcons.Unreadable;
                node.ForeColor = Color.DarkRed;
            }

            UpdateExtractEnabled();
            RenderViewer();
        }

        /// <summary>Renders the header, the content and the note; called again when the language changes.</summary>
        private void RenderViewer()
        {
            var entry = _shownEntry;
            _viewer.ReadOnly = true;
            if (_fontBase != Font)
            {
                // Fonts are GDI objects: replaced only when the form font changes (language switch).
                _boldFont?.Dispose();
                _monoFont?.Dispose();
                _boldFont = new Font(Font, FontStyle.Bold);
                _monoFont = MonospaceFont(Font);
                _fontBase = Font;
                _headerPath.Font = _boldFont;
                _viewer.Font = _monoFont;
            }

            _headerPath.Height = _headerPath.Font.Height + 6;
            _headerDetail.Height = Font.Height + 6;
            _viewNote.Text = string.Empty;
            if (entry == null)
            {
                _headerPath.Text = string.Empty;
                _headerDetail.Text = string.Empty;
                _viewer.Text = string.Empty;
                return;
            }

            _headerPath.Text = entry.Path;
            _headerDetail.Text = HeaderDetail(entry);
            if (_viewerError != null)
            {
                _viewer.Text = _viewerError;
                return;
            }

            if (entry.Unreadable != null)
            {
                _viewer.Text = Strings.Format("Files_Unreadable", entry.Unreadable);
                return;
            }

            if (entry.NotViewable != null)
            {
                _viewer.Text = Strings.Format("Files_NotViewable", entry.NotViewable);
                return;
            }

            var preview = _shownPreview;
            _viewer.Text = preview == null ? string.Empty : preview.Text;
            if (preview != null && preview.Truncated)
            {
                _viewNote.Text = Strings.Format("Files_ShownOf", Formatters.Bytes(preview.ShownBytes), Formatters.Bytes(preview.TotalBytes));
            }

            UpdateViewerEditable();
        }

        private static string HeaderDetail(FsEntry e)
        {
            if (e.Unreadable != null)
            {
                return string.Empty;
            }

            string kind;
            switch (e.Kind)
            {
                case FsEntryKind.Directory:
                    kind = Strings.Get("Files_KindDirectory");
                    break;
                case FsEntryKind.Symlink:
                    kind = Strings.Format("Files_LinkTarget", e.LinkTarget ?? string.Empty);
                    break;
                case FsEntryKind.Special:
                    kind = Strings.Format("Files_SpecialFile", SpecialName(e.Special));
                    break;
                default:
                    kind = e.Size >= 1024
                        ? Formatters.Bytes(e.Size) + " (" + e.Size.ToString("N0", LanguageService.Culture) + " B)"
                        : e.Size.ToString("N0", LanguageService.Culture) + " B";
                    break;
            }

            return Strings.Format("Files_Header", kind, FsEntry.FormatMode(e.Mode), e.Uid, e.Gid, e.Modified.ToLocalTime().ToString("g", LanguageService.Culture));
        }

        private void UpdateExtractEnabled()
        {
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            bool can = _view != null && entry != null && entry.Unreadable == null && !_planning;
            _extract.Enabled = can && !_toolRunning;
            _menuExtract.Enabled = can && !_toolRunning;
            RenderToolsButton();
            UpdateEditUi();
        }

        /// <summary>The Tools button: a menu button, or Cancel while a tool analyses; its tooltip says why it is disabled.</summary>
        private void RenderToolsButton()
        {
            if (_toolRunning)
            {
                _tools.Text = Strings.Get("Common_Cancel");
                _tools.Enabled = true;
                _tips.SetToolTip(_tools, Strings.Get("Tools_Analysing"));
                return;
            }

            _tools.Text = Strings.Get("Tools_Button") + " ▾";
            bool open = _view != null;
            _tools.Enabled = open && !_planning;
            _tips.SetToolTip(
                _tools,
                !open ? Strings.Get("Tools_NoFileSystem") : _planning ? Strings.Get("Tools_Busy") : Strings.Get("Tooltip_Tools"));
        }

        private void OnToolsClick()
        {
            if (_toolRunning)
            {
                _toolCts?.Cancel();
                return;
            }

            if (_view != null && _toolsMenu.Items.Count > 0)
            {
                _toolsMenu.Show(_tools, new Point(0, _tools.Height));
            }
        }

        /// <summary>
        /// Opens a result window that does not block the main window (the person can keep browsing, or open the other
        /// tool); it belongs to the main window, so it closes and minimizes with it, and it holds no handle on the source.
        /// </summary>
        private void ShowTool(Form window)
        {
            window.FormClosed += (s, e) => window.Dispose();
            window.Show(FindForm());
        }

        private DiagnosticsSource BuildToolSource()
        {
            var item = _partition.SelectedItem as PartitionItem;
            if (item == null || _view == null)
            {
                return null;
            }

            var p = item.Partition;
            bool device = DeviceMode;
            return new DiagnosticsSource
            {
                ImagePath = device ? null : _openedPath,
                Device = device ? _openedDevice : null,
                PartitionNumber = p.Number,
                DisplayName = device ? new DeviceItem(_openedDevice).ToString() : Path.GetFileName(_openedPath ?? string.Empty),
                State = p.State,
                HasUnreadBootPartition = _source != null
                    && _source.Partitions.Any(x => x.Number != p.Number && x.FileSystem != null && x.FileSystem.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
            };
        }

        /// <summary>
        /// Runs a read-only diagnostic tool on its own handle of the opened source (not the tab's
        /// view, so the tree stays responsive), then shows its result. Cancel stops it; a read error
        /// is shown in the status line and no window opens.
        /// </summary>
        private async void RunTool<T>(Func<SystemReader, DiagnosticsSource, T> tool, Action<T, DiagnosticsSource> show)
        {
            var source = BuildToolSource();
            if (source == null || _toolRunning)
            {
                return;
            }

            var openedView = _view;
            var cts = new CancellationTokenSource();
            _toolCts = cts;
            _toolRunning = true;
            UpdateExtractEnabled();
            UseWaitCursor = true;
            SetStatus(() => Strings.Get("Tools_Analysing"), false);
            T result;
            try
            {
                result = await Task.Run(() => DiagnosticsRunner.Run(_access, source, tool, cts.Token));
            }
            catch (OperationCanceledException)
            {
                result = default(T);
                cts.Cancel();
            }
            catch (Exception ex) when (ex is ImagingException || ex is ObjectDisposedException || ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!IsDisposed)
                {
                    ShowReadError(ex);
                }

                result = default(T);
                cts.Cancel();
            }
            finally
            {
                _toolRunning = false;
                _toolCts = null;
                if (!IsDisposed)
                {
                    UseWaitCursor = false;
                    UpdateExtractEnabled();
                }
            }

            if (cts.IsCancellationRequested || IsDisposed)
            {
                if (!IsDisposed && _view != null && _statusText().Equals(Strings.Get("Tools_Analysing")))
                {
                    RenderCounts();
                }

                return;
            }

            if (!ReferenceEquals(openedView, _view))
            {
                return; // another file system was opened meanwhile; the result belongs to the old one
            }

            RenderCounts();
            show(result, source);
        }

        /// <summary>
        /// Folder → plan (names, sizes, conflicts) → refusals → one question about existing files →
        /// run through the shared operation panel. Nothing is written before the last step.
        /// </summary>
        private async void ExtractSelected()
        {
            var entry = _tree.SelectedNode?.Tag as FsEntry;
            var view = _view;
            var item = _partition.SelectedItem as PartitionItem;
            var device = DeviceMode ? _openedDevice : null;
            string image = DeviceMode ? null : _openedPath;
            if (entry == null || view == null || item == null || entry.Unreadable != null || _planning)
            {
                return;
            }

            string destination;
            using (var dialog = new FolderBrowserDialog { Description = Strings.Get("Files_ExtractTitle"), ShowNewFolderButton = true })
            {
                if (Directory.Exists(_settings.LastExtractFolder))
                {
                    dialog.SelectedPath = _settings.LastExtractFolder;
                }

                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                destination = dialog.SelectedPath;
            }

            _settings.LastExtractFolder = destination;
            _planning = true;
            UpdateExtractEnabled();
            UseWaitCursor = true;
            SetStatus(() => Strings.Get("Files_Planning"), false);
            ExtractionPlan plan;
            try
            {
                plan = await Task.Run(() => ExtractionPlanner.Plan(
                    view,
                    entry,
                    destination,
                    _access.GetPathInfo(destination),
                    ConflictPolicy.Replace,
                    image,
                    CancellationToken.None));
            }
            catch (Exception ex) when (ex is ImagingException || ex is ObjectDisposedException || ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!IsDisposed)
                {
                    ShowReadError(ex);
                }

                return;
            }
            finally
            {
                _planning = false;
                if (!IsDisposed)
                {
                    UseWaitCursor = false;
                    UpdateExtractEnabled();
                }
            }

            if (IsDisposed || !ReferenceEquals(view, _view))
            {
                return;
            }

            RenderCounts();
            string name = Strings.Get("App_Name");
            if (plan.ProtectedPathHit != null)
            {
                MessageBox.Show(this, Strings.Get("Extract_DestinationIsSource"), name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (plan.NothingToExtract)
            {
                MessageBox.Show(this, Strings.Get("Extract_Nothing"), name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (plan.InsufficientSpace)
            {
                MessageBox.Show(
                    this,
                    Strings.Format("Extract_NoSpace", Formatters.Bytes(plan.TotalBytes), Formatters.Bytes(plan.FreeBytes.Value)),
                    name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            var policy = ConflictPolicy.Skip;
            if (plan.Conflicts > 0)
            {
                using (var ask = new ExtractConflictDialog(plan.Conflicts))
                {
                    if (ask.ShowDialog(this) != DialogResult.OK || !ask.Choice.HasValue)
                    {
                        return;
                    }

                    policy = ask.Choice.Value;
                }
            }

            StartRequested?.Invoke(this, new OperationRequest
            {
                Kind = OperationKind.ExtractFiles,
                BrowseImagePath = image,
                BrowseDevice = device,
                BrowsePartitionNumber = item.Partition.Number,
                ExtractEntryPath = entry.Path,
                ExtractEntryInode = entry.Inode,
                ExtractDestination = destination,
                ExtractConflictPolicy = policy,
                EjectAfter = false
            });
        }

        private void ShowPartitionState(BrowsePartition partition)
        {
            var state = partition.State;
            if (state == null || state.Condition == FileSystemCondition.Clean)
            {
                SetState(() => Strings.Get("Files_StateClean"), SystemColors.ControlText);
            }
            else if (state.Condition == FileSystemCondition.NeedsRepair)
            {
                SetState(() => Strings.Get("Files_StateNeedsRepair"), Color.FromArgb(160, 90, 0));
            }
            else
            {
                SetState(() => Strings.Get("Files_StateDamaged"), Color.DarkRed);
            }
        }

        private void RenderCounts()
        {
            var view = _view;
            if (view == null)
            {
                return;
            }

            var counts = view.Counts;
            string key = counts.Approximate ? "Files_CountsApprox" : "Files_Counts";
            SetStatus(() => Strings.Format(key, counts.Files.ToString("N0", LanguageService.Culture), counts.Directories.ToString("N0", LanguageService.Culture)), false);
        }

        private void OnBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (node.Nodes.Count == 1 && ReferenceEquals(node.Nodes[0].Tag, LoadingTag))
            {
                LoadChildren(node);
            }
        }

        private async void LoadChildren(TreeNode node)
        {
            var entry = (FsEntry)node.Tag;
            var view = Tree;
            int generation = _generation;
            node.Nodes[0].Text = Strings.Get("Files_Opening");
            IReadOnlyList<FsEntry> children;
            try
            {
                children = await Task.Run(() => view.GetChildren(entry, CancellationToken.None));
            }
            catch (Exception ex) when (ex is ImagingException || ex is ObjectDisposedException || ex is IOException)
            {
                if (generation != _generation || IsDisposed || node.TreeView == null)
                {
                    return;
                }

                node.Nodes.Clear();
                node.Nodes.Add(new TreeNode(Strings.Format("Files_ReadError", ex.Message))
                {
                    ImageKey = FilesTreeIcons.Unreadable,
                    SelectedImageKey = FilesTreeIcons.Unreadable,
                    ForeColor = Color.DarkRed
                });
                return;
            }

            if (generation != _generation || IsDisposed || node.TreeView == null)
            {
                return;
            }

            _tree.BeginUpdate();
            try
            {
                node.Nodes.Clear();
                foreach (var child in children)
                {
                    node.Nodes.Add(CreateNode(child));
                }
            }
            finally
            {
                _tree.EndUpdate();
            }
        }

        private static TreeNode CreateNode(FsEntry entry)
        {
            string text = entry.Name;
            string icon;
            string tip = null;
            if (entry.Unreadable != null)
            {
                icon = FilesTreeIcons.Unreadable;
                tip = Strings.Format("Files_Unreadable", entry.Unreadable);
            }
            else
            {
                switch (entry.Kind)
                {
                    case FsEntryKind.Directory:
                        icon = FilesTreeIcons.Folder;
                        break;
                    case FsEntryKind.Symlink:
                        icon = FilesTreeIcons.Link;
                        if (entry.LinkTarget != null)
                        {
                            text += " → " + entry.LinkTarget;
                            tip = Strings.Format("Files_LinkTarget", entry.LinkTarget);
                        }

                        break;
                    case FsEntryKind.Special:
                        icon = FilesTreeIcons.Special;
                        tip = Strings.Format("Files_SpecialFile", SpecialName(entry.Special));
                        break;
                    default:
                        icon = FilesTreeIcons.File;
                        break;
                }
            }

            if (entry.Pending != PendingState.None)
            {
                text += " [" + Strings.Get("Edit_Pending_" + entry.Pending) + "]";
            }

            var node = new TreeNode(text) { Tag = entry, ToolTipText = tip, ImageKey = icon, SelectedImageKey = icon };
            if (entry.Unreadable != null)
            {
                node.ForeColor = Color.DarkRed;
            }
            else if (entry.Pending != PendingState.None)
            {
                node.ForeColor = Color.DarkGreen;
            }

            if (entry.IsDirectory && entry.Unreadable == null)
            {
                node.Nodes.Add(new TreeNode(string.Empty) { Tag = LoadingTag });
            }

            return node;
        }

        internal static string SpecialName(SpecialKind kind)
        {
            switch (kind)
            {
                case SpecialKind.CharDevice: return "character device";
                case SpecialKind.BlockDevice: return "block device";
                case SpecialKind.Fifo: return "FIFO";
                case SpecialKind.Socket: return "socket";
                default: return "special";
            }
        }

        private void ShowReadError(Exception ex)
        {
            string detail = ex is ImagingException ie ? ie.Detail : ex.Message;
            SetState(() => string.Empty, SystemColors.ControlText);
            SetStatus(() => Strings.Format("Files_ReadError", detail), true);
        }

        private void CloseView()
        {
            _previewCts?.Cancel();
            _shownEntry = null;
            _shownPreview = null;
            _viewerError = null;
            RenderViewer();
            _tree.Nodes.Clear();
            EndEdit();
            var view = _view;
            _view = null;
            view?.Dispose();
            UpdateExtractEnabled();
        }

        private void CloseSource()
        {
            CloseView();
            _partition.Items.Clear();
            var source = _source;
            _source = null;
            source?.Dispose();
        }

        private void SetState(Func<string> text, Color color)
        {
            _stateText = text;
            _stateColor = color;
            RenderState();
        }

        private void SetStatus(Func<string> text, bool error)
        {
            _statusText = text;
            _statusColor = error ? Color.DarkRed : SystemColors.ControlText;
            RenderStatus();
        }

        private void RenderState()
        {
            _state.Text = _stateText();
            _state.ForeColor = _stateColor;
            _state.Visible = _state.Text.Length > 0;
        }

        private void RenderStatus()
        {
            _status.Text = _statusText();
            _status.ForeColor = _statusColor;
        }

        private void PlaceSplitter()
        {
            const int minPanel = 160;
            if (_splitterPlaced || _split.Width < 2 * minPanel + _split.SplitterWidth + 40)
            {
                return;
            }

            _splitterPlaced = true;
            _split.Panel1MinSize = minPanel;
            _split.Panel2MinSize = minPanel;
            _split.SplitterDistance = Math.Max(minPanel, _split.Width / 3);
        }

        private void FitLabels()
        {
            int width = Math.Max(100, _root.ClientSize.Width - _root.Padding.Horizontal - 8);
            _state.MaximumSize = new Size(width, 0);
            _status.MaximumSize = new Size(width, 0);
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

        private sealed class PartitionItem
        {
            public PartitionItem(BrowsePartition partition)
            {
                Partition = partition;
            }

            public BrowsePartition Partition { get; }

            public void Refresh()
            {
            }

            public override string ToString()
            {
                string name = string.IsNullOrEmpty(Partition.Name) ? string.Empty : " " + Partition.Name;
                return "#" + Partition.Number + name + " (" + Partition.FileSystem + ") " + Formatters.Bytes(Partition.Length);
            }
        }
    }
}
