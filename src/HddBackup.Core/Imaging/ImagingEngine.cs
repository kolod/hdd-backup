using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Progress;

namespace HddBackup.Core.Imaging
{
    public interface IImagingEngine
    {
        /// <summary>
        /// Checks the request synchronously. Throws <see cref="ImagingException"/> on a violation
        /// (nothing is touched); returns warnings the user should acknowledge.
        /// </summary>
        IReadOnlyList<ValidationWarning> Validate(OperationRequest request);

        /// <summary>
        /// Runs the operation on background threads. Expected failures are reported through
        /// <see cref="OperationResult.ErrorCode"/>; the task does not fault for them.
        /// </summary>
        Task<OperationResult> RunAsync(OperationRequest request, CancellationToken cancellationToken);

        /// <summary>Thread-safe snapshot; the UI polls it.</summary>
        OperationProgress CurrentProgress { get; }
    }

    /// <summary>
    /// Backup, restore and image verification. One instance runs one operation.
    /// Progress reaches 100% only after data is committed, verified (if requested), the device
    /// is released and, if requested, ejected.
    /// </summary>
    public sealed class ImagingEngine : IImagingEngine
    {
        public const int DefaultBufferSize = 4 << 20;
        public const int DefaultBufferCount = 4;
        private const string PartialSuffix = ".partial";

        private readonly IDeviceAccess _access;
        private readonly int _bufferSize;
        private readonly int _bufferCount;
        private PhaseModel _model;
        private int _started;

        public ImagingEngine(IDeviceAccess access, int bufferSize = DefaultBufferSize, int bufferCount = DefaultBufferCount)
        {
            _access = access ?? throw new ArgumentNullException(nameof(access));
            if (bufferSize <= 0 || bufferSize % 4096 != 0)
            {
                throw new ArgumentException("Buffer size must be a positive multiple of 4096.", nameof(bufferSize));
            }

            _bufferSize = bufferSize;
            _bufferCount = Math.Max(2, bufferCount);
        }

        public OperationProgress CurrentProgress => Volatile.Read(ref _model)?.Snapshot() ?? OperationProgress.Idle;

        public IReadOnlyList<ValidationWarning> Validate(OperationRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.IsBackup)
            {
                return DestinationChecks.Check(request, _access.GetPathInfo(request.ImagePath));
            }

            if (request.Kind == OperationKind.ShrinkImage)
            {
                return ValidateShrink(request);
            }

            if (request.Kind == OperationKind.ConvertImage)
            {
                return ValidateConvert(request, ImageDescriptor.Load(request.ImagePath));
            }

            var image = ImageDescriptor.Load(request.ImagePath);
            return request.IsRestore
                ? RestoreGuard.Check(request, image, _access.GetPathInfo(request.ImagePath))
                : image.Warnings;
        }

        public Task<OperationResult> RunAsync(OperationRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("An engine instance runs a single operation.");
            }

