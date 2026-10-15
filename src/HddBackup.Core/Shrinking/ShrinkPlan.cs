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
        AlreadyFits
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

        /// <summary>Root file system feature names (empty when the root is not ext4).</summary>
        public IReadOnlyList<string> Features { get; set; } = Array.Empty<string>();

        public bool Fits(long targetBytes) => IsShrinkable && MinimumImageBytes <= targetBytes;

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

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "  Image {0} bytes, minimum {1} bytes", SourceImageBytes, MinimumImageBytes));
            sb.AppendLine("  Blockers: " + (IsShrinkable ? "none" : string.Join("; ", Blockers)));
            return sb.ToString();
        }
    }
}
