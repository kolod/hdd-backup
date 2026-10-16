using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.App.Localization;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Shrinking;

namespace HddBackup.App.Forms
{
    internal enum AnalysisState
    {
        None,
        Running,
        Done,
        Failed
    }

    /// <summary>
    /// The image chosen on the Restore and Image tabs (one shared selection), with its sidecars and,
    /// for full-device images, the shrink analysis computed in the background.
    /// </summary>
    internal sealed class SelectedImage : IDisposable
    {
        private readonly SynchronizationContext _ui;
        private CancellationTokenSource _analysisCts;
        private long _analyzedLength;
        private DateTime _analyzedWriteTime;

        public SelectedImage()
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        }

        /// <summary>Raised on the UI thread after any property changed.</summary>
        public event EventHandler Changed;

        public string Path { get; private set; } = string.Empty;

        public ImageDescriptor Descriptor { get; private set; }

        /// <summary>Localized reason when the image could not be loaded.</summary>
        public string LoadError { get; private set; }

        /// <summary>Device bytes the image's partitions need; 0 when unknown or not a full-device image.</summary>
        public long RequiredDeviceBytes { get; private set; }

        public ShrinkPlan Analysis { get; private set; }

        public AnalysisState AnalysisState { get; private set; }

        public string AnalysisError { get; private set; }

        public bool IsFullDevice => Descriptor != null && Descriptor.Kind == ImageKind.FullDevice;

        public void SetPath(string path)
        {
            path = (path ?? string.Empty).Trim();
            if (string.Equals(path, Path, StringComparison.Ordinal))
            {
                return;
            }

            Path = path;
            Load(keepAnalysis: false);
        }

        /// <summary>Re-reads the image and its sidecars, e.g. after checksum files were saved.</summary>
        public void Reload()
        {
            bool unchanged = AnalysisState == AnalysisState.Done && File.Exists(Path)
                && new FileInfo(Path).Length == _analyzedLength && File.GetLastWriteTimeUtc(Path) == _analyzedWriteTime;
            Load(keepAnalysis: unchanged);
        }

        public void Dispose()
        {
            _analysisCts?.Cancel();
            _analysisCts?.Dispose();
            _analysisCts = null;
        }

        private void Load(bool keepAnalysis)
        {
            Descriptor = null;
            LoadError = null;
            RequiredDeviceBytes = 0;
            if (!keepAnalysis)
            {
                CancelAnalysis();
                Analysis = null;
                AnalysisState = AnalysisState.None;
                AnalysisError = null;
            }

            if (Path.Length > 0)
            {
                try
                {
                    Descriptor = ImageDescriptor.Load(Path);
                }
                catch (ImagingException ex)
                {
                    LoadError = Formatters.Error(ex.Code);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
                {
                    LoadError = ex.Message;
                }
            }

            if (IsFullDevice)
            {
                try
                {
                    RequiredDeviceBytes = ShrinkPlanner.RequiredDeviceBytes(Path);
                }
                catch (Exception)
                {
                    RequiredDeviceBytes = 0; // analysis reports the problem
                }

                if (!keepAnalysis)
                {
                    StartAnalysis();
                }
            }
            else if (keepAnalysis)
            {
                CancelAnalysis();
                Analysis = null;
                AnalysisState = AnalysisState.None;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void StartAnalysis()
        {
            var cts = new CancellationTokenSource();
            _analysisCts = cts;
            string path = Path;
            long length = new FileInfo(path).Length;
            DateTime written = File.GetLastWriteTimeUtc(path);
            AnalysisState = AnalysisState.Running;
            Task.Run(() => ShrinkPlanner.Analyze(path, cts.Token), cts.Token).ContinueWith(
                task => _ui.Post(
                    _ =>
                    {
                        if (cts.IsCancellationRequested || !ReferenceEquals(cts, _analysisCts))
                        {
                            return; // superseded by another image
                        }

                        if (task.Status == TaskStatus.RanToCompletion)
                        {
                            Analysis = task.Result;
                            AnalysisState = AnalysisState.Done;
                            _analyzedLength = length;
                            _analyzedWriteTime = written;
                        }
                        else
                        {
                            AnalysisState = AnalysisState.Failed;
                            AnalysisError = task.Exception?.GetBaseException().Message ?? string.Empty;
                        }

                        Changed?.Invoke(this, EventArgs.Empty);
                    },
                    null),
                TaskScheduler.Default);
        }

        private void CancelAnalysis()
        {
            if (_analysisCts != null)
            {
                _analysisCts.Cancel();
                _analysisCts.Dispose();
                _analysisCts = null;
            }
        }
    }
}
