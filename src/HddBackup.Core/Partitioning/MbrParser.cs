using System.Collections.Generic;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Partitioning
{
    /// <summary>Parses classic MBR partition tables, including extended/logical chains.</summary>
    public static class MbrParser
    {
        public const byte ProtectiveType = 0xEE;
        private const int EntriesOffset = 446;
        private const int EntrySize = 16;
        private const int MaxLogicalPartitions = 128;

        public static bool HasSignature(byte[] sector)
        {
            return sector.Length >= 512 && sector[510] == 0x55 && sector[511] == 0xAA;
        }

        public static bool IsProtective(byte[] sector0)
        {
            if (!HasSignature(sector0))
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                if (sector0[EntriesOffset + (i * EntrySize) + 4] == ProtectiveType)
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsExtendedType(byte type)
        {
            return type == 0x05 || type == 0x0F || type == 0x85;
        }

        /// <summary>
        /// Returns the parsed table, or null when sector 0 does not hold a plausible MBR
        /// (invalid status bytes, or entries outside the device).
        /// </summary>
        public static PartitionTable TryParse(IBlockDevice device, byte[] sector0)
        {
            if (!HasSignature(sector0))
            {
                return null;
            }

            long sectorSize = device.SectorSize;
            long totalSectors = device.Size / sectorSize;
            var partitions = new List<PartitionInfo>();
            bool beyondEnd = false;

            for (int slot = 0; slot < 4; slot++)
            {
                int o = EntriesOffset + (slot * EntrySize);
                byte status = sector0[o];
                byte type = sector0[o + 4];
                long start = BinaryHelpers.U32(sector0, o + 8);
                long count = BinaryHelpers.U32(sector0, o + 12);

                if (type == 0 || count == 0)
                {
                    continue;
                }

                if ((status != 0x00 && status != 0x80) || start == 0)
                {
                    return null;
                }

                // Entries past the reported end are kept. Whether that matters is decided later
                // from the file systems (PartitionTable.WithDeviceCheck); only an extended
                // container is flagged here, because logical partitions past the end are invisible.
                if (IsExtendedType(type))
                {
                    if (start + count > totalSectors)
                    {
                        beyondEnd = true;
                    }

                    if (start >= totalSectors)
                    {
                        continue; // EBRs are unreadable; logical partitions stay unknown.
                    }

                    if (!ReadLogicals(device, start, count, partitions))
                    {
                        return null;
                    }

                    continue;
                }

                partitions.Add(new PartitionInfo
                {
                    Number = slot + 1,
                    Offset = start * sectorSize,
                    Length = count * sectorSize,
                    MbrType = type
                });
            }

            partitions.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return new PartitionTable(TableStyle.Mbr, partitions, null, beyondEnd);
        }

        /// <summary>Raw primary entries for diagnostics: (slot, status, type, start LBA, sector count).</summary>
        public static IEnumerable<string> DescribeEntries(byte[] sector0)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                int o = EntriesOffset + (slot * EntrySize);
                yield return string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "MBR entry {0}: status 0x{1:X2}, type 0x{2:X2}, start LBA {3}, sectors {4}",
                    slot + 1,
                    sector0[o],
                    sector0[o + 4],
                    BinaryHelpers.U32(sector0, o + 8),
                    BinaryHelpers.U32(sector0, o + 12));
            }
        }

        private static bool ReadLogicals(IBlockDevice device, long extStart, long extCount, List<PartitionInfo> partitions)
        {
            int sectorSize = device.SectorSize;
            long extEnd = extStart + extCount;
            long deviceSectors = device.Size / sectorSize;
            var sector = new byte[sectorSize];
            var visited = new HashSet<long>();
            long ebr = extStart;
            int number = 5;

            while (visited.Count < MaxLogicalPartitions)
            {
                if (ebr < extStart || ebr >= extEnd || !visited.Add(ebr))
                {
                    return false;
                }

                if (ebr >= deviceSectors)
                {
                    return true; // the rest of the chain lies past the reported capacity
                }

                device.Read(ebr * sectorSize, sector, 0, sectorSize);
                if (!HasSignature(sector))
                {
                    return false;
                }

                byte type = sector[EntriesOffset + 4];
                long relStart = BinaryHelpers.U32(sector, EntriesOffset + 8);
                long count = BinaryHelpers.U32(sector, EntriesOffset + 12);
                if (type != 0 && count != 0)
                {
                    long start = ebr + relStart;
                    if (start + count > extEnd)
                    {
                        return false;
                    }

                    partitions.Add(new PartitionInfo
                    {
                        Number = number++,
                        Offset = start * sectorSize,
                        Length = count * sectorSize,
                        MbrType = type
                    });
                }

                int next = EntriesOffset + EntrySize;
                byte nextType = sector[next + 4];
                if (!IsExtendedType(nextType))
                {
                    return true;
                }

                ebr = extStart + BinaryHelpers.U32(sector, next + 8);
            }

            return false;
        }
    }
}
