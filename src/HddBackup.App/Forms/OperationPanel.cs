using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using HddBackup.App.Localization;
using HddBackup.App.Platform;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;

namespace HddBackup.App.Forms
{
    /// <summary>
    /// Live progress (phase, bar, bytes, speeds, elapsed/remaining time) and the final result.
    /// Polls the engine every 250 ms, so I/O threads never wait for the UI.
    /// </summary>
    internal sealed class OperationPanel : UserControl, ILocalizable
    {
        private static readonly Color SuccessColor = Color.FromArgb(223, 240, 216);
        private static readonly Color WarningColor = Color.FromArgb(252, 248, 227);
        private static readonly Color ErrorColor = Color.FromArgb(242, 222, 222);

        private readonly Label _phase = new Label { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
        private readonly Button _cancel = Ui.Button();
        private readonly ProgressBar _bar = new ProgressBar { Maximum = 1000, Dock = DockStyle.Top, Height = 22 };
        private readonly Label _bytes = new Label { AutoSize = true };
        private readonly Label _speed = new Label { AutoSize = true };
        private readonly Label _time = new Label { AutoSize = true };
        // UseMnemonic = false: paths and device IDs may contain '&', which must be shown literally.
        private readonly Label _banner = new Label { AutoSize = true, Padding = new Padding(8), Dock = DockStyle.Top, Visible = false, UseMnemonic = false };
        private readonly Button _copy = Ui.Button();
        private readonly Button _close = Ui.Button();
        private readonly Timer _timer = new Timer { Interval = 250 };
        private readonly TaskbarProgress _taskbar;

        private IImagingEngine _engine;
        private OperationRequest _request;
        private OperationResult _result;
        private OperationProgress _last = OperationProgress.Idle;
        private string _step;
        private string _extraLine;

        public OperationPanel(TaskbarProgress taskbar)
        {
            _taskbar = taskbar;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true, Padding = new Padding(8, 0, 8, 8) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var header = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(_phase, 0, 0);
            header.Controls.Add(_cancel, 1, 0);

            foreach (var control in new Control[] { header, _bar, _bytes, _speed, _time, _banner })
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(control);
            }

            var footer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(_copy, 0, 0);
            footer.Controls.Add(_close, 1, 0);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(footer);

            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Controls.Add(root);

            _copy.Visible = false;
            _close.Visible = false;
            _cancel.Click += (s, e) => CancelRequested?.Invoke(this, EventArgs.Empty);
            _copy.Click += (s, e) => CopyChecksum();
            _close.Click += (s, e) => CloseResult();
            _timer.Tick += (s, e) => Refresh(_engine.CurrentProgress);
            Visible = false;
            ApplyStrings();
        }

        public event EventHandler CancelRequested;

        public void Start(IImagingEngine engine, OperationRequest request, string step = null)
        {
            _engine = engine;
            _request = request;
            _step = step;
            _extraLine = null;
            _result = null;
            _last = OperationProgress.Idle;
            _banner.Visible = false;
            _copy.Visible = false;
            _close.Visible = false;
            _cancel.Visible = true;
            _cancel.Enabled = true;
            _bar.Value = 0;
            Visible = true;
            _taskbar.SetState(TaskbarState.Normal);
            _taskbar.SetValue(0);
            Refresh(engine.CurrentProgress);
            _timer.Start();
        }

        /// <summary>Taskbar shows "paused" while the user decides whether to cancel.</summary>
        public void SetConfirmingCancel(bool confirming)
        {
            _taskbar.SetState(confirming ? TaskbarState.Paused : TaskbarState.Normal);
        }

        public void ShowResult(OperationResult result, string extraLine = null)
        {
            _timer.Stop();
            _result = result;
            _extraLine = extraLine;
            _cancel.Visible = false;
            _close.Visible = true;
            Refresh(_engine.CurrentProgress);
            RenderResult();
            _taskbar.SetState(result.State == OperationState.Completed ? TaskbarState.None : TaskbarState.Error);
            if (result.State != OperationState.Completed)
            {
                _taskbar.SetValue(_last.Percent);
            }
        }

