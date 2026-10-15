using System;
using System.Text;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Partitioning
{
    public class FileSystemSizeTests
    {
        private static void Put16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        private static void Put32(byte[] b, int o, long v) => BitConverter.GetBytes((uint)v).CopyTo(b, o);

        private static void Put64(byte[] b, int o, long v) => BitConverter.GetBytes(v).CopyTo(b, o);

        internal static void StampFat16(byte[] target, long offset, long totalSectors)
        {
            Encoding.ASCII.GetBytes("FAT16   ").CopyTo(target, offset + 54);
            Put16(target, (int)offset + 0x0B, 512);
            if (totalSectors < 0x10000)
            {
                Put16(target, (int)offset + 0x13, (int)totalSectors);
                Put32(target, (int)offset + 0x20, 0);
            }
            else
            {
                Put16(target, (int)offset + 0x13, 0);
                Put32(target, (int)offset + 0x20, totalSectors);
            }
        }

        [Fact]
        public void Fat16AndFat32_UseTheRightTotalField()
        {
            var small = new byte[4096];
            StampFat16(small, 0, 30000);
            Assert.Equal(30000L * 512, FileSystemProbe.DetectSize(small));

            var large = new byte[4096];
            StampFat16(large, 0, 244026);
            Assert.Equal(244026L * 512, FileSystemProbe.DetectSize(large));

            var fat32 = new byte[4096];
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(fat32, 82);
            Put16(fat32, 0x0B, 512);
            Put32(fat32, 0x20, 524288);
            Assert.Equal(524288L * 512, FileSystemProbe.DetectSize(fat32));
        }

        [Fact]
        public void Ntfs_IncludesBackupBootSector()
        {
            var data = new byte[4096];
            Encoding.ASCII.GetBytes("NTFS    ").CopyTo(data, 3);
            Put16(data, 0x0B, 512);
            Put64(data, 0x28, 1000);
            Assert.Equal(1001L * 512, FileSystemProbe.DetectSize(data));
        }

        [Fact]
        public void ExFat_UsesVolumeLengthAndShift()
        {
            var data = new byte[4096];
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(data, 3);
            Put64(data, 0x48, 2048);
            data[0x6C] = 12;
            Assert.Equal(2048L * 4096, FileSystemProbe.DetectSize(data));
        }

        [Fact]
        public void Ext4_UsesBlockCountAndBlockSize_Including64BitHigh()
        {
            var data = new byte[4096];
            data[1024 + 56] = 0x53;
            data[1024 + 57] = 0xEF;
            Put32(data, 1024 + 4, 3750400);
            Put32(data, 1024 + 24, 2); // 4096-byte blocks
            Put32(data, 1024 + 96, 0x40 | 0x80); // extents + 64bit
            Put32(data, 1024 + 0x150, 1);
            Assert.Equal(((1L << 32) + 3750400) * 4096, FileSystemProbe.DetectSize(data));
        }

        [Fact]
        public void UnknownData_HasNoSize()
        {
            Assert.Null(FileSystemProbe.DetectSize(new byte[4096]));
        }

        [Fact]
        public void SloppyEntryWithFittingFileSystem_IsUsable()
        {
            // The 128 MB MMC card from the field: entry starts at 99 and is as long as the whole card.
            var device = TestImageBuilder.Mbr(244125L * 512, 512, new TestPartition(99L * 512, 244125L * 512, 0x06));
            StampFat16(device.Data, 99L * 512, 244026);

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Mbr, table.Style);
            Assert.Equal("FAT16", table.Partitions[0].FileSystem);
            Assert.True(table.EntriesOvershootDevice);
            Assert.False(table.ExtendsBeyondDevice);
        }

        [Fact]
        public void FileSystemPastReportedEnd_IsNotUsable()
        {
            var device = TestImageBuilder.Mbr(244125L * 512, 512, new TestPartition(99L * 512, 244125L * 512, 0x06));
            StampFat16(device.Data, 99L * 512, 244125);

            Assert.True(PartitionTableReader.Read(device).ExtendsBeyondDevice);
        }

        [Fact]
        public void RicohLikeSdhc_FirstFat32PartitionAlreadyCrossesTheEnd()
        {
            // Reader exposes 8 MiB; FAT32 boot partition spans 1..9 MiB, ext4 follows.
            var device = TestImageBuilder.Mbr(8 << 20, 512,
                new TestPartition(1 << 20, 8 << 20, 0x0C),
                new TestPartition(9 << 20, 64 << 20, 0x83));
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(device.Data, (1 << 20) + 82);
            Put16(device.Data, (1 << 20) + 0x0B, 512);
            Put32(device.Data, (1 << 20) + 0x20, (8 << 20) / 512);

            Assert.True(PartitionTableReader.Read(device).ExtendsBeyondDevice);
        }
    }
}
