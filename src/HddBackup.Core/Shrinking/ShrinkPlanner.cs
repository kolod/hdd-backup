using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Shrinking
{
    /// <summary>
    /// Fast, read-only analysis: partition layout and the root file system's superblock counters.
    /// Works on raw and gzip images (gzip: only the image start and the start of each partition
    /// are kept while streaming). Deep checks happen during the shrink itself.
    /// </summary>
    public static class ShrinkPlanner
    {
        public const long Alignment = 1024 * 1024;
        public const long MinimumMarginBytes = 128L * 1024 * 1024;
        public const int GptTailSectors = ImageHead.GptTailSectors;
        private const int PrefixBytes = 1024 * 1024;

        public static ShrinkPlan Analyze(string imagePath, CancellationToken cancel)
        {
            var plan = new ShrinkPlan { SourcePath = imagePath };
            using (var device = OpenForAnalysis(imagePath, out bool gzip))
            {
                plan.SourceIsGzip = gzip;
                var table = PartitionTableReader.Read(device);
                plan.TableStyle = table.Style;
                plan.Partitions = table.Partitions;
                if ((table.Style != TableStyle.Mbr && table.Style != TableStyle.Gpt) || table.Partitions.Count == 0)
                {
                    plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedLayout, "no MBR or GPT partition table"));
                    return plan;
                }

                if (table.Partitions.Any(p => p.Number >= 5 && table.Style == TableStyle.Mbr))
                {
                    plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedLayout, "extended partitions"));
                    return plan;
                }

                var root = table.Partitions.OrderBy(p => p.Offset).Last();
                plan.RootPartition = root;
                bool earlierExt4 = root.FileSystem != "ext4" && table.Partitions.Any(p => p != root && p.FileSystem == "ext4");
                if (earlierExt4 || table.Partitions.Any(p => p != root && p.End > root.Offset))
                {
                    plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.RootNotLastPartition));
                    return plan;
                }

                plan.SourceImageBytes = root.End + (table.Style == TableStyle.Gpt ? GptTailSectors * 512L : 0);
                cancel.ThrowIfCancellationRequested();

                var raw = new byte[Ext4Superblock.Size];
                device.Read(root.Offset + Ext4Superblock.ByteOffset, raw, 0, raw.Length);
                var sb = new Ext4Superblock(raw);
                if (sb.Magic != Ext4Superblock.MagicValue || !sb.HasExtents)
                {
                    plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.RootNotExt4, root.FileSystem));
                    return plan;
                }

                plan.Features = Ext4Features.All(sb);
                AddSuperblockBlockers(plan, sb);
                AddFileSystemStates(plan, device, table, root, sb);
                if (plan.IsRepairable)
                {
                    // Unclean shutdown: the counters cannot be trusted. Uncompressed images are read
                    // through the replayed journal now; compressed ones are measured by the operation.
                    if (!gzip)
                    {
                        AnalyzeRecovered(plan, device, root, cancel);
                    }

                    return plan;
                }

                if (!plan.IsShrinkable)
                {
                    return plan;
                }

                plan.BlockSize = sb.BlockSize;
                long journalBlocks = sb.HasJournal && sb.JournalBackupType == 1 ? sb.GetJournalBackup(16) / sb.BlockSize : 0;
                long usedBlocks = sb.BlocksCount - sb.FreeBlocksCount;
                long dataBlocks = Math.Max(0, usedBlocks - SourceOverheadBlocks(sb) - journalBlocks);
                plan.UsedDataBytes = dataBlocks * sb.BlockSize;
                plan.UsedInodes = sb.InodesCount - sb.FreeInodesCount;

                // Directories are rewritten linearly; allow 1% plus a little for that and extent trees.
                long estimate = dataBlocks + (dataBlocks / 100) + 256;
                long fsBlocks = MinimumFsBlocks(estimate, plan.UsedInodes, journalBlocks, sb);
                plan.MinimumImageBytes = ImageBytesFor(root.Offset, fsBlocks * sb.BlockSize, table.Style);
                if (plan.MinimumImageBytes >= plan.SourceImageBytes)
                {
                    plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.NotEnoughSpaceEvenAtMinimum));
                }

                return plan;
            }
        }

        /// <summary>Logical size an image needs on a device (end of the last partition, plus backup GPT).</summary>
        public static long RequiredDeviceBytes(string imagePath) => ImageHead.RequiredDeviceBytes(imagePath);

        /// <summary>Converts <see cref="Ext4Reader.Validate"/> problem codes into blockers.</summary>
        internal static List<ShrinkBlockerInfo> FromReaderProblems(IEnumerable<string> problems)
        {
            var result = new List<ShrinkBlockerInfo>();
            var features = new List<string>();
            foreach (var p in problems)
            {
                if (p.StartsWith("feature:", StringComparison.Ordinal))
                {
                    features.Add(p.Substring(8));
                }
                else if (p == "needs_recovery")
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.JournalNeedsRecovery));
                }
                else if (p == "not_clean")
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.FsNotClean));
                }
                else if (p == "errors")
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.FsHasErrors));
                }
                else if (p == "orphans")
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.OrphanInodes));
                }
                else if (p.StartsWith("inode_size:", StringComparison.Ordinal))
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedInodeSize, p.Substring(11)));
                }
                else
                {
                    result.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedLayout, p));
                }
            }

            if (features.Count > 0)
            {
                result.Insert(0, new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedFeature, string.Join(", ", features)));
            }

            return result;
        }

        /// <summary>Superblock-level preconditions (research R-3); deeper checks run during the shrink.</summary>
        internal static void AddSuperblockBlockers(ShrinkPlan plan, Ext4Superblock sb)
        {
            var unsupported = Ext4Features.Unsupported(sb);
            if (unsupported.Count > 0)
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedFeature, string.Join(", ", unsupported)));
            }

            var reasons = RepairReasonsOf(sb);
            if (reasons != RepairReasons.None)
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.NeedsRepair, reasons.ToString()));
            }

            if ((sb.State & Ext4Superblock.StateErrors) != 0 || sb.ErrorCount != 0)
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.FsHasErrors));
            }

            if (sb.LogBlockSize > 2)
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedBlockSize, sb.BlockSize.ToString()));
            }

            if (sb.InodeSize != 128 && sb.InodeSize != 256)
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.UnsupportedInodeSize, sb.InodeSize.ToString()));
            }

            if (sb.HasMetadataCsum && sb.Checksum != sb.ComputeChecksum())
            {
                plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.ChecksumMismatch, "superblock"));
            }
        }

        /// <summary>What an unclean shutdown left in this file system (feature 006).</summary>
        internal static RepairReasons RepairReasonsOf(Ext4Superblock sb)
        {
            var reasons = RepairReasons.None;
            if (sb.NeedsRecovery)
            {
                reasons |= RepairReasons.JournalNeedsRecovery;
            }

            if (sb.LastOrphan != 0 || (sb.State & Ext4Superblock.StateOrphans) != 0)
            {
                reasons |= RepairReasons.OrphanInodes;
            }

            if ((sb.State & Ext4Superblock.StateValid) == 0)
            {
                reasons |= RepairReasons.NotClean;
            }

            return reasons;
        }

        /// <summary>State of every ext4 partition, from its superblock (research R-5).</summary>
        private static void AddFileSystemStates(ShrinkPlan plan, IBlockDevice device, PartitionTable table, PartitionInfo root, Ext4Superblock rootSb)
        {
            foreach (var p in table.Partitions.OrderBy(x => x.Offset))
            {
                Ext4Superblock sb = rootSb;
                if (!ReferenceEquals(p, root))
                {
                    if (p.FileSystem != "ext4")
                    {
                        continue;
                    }

                    var raw = new byte[Ext4Superblock.Size];
                    device.Read(p.Offset + Ext4Superblock.ByteOffset, raw, 0, raw.Length);
                    sb = new Ext4Superblock(raw);
                    if (sb.Magic != Ext4Superblock.MagicValue)
                    {
                        continue;
                    }
                }

                var state = new FileSystemState { PartitionNumber = p.Number, Name = p.Name ?? string.Empty, IsRoot = ReferenceEquals(p, root) };
                state.Reasons = RepairReasonsOf(sb);
                if ((sb.State & Ext4Superblock.StateErrors) != 0 || sb.ErrorCount != 0)
                {
                    state.Condition = FileSystemCondition.Damaged;
                    state.Problems = new[] { "The kernel recorded file system errors." };
                }
                else
                {
                    state.Condition = state.Reasons == RepairReasons.None ? FileSystemCondition.Clean : FileSystemCondition.NeedsRepair;
                }

                plan.FileSystems.Add(state);
            }
        }

        /// <summary>
        /// Reads the root file system through its replayed journal (uncompressed images): exact sizes,
        /// orphan count, and structural damage, which turns the plan into a refusal (FR-006).
        /// </summary>
        private static void AnalyzeRecovered(ShrinkPlan plan, IBlockDevice device, PartitionInfo root, CancellationToken cancel)
        {
            var state = plan.RootFileSystem;
            using (var view = new PartitionView(device, root.Offset, root.Length))
            {
                try
                {
                    JournalOverlay overlay;
                    int blockSize;
                    using (var raw = new Ext4Reader(view))
                    {
                        blockSize = raw.BlockSize;
                        var journal = Jbd2Journal.Open(raw);
                        overlay = journal == null ? JournalOverlay.Empty : journal.Replay();
                    }

                    var recovered = new OverlayBlockDevice(view, overlay, blockSize);
                    var structural = Ext4Checker.CheckDetailed(recovered)
                        .Where(f => f.Kind == Ext4FindingKind.Structural).Select(f => f.Message).ToList();
                    if (structural.Count > 0)
                    {
                        MarkDamaged(plan, state, structural);
                        return;
                    }

                    using (var reader = new Ext4Reader(recovered))
                    {
                        var orphans = OrphanList.Read(reader);
                        state.OrphanCount = orphans.Count;
                        var copy = Ext4CopyPlan.Build(reader, orphans, cancel);
                        var sb = reader.Superblock;
                        long exactData = copy.DataBlocks + copy.DirectoryBlocks + copy.ExtentTreeBlocks + copy.XattrBlockRefs.Count + 1;
                        long minFs = MinimumFsBlocks(exactData, copy.HighestInode + 16, copy.SourceJournalBlocks, sb);
                        plan.BlockSize = sb.BlockSize;
                        plan.UsedDataBytes = exactData * sb.BlockSize;
                        plan.UsedInodes = copy.Nodes.Count;
                        plan.MinimumImageBytes = ImageBytesFor(root.Offset, minFs * sb.BlockSize, plan.TableStyle);
                    }
                }
                catch (Ext4FormatException ex)
                {
                    MarkDamaged(plan, state, new List<string> { ex.Message });
                }
            }
        }

        private static void MarkDamaged(ShrinkPlan plan, FileSystemState state, List<string> problems)
        {
            state.Condition = FileSystemCondition.Damaged;
            state.Problems = problems;
            plan.Blockers.RemoveAll(b => b.Blocker == ShrinkBlocker.NeedsRepair);
            plan.Blockers.Add(new ShrinkBlockerInfo(ShrinkBlocker.StructuralDamage, string.Join("; ", problems.Take(3))));
        }

        /// <summary>
        /// Smallest file system (in blocks) holding <paramref name="dataBlocks"/> plus journal,
        /// metadata for that size and the safety margin (research R-4, FR-011).
        /// </summary>
        internal static long MinimumFsBlocks(long dataBlocks, long usedInodes, long sourceJournalBlocks, Ext4Superblock sb, bool includeMargin = true)
        {
            int bs = sb.BlockSize;
            long margin = includeMargin ? MarginBlocks(dataBlocks, bs) : 0;
            long minInodes = usedInodes + (usedInodes / 10) + 16;
            long blocks = dataBlocks + margin + sourceJournalBlocks;
            for (int i = 0; i < 20; i++)
            {
                var layout = CreateLayout(blocks, minInodes, sb);
                long journal = Math.Min(sourceJournalBlocks, Ext4Layout.DefaultJournalBlocks(layout.BlocksCount));
                long metadata = layout.MetadataExtents.Sum(e => e.Count) + (sb.HasResizeInode ? 1 : 0);
                long needed = dataBlocks + margin + journal + metadata;
                if (needed <= layout.BlocksCount)
                {
                    return layout.BlocksCount;
                }

                blocks = needed + (layout.BlocksPerGroup / 8);
            }

            throw new ImagingException(ErrorCode.ShrinkNotPossible, "Could not compute a minimum size.");
        }

