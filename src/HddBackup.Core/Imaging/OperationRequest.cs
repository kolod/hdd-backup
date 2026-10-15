using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Imaging
{
    public enum OperationKind
    {
        BackupDevice,
        BackupPartition,
        RestoreDevice,
        RestorePartition,
        VerifyImage,

        /// <summary>Create a smaller copy of an image by rebuilding its last (ext4) partition.</summary>
        ShrinkImage
    }

    public enum CompressionChoice
    {
        Optimal,
        Fastest
    }

    /// <summary>Non-blocking findings of validation that the UI should show before starting.</summary>
    public enum ValidationWarning
    {
        /// <summary>The target is larger than the image; the rest stays unallocated.</summary>
        TargetLarger,

        /// <summary>The uncompressed image size is unknown, so it is checked while writing.</summary>
        ImageSizeUnknown,

        /// <summary>A compressed image may exceed the 4 GB file limit of a FAT destination.</summary>
        MayExceedFat32Limit,

        /// <summary>The target is a USB hard disk or SSD rather than removable media.</summary>
        ExternalDisk,

        /// <summary>The image has no checksum file, so it cannot be verified against a stored value.</summary>
        NoStoredChecksum,

        /// <summary>The name recorded inside a checksum file differs from the image file name.</summary>
        ChecksumLabelMismatch,

        /// <summary>A checksum file next to the image could not be parsed.</summary>
        ChecksumUnreadable,

        /// <summary>The content checksum file of a .img.gz would overwrite the checksum of an existing .img.</summary>
        ContentChecksumCollides,

        /// <summary>The reader hides the end of the card; the image will not contain it.</summary>
        IncompleteBackup
    }

    public sealed class OperationRequest
    {
        public OperationKind Kind { get; set; }

        /// <summary>Source for backups, target for restores; unused for image verification.</summary>
        public DeviceInfo Device { get; set; }

        /// <summary>Source partition (backup) or target partition (restore) for partition kinds.</summary>
        public PartitionInfo Partition { get; set; }

        /// <summary>Image path; the format follows the extension (.gz = gzip) for backups.</summary>
        public string ImagePath { get; set; }

        public bool VerifyAfter { get; set; }

        public bool EjectAfter { get; set; } = true;

        public CompressionChoice Compression { get; set; } = CompressionChoice.Optimal;

        /// <summary>Required to write to a USB disk that is not removable media.</summary>
        public bool ExternalDiskConfirmed { get; set; }

        /// <summary>
        /// Backup only: read whatever an unsuitable reader exposes (see
        /// <see cref="Devices.DeviceInfo.IsCapacityMisreported"/>), knowing the image will be
        /// incomplete. Never applies to restores.
        /// </summary>
        public bool AllowIncompleteBackup { get; set; }

        /// <summary>ShrinkImage: path of the new, smaller image (.img or .img.gz).</summary>
        public string ShrinkOutputPath { get; set; }

        /// <summary>ShrinkImage: maximum size of the result; null ⇒ smallest possible.</summary>
        public long? ShrinkTargetBytes { get; set; }

        public bool IsBackup => Kind == OperationKind.BackupDevice || Kind == OperationKind.BackupPartition;

        public bool IsRestore => Kind == OperationKind.RestoreDevice || Kind == OperationKind.RestorePartition;

        public bool IsPartition => Kind == OperationKind.BackupPartition || Kind == OperationKind.RestorePartition;
    }
}
