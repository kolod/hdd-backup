using System;
using System.Collections.Generic;
using System.Text;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Partitioning
{
    /// <summary>Parses GUID partition tables, falling back to the backup header and array.</summary>
    public static class GptParser
    {
        private const string Signature = "EFI PART";
        private const int MinHeaderSize = 92;
        private const int MaxEntries = 1024;

        /// <summary>Returns the table, or null when neither header copy is valid.</summary>
        public static PartitionTable TryParse(IBlockDevice device)
        {
            long sectorSize = device.SectorSize;
            long lastLba = (device.Size / sectorSize) - 1;
            if (lastLba < 3)
            {
                return null;
            }

            var primary = ReadHeader(device, 1, lastLba);
            var backup = ReadHeader(device, lastLba, lastLba);

            foreach (var header in new[] { primary, backup })
            {
                if (header == null)
                {
                    continue;
                }

                var entries = ReadEntries(device, header, lastLba);
                if (entries == null)
                {
                    // Header is fine but its array is damaged: try the other copy's array.
                    var other = ReferenceEquals(header, primary) ? backup : primary;
                    entries = other == null ? null : ReadEntries(device, other, lastLba);
                }

                if (entries != null)
                {
                    return new PartitionTable(TableStyle.Gpt, entries, header.DiskGuid);
                }
            }

            return null;
        }

        private static Header ReadHeader(IBlockDevice device, long lba, long lastLba)
        {
            int sectorSize = device.SectorSize;
            var sector = new byte[sectorSize];
            device.Read(lba * sectorSize, sector, 0, sectorSize);

            if (!BinaryHelpers.AsciiAt(sector, 0, Signature))
            {
                return null;
            }

            int headerSize = (int)BinaryHelpers.U32(sector, 12);
            if (headerSize < MinHeaderSize || headerSize > sectorSize)
            {
                return null;
            }

            uint storedCrc = BinaryHelpers.U32(sector, 16);
            var copy = new byte[headerSize];
            Buffer.BlockCopy(sector, 0, copy, 0, headerSize);
            BinaryHelpers.PutU32(copy, 16, 0);
            if (Crc32.Compute(copy, 0, headerSize) != storedCrc)
            {
                return null;
            }

            var header = new Header
            {
                MyLba = BinaryHelpers.U64(sector, 24),
                FirstUsable = BinaryHelpers.U64(sector, 40),
                LastUsable = BinaryHelpers.U64(sector, 48),
                DiskGuid = new Guid(Slice(sector, 56, 16)),
                EntriesLba = BinaryHelpers.U64(sector, 72),
                EntryCount = (int)Math.Min(BinaryHelpers.U32(sector, 80), int.MaxValue),
                EntrySize = (int)Math.Min(BinaryHelpers.U32(sector, 84), int.MaxValue),
                EntriesCrc = BinaryHelpers.U32(sector, 88)
            };

            bool valid = header.MyLba == lba
                && header.EntryCount > 0 && header.EntryCount <= MaxEntries
                && header.EntrySize >= 128 && header.EntrySize % 8 == 0 && header.EntrySize <= 4096
                && header.EntriesLba > 0 && header.EntriesLba <= lastLba
                && header.FirstUsable <= header.LastUsable && header.LastUsable <= lastLba;
            return valid ? header : null;
        }

        private static List<PartitionInfo> ReadEntries(IBlockDevice device, Header header, long lastLba)
        {
            int sectorSize = device.SectorSize;
            long arrayBytes = (long)header.EntryCount * header.EntrySize;
            long sectors = (arrayBytes + sectorSize - 1) / sectorSize;
            if (header.EntriesLba + sectors - 1 > lastLba)
            {
                return null;
            }

            var array = new byte[sectors * sectorSize];
            device.Read(header.EntriesLba * sectorSize, array, 0, array.Length);
            if (Crc32.Compute(array, 0, (int)arrayBytes) != header.EntriesCrc)
            {
                return null;
            }

            var result = new List<PartitionInfo>();
            int number = 1;
            for (int i = 0; i < header.EntryCount; i++)
            {
                int o = i * header.EntrySize;
                var type = new Guid(Slice(array, o, 16));
                if (type == Guid.Empty)
                {
                    continue;
                }

                long first = BinaryHelpers.U64(array, o + 32);
                long last = BinaryHelpers.U64(array, o + 40);
                if (first < 1 || last < first || last > lastLba)
                {
                    return null;
                }

                result.Add(new PartitionInfo
                {
                    Number = number++,
                    Offset = first * sectorSize,
                    Length = (last - first + 1) * sectorSize,
                    GptType = type,
                    GptId = new Guid(Slice(array, o + 16, 16)),
                    Name = Encoding.Unicode.GetString(array, o + 56, Math.Min(72, header.EntrySize - 56)).TrimEnd('\0')
                });
            }

            result.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        private sealed class Header
        {
            public long MyLba;
            public long FirstUsable;
            public long LastUsable;
            public Guid DiskGuid;
            public long EntriesLba;
            public int EntryCount;
            public int EntrySize;
            public uint EntriesCrc;
        }
    }
}