/// <summary>
        /// Free space kept in a shrunk file system (FR-011): 128 MiB for normal system images,
        /// half the data plus 16 MiB for tiny ones, and never less than 5 % of the data.
        /// </summary>
        internal static long MarginBlocks(long dataBlocks, int blockSize)
        {
            long small = (dataBlocks / 2) + ((16L << 20) / blockSize);
            return Math.Max(dataBlocks / 20, Math.Min(MinimumMarginBytes / blockSize, small));
        }

        internal static Ext4Layout CreateLayout(long blocks, long minInodes, Ext4Superblock sb)
        {
            return Ext4Layout.Create(blocks, sb.BlockSize, sb.InodeSize, 16384, minInodes, sb.DescriptorSize, sb.HasResizeInode, Math.Max(sb.LogGroupsPerFlex, 0));
        }

        /// <summary>Image size for a root partition of <paramref name="fsBytes"/> at <paramref name="rootOffset"/>, 1 MiB aligned.</summary>
        internal static long ImageBytesFor(long rootOffset, long fsBytes, TableStyle style)
        {
            long end = rootOffset + fsBytes;
            if (style == TableStyle.Gpt)
            {
                end += GptTailSectors * 512L;
            }

            return (end + Alignment - 1) / Alignment * Alignment;
        }

        /// <summary>Metadata blocks of the source layout (superblock areas, bitmaps, inode tables).</summary>
        private static long SourceOverheadBlocks(Ext4Superblock sb)
        {
            long groups = (sb.BlocksCount - sb.FirstDataBlock + sb.BlocksPerGroup - 1) / sb.BlocksPerGroup;
            int descPerBlock = sb.BlockSize / sb.DescriptorSize;
            long gdt = (groups + descPerBlock - 1) / descPerBlock;
            long itable = ((long)sb.InodesPerGroup * sb.InodeSize + sb.BlockSize - 1) / sb.BlockSize;
            long backups = 0;
            for (long g = 0; g < groups; g++)
            {
                if (g <= 1 || IsPower(g, 3) || IsPower(g, 5) || IsPower(g, 7))
                {
                    backups++;
                }
            }

            return (backups * (1 + gdt + sb.ReservedGdtBlocks)) + (groups * (2 + itable)) + (sb.HasResizeInode ? 1 : 0);
        }

        private static bool IsPower(long value, long b)
        {
            long p = b;
            while (p < value)
            {
                p *= b;
            }

            return p == value;
        }

        /// <summary>Random-access view for analysis: the file itself, or the needed parts of a gzip stream.</summary>
        private static IBlockDevice OpenForAnalysis(string path, out bool gzip)
        {
            using (var probe = File.OpenRead(path))
            {
                gzip = probe.Length >= 18 && probe.ReadByte() == 0x1F && probe.ReadByte() == 0x8B;
            }

            if (!gzip)
            {
                return FileBlockDevice.OpenRead(path);
            }

            return GzipSampleDevice.Load(path);
        }

        /// <summary>
        /// Holds the first megabyte of a gzip image plus the area around the last partition's
        /// superblock; everything else reads as zeros. Enough for table and superblock analysis.
        /// </summary>
        private sealed class GzipSampleDevice : IBlockDevice
        {
            private const int SampleBytes = 64 * 1024;
            private readonly byte[] _head;
            private readonly List<KeyValuePair<long, byte[]>> _samples;

            private GzipSampleDevice(byte[] head, List<KeyValuePair<long, byte[]>> samples)
            {
                _head = head;
                _samples = samples;
            }

            public long Size => long.MaxValue / 2 / 512 * 512;

            public int SectorSize => 512;

            public bool CanWrite => false;

            public static GzipSampleDevice Load(string path)
            {
                using (var reader = new ImageReader(path))
                {
                    var head = new byte[PrefixBytes];
                    int n = reader.ReadFull(head, 0, head.Length);
                    var samples = new List<KeyValuePair<long, byte[]>>();
                    var table = PartitionTableReader.Read(new GzipSampleDevice(head, samples));
                    if (n < head.Length)
                    {
                        return new GzipSampleDevice(head, samples);
                    }

                    // One forward pass: keep the first SampleBytes of every partition (boot sectors, superblocks).
                    long position = head.Length;
                    var scratch = new byte[1 << 20];
                    foreach (long start in table.Partitions.Select(p => p.Offset).Distinct().OrderBy(o => o))
                    {
                        if (start < position)
                        {
                            continue; // inside the head or the previous sample
                        }

                        while (position < start)
                        {
                            int got = reader.ReadFull(scratch, 0, (int)Math.Min(scratch.Length, start - position));
                            if (got == 0)
                            {
                                return new GzipSampleDevice(head, samples);
                            }

                            position += got;
                        }

                        var sample = new byte[SampleBytes];
                        int read = reader.ReadFull(sample, 0, sample.Length);
                        samples.Add(new KeyValuePair<long, byte[]>(start, sample));
                        position += read;
                        if (read < sample.Length)
                        {
                            break;
                        }
                    }

                    return new GzipSampleDevice(head, samples);
                }
            }

            public int Read(long offset, byte[] buffer, int index, int count)
            {
                Array.Clear(buffer, index, count);
                Copy(_head, 0, offset, buffer, index, count);
                foreach (var sample in _samples)
                {
                    Copy(sample.Value, sample.Key, offset, buffer, index, count);
                }

                return count;
            }

            public void Write(long offset, byte[] buffer, int index, int count) => throw new InvalidOperationException();

            public void Flush()
            {
            }

            public void Dispose()
            {
            }

            private static void Copy(byte[] source, long sourceOffset, long offset, byte[] buffer, int index, int count)
            {
                long from = Math.Max(offset, sourceOffset);
                long to = Math.Min(offset + count, sourceOffset + source.Length);
                if (to > from)
                {
                    Buffer.BlockCopy(source, (int)(from - sourceOffset), buffer, index + (int)(from - offset), (int)(to - from));
                }
            }
        }
    }
}
