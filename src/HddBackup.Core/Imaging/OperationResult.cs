using System;
using System.Collections.Generic;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Progress;

namespace HddBackup.Core.Imaging
{
    public sealed class OperationResult
    {
        public OperationKind Kind { get; set; }

        /// <summary>Completed, Cancelled or Failed.</summary>
        public OperationState State { get; set; } = OperationState.Failed;

        /// <summary>The utility no longer uses the device and all written data is committed.</summary>
        public bool SafeToRemove { get; set; }

        /// <summary>The image (backup) or device (restore) holds complete, valid content.</summary>
        public bool ContentComplete { get; set; }

        public string ImagePath { get; set; }

        /// <summary>
        /// The backup was taken through a reader that hides the end of the card: the image holds
        /// only what the reader exposes.
        /// </summary>
        public bool ImageIncomplete { get; set; }

        /// <summary>True when the image is gzip-compressed (two checksum files apply).</summary>
        public bool IsGzipImage { get; set; }

        /// <summary>SHA-256 of the uncompressed content (equals the device or partition).</summary>
        public string ContentSha256 { get; set; }

        /// <summary>SHA-256 of the image file as stored (differs from content for .img.gz).</summary>
        public string FileSha256 { get; set; }

        public IReadOnlyList<VerificationResult> Verifications { get; set; } = Array.Empty<VerificationResult>();

        public ErrorCode ErrorCode { get; set; }

        public long? ErrorOffset { get; set; }

        /// <summary>English technical detail of the error, for the details view.</summary>
        public string ErrorDetail { get; set; }

        /// <summary>Set when safe removal was refused; data is already committed in that case.</summary>
        public string EjectVetoedBy { get; set; }

        public bool Ejected { get; set; }

        /// <summary>Bytes of image data processed.</summary>
        public long DataBytes { get; set; }

        public TimeSpan Duration { get; set; }

        /// <summary>ShrinkImage with a repair: the file system was repaired.</summary>
        public bool Repaired { get; set; }

        /// <summary>The repair kept the original size (a repaired copy, not shrunk).</summary>
        public bool RepairedSameSize { get; set; }

        /// <summary>Journal transactions applied by the repair.</summary>
        public int RepairJournalTransactions { get; set; }

        /// <summary>Orphan inodes removed (or truncations finished) by the repair.</summary>
        public int RepairOrphansRemoved { get; set; }

        /// <summary>ConvertImage: the committed new image.</summary>
        public string OutputPath { get; set; }

        /// <summary>File names of checksum files written by the operation (conversion or checksum calculation).</summary>
        public IReadOnlyList<string> SavedChecksumFiles { get; set; } = Array.Empty<string>();
    }
}
