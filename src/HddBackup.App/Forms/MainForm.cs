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

namespace HddBackup.App.Forms
{
    /// <summary>Main window: Back Up and Restore/Verify tabs plus the shared operation panel.</summary>
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
        private readonly BackupTab _backup;
        private readonly RestoreTab _restore;
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
            ClientSize = new Size(820, 680);
            MinimumSize = new Size(640, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            _backup = new BackupTab(settings) { Dock = DockStyle.Fill };
            _restore = new RestoreTab(settings) { Dock = DockStyle.Fill };
            _backupPage.Controls.Add(_backup);
            _restorePage.Controls.Add(_restore);
            _tabs.TabPages.Add(_backupPage);
            _tabs.TabPages.Add(_restorePage);

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
                using (var diagnostics = new DiagnosticsDialog())
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
            foreach (ToolStripMenuItem item in _languageMenu.DropDownItems)
            {
                item.Checked = string.Equals((string)item.Tag, LanguageService.Current, StringComparison.Ordinal);
            }

            _backup.ApplyStrings();
            _restore.ApplyStrings();
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
        }

        private async void StartOperation(OperationRequest request)
        {
            if (_running != null)
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
            else if (request.IsBackup && warnings.Count > 0)
            {
                var text = string.Join(Environment.NewLine, warnings.Select(w => "• " + Strings.Get("Warning_" + w)))
                    + Environment.NewLine + Environment.NewLine + Strings.Get("Warning_Continue");
                if (MessageBox.Show(this, text, Strings.Get("App_Name"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return;
                }
            }

            _running = request;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _operation.Start(engine, request);
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

            _operation.ShowResult(result);
            _cts.Dispose();
            _cts = null;

            if (_closeWhenDone)
            {
                Close();
                return;
            }

            if (request.Kind == OperationKind.VerifyImage)
            {
                OfferToSaveChecksums(result);
            }

            if (_refreshPending || !request.IsBackup || request.EjectAfter)
            {
                _refreshPending = false;
                RefreshDevices();
            }
        }

        private void OfferToSaveChecksums(OperationResult result)
        {
            if (result.State != OperationState.Completed
                || result.Verifications.Any(v => v.Status != VerificationStatus.NotAvailable)
                || !Ask("Verify_SaveChecksumsQuestion", MessageBoxIcon.Question))
            {
                return;
            }

            try
            {
                ImagingEngine.SaveChecksumFiles(result.ImagePath, result.IsGzipImage, result.FileSha256, result.ContentSha256);
                MessageBox.Show(this, Strings.Get("Verify_ChecksumsSaved"), Strings.Get("App_Name"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                _restore.ReloadImage();
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, Strings.Get("App_Name"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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
            _backup.Enabled = !busy;
            _restore.Enabled = !busy;
            _languageMenu.Enabled = !busy;
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
