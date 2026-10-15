using System;
using System.Collections.Generic;
using System.Linq;

namespace HddBackup.Core.Partitioning
{
    public enum TableStyle
    {
        /// <summary>No partition table (blank medium or a file system starting at sector 0).</summary>
        None,
        Mbr,
        Gpt,
        /// <summary>A table is present but damaged or unrecognized.</summary>
        Unknown
    }

    public sealed class PartitionInfo
    {
        /// <summary>1-based number: MBR primaries 1-4 by slot, logicals from 5; GPT in entry order.</summary>
        public int Number { get; set; }

        /// <summary>Start in bytes from the beginning of the device.</summary>
        public long Offset { get; set; }

        public long Length { get; set; }

        public byte? MbrType { get; set; }

        public Guid? GptType { get; set; }

        public Guid? GptId { get; set; }

        /// <summary>GPT partition name; empty for MBR.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Detected file system name, or "unknown".</summary>
        public string FileSystem { get; set; } = FileSystemProbe.UnknownName;

        /// <summary>Bytes the file system occupies according to its own metadata; null when unknown.</summary>
        public long? FileSystemSize { get; set; }

        public long End => Offset + Length;

        /// <summary>End of the data that matters: the file system end when known, otherwise the partition end.</summary>
        public long UsedEnd => Offset + Math.Min(Length, FileSystemSize ?? Length);
    }

    public sealed class PartitionTable
    {
        public PartitionTable(TableStyle style, IReadOnlyList<PartitionInfo> partitions, Guid? diskGuid = null, bool extendsBeyondDevice = false)
        {
            Style = style;
            Partitions = partitions ?? Array.Empty<PartitionInfo>();
            DiskGuid = diskGuid;
            ExtendsBeyondDevice = extendsBeyondDevice;
        }

        /// <summary>
        /// Data reaches past the size the device reports: a partition starts beyond the end, or a
        /// file system (or, if unknown, a partition) ends beyond it. Typical for card readers whose
        /// driver misreports the capacity (e.g. old readers with SDHC cards). A partition entry that
        /// merely overshoots while its file system fits does not count.
        /// </summary>
        public bool ExtendsBeyondDevice { get; }

        /// <summary>At least one partition entry overshoots the device end, even if its data fits.</summary>
        public bool EntriesOvershootDevice { get; private set; }

        internal PartitionTable WithDeviceCheck(long deviceSize)
        {
            bool beyond = ExtendsBeyondDevice || Partitions.Any(p => p.Offset >= deviceSize || p.UsedEnd > deviceSize);
            return new PartitionTable(Style, Partitions, DiskGuid, beyond)
            {
                EntriesOvershootDevice = Partitions.Any(p => p.End > deviceSize)
            };
        }

        public static PartitionTable Empty { get; } = new PartitionTable(TableStyle.None, Array.Empty<PartitionInfo>());

        public static PartitionTable Damaged { get; } = new PartitionTable(TableStyle.Unknown, Array.Empty<PartitionInfo>());

        public TableStyle Style { get; }

        public Guid? DiskGuid { get; }

        /// <summary>Partitions ordered by offset.</summary>
        public IReadOnlyList<PartitionInfo> Partitions { get; }
    }
}
