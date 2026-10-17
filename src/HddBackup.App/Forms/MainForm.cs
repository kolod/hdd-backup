using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Platform;
using HddBackup.App.Settings;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Shrinking;
using System.IO;

namespace HddBackup.App.Forms
{
    /// <summary>Main window: Back Up, Restore and Image tabs plus the shared operation panel.</summary>
    internal sealed class MainForm : Form, ILocalizable
    {
        private readonly PortableSettings _settings;
        private readonly IDeviceEnumerator _enumerator = new DeviceEnumerator();
        private readonly IDeviceAccess _access = new WindowsDeviceAccess();
        private readonly MenuStrip _menu = new MenuStrip();
        private readonly ToolStripMenuItem _languageMenu = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _helpMenu = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _aboutItem = new ToolStripMenuItem();
        private readonly ToolStripMenuItem _diagnosticsItem = new ToolStripMenuItem();
        private readonly TabControl _tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TabPage _backupPage = new TabPage();
        private readonly TabPage _restorePage = new TabPage();
        private readonly TabPage _imagePage = new TabPage();
        private readonly BackupTab _backup;
        private readonly RestoreTab _restore;
        private readonly ImageTab _imageTab;
        private readonly SelectedImage _image;
        private DeviceChangeWatcher _watcher;
        private OperationPanel _operation;
        private CancellationTokenSource _cts;
        private OperationRequest _running;
        private bool _closeWhenDone;
        private bool _refreshPending;

        public MainForm(PortableSettings settings)
        {
            _settings = settings;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = UiFont.For(LanguageService.Current);
            ClientSize = new Size(980, 680);
            MinimumSize = new Size(800, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = AppIcon.Icon;

            _image = new SelectedImage(); // after the base constructor installed the UI synchronization context
            _backup = new BackupTab(settings) { Dock = DockStyle.Fill };
            _restore = new RestoreTab(settings, _image) { Dock = DockStyle.Fill };
            _backupPage.Controls.Add(_backup);
            _imageTab = new ImageTab(settings, _image) { Dock = DockStyle.Fill };
            _restorePage.Controls.Add(_restore);
            _imagePage.Controls.Add(_imageTab);
            _tabs.TabPages.Add(_backupPage);
            _tabs.TabPages.Add(_restorePage);
            _tabs.TabPages.Add(_imagePage);

            foreach (var language in LanguageService.Supported)
            {
                var item = new ToolStripMenuItem(language.NativeName) { Tag = language.Code };
                item.Click += (s, e) => ChangeLanguage((string)((ToolStripMenuItem)s).Tag);
                _languageMenu.DropDownItems.Add(item);
            }

            _aboutItem.Click += (s, e) =>
            {
                using (var about = new AboutDialog())
                {
                    about.ShowDialog(this);
                }
            };
            _diagnosticsItem.Click += (s, e) =>
            {
                using (var diagnostics = new DiagnosticsDialog(_image.IsFullDevice ? _image.Descriptor.Path : null))
                {
                    diagnostics.ShowDialog(this);
                }
            };
            _helpMenu.DropDownItems.Add(_diagnosticsItem);
            _helpMenu.DropDownItems.Add(new ToolStripSeparator());
            _helpMenu.DropDownItems.Add(_aboutItem);
            _menu.Items.AddRange(new ToolStripItem[] { _languageMenu, _helpMenu });

            Controls.Add(_tabs);
            Controls.Add(_menu);
            MainMenuStrip = _menu;

            _backup.StartRequested += (s, request) => StartOperation(request);
            _restore.StartRequested += (s, request) => StartOperation(request);
            _imageTab.StartRequested += (s, request) => StartOperation(request);
            LanguageService.LanguageChanged += OnLanguageChanged;
            ApplyStrings();
        }

        public void ApplyStrings()
        {
            Text = Strings.Get("App_Title");
            _languageMenu.Text = Strings.Get("Menu_Language");
            _helpMenu.Text = Strings.Get("Menu_Help");
            _aboutItem.Text = Strings.Get("Menu_About");
            _diagnosticsItem.Text = Strings.Get("Menu_Diagnostics");
            _backupPage.Text = Strings.Get("Tab_Backup");
            _restorePage.Text = Strings.Get("Tab_Restore");
            _imagePage.Text = Strings.Get("Tab_Image");
            foreach (ToolStripMenuItem item in _languageMenu.DropDownItems)
            {
                item.Checked = string.Equals((string)item.Tag, LanguageService.Current, StringComparison.Ordinal);
            }

            _backup.ApplyStrings();
            _restore.ApplyStrings();
            _imageTab.ApplyStrings();
            _operation?.ApplyStrings();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _operation = new OperationPanel(new TaskbarProgress(Handle)) { Dock = DockStyle.Bottom };
            _operation.CancelRequested += (s, args) => ConfirmCancel();
            Controls.Add(_operation);
            _operation.BringToFront();
            _tabs.BringToFront();
            _watcher = new DeviceChangeWatcher();
            _watcher.DevicesChanged += (s, args) => RefreshDevices();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshDevices();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_running != null)
            {
                e.Cancel = true;
                if (!_closeWhenDone && Ask(_running.IsRestore ? "Close_ConfirmRunningRestore" : "Close_ConfirmRunning"))
                {
                    _closeWhenDone = true;
                    _cts?.Cancel();
                }

                return;
            }

            _settings.SaveIfChanged();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                LanguageService.LanguageChanged -= OnLanguageChanged;
                _watcher?.Dispose();
                _image.Dispose();
                _cts?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            Font = UiFont.For(LanguageService.Current);
            ApplyStrings();
        }

