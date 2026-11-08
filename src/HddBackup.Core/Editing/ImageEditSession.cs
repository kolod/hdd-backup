using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;

namespace HddBackup.Core.Editing
{
    /// <summary>Why an image or card cannot be edited; the UI turns the code into text.</summary>
    public enum EditRefused
    {
        Compressed,
        NotBrowsable,
        NotWritable,
        NeedsRecovery,
        HasOrphans,
        HasErrors,
        NotClean,
        UnsupportedFeature,
        InUseByAnotherInstance,
        NotRemovable,
        SystemDisk,
        WriteProtected
    }

    public sealed class EditRefusedException : Exception
    {
        public EditRefusedException(EditRefused reason, string detail = null)
            : base(reason + (detail == null ? string.Empty : ": " + detail))
        {
            Reason = reason;
            Detail = detail ?? string.Empty;
        }

        public EditRefused Reason { get; }

        public string Detail { get; }
    }

    public enum SaveStatus
    {
        Saved,
        SavedChecksumStale,
        Refused,
        Failed,
        Interrupted
    }

    public sealed class SaveResult
    {
        public SaveStatus Status { get; internal set; }

        public int ChangesWritten { get; internal set; }

        public IReadOnlyList<string> ChecksumFiles { get; internal set; } = new string[0];

        public EditError? Error { get; internal set; }

        public string Detail { get; internal set; } = string.Empty;

        public long Needed { get; internal set; }

        public long Available { get; internal set; }

        /// <summary>For a failed write: the absolute offset in the image.</summary>
        public long? Offset { get; internal set; }

        public ErrorCode ErrorCode { get; internal set; }
    }

    public enum SessionState
    {
        Editing,
        Interrupted
    }

    /// <summary>
    /// Edit mode of an image: the waiting changes, the diff file beside the image, Save and Finish save
    /// (research R-8, R-9). The image itself is not written to before <see cref="Save"/>.
    /// </summary>
    public sealed class ImageEditSession : IDisposable
    {
        private readonly string _diffPath;
        private readonly string _redoPath;
        private readonly string _lockPath;
        private readonly long _offset;
        private readonly long _length;
        private readonly List<EditChange> _changes = new List<EditChange>();
        private FileStream _lock;
        private ImageIdentity _identity;

        private ImageEditSession(string imagePath, int partitionNumber, long offset, long length)
        {
            ImagePath = imagePath;
            PartitionNumber = partitionNumber;
            _offset = offset;
            _length = length;
            _diffPath = DiffFile.PathFor(imagePath);
            _redoPath = RedoLog.PathFor(imagePath);
            _lockPath = imagePath + ".hddbackup-lock";
        }

        public string ImagePath { get; }

        public int PartitionNumber { get; }

        public SessionState State { get; private set; } = SessionState.Editing;

        /// <summary>Of a diff file found at open time; <see cref="DiffStatus.ImageChanged"/> means the changes cannot be used.</summary>
        public DiffStatus DiffStatus { get; private set; } = DiffStatus.None;

        public IReadOnlyList<EditChange> Changes => _changes;

        public string DiffPath => _diffPath;

        /// <summary>Offset and length of the edited partition in the image.</summary>
        public long PartitionOffset => _offset;

        public long PartitionLength => _length;

        public static ImageEditSession Open(string imagePath)
        {
            return Open(imagePath, 0);
        }

        /// <summary>
        /// Opens edit mode for the ext4 partition <paramref name="partitionNumber"/> (0 = the first one that can be
        /// edited). Throws <see cref="EditRefusedException"/> with the reason when it cannot be edited; writes nothing.
        /// </summary>
        public static ImageEditSession Open(string imagePath, int partitionNumber)
        {
            if (ImageDescriptor.IsGzipPath(imagePath))
            {
                throw new EditRefusedException(EditRefused.Compressed);
            }

            if (new FileInfo(imagePath).IsReadOnly)
            {
                throw new EditRefusedException(EditRefused.NotWritable);
            }

            long offset;
            long length;
            int number;
            using (var source = BrowseSource.OpenImage(imagePath))
            {
                var partition = source.Browsable.FirstOrDefault(p => partitionNumber == 0 || p.Number == partitionNumber);
                if (partition == null)
                {
                    throw new EditRefusedException(EditRefused.NotBrowsable);
                }

                offset = partition.Offset;
                length = partition.Length;
                number = partition.Number;
            }

            var session = new ImageEditSession(imagePath, number, offset, length);
            session.Start();
            return session;
        }