        public void ApplyStrings()
        {
            _phase.Font = new Font(Font, FontStyle.Bold);
            _cancel.Text = Strings.Get("Common_Cancel");
            _copy.Text = Strings.Get("Result_CopySha");
            _close.Text = Strings.Get("Common_Close");
            if (_engine != null)
            {
                Refresh(_last);
                if (_result != null)
                {
                    RenderResult();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }

            base.Dispose(disposing);
        }

                /// <summary>Hides the finished operation's panel; only possible once the operation is over.</summary>
        private void CloseResult()
        {
            if (_result == null)
            {
                return;
            }

            Visible = false;
            _taskbar.SetState(TaskbarState.None);
        }

        private void Refresh(OperationProgress p)
        {
            _last = p;
            _phase.Text = _step == null ? PhaseText(p.Phase) : Strings.Format("Shrink_StepPhase", _step, PhaseText(p.Phase));
            if (_result != null)
            {
                // After the end the last phase (release/eject) has no data; show the whole run instead.
                double seconds = _result.Duration.TotalSeconds;
                _bar.Value = (int)Math.Round(Math.Max(0, Math.Min(100, p.Percent)) * 10);
                _bytes.Text = Formatters.Bytes(_result.DataBytes) + "    " + p.Percent.ToString("0.0", LanguageService.Culture) + " %";
                _speed.Text = Strings.Format("Progress_AverageSpeed", Formatters.Speed(seconds > 0 ? _result.DataBytes / seconds : 0));
                _time.Text = Strings.Format("Progress_Time", Formatters.Duration(_result.Duration), Formatters.Duration(TimeSpan.Zero));
                return;
            }

            _bar.Value = (int)Math.Round(Math.Max(0, Math.Min(100, p.Percent)) * 10);
            _bytes.Text = (p.BytesTotal.HasValue
                ? Strings.Format("Progress_BytesOf", Formatters.Bytes(p.BytesProcessed), Formatters.Bytes(p.BytesTotal.Value))
                : Formatters.Bytes(p.BytesProcessed))
                + "    " + p.Percent.ToString("0.0", LanguageService.Culture) + " %";
            _speed.Text = Strings.Format("Progress_Speed", Formatters.Speed(p.CurrentSpeed), Formatters.Speed(p.AverageSpeed));
            string remaining = p.Remaining.HasValue ? Formatters.Duration(p.Remaining.Value) : Strings.Get("Progress_Calculating");
            _time.Text = Strings.Format("Progress_Time", Formatters.Duration(p.Elapsed), remaining);
            if (!p.IsTerminal)
            {
                _taskbar.SetValue(p.Percent);
            }
        }

        private string PhaseText(OperationPhase phase)
        {
            if (_result != null)
            {
                return Strings.Get(_result.State == OperationState.Completed ? "Phase_Done" : "Phase_Stopped");
            }

            if (_request?.Kind == OperationKind.ShrinkImage)
            {
                return Strings.Get("Phase_Shrink_" + phase);
            }

            if (phase == OperationPhase.Transferring && _request != null)
            {
                return Strings.Get(_request.IsBackup ? "Phase_Transferring_Backup" : "Phase_Transferring_Restore");
            }

            if (phase == OperationPhase.Verifying && _request?.Kind == OperationKind.VerifyImage)
            {
                return Strings.Get("Phase_Transferring_Verify");
            }

            return Strings.Get("Phase_" + phase);
        }

        private void RenderResult()
        {
            var r = _result;
            var lines = new List<string>();
            Color color;
            switch (r.State)
            {
                case OperationState.Completed:
                    color = SuccessColor;
                    if (r.Kind == OperationKind.VerifyImage)
                    {
                        lines.Add(Strings.Get("Result_VerifyDone"));
                    }
                    else if (r.Kind == OperationKind.ShrinkImage)
                    {
                        lines.Add(Strings.Format("Shrink_ResultSaved", r.ImagePath, Formatters.Bytes(r.DataBytes)));
                    }
                    else
                    {
                        lines.Add(Strings.Get("Result_SafeToRemove"));
                        if (r.Kind == OperationKind.BackupDevice || r.Kind == OperationKind.BackupPartition)
                        {
                            lines.Add(Strings.Format("Result_BackupSaved", r.ImagePath));
                            if (r.ImageIncomplete)
                            {
                                color = WarningColor;
                                lines.Add(Strings.Get("Result_ImageIncomplete"));
                            }
                        }
                        else
                        {
                            lines.Add(Strings.Get("Result_RestoreDone"));
                        }
                    }

                    if (r.EjectVetoedBy != null)
                    {
                        color = WarningColor;
                        var by = r.EjectVetoedBy.Length > 0 ? r.EjectVetoedBy : Strings.Get("Common_AnotherProgram");
                        lines.Add(Strings.Format("Result_EjectVetoed", by));
                    }

                    break;
                case OperationState.Cancelled:
                    color = WarningColor;
                    lines.Add(Strings.Get("Result_Cancelled"));
                    AddDeviceState(r, lines);
                    break;
                default:
                    color = ErrorColor;
                    lines.Add(Strings.Format("Result_Failed", Formatters.Error(r.ErrorCode)));
                    if (r.ErrorOffset.HasValue)
                    {
                        lines.Add(Strings.Format("Result_AtOffset", Formatters.Offset(r.ErrorOffset.Value)));
                    }

                    AddDeviceState(r, lines);
                    if (r.ErrorCode == ErrorCode.ShrinkNotPossible)
                    {
                        lines.Add(Formatters.BlockersFromDetail(r.ErrorDetail));
                    }
                    else if (!string.IsNullOrEmpty(r.ErrorDetail))
                    {
                        lines.Add(Strings.Format("Error_Details", r.ErrorDetail));
                    }

                    break;
            }

            foreach (var v in r.Verifications)
            {
                string kind = Strings.Get(v.Kind == ChecksumKind.File ? "Check_File" : "Check_Content");
                lines.Add(kind + ": " + Strings.Get("Check_" + v.Status));
            }

            string sha = r.ContentSha256;
            if (!string.IsNullOrEmpty(sha))
            {
                lines.Add(Strings.Format("Result_Sha256", sha));
            }

            if (!string.IsNullOrEmpty(_extraLine))
            {
                lines.Add(_extraLine);
            }

            lines.Add(Strings.Format("Result_Duration", Formatters.Duration(r.Duration)));
            _banner.BackColor = color;
            _banner.Text = string.Join(Environment.NewLine, lines);
            _banner.MaximumSize = new Size(Math.Max(200, Width - 24), 0);
            _banner.Visible = true;
            _copy.Visible = !string.IsNullOrEmpty(sha);
        }

        private static void AddDeviceState(OperationResult r, List<string> lines)
        {
            if (r.Kind == OperationKind.VerifyImage)
            {
                return;
            }

            if (r.Kind == OperationKind.ShrinkImage)
            {
                lines.Add(Strings.Get("Shrink_NothingSaved"));
                return;
            }

            if (r.ErrorCode == ErrorCode.DeviceRemoved)
            {
                lines.Add(Strings.Get("Result_DeviceAlreadyRemoved"));
            }
            else
            {
                lines.Add(Strings.Get(r.SafeToRemove ? "Result_DeviceReleased" : "Result_DeviceMayBeBusy"));
            }

            bool isRestore = r.Kind == OperationKind.RestoreDevice || r.Kind == OperationKind.RestorePartition;
            if (isRestore)
            {
                lines.Add(Strings.Get("Result_ContentIncomplete"));
            }
            else
            {
                lines.Add(Strings.Get("Result_NoImageSaved"));
            }
        }

        private void CopyChecksum()
        {
            if (!string.IsNullOrEmpty(_result?.ContentSha256))
            {
                try
                {
                    Clipboard.SetText(_result.ContentSha256);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Clipboard busy; the value is still visible on screen.
                }
            }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            _banner.MaximumSize = new Size(Math.Max(200, Width - 24), 0);
        }

        internal static string ImageName(string path) => Path.GetFileName(path ?? string.Empty);
    }
}