            var model = request.Kind == OperationKind.ShrinkImage
                ? new PhaseModel(ShrinkShares(request))
                : new PhaseModel(request.Kind == OperationKind.ConvertImage || (request.VerifyAfter && request.Kind != OperationKind.VerifyImage));
            Volatile.Write(ref _model, model);
            return Task.Factory.StartNew(
                () => Run(request, model, cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        /// <summary>Writes checksum sidecars for an existing image (offered after verification).</summary>
        public static void SaveChecksumFiles(string imagePath, bool isGzip, string fileSha256, string contentSha256)
        {
            var partials = new List<string>
            {
                WritePartialChecksum(ImageDescriptor.FileSidecarPath(imagePath), fileSha256, Path.GetFileName(imagePath))
            };
            if (isGzip)
            {
                partials.Add(WritePartialChecksum(
                    ImageDescriptor.ContentSidecarPath(imagePath), contentSha256, ImageDescriptor.ContentFileName(imagePath)));
            }

            CommitFiles(partials);
        }

        private OperationResult Run(OperationRequest request, PhaseModel model, CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new OperationResult { Kind = request.Kind, ImagePath = request.ImagePath };
            model.SetState(OperationState.Preparing);
            model.BeginPhase(OperationPhase.Preparing, 0, null);
            try
            {
                if (request.IsBackup)
                {
                    RunBackup(request, model, result, ct);
                }
                else if (request.Kind == OperationKind.ShrinkImage)
                {
                    RunShrink(request, model, result, ct);
                }
                else if (request.Kind == OperationKind.ConvertImage)
                {
                    RunConvert(request, model, result, ct);
                }
                else if (request.IsRestore)
                {
                    RunRestore(request, model, result, ct);
                }
                else
                {
                    RunVerify(request, model, result, ct);
                }
            }
            catch (Exception ex)
            {
                // Only reachable through programming errors; never leave the UI without a result.
                result.State = OperationState.Failed;
                result.ErrorCode = ErrorCode.Unexpected;
                result.ErrorDetail = ex.ToString();
                result.ContentComplete = false;
            }

            result.Duration = stopwatch.Elapsed;
            model.SetState(result.State);
            return result;
        }

        private void RunBackup(OperationRequest request, PhaseModel model, OperationResult result, CancellationToken ct)
        {
            IBlockDevice device = null;
            ImageWriter writer = null;
            var partials = new List<string>();
            bool committed = false;
            string path = request.ImagePath;
            try
            {
                DestinationChecks.Check(request, _access.GetPathInfo(path));
                ct.ThrowIfCancellationRequested();

                device = _access.OpenForRead(request.Device);
                if (device.Size != request.Device.Size)
                {
                    throw new ImagingException(ErrorCode.DeviceChanged, "The device size changed since it was listed.");
                }

                var partition = request.Partition;
                IBlockDevice source = request.Kind == OperationKind.BackupPartition
                    ? new PartitionView(device, partition.Offset, partition.Length)
                    : device;
                long baseOffset = request.Kind == OperationKind.BackupPartition ? partition.Offset : 0;
                bool gzip = ImageDescriptor.IsGzipPath(path);
                result.IsGzipImage = gzip;
                result.ImageIncomplete = request.Device.IsCapacityMisreported && request.Kind == OperationKind.BackupDevice;
                writer = new ImageWriter(
                    path, gzip, request.Compression == CompressionChoice.Fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal);

                using (var content = new Sha256Accumulator())
                {
                    long total = source.Size;
                    long next = 0;
                    model.SetState(OperationState.Running);
                    model.BeginPhase(OperationPhase.Transferring, total, total);
                    var sink = writer;
                    BufferPipeline.Run(
                        _bufferSize,
                        _bufferCount,
                        chunk =>
                        {
                            if (next >= total)
                            {
                                return false;
                            }

                            int n = (int)Math.Min(chunk.Buffer.Length, total - next);
                            source.Read(next, chunk.Buffer, 0, n);
                            chunk.Offset = next;
                            chunk.Count = n;
                            next += n;
                            return true;
                        },
                        new Action<Chunk>[]
                        {
                            c => content.Append(c.Buffer, 0, c.Count),
                            c =>
                            {
                                sink.Write(c.Buffer, 0, c.Count, baseOffset + c.Offset);
                                model.Advance(c.Count, c.Count);
                            }
                        },
                        ct);

                    result.DataBytes = total;
                    model.SetState(OperationState.Finalizing);
                    model.BeginPhase(OperationPhase.Flushing, 0, null);
                    writer.Complete();
                    result.ContentSha256 = content.Finish();
                }

                result.FileSha256 = gzip ? writer.FileHash : result.ContentSha256;
                partials.Add(WritePartialChecksum(ImageDescriptor.FileSidecarPath(path), result.FileSha256, Path.GetFileName(path)));
                if (gzip)
                {
                    partials.Add(WritePartialChecksum(
                        ImageDescriptor.ContentSidecarPath(path), result.ContentSha256, ImageDescriptor.ContentFileName(path)));
                }

                if (request.Kind == OperationKind.BackupPartition)
                {
                    string metadataPartial = PartitionMetadataFile.PathFor(path) + PartialSuffix;
                    partials.Add(metadataPartial);
                    PartitionMetadataFile.Write(metadataPartial, BuildMetadata(request, result.ContentSha256));
                }

                ct.ThrowIfCancellationRequested();
                if (request.VerifyAfter)
                {
                    var progress = new ImageProgress(writer.FileLength, result.DataBytes);
                    model.BeginPhase(OperationPhase.Verifying, progress.Total, result.DataBytes);
                    VerifyWrittenImage(writer.PartialPath, result, model, progress, ct);
                }

                ct.ThrowIfCancellationRequested();
                partials.Add(writer.PartialPath); // the image itself is renamed last
                CommitFiles(partials);
                committed = true;
                result.ContentComplete = true;

                model.BeginPhase(OperationPhase.Releasing, 0, null);
                device.Dispose();
                device = null;

                if (request.EjectAfter)
                {
                    model.BeginPhase(OperationPhase.Ejecting, 0, null);
                    Eject(request.Device, result);
                }

                result.SafeToRemove = true;
                result.State = OperationState.Completed;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                RecordFailure(result, ex);
            }
            finally
            {
                if (!committed)
                {
                    writer?.Abort();
                    foreach (var partial in partials)
                    {
                        TryDelete(partial);
                    }
                }

                device?.Dispose();
                if (result.State != OperationState.Completed)
                {
                    // The source was opened read-only: once closed it is safe unless it vanished.
                    result.SafeToRemove = result.ErrorCode != ErrorCode.DeviceRemoved;
                }
            }
        }

        private void RunRestore(OperationRequest request, PhaseModel model, OperationResult result, CancellationToken ct)
        {
            IExclusiveDevice exclusive = null;
            ImageReader reader = null;
            bool wrote = false;
            string path = request.ImagePath;
            try
            {
                var image = ImageDescriptor.Load(path);
                RestoreGuard.Check(request, image, _access.GetPathInfo(path));
                result.IsGzipImage = image.Format == ImageFormat.Gzip;
                ct.ThrowIfCancellationRequested();

                reader = new ImageReader(path);
                exclusive = _access.OpenExclusiveForWrite(request.Device);
                var device = exclusive.Device;
                if (device.Size != request.Device.Size)
                {
                    throw new ImagingException(ErrorCode.DeviceChanged, "The device size changed since it was listed.");
                }

                var partition = request.Partition;
                bool isPartition = request.Kind == OperationKind.RestorePartition;
                IBlockDevice target = isPartition ? new PartitionView(device, partition.Offset, partition.Length) : device;
                long baseOffset = isPartition ? partition.Offset : 0;
                int sector = target.SectorSize;
                long next = 0;

                using (var content = new Sha256Accumulator())
                {
                    model.SetState(OperationState.Running);
                    var progress = new ImageProgress(reader.FileLength, image.ProgressSize);
                    model.BeginPhase(OperationPhase.Transferring, progress.Total, image.DataSize);
                    var source = reader;
                    BufferPipeline.Run(
                        _bufferSize,
                        _bufferCount,
                        chunk =>
                        {
                            int n = source.ReadFull(chunk.Buffer, 0, chunk.Buffer.Length);
                            if (n == 0)
                            {
                                return false;
                            }

                            int aligned = RoundUp(n, sector);
                            if (aligned > n)
                            {
                                Array.Clear(chunk.Buffer, n, aligned - n);
                            }

                            if (next + aligned > target.Size)
                            {
                                throw new ImagingException(ErrorCode.SizeMismatch, "The image is larger than the target.", baseOffset + next);
                            }

                            chunk.Offset = next;
                            chunk.Count = n;
                            chunk.AlignedCount = aligned;
                            chunk.Units = progress.Units(source.FilePosition, next + n);
                            next += n;
                            return true;
                        },
                        new Action<Chunk>[]
                        {
                            c => content.Append(c.Buffer, 0, c.Count),
                            c =>
                            {
                                wrote = true;
                                target.Write(c.Offset, c.Buffer, 0, c.AlignedCount);
                                model.SetUnits(c.Units, c.Offset + c.Count);
                            }
                        },
                        ct);

                    result.DataBytes = next;
                    model.SetState(OperationState.Finalizing);
                    model.BeginPhase(OperationPhase.Flushing, 0, null);
                    target.Flush();
                    result.ContentSha256 = content.Finish();
                }

                result.FileSha256 = reader.FinishFileHash();
                reader.Dispose();
                reader = null;

                result.Verifications = CompareChecksums(image, result);
                if (result.Verifications.Any(v => v.Status == VerificationStatus.Mismatch))
                {
                    throw new ImagingException(ErrorCode.ChecksumMismatch, "The image does not match its stored checksum.");
                }

                if (request.VerifyAfter)
                {
                    model.BeginPhase(OperationPhase.Verifying, next, next);
                    VerifyDevice(path, target, baseOffset, model, ct);
                }

                model.BeginPhase(OperationPhase.Releasing, 0, null);
                exclusive.Dispose();
                exclusive = null;
                result.ContentComplete = true;

                if (request.EjectAfter)
                {
                    model.BeginPhase(OperationPhase.Ejecting, 0, null);
                    Eject(request.Device, result);
                }

                result.SafeToRemove = true;
                result.State = OperationState.Completed;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                RecordFailure(result, ex);
            }
            finally
            {
                reader?.Dispose();
                if (exclusive != null)
                {
                    bool flushed = true;
                    if (wrote)
                    {
                        try
                        {
                            exclusive.Device.Flush();
                        }
                        catch (Exception)
                        {
                            flushed = false;
                        }
                    }

                    try
                    {
                        exclusive.Dispose();
                    }
                    catch (Exception)
                    {
                        flushed = false;
                    }

                    result.SafeToRemove = flushed && result.ErrorCode != ErrorCode.DeviceRemoved;
                }
                else if (result.State != OperationState.Completed)
                {
                    result.SafeToRemove = result.ErrorCode != ErrorCode.DeviceRemoved;
                }
            }
        }

        private void RunVerify(OperationRequest request, PhaseModel model, OperationResult result, CancellationToken ct)
        {
            try
            {
                var image = ImageDescriptor.Load(request.ImagePath);
                result.IsGzipImage = image.Format == ImageFormat.Gzip;
                using (var reader = new ImageReader(request.ImagePath))
                using (var content = new Sha256Accumulator())
                {
                    model.SetState(OperationState.Running);
                    var progress = new ImageProgress(reader.FileLength, image.ProgressSize);
                    model.BeginPhase(OperationPhase.Verifying, progress.Total, image.DataSize);
                    BufferPipeline.Run(
                        _bufferSize,
                        _bufferCount,
                        chunk =>
                        {
                            int n = reader.ReadFull(chunk.Buffer, 0, chunk.Buffer.Length);
                            if (n == 0)
                            {
                                return false;
                            }

                            chunk.Offset = reader.Position - n;
                            chunk.Count = n;
                            chunk.Units = progress.Units(reader.FilePosition, reader.Position);
                            return true;
                        },
                        new Action<Chunk>[]
                        {
                            c =>
                            {
                                content.Append(c.Buffer, 0, c.Count);
                                model.SetUnits(c.Units, c.Offset + c.Count);
                            }
                        },
                        ct);

                    model.SetState(OperationState.Finalizing);
                    result.DataBytes = reader.Position;
                    result.ContentSha256 = content.Finish();
                    result.FileSha256 = reader.FinishFileHash();
                }

                result.Verifications = CompareChecksums(image, result);
                if (result.Verifications.Any(v => v.Status == VerificationStatus.Mismatch))
                {
                    throw new ImagingException(ErrorCode.ChecksumMismatch, "The image does not match its stored checksum.");
                }

                if (request.SaveMissingChecksums)
                {
                    ct.ThrowIfCancellationRequested();
                    result.SavedChecksumFiles = SaveMissingChecksumFiles(image, result);
                }

                result.ContentComplete = true;
                result.SafeToRemove = true;
                result.State = OperationState.Completed;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                RecordFailure(result, ex);
                result.SafeToRemove = true;
            }
        }

        /// <summary>Writes the checksum files an image does not have yet; returns their file names.</summary>
        private static IReadOnlyList<string> SaveMissingChecksumFiles(ImageDescriptor image, OperationResult result)
        {
            var partials = new List<string>();
            var saved = new List<string>();
            string path = image.Path;
            if (image.FileChecksum == null)
            {
                partials.Add(WritePartialChecksum(ImageDescriptor.FileSidecarPath(path), result.FileSha256, Path.GetFileName(path)));
                saved.Add(Path.GetFileName(ImageDescriptor.FileSidecarPath(path)));
            }

            if (image.Format == ImageFormat.Gzip && image.ContentChecksum == null)
            {
                partials.Add(WritePartialChecksum(ImageDescriptor.ContentSidecarPath(path), result.ContentSha256, ImageDescriptor.ContentFileName(path)));
                saved.Add(Path.GetFileName(ImageDescriptor.ContentSidecarPath(path)));
            }

            try
            {
                CommitFiles(partials);
            }
            catch (Exception)
            {
                foreach (var partial in partials)
                {
                    TryDelete(partial);
                }

                throw;
            }

            return saved;
        }

        private IReadOnlyList<ValidationWarning> ValidateConvert(OperationRequest request, ImageDescriptor image)
        {
            if (string.IsNullOrWhiteSpace(request.OutputPath))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No output file selected.");
            }

            if (FileIdentity.IsSameFile(request.OutputPath, request.ImagePath))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "The output is the source image.");
            }

