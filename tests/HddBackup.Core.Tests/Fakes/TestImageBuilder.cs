using System;
using System.Collections.Generic;
using System.Text;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Tests.Fakes
{
    public sealed class TestPartition
    {
        public TestPartition(long offset, long length, byte mbrType)
        {
            Offset = offset;
            Length = length;
            MbrType = mbrType;
        }

        public long Offset { get; }

        public long Length { get; }

        public byte MbrType { get; }

        public string Name { get; set; } = string.Empty;

        /// <summary>"FAT32", "exFAT" or "NTFS" to stamp a boot sector signature.</summary>
        public string FileSystemSignature { get; set; }
    }

    /// <summary>Builds in-memory disks with deterministic content and valid partition tables.</summary>
    public static class TestImageBuilder
    {
        public static readonly Guid DiskGuid = new Guid("11111111-2222-3333-4444-555555555555");
        public static readonly Guid BasicDataType = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

        public static MemoryBlockDevice Mbr(long size, int sectorSize, params TestPartition[] partitions)
        {
            var device = new MemoryBlockDevice(size, sectorSize);
            device.Fill(size.GetHashCode() ^ sectorSize);
            var sector0 = NewMbrSector(sectorSize);
            for (int i = 0; i < partitions.Length; i++)
            {
                WriteEntry(sector0, i, partitions[i].MbrType, partitions[i].Offset / sectorSize, partitions[i].Length / sectorSize);
            }

            Buffer.BlockCopy(sector0, 0, device.Data, 0, sectorSize);
            StampFileSystems(device, partitions);
            return device;
        }

        public static MemoryBlockDevice MbrWithLogicals(
            long size, int sectorSize, TestPartition primary, long extendedOffset, long extendedLength, TestPartition[] logicals)
        {
            var device = new MemoryBlockDevice(size, sectorSize);
            device.Fill(17);
            long ss = sectorSize;
            var sector0 = NewMbrSector(sectorSize);
            WriteEntry(sector0, 0, primary.MbrType, primary.Offset / ss, primary.Length / ss);
            WriteEntry(sector0, 1, 0x0F, extendedOffset / ss, extendedLength / ss);
            Buffer.BlockCopy(sector0, 0, device.Data, 0, sectorSize);

            long extStart = extendedOffset / ss;
            for (int i = 0; i < logicals.Length; i++)
            {
                // Each EBR sits one MiB before its logical partition.
                long ebr = (logicals[i].Offset / ss) - ((1 << 20) / ss);
                var sector = NewMbrSector(sectorSize);
                WriteEntry(sector, 0, logicals[i].MbrType, (logicals[i].Offset / ss) - ebr, logicals[i].Length / ss);
                if (i + 1 < logicals.Length)
                {
                    long nextEbr = (logicals[i + 1].Offset / ss) - ((1 << 20) / ss);
                    WriteEntry(sector, 1, 0x05, nextEbr - extStart, 1);
                }

                Buffer.BlockCopy(sector, 0, device.Data, (int)(ebr * ss), sectorSize);
            }

            return device;
        }

        public static MemoryBlockDevice Gpt(long size, int sectorSize, params TestPartition[] partitions)
        {
            var device = new MemoryBlockDevice(size, sectorSize);
            device.Fill(size.GetHashCode() ^ (sectorSize * 31));
            long ss = sectorSize;
            long lastLba = (size / ss) - 1;
            const int entryCount = 128;
            const int entrySize = 128;
            int arrayBytes = entryCount * entrySize;
            long arraySectors = (arrayBytes + ss - 1) / ss;

            // Protective MBR.
            var mbr = NewMbrSector(sectorSize);
            WriteEntry(mbr, 0, MbrParser.ProtectiveType, 1, Math.Min(lastLba, uint.MaxValue));
            Buffer.BlockCopy(mbr, 0, device.Data, 0, sectorSize);

            var array = new byte[arraySectors * ss];
            for (int i = 0; i < partitions.Length; i++)
            {
                int o = i * entrySize;
                Buffer.BlockCopy(BasicDataType.ToByteArray(), 0, array, o, 16);
                Buffer.BlockCopy(new Guid(i + 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11).ToByteArray(), 0, array, o + 16, 16);
                PutU64(array, o + 32, partitions[i].Offset / ss);
                PutU64(array, o + 40, ((partitions[i].Offset + partitions[i].Length) / ss) - 1);
                var name = Encoding.Unicode.GetBytes(partitions[i].Name);
                Buffer.BlockCopy(name, 0, array, o + 56, Math.Min(72, name.Length));
            }

            uint arrayCrc = Crc32.Compute(array, 0, arrayBytes);
            long backupArrayLba = lastLba - arraySectors;

            Buffer.BlockCopy(array, 0, device.Data, (int)(2 * ss), array.Length);
            Buffer.BlockCopy(array, 0, device.Data, (int)(backupArrayLba * ss), array.Length);

            var primary = GptHeader(sectorSize, 1, lastLba, 2, arraySectors, entryCount, entrySize, arrayCrc, lastLba);
            var backup = GptHeader(sectorSize, lastLba, 1, backupArrayLba, arraySectors, entryCount, entrySize, arrayCrc, lastLba);
            Buffer.BlockCopy(primary, 0, device.Data, (int)ss, sectorSize);
            Buffer.BlockCopy(backup, 0, device.Data, (int)(lastLba * ss), sectorSize);
            StampFileSystems(device, partitions);
            return device;
        }

        private static byte[] GptHeader(
            int sectorSize, long myLba, long alternateLba, long entriesLba, long arraySectors,
            int entryCount, int entrySize, uint arrayCrc, long lastLba)
        {
            var h = new byte[sectorSize];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(h, 0);
            PutU32(h, 8, 0x00010000);
            PutU32(h, 12, 92);
            PutU64(h, 24, myLba);
            PutU64(h, 32, alternateLba);
            PutU64(h, 40, 2 + arraySectors);
            PutU64(h, 48, lastLba - arraySectors - 1);
            Buffer.BlockCopy(DiskGuid.ToByteArray(), 0, h, 56, 16);
            PutU64(h, 72, entriesLba);
            PutU32(h, 80, (uint)entryCount);
            PutU32(h, 84, (uint)entrySize);
            PutU32(h, 88, arrayCrc);
            PutU32(h, 16, Crc32.Compute(h, 0, 92));
            return h;
        }

        private static byte[] NewMbrSector(int sectorSize)
        {
            var sector = new byte[sectorSize];
            sector[510] = 0x55;
            sector[511] = 0xAA;
            return sector;
        }

        private static void WriteEntry(byte[] sector, int slot, byte type, long start, long count)
        {
            int o = 446 + (slot * 16);
            sector[o] = 0x00;
            sector[o + 4] = type;
            PutU32(sector, o + 8, (uint)start);
            PutU32(sector, o + 12, (uint)count);
        }

        private static void StampFileSystems(MemoryBlockDevice device, IEnumerable<TestPartition> partitions)
        {
            foreach (var p in partitions)
            {
                if (p.Offset + 4096 > device.Size)
                {
                    continue; // partition lies past the (simulated) reported capacity
                }

                if (p.FileSystemSignature == null)
                {
                    // Make sure random data does not accidentally look like a file system.
                    device.Data[p.Offset + 3] = 0;
                    device.Data[p.Offset + 54] = 0;
                    device.Data[p.Offset + 82] = 0;
                    device.Data[p.Offset + 1024 + 56] = 0;
                    continue;
                }

                switch (p.FileSystemSignature)
                {
                    case "FAT32":
                        Encoding.ASCII.GetBytes("FAT32   ").CopyTo(device.Data, p.Offset + 82);
                        break;
                    case "exFAT":
                        Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(device.Data, p.Offset + 3);
                        break;
                    case "NTFS":
                        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(device.Data, p.Offset + 3);
                        break;
                }
            }
        }

        private static void PutU32(byte[] b, int o, uint v)
        {
            BitConverter.GetBytes(v).CopyTo(b, o);
        }

        private static void PutU64(byte[] b, int o, long v)
        {
            BitConverter.GetBytes(v).CopyTo(b, o);
        }
    }
}