        private void Start()
        {
            ImageIdentity current;
            using (var file = FileBlockDevice.OpenRead(ImagePath))
            using (var part = new PartitionView(file, _offset, _length))
            {
                using (var reader = new Ext4Reader(part))
                {
                    var support = Ext4EditSupportCheck.Check(reader.Superblock);
                    if (!support.Editable)
                    {
                        throw new EditRefusedException(MapRefusal(support.Reason), string.Join(", ", support.Features));
                    }
                }

                current = ImageIdentity.Of(part, new FileInfo(ImagePath).Length, PartitionNumber, _offset);
            }

            try
            {
                _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                throw new EditRefusedException(EditRefused.InUseByAnotherInstance);
            }

            _identity = current;
            var redo = RedoLog.Read(_redoPath);
            if (redo != null)
            {
                State = SessionState.Interrupted;
                return;
            }

            var diff = DiffFile.Read(_diffPath, out var status);
            DiffStatus = status;
            if (diff != null)
            {
                _changes.AddRange(diff.Changes);
                if (!diff.Identity.Matches(current))
                {
                    DiffStatus = DiffStatus.ImageChanged;
                }
            }
        }

        internal static EditRefused MapRefusal(EditRefusal reason)
        {
            switch (reason)
            {
                case EditRefusal.NeedsRecovery: return EditRefused.NeedsRecovery;
                case EditRefusal.HasOrphans: return EditRefused.HasOrphans;
                case EditRefusal.HasErrors: return EditRefused.HasErrors;
                case EditRefusal.NotClean: return EditRefused.NotClean;
                default: return EditRefused.UnsupportedFeature;
            }
        }

        /// <summary>The changes cannot be saved as they are (a diff of another version of the image); only Discard works.</summary>
        public bool IsStale => DiffStatus == DiffStatus.ImageChanged || DiffStatus == DiffStatus.Corrupt || DiffStatus == DiffStatus.Unsupported;

        /// <summary>Records a change in the diff file; the image is not touched.</summary>
        public void Add(EditChange change)
        {
            if (State != SessionState.Editing || IsStale)
            {
                throw new InvalidOperationException("The session cannot take changes now.");
            }

            _changes.Add(change);
            try
            {
                Persist();
            }
            catch
            {
                _changes.Remove(change);
                throw;
            }
        }

        /// <summary>Replaces all changes (for example after the user undid the last one in the UI).</summary>
        public void Replace(IEnumerable<EditChange> changes)
        {
            var keep = _changes.ToList();
            _changes.Clear();
            _changes.AddRange(changes);
            try
            {
                Persist();
            }
            catch
            {
                _changes.Clear();
                _changes.AddRange(keep);
                throw;
            }
        }

        private void Persist()
        {
            if (_changes.Count == 0)
            {
                if (File.Exists(_diffPath))
                {
                    File.Delete(_diffPath);
                }

                return;
            }

            new DiffFile(_identity, _changes).Write(_diffPath);
        }

        /// <summary>Drops every waiting change and the diff file; the image is not touched.</summary>
        public void Discard()
        {
            _changes.Clear();
            DiffStatus = DiffStatus.None;
            if (File.Exists(_diffPath))
            {
                File.Delete(_diffPath);
            }
        }

