using System.Collections.Generic;

namespace HddBackup.Core.Browsing
{
    /// <summary>What to do with files that already exist in the destination. Nothing is replaced unless asked.</summary>
    public enum ConflictPolicy
    {
        Skip,
        Replace
    }

    public enum ExtractionAction
    {
        CreateDirectory,
        Copy,
        SkipExisting,
        SkipSymlink,
        SkipSpecial,
        Fail
    }

    public enum ExtractionIssueKind
    {
        Skipped,
        Renamed,
        Failed
    }

    /// <summary>Why an item was skipped, renamed or failed; each has a localized text in the UI.</summary>
    public enum ExtractionIssueReason
    {
        SymbolicLink,
        SpecialFile,
        AlreadyExists,
        WindowsName,
        PathTooLong,
        FatLimit,
        Unreadable,
        NotExtractable,
        WriteFailed
    }

    public sealed class ExtractionIssue
    {
        public ExtractionIssueKind Kind { get; internal set; }

        public ExtractionIssueReason Reason { get; internal set; }

        /// <summary>Path in the file system.</summary>
        public string Path { get; internal set; } = string.Empty;

        /// <summary>The name given on Windows (renamed items only).</summary>
        public string ResultName { get; internal set; } = string.Empty;

        /// <summary>Extra text for the reason, e.g. the error message.</summary>
        public string Detail { get; internal set; } = string.Empty;
    }

    public sealed class ExtractionItem
    {
        public FsEntry Source { get; internal set; }

        /// <summary>Path relative to the destination folder, with backslashes.</summary>
        public string RelativePath { get; internal set; } = string.Empty;

        public ExtractionAction Action { get; internal set; }

        /// <summary>Bytes to copy (files that are copied).</summary>
        public long Bytes { get; internal set; }
    }

    public sealed class ExtractionPlan
    {
        public string DestinationFolder { get; internal set; } = string.Empty;

        public List<ExtractionItem> Items { get; } = new List<ExtractionItem>();

        /// <summary>Skipped, renamed and failed items found while planning.</summary>
        public List<ExtractionIssue> Issues { get; } = new List<ExtractionIssue>();

        public long Files { get; internal set; }

        public long Directories { get; internal set; }

        /// <summary>Bytes the copied files add up to.</summary>
        public long TotalBytes { get; internal set; }

        /// <summary>Existing destination files that would be replaced (or are skipped, under <see cref="ConflictPolicy.Skip"/>).</summary>
        public int Conflicts { get; internal set; }

        /// <summary>Free space of the destination when known.</summary>
        public long? FreeBytes { get; internal set; }

        public bool InsufficientSpace => FreeBytes.HasValue && TotalBytes > FreeBytes.Value;

        /// <summary>A destination file that is the protected image itself; null when none.</summary>
        public string ProtectedPathHit { get; internal set; }

        public bool NothingToExtract => Files == 0 && Directories == 0;
    }

    public sealed class ExtractionSummary
    {
        public string DestinationFolder { get; internal set; } = string.Empty;

        public long FilesExtracted { get; internal set; }

        public long DirectoriesCreated { get; internal set; }

        public long BytesExtracted { get; internal set; }

        public List<ExtractionIssue> Issues { get; } = new List<ExtractionIssue>();
    }
}
