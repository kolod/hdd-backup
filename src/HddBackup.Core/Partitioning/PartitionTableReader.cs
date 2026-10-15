using HddBackup.Core.Devices;

namespace HddBackup.Core.Partitioning
{
    /// <summary>Detects and parses the partition table of a device.</summary>
    public static class PartitionTableReader
    {
        public static PartitionTable Read(IBlockDevice device)
        {
            int sectorSize = device.SectorSize;
            if (device.Size < 2L * sectorSize)
            {
                return PartitionTable.Empty;
            }

            var sector0 = new byte[sectorSize];
            device.Read(0, sector0, 0, sectorSize);

            PartitionTable table;
            if (MbrParser.IsProtective(sector0))
            {
                table = GptParser.TryParse(device) ?? PartitionTable.Damaged;
            }
            else if (MbrParser.HasSignature(sector0))
            {
                table = MbrParser.TryParse(device, sector0);
                if (table == null)
                {
                    // A boot sector at LBA 0 ("superfloppy") also ends with 55 AA.
                    table = FileSystemProbe.Detect(sector0) != null ? PartitionTable.Empty : PartitionTable.Damaged;
                }
            }
            else
            {
                // GPT without a protective MBR is non-standard but still worth recognising.
                table = GptParser.TryParse(device) ?? PartitionTable.Empty;
            }

            foreach (var partition in table.Partitions)
            {
                try
                {
                    var start = FileSystemProbe.ReadStart(device, partition.Offset, partition.Length);
                    partition.FileSystem = (start == null ? null : FileSystemProbe.Detect(start)) ?? FileSystemProbe.UnknownName;
                    partition.FileSystemSize = start == null ? null : FileSystemProbe.DetectSize(start);
                }
                catch (System.Exception)
                {
                    partition.FileSystem = FileSystemProbe.UnknownName;
                    partition.FileSystemSize = null;
                }
            }

            return table.WithDeviceCheck(device.Size);
        }
    }
}