        /// <summary>
        /// Writes the waiting changes into the image: builds the write set, checks it, commits it to the redo log,
        /// replays it, removes the redo and diff files, then updates the checksum files.
        /// </summary>
        private SaveResult SaveCore(Action<SavePhase, long, long> progress, CancellationToken cancellationToken)
        {
            if (State != SessionState.Editing || IsStale || _changes.Count == 0)
            {
                return new SaveResult { Status = SaveStatus.Refused, Detail = IsStale ? "stale" : "nothing to save" };
            }

            WriteSet set;
            progress?.Invoke(SavePhase.Prepare, 0, 1);
            try
            {
                using (var file = FileBlockDevice.OpenReadWrite(ImagePath))
                using (var part = new PartitionView(file, _offset, _length))
                {
                    var now = ImageIdentity.Of(part, new FileInfo(ImagePath).Length, PartitionNumber, _offset);
                    if (!now.Matches(_identity))
                    {
                        return new SaveResult { Status = SaveStatus.Refused, Error = EditError.EntryChanged, Detail = "image changed" };
                    }

                    var editor = new Ext4Editor(part);
                    foreach (var change in _changes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        EditChange.Apply(editor, change);
                    }

                    set = editor.Writes;
                    var findings = EditPreflight.Check(set, part);
                    if (findings.Count > 0)
                    {
                        return new SaveResult { Status = SaveStatus.Refused, Error = EditError.Internal, Detail = findings[0] };
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    new RedoLog(now, set).Write(_redoPath);
                    State = SessionState.Interrupted;
                    return Replay(part, set, _changes.Count, progress, cancellationToken);
                }
            }
            catch (EditException ex)
            {
                return new SaveResult { Status = SaveStatus.Refused, Error = ex.Code, Detail = ex.Detail, Needed = ex.Needed, Available = ex.Available };
            }
            catch (OperationCanceledException)
            {
                return new SaveResult { Status = State == SessionState.Interrupted ? SaveStatus.Interrupted : SaveStatus.Failed };
            }
            catch (ImagingException ex)
            {
                return new SaveResult { Status = SaveStatus.Interrupted, ErrorCode = ex.Code, Offset = ex.Offset, Detail = ex.Detail };
            }
            catch (IOException ex)
            {
                return new SaveResult { Status = State == SessionState.Interrupted ? SaveStatus.Interrupted : SaveStatus.Failed, Detail = ex.Message };
            }
        }

        /// <summary>Completes a save that was interrupted after its redo log was written.</summary>
        private SaveResult FinishCore(Action<SavePhase, long, long> progress, CancellationToken cancellationToken)
        {
            var redo = RedoLog.Read(_redoPath);
            if (redo == null)
            {
                State = SessionState.Editing;
                return new SaveResult { Status = SaveStatus.Refused, Detail = "no interrupted save" };
            }

            try
            {
                using (var file = FileBlockDevice.OpenReadWrite(ImagePath))
                using (var part = new PartitionView(file, _offset, _length))
                {
                    return Replay(part, redo.Writes, _changes.Count, progress, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return new SaveResult { Status = SaveStatus.Interrupted };
            }
            catch (ImagingException ex)
            {
                return new SaveResult { Status = SaveStatus.Interrupted, ErrorCode = ex.Code, Offset = ex.Offset, Detail = ex.Detail };
            }
            catch (IOException ex)
            {
                return new SaveResult { Status = SaveStatus.Interrupted, Detail = ex.Message };
            }
        }

        public SaveResult Save(Action<SavePhase, long, long> progress, CancellationToken cancellationToken) =>
            Complete(SaveCore(progress, cancellationToken), progress, cancellationToken);

        public SaveResult FinishSave(Action<SavePhase, long, long> progress, CancellationToken cancellationToken) =>
            Complete(FinishCore(progress, cancellationToken), progress, cancellationToken);

        private SaveResult Complete(SaveResult result, Action<SavePhase, long, long> progress, CancellationToken ct)
        {
            if (result.Status != SaveStatus.Saved)
            {
                return result;
            }

            try
            {
                result.ChecksumFiles = SaveChecksums.Update(ImagePath, (done, all) => progress?.Invoke(SavePhase.Checksum, done, all), ct);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OperationCanceledException)
            {
                result.Status = SaveStatus.SavedChecksumStale;
                result.Detail = ex.Message;
            }

            return result;
        }

        private SaveResult Replay(IBlockDevice part, WriteSet set, int changes, Action<SavePhase, long, long> progress, CancellationToken ct)
        {
            long total = Math.Max(1, set.TotalBytes);
            set.ApplyTo(part, done => progress?.Invoke(SavePhase.Write, done, total), ct);
            File.Delete(_redoPath);
            if (File.Exists(_diffPath))
            {
                File.Delete(_diffPath);
            }

            _changes.Clear();
            State = SessionState.Editing;
            DiffStatus = DiffStatus.None;
            _identity = null;
            return new SaveResult { Status = SaveStatus.Saved, ChangesWritten = changes };
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    public enum SavePhase
    {
        Prepare,
        Write,
        Checksum
    }

    /// <summary>Recalculates or creates the checksum file beside an image (research R-11); never touches a compressed twin's file.</summary>
    internal static class SaveChecksums
    {
        public static IReadOnlyList<string> Update(string imagePath, Action<long, long> progress, CancellationToken ct)
        {
            string sidecar = ImageDescriptor.FileSidecarPath(imagePath);
            string hash = Hash(imagePath, progress, ct);
            ChecksumFile.Write(sidecar, hash, Path.GetFileName(imagePath));
            return new[] { sidecar };
        }

        public static string Hash(string path, Action<long, long> progress, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                var buffer = new byte[1 << 20];
                long total = f.Length;
                long done = 0;
                int n;
                while ((n = f.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    done += n;
                    progress?.Invoke(done, total);
                }

                sha.TransformFinalBlock(buffer, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>Checks the file system as it would be after the writes, with the product's own checker (research R-7).</summary>
    internal static class EditPreflight
    {
        public static List<string> Check(WriteSet set, IBlockDevice partition)
        {
            var overlay = new RecordingBlockDevice(partition);
            foreach (var w in set.Writes)
            {
                overlay.Write(w.Offset, w.Data, 0, w.Data.Length);
            }

            return Ext4Checker.Check(overlay);
        }
    }
}
