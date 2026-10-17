using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Progress;

namespace HddBackup.Core.Shrinking
{
    public sealed class ShrinkRequest
    {
        public string SourcePath { get; set; }

        /// <summary>Result image path (.img or .img.gz). Never the source.</summary>
        public string OutputPath { get; set; }

        /// <summary>Maximum result size; null ⇒ smallest possible.</summary>
        public long? TargetImageBytes { get; set; }

        public CompressionLevel Compression { get; set; } = CompressionLevel.Optimal;

        /// <summary>Free space at the output folder, when known.</summary>
        public long? FreeBytes { get; set; }

        /// <summary>
        /// Repair after an unclean shutdown (feature 006): read the source through its replayed
        /// journal, leave orphans out, refuse structural damage; a target equal to the source size
        /// gives a repaired copy of the same size.
        /// </summary>
        public bool Repair { get; set; }
    }

    public sealed class ShrinkOutcome
    {
        public string OutputPath { get; set; }

        public long ImageBytes { get; set; }

        public bool IsGzip { get; set; }

        public string ContentSha256 { get; set; }

        public string FileSha256 { get; set; }

        public long ComparedBytes { get; set; }

        public bool Repaired { get; set; }

        public int JournalTransactions { get; set; }

        public int OrphansRemoved { get; set; }
    }

    /// <summary>
    /// Produces a smaller copy of a two-partition (boot + ext4 root) image by rebuilding the root
    /// file system (research R-1). The source is only read. Success is reported only after the
    /// structural check, the tree comparison and the prefix comparison pass (research R-6).
    /// </summary>
    internal static class ImageShrinker
    {
        private const string PartialSuffix = ".partial";
        private const int ChunkBytes = 1 << 20;

        public static ShrinkOutcome Run(ShrinkRequest request, PhaseModel model, CancellationToken cancel)
        {
            if (FileIdentity.IsSameFile(request.SourcePath, request.OutputPath))
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "The output must not be the source image.");
            }

            var analysis = ShrinkPlanner.Analyze(request.SourcePath, cancel);
            if (!analysis.CanShrink(request.Repair))
            {
                throw Blocked(analysis.Blockers);
            }

            bool gzipOutput = ImageDescriptor.IsGzipPath(request.OutputPath);
            string rawOutput = gzipOutput ? request.OutputPath + ".raw" + PartialSuffix : request.OutputPath + PartialSuffix;
            string workCopy = analysis.SourceIsGzip ? request.OutputPath + ".work" + PartialSuffix : null;
            var partials = new List<string> { rawOutput };
            if (workCopy != null)
            {
                partials.Add(workCopy);
            }

            long estimatedOutput = request.TargetImageBytes ?? analysis.MinimumImageBytes;
            long needed = estimatedOutput + (workCopy != null ? analysis.SourceImageBytes : 0) + (gzipOutput ? estimatedOutput : 0);
            if (request.FreeBytes.HasValue && request.FreeBytes.Value < needed)
            {
                throw new ImagingException(ErrorCode.DestinationFull, needed + " bytes are needed in the output folder.");
            }

