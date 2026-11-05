using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Browsing
{
    /// <summary>Why a source offers nothing to browse.</summary>
    public enum BrowseProblem
    {
        None,

        /// <summary>A compressed image; it must be converted to .img first.</summary>
        NeedsConversion,

        /// <summary>No ext4 partition that this program can browse.</summary>
        NoExt4,

        /// <summary>The source could not be read.</summary>
        Unreadable
    }

    /// <summary>A partition of a browsed source.</summary>
    public sealed class BrowsePartition
    {
        public int Number { get; internal set; }

        public string Name { get; internal set; } = string.Empty;

        public long Offset { get; internal set; }

        public long Length { get; internal set; }

        public string FileSystem { get; internal set; } = string.Empty;

        /// <summary>ext4, valid superblock, supported features and block size.</summary>
        public bool IsBrowsable { get; internal set; }

        /// <summary>Why the partition cannot be browsed (feature name, "not ext4", ...); empty when browsable.</summary>
        public string Unsupported { get; internal set; } = string.Empty;

        /// <summary>Clean, needs repair or damaged; null for partitions that are not ext4.</summary>
        public FileSystemState State { get; internal set; }
    }
}
