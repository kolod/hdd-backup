using System;
using System.Collections.Generic;
using System.Globalization;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Shrinking
{
    /// <summary>Why an image cannot be shrunk (each has a localized text in the UI).</summary>
    public enum ShrinkBlocker
    {
        NotEnoughSpaceEvenAtMinimum,
        RootNotLastPartition,
        RootNotExt4,
        UnsupportedLayout,
        FsNotClean,
        FsHasErrors,
        OrphanInodes,
        JournalNeedsRecovery,
        UnsupportedFeature,
        ChecksumMismatch,
        UnsupportedBlockSize,
        UnsupportedInodeSize,
        AlreadyFits,

        /// <summary>Unclean shutdown (journal, orphans, not clean); removed by a repair (feature 006).</summary>
        NeedsRepair,

        /// <summary>Damage not caused by an unclean shutdown; never repaired.</summary>
        StructuralDamage
    }

    public enum FileSystemCondition
    {
        Clean,
        NeedsRepair,
        Damaged
    }

    [Flags]
    public enum RepairReasons
    {
        None = 0,
        JournalNeedsRecovery = 1,
        OrphanInodes = 2,
        NotClean = 4
    }

    /// <summary>State of one ext4 partition of an image (data-model.md "FileSystemState").</summary>
    public sealed class FileSystemState
    {
        public int PartitionNumber { get; set; }

        public string Name { get; set; } = string.Empty;

        public FileSystemCondition Condition { get; set; }

        public RepairReasons Reasons { get; set; }

        /// <summary>Orphan inodes found; null when not counted (compressed image before the operation).</summary>
        public int? OrphanCount { get; set; }

        /// <summary>Damage found (English technical text), for <see cref="FileSystemCondition.Damaged"/>.</summary>
        public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();

        /// <summary>The root (last) partition: the only one that is repaired or shrunk.</summary>
        public bool IsRoot { get; set; }
    }

    public sealed class ShrinkBlockerInfo
    {
        public ShrinkBlockerInfo(ShrinkBlocker blocker, string detail = null)
        {
            Blocker = blocker;
            Detail = detail ?? string.Empty;
        }

        public ShrinkBlocker Blocker { get; }

        /// <summary>Technical detail shown verbatim (e.g. the unsupported feature name).</summary>
        public string Detail { get; }

        public override string ToString() => Detail.Length == 0 ? Blocker.ToString() : Blocker + ": " + Detail;
    }

    /// <summary>Read-only analysis of an image (data-model.md "ShrinkPlan").</summary>
    public sealed class ShrinkPlan
    {
        public string SourcePath { get; set; }

        public bool SourceIsGzip { get; set; }

        public TableStyle TableStyle { get; set; }

        public IReadOnlyList<PartitionInfo> Partitions { get; set; } = Array.Empty<PartitionInfo>();

        public PartitionInfo RootPartition { get; set; }

        public int BlockSize { get; set; }

        /// <summary>Logical image size: end of the last partition (plus the backup GPT).</summary>
        public long SourceImageBytes { get; set; }

        /// <summary>Space the root file system needs for data (estimated from its counters).</summary>
        public long UsedDataBytes { get; set; }

        public long UsedInodes { get; set; }

        public long MinimumImageBytes { get; set; }

        public List<ShrinkBlockerInfo> Blockers { get; } = new List<ShrinkBlockerInfo>();

        public bool IsShrinkable => Blockers.Count == 0;

        /// <summary>The only obstacle is an unclean shutdown, which a repair removes.</summary>
        public bool IsRepairable => Blockers.Count > 0 && Blockers.TrueForAll(b => b.Blocker == ShrinkBlocker.NeedsRepair);

        /// <summary>The root file system can be rebuilt, with a repair when <paramref name="repair"/>.</summary>
        public bool CanShrink(bool repair) => IsShrinkable || (repair && IsRepairable);

        /// <summary>The ext4 partitions of the image and their state.</summary>
        public List<FileSystemState> FileSystems { get; } = new List<FileSystemState>();

        public FileSystemState RootFileSystem => FileSystems.Find(f => f.IsRoot);

        /// <summary>Root file system feature names (empty when the root is not ext4).</summary>
        public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();

        public bool Fits(long targetBytes) => IsShrinkable && MinimumImageBytes <= targetBytes;

        /// <summary>Fits when rebuilt with a repair (a minimum of 0 is measured during the operation).</summary>
        public bool Fits(long targetBytes, bool repair) => CanShrink(repair) && MinimumImageBytes <= targetBytes;

        /// <summary>Technical English summary for the diagnostics report.</summary>
        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Shrink analysis: " + SourcePath + (SourceIsGzip ? " (gzip)" : string.Empty));
            sb.AppendLine("  Table: " + TableStyle);
            foreach (var p in Partitions)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  #{0} {1} offset {2} length {3}{4}", p.Number, p.FileSystem, p.Offset, p.Length, ReferenceEquals(p, RootPartition) ? " [root]" : string.Empty));
            }

            if (Features.Count > 0)
            {
                sb.AppendLine("  Features: " + string.Join(" ", Features));
            }

            if (BlockSize > 0)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Block size {0}, used data {1} bytes, used inodes {2}", BlockSize, UsedDataBytes, UsedInodes));
            }

            foreach (var fs in FileSystems)
            {
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  File system #{0}{1}: {2}{3}{4}{5}",
                    fs.PartitionNumber,
                    fs.IsRoot ? " [root]" : string.Empty,
                    fs.Condition,
                    fs.Reasons == RepairReasons.None ? string.Empty : " (" + fs.Reasons + ")",
                    fs.OrphanCount.HasValue ? ", orphans " + fs.OrphanCount.Value : string.Empty,
                    fs.Problems.Count > 0 ? " — " + string.Join("; ", fs.Problems) : string.Empty));
            }

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Image {0} bytes, minimum {1} bytes", SourceImageBytes, MinimumImageBytes));
            sb.AppendLine("  Blockers: " + (IsShrinkable ? "none" : string.Join("; ", Blockers)));
            return sb.ToString();
        }
    }
}