            bool committed = false;
            try
            {
                string sourceRaw = request.SourcePath;
                if (workCopy != null)
                {
                    Decompress(request.SourcePath, workCopy, model, cancel);
                    sourceRaw = workCopy;
                }

                var outcome = Rebuild(request, analysis, sourceRaw, rawOutput, model, cancel);
                model.SetState(OperationState.Finalizing);
                model.BeginPhase(OperationPhase.Flushing, outcome.ImageBytes, outcome.ImageBytes);
                FinishOutput(request, rawOutput, gzipOutput, outcome, partials, model, cancel);
                committed = true;
                return outcome;
            }
            finally
            {
                foreach (var p in partials)
                {
                    if (!committed || p == workCopy || (gzipOutput && p == rawOutput))
                    {
                        TryDelete(p);
                    }
                }
            }
        }

        internal static ImagingException Blocked(IEnumerable<ShrinkBlockerInfo> blockers)
        {
            return new ImagingException(ErrorCode.ShrinkNotPossible, string.Join("; ", blockers.Select(b => b.ToString())));
        }

        private static void Decompress(string source, string target, PhaseModel model, CancellationToken cancel)
        {
            using (var reader = new ImageReader(source))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes))
            {
                model.SetState(OperationState.Running);
                model.BeginPhase(OperationPhase.Preparing, reader.FileLength, null);
                var buffer = new byte[ChunkBytes];
                int n;
                while ((n = reader.ReadFull(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    model.SetUnits(reader.FilePosition, reader.Position);
                }

                output.Flush(true);
            }
        }

        private static ShrinkOutcome Rebuild(ShrinkRequest request, ShrinkPlan analysis, string sourceRaw, string rawOutput, PhaseModel model, CancellationToken cancel)
        {
            using (var source = FileBlockDevice.OpenRead(sourceRaw))
            {
                var root = analysis.RootPartition;
                int replayed = 0;
                using (var sourceRoot = new PartitionView(source, root.Offset, root.Length))
                using (var reader = new Ext4Reader(request.Repair ? Recovered(sourceRoot, out replayed) : sourceRoot))
                {
                    var problems = reader.Validate();
                    if (request.Repair)
                    {
                        // What the repair removes; anything else still blocks.
                        problems.RemoveAll(p => p == "needs_recovery" || p == "not_clean" || p == "orphans");
                    }

                    if (problems.Count > 0)
                    {
                        throw Blocked(ShrinkPlanner.FromReaderProblems(problems));
                    }

                    Ext4CopyPlan plan;
                    OrphanList orphans = OrphanList.None;
                    try
                    {
                        if (request.Repair)
                        {
                            orphans = OrphanList.Read(reader);
                        }

                        plan = Ext4CopyPlan.Build(reader, orphans, cancel);
                    }
                    catch (Ext4FormatException ex)
                    {
                        throw Blocked(new[] { new ShrinkBlockerInfo(ShrinkBlocker.ChecksumMismatch, ex.Message) });
                    }

                    var sb = reader.Superblock;
                    int bs = sb.BlockSize;
                    long exactData = plan.DataBlocks + plan.DirectoryBlocks + plan.ExtentTreeBlocks + plan.XattrBlockRefs.Count + 1;
                    long minInodes = plan.HighestInode + 16;
                    // A repaired copy of the same size needs no extra free space, only room for the data.
                    bool sameSize = request.Repair && request.TargetImageBytes.HasValue && request.TargetImageBytes.Value >= analysis.SourceImageBytes;
                    long minFs = ShrinkPlanner.MinimumFsBlocks(exactData, minInodes, plan.SourceJournalBlocks, sb, includeMargin: !sameSize);

                    long fsBlocks;
                    long gptTail = analysis.TableStyle == TableStyle.Gpt ? ShrinkPlanner.GptTailSectors * 512L : 0;
                    if (request.TargetImageBytes.HasValue)
                    {
                        long usable = (request.TargetImageBytes.Value - gptTail) / ShrinkPlanner.Alignment * ShrinkPlanner.Alignment;
                        fsBlocks = Math.Min((usable - root.Offset) / bs, sb.BlocksCount);
                        if (fsBlocks < minFs)
                        {
                            throw Blocked(new[] { new ShrinkBlockerInfo(ShrinkBlocker.NotEnoughSpaceEvenAtMinimum, (ShrinkPlanner.ImageBytesFor(root.Offset, minFs * bs, analysis.TableStyle)) + " bytes needed") });
                        }
                    }
                    else
                    {
                        long imageBytes = ShrinkPlanner.ImageBytesFor(root.Offset, minFs * bs, analysis.TableStyle);
                        fsBlocks = (imageBytes - gptTail - root.Offset) / bs;
                    }

                    if (fsBlocks >= sb.BlocksCount && !request.Repair)
                    {
                        throw Blocked(new[] { new ShrinkBlockerInfo(ShrinkBlocker.AlreadyFits) });
                    }

                    fsBlocks = Math.Min(fsBlocks, sb.BlocksCount);

                    var layout = ShrinkPlanner.CreateLayout(fsBlocks, minInodes, sb);
                    long fsBytes = layout.BlocksCount * bs;
                    long outputBytes = ShrinkPlanner.ImageBytesFor(root.Offset, fsBytes, analysis.TableStyle);
                    if (request.TargetImageBytes.HasValue && outputBytes > request.TargetImageBytes.Value)
                    {
                        outputBytes = request.TargetImageBytes.Value / 512 * 512;
                    }

                    using (var output = FileBlockDevice.Create(rawOutput, outputBytes))
                    {
                        CopyRange(source, output, 0, root.Offset, cancel);
                        PartitionTableEditor.ResizeLastPartition(output, analysis.TableStyle, root.Offset, fsBytes);

                        using (var targetRoot = new PartitionView(output, root.Offset, fsBytes))
                        {
                            model.SetState(OperationState.Running);
                            model.BeginPhase(OperationPhase.Transferring, Math.Max(1, plan.TotalBytesToCopy), plan.TotalBytesToCopy);
                            long journal = Math.Min(plan.SourceJournalBlocks, Ext4Layout.DefaultJournalBlocks(layout.BlocksCount));
                            var writer = new Ext4Writer(targetRoot, layout, reader, plan);
                            writer.Build(journal, copied => model.SetUnits(copied, copied), cancel);

                            model.BeginPhase(OperationPhase.Verifying, Math.Max(1, plan.TotalBytesToCopy), plan.TotalBytesToCopy);
                            var findings = Ext4Checker.Check(targetRoot);
                            if (findings.Count > 0)
                            {
                                throw new ImagingException(ErrorCode.ShrinkVerifyFailed, "Structure: " + string.Join("; ", findings.Take(5)));
                            }

                            using (var check = new Ext4Reader(targetRoot))
                            {
                                var diffs = Ext4TreeComparer.Compare(reader, check, cancel);
                                if (diffs.Count > 0)
                                {
                                    throw new ImagingException(ErrorCode.ShrinkVerifyFailed, "Content: " + string.Join("; ", diffs.Take(5)));
                                }
                            }

                            model.SetUnits(plan.TotalBytesToCopy, plan.TotalBytesToCopy);
                        }

                        VerifyPrefix(source, output, root.Offset, analysis.TableStyle, cancel);
                        output.Flush();
                    }

                    return new ShrinkOutcome
                    {
                        OutputPath = request.OutputPath,
                        ImageBytes = outputBytes,
                        IsGzip = ImageDescriptor.IsGzipPath(request.OutputPath),
                        ComparedBytes = plan.TotalBytesToCopy,
                        Repaired = request.Repair,
                        JournalTransactions = replayed,
                        OrphansRemoved = orphans.Count
                    };
                }
            }
        }

        /// <summary>
        /// The root file system as recovery would leave it (journal replayed), after refusing
        /// structural damage (FR-006). Read-only; the source is never written.
        /// </summary>
        private static IBlockDevice Recovered(IBlockDevice root, out int replayedTransactions)
        {
            try
            {
                JournalOverlay overlay;
                int blockSize;
                using (var raw = new Ext4Reader(root))
                {
                    blockSize = raw.BlockSize;
                    var journal = Jbd2Journal.Open(raw);
                    overlay = journal == null ? JournalOverlay.Empty : journal.Replay();
                }

                var recovered = new OverlayBlockDevice(root, overlay, blockSize);
                var structural = Ext4Checker.CheckDetailed(recovered).Where(f => f.Kind == Ext4FindingKind.Structural).Select(f => f.Message).ToList();
                if (structural.Count > 0)
                {
                    throw Blocked(new[] { new ShrinkBlockerInfo(ShrinkBlocker.StructuralDamage, string.Join("; ", structural.Take(3))) });
                }

                replayedTransactions = overlay.Replayed;
                return recovered;
            }
            catch (Ext4FormatException ex)
            {
                throw Blocked(new[] { new ShrinkBlockerInfo(ShrinkBlocker.StructuralDamage, ex.Message) });
            }
        }

        private static void CopyRange(IBlockDevice source, IBlockDevice target, long offset, long length, CancellationToken cancel)
        {
            var buffer = new byte[ChunkBytes];
            for (long done = 0; done < length; done += buffer.Length)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - done);
                source.Read(offset + done, buffer, 0, n);
                target.Write(offset + done, buffer, 0, n);
            }
        }

        /// <summary>Everything before the root partition must equal the source, except the partition table.</summary>
        private static void VerifyPrefix(IBlockDevice source, IBlockDevice output, long length, TableStyle style, CancellationToken cancel)
        {
            var a = new byte[ChunkBytes];
            var b = new byte[ChunkBytes];
            for (long done = 0; done < length; done += a.Length)
            {
                cancel.ThrowIfCancellationRequested();
                int n = (int)Math.Min(a.Length, length - done);
                source.Read(done, a, 0, n);
                output.Read(done, b, 0, n);
                for (int i = 0; i < n; i++)
                {
                    if (a[i] != b[i] && !PartitionTableEditor.IsTableArea(done + i, style))
                    {
                        throw new ImagingException(ErrorCode.ShrinkVerifyFailed, "Data before the root partition differs.", done + i);
                    }
                }
            }
        }

        private static void FinishOutput(ShrinkRequest request, string rawOutput, bool gzipOutput, ShrinkOutcome outcome, List<string> partials, PhaseModel model, CancellationToken cancel)
        {
            string finalPath = request.OutputPath;
            string imagePartial = gzipOutput ? finalPath + PartialSuffix : rawOutput;
            if (gzipOutput)
            {
                partials.Add(imagePartial);
            }

            using (var input = new FileStream(rawOutput, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.SequentialScan))
            using (var content = new Sha256Accumulator())
            {
                FileStream gzFile = null;
                HashingStream fileHash = null;
                GZipStream gz = null;
                if (gzipOutput)
                {
                    gzFile = new FileStream(imagePartial, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes);
                    fileHash = new HashingStream(gzFile, leaveOpen: true);
                    gz = new GZipStream(fileHash, request.Compression, leaveOpen: true);
                }

                try
                {
                    var buffer = new byte[ChunkBytes];
                    int n;
                    long done = 0;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        content.Append(buffer, 0, n);
                        gz?.Write(buffer, 0, n);
                        done += n;
                        model.SetUnits(done, done);
                    }

                    outcome.ContentSha256 = content.Finish();
                    if (gzipOutput)
                    {
                        gz.Dispose();
                        gzFile.Flush(true);
                        outcome.FileSha256 = fileHash.FinishHash();
                    }
                    else
                    {
                        outcome.FileSha256 = outcome.ContentSha256;
                    }
                }
                finally
                {
                    gz?.Dispose();
                    fileHash?.Dispose();
                    gzFile?.Dispose();
                }
            }

            outcome.IsGzip = gzipOutput;
            var sidecars = new List<string>();
            string fileSidecar = ImageDescriptor.FileSidecarPath(finalPath) + PartialSuffix;
            ChecksumFile.Write(fileSidecar, outcome.FileSha256, Path.GetFileName(finalPath));
            sidecars.Add(fileSidecar);
            if (gzipOutput)
            {
                string contentSidecar = ImageDescriptor.ContentSidecarPath(finalPath) + PartialSuffix;
                ChecksumFile.Write(contentSidecar, outcome.ContentSha256, ImageDescriptor.ContentFileName(finalPath));
                sidecars.Add(contentSidecar);
            }

            partials.AddRange(sidecars);
            var commits = new List<string>(sidecars) { imagePartial };
            var done2 = new List<string>();
            try
            {
                foreach (var partial in commits)
                {
                    string final = partial.Substring(0, partial.Length - PartialSuffix.Length);
                    if (File.Exists(final))
                    {
                        File.Delete(final);
                    }

                    File.Move(partial, final);
                    done2.Add(final);
                }
            }
            catch
            {
                foreach (var f in done2)
                {
                    TryDelete(f);
                }

                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best effort cleanup of partial files.
            }
        }
    }
}