            bool gzip = ImageDescriptor.IsGzipPath(request.OutputPath);
            if (gzip == (image.Format == ImageFormat.Gzip))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "The output has the same format as the source.");
            }

            long? size = gzip ? null : image.DataSize ?? image.ProgressSize;
            return DestinationChecks.CheckOutputFile(request.OutputPath, size, _access.GetPathInfo(request.OutputPath));
        }

        /// <summary>
        /// ConvertImage: copies the content of an image into the other format. The source is checked
        /// against its stored checksums; the output gets the sidecars of a backup in its format, is
        /// verified by reading it back and is committed only then. The source is never written.
        /// </summary>
        private void RunConvert(OperationRequest request, PhaseModel model, OperationResult result, CancellationToken ct)
        {
            ImageWriter writer = null;
            var partials = new List<string>();
            var sidecars = new List<string>();
            bool committed = false;
            result.SafeToRemove = true; // no device is involved
            try
            {
                var image = ImageDescriptor.Load(request.ImagePath);
                ValidateConvert(request, image);
                ct.ThrowIfCancellationRequested();

                string output = request.OutputPath;
                bool gzip = ImageDescriptor.IsGzipPath(output);
                result.IsGzipImage = gzip;
                using (var reader = new ImageReader(request.ImagePath))
                using (var content = new Sha256Accumulator())
                {
                    writer = new ImageWriter(
                        output, gzip, request.Compression == CompressionChoice.Fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal);
                    var progress = new ImageProgress(reader.FileLength, image.ProgressSize);
                    model.SetState(OperationState.Running);
                    model.BeginPhase(OperationPhase.Transferring, progress.Total, image.DataSize);
                    var sink = writer;
                    BufferPipeline.Run(
                        _bufferSize,
                        _bufferCount,
                        chunk =>
                        {
                            int n = reader.ReadFull(chunk.Buffer, 0, chunk.Buffer.Length);
                            if (n == 0)
                            {
                                return false;
                            }

                            chunk.Offset = reader.Position - n;
                            chunk.Count = n;
                            chunk.Units = progress.Units(reader.FilePosition, reader.Position);
                            return true;
                        },
                        new Action<Chunk>[]
                        {
                            c => content.Append(c.Buffer, 0, c.Count),
                            c =>
                            {
                                sink.Write(c.Buffer, 0, c.Count, c.Offset);
                                model.SetUnits(c.Units, c.Offset + c.Count);
                            }
                        },
                        ct);

                    result.DataBytes = reader.Position;
                    result.ContentSha256 = content.Finish();
                    result.FileSha256 = reader.FinishFileHash();
                }

                // A corrupt source must not become a new image with fresh, matching checksums.
                result.Verifications = CompareChecksums(image, result);
                if (result.Verifications.Any(v => v.Status == VerificationStatus.Mismatch))
                {
                    throw new ImagingException(ErrorCode.ChecksumMismatch, "The image does not match its stored checksum.");
                }

                model.SetState(OperationState.Finalizing);
                model.BeginPhase(OperationPhase.Flushing, 0, null);
                writer.Complete();
                result.FileSha256 = gzip ? writer.FileHash : result.ContentSha256;

                if (gzip)
                {
                    AddSidecar(partials, sidecars, ImageDescriptor.FileSidecarPath(output), result.FileSha256, Path.GetFileName(output));
                    AddSidecar(partials, sidecars, ImageDescriptor.ContentSidecarPath(output), result.ContentSha256, ImageDescriptor.ContentFileName(output));
                }
                else
                {
                    AddSidecar(partials, sidecars, ImageDescriptor.FileSidecarPath(output), result.ContentSha256, Path.GetFileName(output));
                }

                if (image.Kind == ImageKind.Partition)
                {
                    string metadataPartial = PartitionMetadataFile.PathFor(output) + PartialSuffix;
                    partials.Add(metadataPartial);
                    File.Copy(PartitionMetadataFile.PathFor(request.ImagePath), metadataPartial, true);
                }

                ct.ThrowIfCancellationRequested();
                model.BeginPhase(OperationPhase.Verifying, writer.FileLength, result.DataBytes);
                VerifyWrittenImage(writer.PartialPath, result, model, new ImageProgress(writer.FileLength, result.DataBytes), ct);

                ct.ThrowIfCancellationRequested();
                partials.Add(writer.PartialPath); // the image itself is renamed last
                CommitFiles(partials);
                committed = true;
                result.OutputPath = output;
                result.SavedChecksumFiles = sidecars;
                result.ContentComplete = true;
                result.State = OperationState.Completed;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                RecordFailure(result, ex);
            }
            finally
            {
                if (!committed)
                {
                    writer?.Abort();
                    foreach (var partial in partials)
                    {
                        TryDelete(partial);
                    }
                }

                writer?.Dispose();
            }
        }

        /// <summary>
        /// Adds a checksum sidecar as a partial file, unless the same checksum for the same name is
        /// already there (a .img and the .img.gz of the same content share the content sidecar).
        /// </summary>
        private static void AddSidecar(List<string> partials, List<string> saved, string path, string sha256, string label)
        {
            if (File.Exists(path))
            {
                try
                {
                    var existing = ChecksumFile.Read(path, ChecksumKind.File);
                    if (string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Label, label, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException)
                {
                    // Unreadable: it is replaced.
                }
            }

            partials.Add(WritePartialChecksum(path, sha256, label));
            saved.Add(Path.GetFileName(path));
        }

        private IReadOnlyList<ValidationWarning> ValidateShrink(OperationRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ShrinkOutputPath))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "No output file selected.");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(request.ShrinkOutputPath));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Output folder does not exist.");
            }

            var plan = Shrinking.ShrinkPlanner.Analyze(request.ImagePath, CancellationToken.None);
            if (!plan.CanShrink(request.RepairFileSystem))
            {
                throw Shrinking.ImageShrinker.Blocked(plan.Blockers);
            }

            if (request.ShrinkTargetBytes.HasValue && request.ShrinkTargetBytes.Value < plan.MinimumImageBytes)
            {
                throw Shrinking.ImageShrinker.Blocked(new[] { new Shrinking.ShrinkBlockerInfo(Shrinking.ShrinkBlocker.NotEnoughSpaceEvenAtMinimum, plan.MinimumImageBytes + " bytes needed") });
            }

            return Array.Empty<ValidationWarning>();
        }

        private static IDictionary<OperationPhase, double> ShrinkShares(OperationRequest request)
        {
            bool gzipSource = request.ImagePath != null && ImageDescriptor.IsGzipPath(request.ImagePath);
            return new Dictionary<OperationPhase, double>
            {
                { OperationPhase.Preparing, gzipSource ? 0.15 : 0.01 },
                { OperationPhase.Transferring, gzipSource ? 0.45 : 0.55 },
                { OperationPhase.Verifying, 0.25 },
                { OperationPhase.Flushing, gzipSource ? 0.15 : 0.19 }
            };
        }

        private void RunShrink(OperationRequest request, PhaseModel model, OperationResult result, CancellationToken ct)
        {
            try
            {
                ValidateShrink(request);
                var outcome = Shrinking.ImageShrinker.Run(
                    new Shrinking.ShrinkRequest
                    {
                        SourcePath = request.ImagePath,
                        OutputPath = request.ShrinkOutputPath,
                        TargetImageBytes = request.ShrinkTargetBytes,
                        Compression = request.Compression == CompressionChoice.Fastest ? CompressionLevel.Fastest : CompressionLevel.Optimal,
                        FreeBytes = _access.GetPathInfo(request.ShrinkOutputPath).FreeBytes,
                        Repair = request.RepairFileSystem
                    },
                    model,
                    ct);

                result.ImagePath = outcome.OutputPath;
                result.IsGzipImage = outcome.IsGzip;
                result.ContentSha256 = outcome.ContentSha256;
                result.FileSha256 = outcome.FileSha256;
                result.DataBytes = outcome.ImageBytes;
                result.Repaired = outcome.Repaired;
                result.RepairJournalTransactions = outcome.JournalTransactions;
                result.RepairOrphansRemoved = outcome.OrphansRemoved;
                result.ContentComplete = true;
                result.SafeToRemove = true;
                result.State = OperationState.Completed;
            }
            catch (Exception ex) when (IsExpected(ex) || ex is Ext4.Ext4FormatException)
            {
                if (ex is Ext4.Ext4FormatException format)
                {
                    ex = Shrinking.ImageShrinker.Blocked(new[] { new Shrinking.ShrinkBlockerInfo(Shrinking.ShrinkBlocker.ChecksumMismatch, format.Message) });
                }

                RecordFailure(result, ex);
                result.SafeToRemove = true;
            }
        }

        private void VerifyWrittenImage(string partialPath, OperationResult result, PhaseModel model, ImageProgress progress, CancellationToken ct)
        {
            using (var reader = new ImageReader(partialPath))
            using (var content = new Sha256Accumulator())
            {
                BufferPipeline.Run(
                    _bufferSize,
                    _bufferCount,
                    chunk =>
                    {
                        int n = reader.ReadFull(chunk.Buffer, 0, chunk.Buffer.Length);
                        if (n == 0)
                        {
                            return false;
                        }

                        chunk.Offset = reader.Position - n;
                        chunk.Count = n;
                        chunk.Units = progress.Units(reader.FilePosition, reader.Position);
                        return true;
                    },
                    new Action<Chunk>[]
                    {
                        c =>
                        {
                            content.Append(c.Buffer, 0, c.Count);
                            model.SetUnits(c.Units, c.Offset + c.Count);
                        }
                    },
                    ct);

                if (content.Finish() != result.ContentSha256 || reader.FinishFileHash() != result.FileSha256)
                {
                    throw new ImagingException(ErrorCode.VerifyMismatch, "The written image file reads back differently.");
                }
            }
        }

        private void VerifyDevice(string imagePath, IBlockDevice target, long baseOffset, PhaseModel model, CancellationToken ct)
        {
            int sector = target.SectorSize;
            var deviceData = new byte[_bufferSize];
            using (var reader = new ImageReader(imagePath))
            {
                BufferPipeline.Run(
                    _bufferSize,
                    _bufferCount,
                    chunk =>
                    {
                        int n = reader.ReadFull(chunk.Buffer, 0, chunk.Buffer.Length);
                        if (n == 0)
                        {
                            return false;
                        }

                        chunk.Offset = reader.Position - n;
                        chunk.Count = n;
                        chunk.AlignedCount = RoundUp(n, sector);
                        return true;
                    },
                    new Action<Chunk>[]
                    {
                        c =>
                        {
                            target.Read(c.Offset, deviceData, 0, c.AlignedCount);
                            int diff = FirstDifference(c.Buffer, deviceData, c.Count);
                            if (diff >= 0)
                            {
                                throw new ImagingException(
                                    ErrorCode.VerifyMismatch, "The device content differs from the image.", baseOffset + c.Offset + diff);
                            }

                            model.SetUnits(c.Offset + c.Count, c.Offset + c.Count);
                        }
                    },
                    ct);
            }
        }

        private static IReadOnlyList<VerificationResult> CompareChecksums(ImageDescriptor image, OperationResult result)
        {
            var list = new List<VerificationResult>
            {
                VerificationResult.Compare(ChecksumKind.File, image.FileChecksum, result.FileSha256)
            };

            if (image.Format == ImageFormat.Gzip)
            {
                var content = image.ContentChecksum;
                if (content == null && !string.IsNullOrEmpty(image.PartitionMetadata?.Sha256))
                {
                    content = new ChecksumFile(
                        PartitionMetadataFile.PathFor(image.Path), ChecksumKind.Content, image.PartitionMetadata.Sha256.ToLowerInvariant(), string.Empty);
                }

                list.Add(VerificationResult.Compare(ChecksumKind.Content, content, result.ContentSha256));
            }
            else if (image.FileChecksum == null && !string.IsNullOrEmpty(image.PartitionMetadata?.Sha256))
            {
                list[0] = VerificationResult.Compare(
                    ChecksumKind.File,
                    new ChecksumFile(PartitionMetadataFile.PathFor(image.Path), ChecksumKind.File, image.PartitionMetadata.Sha256.ToLowerInvariant(), string.Empty),
                    result.FileSha256);
            }

            return list;
        }

        private static PartitionMetadata BuildMetadata(OperationRequest request, string contentSha256)
        {
            var p = request.Partition;
            return new PartitionMetadata
            {
                TableStyle = request.Device.PartitionTable.Style.ToString().ToUpperInvariant(),
                PartitionNumber = p.Number,
                Offset = p.Offset,
                Length = p.Length,
                SectorSize = request.Device.LogicalSectorSize,
                MbrType = p.MbrType,
                GptType = p.GptType?.ToString("D").ToUpperInvariant(),
                GptId = p.GptId?.ToString("D").ToUpperInvariant(),
                Name = p.Name,
                FileSystem = p.FileSystem,
                SourceModel = request.Device.Model,
                SourceSize = request.Device.Size,
                Sha256 = contentSha256
            };
        }

        private void Eject(DeviceInfo device, OperationResult result)
        {
            try
            {
                var eject = _access.Eject(device);
                result.Ejected = eject.Status == EjectStatus.Ok;
                if (eject.Status == EjectStatus.Vetoed)
                {
                    result.EjectVetoedBy = eject.VetoedBy ?? string.Empty;
                }
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Data is already committed and the handle closed; removal is still safe.
                result.Ejected = false;
            }
        }

        private static string WritePartialChecksum(string finalPath, string sha256, string label)
        {
            string partial = finalPath + PartialSuffix;
            ChecksumFile.Write(partial, sha256, label);
            return partial;
        }

        /// <summary>Renames ".partial" files to their final names in order; undoes on failure.</summary>
        private static void CommitFiles(IReadOnlyList<string> partials)
        {
            var done = new List<string>();
            try
            {
                foreach (var partial in partials)
                {
                    string final = partial.Substring(0, partial.Length - PartialSuffix.Length);
                    if (File.Exists(final))
                    {
                        File.Delete(final);
                    }

                    File.Move(partial, final);
                    done.Add(final);
                }
            }
            catch (Exception)
            {
                foreach (var file in done)
                {
                    TryDelete(file);
                }

                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }

        private static bool IsExpected(Exception ex)
        {
            return ex is ImagingException || ex is OperationCanceledException || ex is IOException || ex is UnauthorizedAccessException;
        }

        private static void RecordFailure(OperationResult result, Exception ex)
        {
            result.ContentComplete = false;
            switch (ex)
            {
                case OperationCanceledException _:
                    result.State = OperationState.Cancelled;
                    break;
                case ImagingException imaging:
                    result.State = OperationState.Failed;
                    result.ErrorCode = imaging.Code;
                    result.ErrorOffset = imaging.Offset;
                    result.ErrorDetail = imaging.Detail;
                    break;
                case IOException io:
                    var mapped = ImageWriter.MapIo(io, null);
                    result.State = OperationState.Failed;
                    result.ErrorCode = mapped.Code;
                    result.ErrorDetail = mapped.Detail;
                    break;
                default:
                    result.State = OperationState.Failed;
                    result.ErrorCode = ErrorCode.InvalidRequest;
                    result.ErrorDetail = ex.Message;
                    break;
            }
        }

        private static int RoundUp(int value, int multiple)
        {
            return (value + multiple - 1) / multiple * multiple;
        }

        private static int FirstDifference(byte[] a, byte[] b, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (a[i] != b[i])
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