        private void ChangeLanguage(string code)
        {
            _settings.Language = code;
            LanguageService.SetLanguage(code);
        }

        private async void RefreshDevices()
        {
            if (_running != null)
            {
                _refreshPending = true;
                return;
            }

            IReadOnlyList<DeviceInfo> devices;
            try
            {
                devices = await Task.Run(() => _enumerator.GetDevices());
            }
            catch (Exception)
            {
                devices = Array.Empty<DeviceInfo>();
            }

            if (IsDisposed)
            {
                return;
            }

            _backup.SetDevices(devices);
            _restore.SetDevices(devices);
            _imageTab.SetDevices(devices);
        }

        private async void StartOperation(OperationRequest request)
        {
            if (_running != null)
            {
                return;
            }

            if (request.Kind == OperationKind.RestoreDevice && await OfferShrinkIfTooLargeAsync(request))
            {
                return;
            }

            var engine = new ImagingEngine(_access);
            IReadOnlyList<ValidationWarning> warnings;
            try
            {
                if (request.IsRestore)
                {
                    // Run every other check first; the external-disk confirmation is asked in the dialog.
                    request.ExternalDiskConfirmed = true;
                }

                warnings = engine.Validate(request);
            }
            catch (ImagingException ex)
            {
                ShowError(ex.Code, ex.Offset, ex.Detail);
                return;
            }

            if (request.IsRestore)
            {
                using (var confirm = new ConfirmWriteDialog(request, warnings))
                {
                    if (confirm.ShowDialog(this) != DialogResult.OK)
                    {
                        return;
                    }

                    request.ExternalDiskConfirmed = request.Device.IsRemovableMedia || confirm.ExternalDiskConfirmed;
                }
            }
            else if ((request.IsBackup || request.Kind == OperationKind.ConvertImage) && warnings.Count > 0)
            {
                var text = string.Join(Environment.NewLine, warnings.Select(w => "• " + Strings.Get("Warning_" + w)))
                    + Environment.NewLine + Environment.NewLine + Strings.Get("Warning_Continue");
                if (MessageBox.Show(this, text, Strings.Get("App_Name"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return;
                }
            }

            var result = await RunEngineAsync(engine, request, null, null);
            if (result == null)
            {
                return;
            }

            // New checksum files, or a converted image sharing a sidecar with the source: show them.
            if (result.SavedChecksumFiles.Count > 0 || (request.Kind == OperationKind.ConvertImage && result.State == OperationState.Completed))
            {
                _image.Reload();
            }

            if (_refreshPending || !request.IsBackup || request.EjectAfter)
            {
                _refreshPending = false;
                RefreshDevices();
            }
        }

        /// <summary>Runs one engine operation with the shared panel; null when the window is closing.</summary>
        private async Task<OperationResult> RunEngineAsync(ImagingEngine engine, OperationRequest request, Func<string> step, Func<string> extraResultLine)
        {
            _running = request;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _operation.Start(engine, request, step);
            OperationResult result;
            try
            {
                result = await engine.RunAsync(request, _cts.Token);
            }
            finally
            {
                _running = null;
                SetBusy(false);
            }

            _operation.ShowResult(result, extraResultLine);
            _cts.Dispose();
            _cts = null;

            if (_closeWhenDone)
            {
                Close();
                return null;
            }

            return result;
        }

        /// <summary>
        /// FR-001/FR-002/FR-003a: when an image is larger than the target device, analyze it and,
        /// if its last ext4 partition can be shrunk to fit, offer "shrink and write". Returns true
        /// when the request was handled here (offered, refused or run).
        /// </summary>
        private async Task<bool> OfferShrinkIfTooLargeAsync(OperationRequest request)
        {
            bool isRaw;
            long fileSize;
            long required;
            try
            {
                var image = ImageDescriptor.Load(request.ImagePath);
                isRaw = image.Format == ImageFormat.Raw;
                fileSize = image.FileSize;
                required = ShrinkPlanner.RequiredDeviceBytes(request.ImagePath);
            }
            catch (Exception)
            {
                return false; // the normal validation reports the problem
            }

            // Reuse the analysis of the shared image when it is for this file.
            bool same = string.Equals(_image.Path, request.ImagePath, StringComparison.OrdinalIgnoreCase);
            ShrinkPlan plan = same && _image.AnalysisState == AnalysisState.Done ? _image.Analysis : null;
            var fit = ImageFit.Evaluate(isRaw, fileSize, required, plan, false, request.Device.Size);
            if (fit == ImageFitResult.Fits)
            {
                return false;
            }

            if (plan == null)
            {
                UseWaitCursor = true;
                try
                {
                    plan = await Task.Run(() => ShrinkPlanner.Analyze(request.ImagePath, CancellationToken.None));
                }
                catch (Exception ex) when (ex is ImagingException || ex is System.IO.IOException || ex is HddBackup.Core.Ext4.Ext4FormatException)
                {
                    return false;
                }
                finally
                {
                    UseWaitCursor = false;
                }

                fit = ImageFit.Evaluate(isRaw, fileSize, required, plan, false, request.Device.Size);
            }

            long imageBytes = isRaw ? fileSize : required;
            if (fit != ImageFitResult.ShrinkToFit)
            {
                var reasons = plan.CanShrink(true)
                    ? Strings.Get("Blocker_NotEnoughSpaceEvenAtMinimum")
                    : Formatters.Blockers(plan.Blockers);
                MessageBox.Show(
                    this,
                    Strings.Format("Shrink_NotPossible", Formatters.Bytes(imageBytes), Formatters.Bytes(request.Device.Size)) + Environment.NewLine + Environment.NewLine + reasons,
                    Strings.Get("App_Name"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return true;
            }

            // Never replace an earlier result (or anything else) silently: pick a free name.
            bool repair = ImageFit.RequiresRepair(plan);
            string output = ImageFileName.FirstFree(repair
                ? ShrinkOutputName.Repaired(request.ImagePath, request.Device.Size, false, shrunk: true)
                : ShrinkOutputName.Default(request.ImagePath, request.Device.Size, false));
            using (var offer = new ShrinkOfferDialog(plan, imageBytes, request.Device.Size, output))
            {
                if (offer.ShowDialog(this) != DialogResult.OK)
                {
                    return true;
                }
            }

            // Same confirmation as a normal write, for the image that will be produced.
            var writeRequest = new OperationRequest
            {
                Kind = OperationKind.RestoreDevice,
                Device = request.Device,
                ImagePath = output,
                VerifyAfter = request.VerifyAfter,
                EjectAfter = request.EjectAfter
            };
            using (var confirm = new ConfirmWriteDialog(writeRequest, Array.Empty<ValidationWarning>(), request.Device.Size, Path.GetFileName(output)))
            {
                if (confirm.ShowDialog(this) != DialogResult.OK)
                {
                    return true;
                }

                writeRequest.ExternalDiskConfirmed = request.Device.IsRemovableMedia || confirm.ExternalDiskConfirmed;
            }

            var shrinkRequest = new OperationRequest
            {
                Kind = OperationKind.ShrinkImage,
                ImagePath = request.ImagePath,
                ShrinkOutputPath = output,
                ShrinkTargetBytes = request.Device.Size,
                RepairFileSystem = repair, // an unclean shutdown is repaired as part of the shrink
                EjectAfter = false
            };
            var shrinkResult = await RunEngineAsync(new ImagingEngine(_access), shrinkRequest, () => Strings.Format("Shrink_Step", 1, 2), null);
            if (shrinkResult == null || shrinkResult.State != OperationState.Completed)
            {
                return true;
            }

            var engine = new ImagingEngine(_access);
            try
            {
                engine.Validate(writeRequest);
            }
            catch (ImagingException ex)
            {
                ShowError(ex.Code, ex.Offset, ex.Detail);
                return true;
            }

            long savedBytes = shrinkResult.DataBytes;
            await RunEngineAsync(
                engine,
                writeRequest,
                () => Strings.Format("Shrink_Step", 2, 2),
                () => Strings.Format("Shrink_ResultSaved", output, Formatters.Bytes(savedBytes)));
            RefreshDevices();
            return true;
        }

        private void ConfirmCancel()
        {
            if (_running == null || _cts == null)
            {
                return;
            }

            _operation.SetConfirmingCancel(true);
            bool cancel = Ask(_running.IsRestore ? "Cancel_ConfirmRestore" : "Cancel_ConfirmOther");
            _operation.SetConfirmingCancel(false);
            if (cancel)
            {
                _cts?.Cancel();
            }
        }

        private void SetBusy(bool busy)
        {
            // The language menu stays available: every visible text, including the progress
            // panel, is re-rendered on a switch and the running operation is not affected.
            _backup.Enabled = !busy;
            _restore.Enabled = !busy;
            _imageTab.Enabled = !busy;
        }

        private bool Ask(string key, MessageBoxIcon icon = MessageBoxIcon.Warning)
        {
            return MessageBox.Show(this, Strings.Get(key), Strings.Get("App_Name"), MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2)
                == DialogResult.Yes;
        }

        private void ShowError(ErrorCode code, long? offset, string detail)
        {
            var text = Formatters.Error(code);
            if (offset.HasValue)
            {
                text += Environment.NewLine + Strings.Format("Result_AtOffset", Formatters.Offset(offset.Value));
            }

            if (!string.IsNullOrEmpty(detail))
            {
                text += Environment.NewLine + Environment.NewLine + Strings.Format("Error_Details", detail);
            }

            MessageBox.Show(this, text, Strings.Get("App_Name"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
