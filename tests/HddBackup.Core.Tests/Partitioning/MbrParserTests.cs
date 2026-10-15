using System.Linq;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Partitioning
{
    public class MbrParserTests
    {
        [Fact]
        public void BlankDevice_HasNoTable()
        {
            var device = new MemoryBlockDevice(4 << 20, 512);

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.None, table.Style);
            Assert.Empty(table.Partitions);
        }

        [Theory]
        [InlineData(512)]
        [InlineData(4096)]
        public void FourPrimaryPartitions_AreParsedInOrder(int sectorSize)
        {
            var device = TestImageBuilder.Mbr(16 << 20, sectorSize,
                new TestPartition(1 << 20, 2 << 20, 0x0C),
                new TestPartition(3 << 20, 2 << 20, 0x07),
                new TestPartition(5 << 20, 4 << 20, 0x83),
                new TestPartition(9 << 20, 6 << 20, 0x0B));

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Mbr, table.Style);
            Assert.Equal(4, table.Partitions.Count);
            Assert.Equal(1 << 20, table.Partitions[0].Offset);
            Assert.Equal(2 << 20, table.Partitions[0].Length);
            Assert.Equal((byte)0x0C, table.Partitions[0].MbrType);
            Assert.Equal(9 << 20, table.Partitions[3].Offset);
            Assert.Equal(new[] { 1, 2, 3, 4 }, table.Partitions.Select(p => p.Number));
        }

        [Fact]
        public void ExtendedPartition_LogicalPartitionsAreListedAndContainerIsNot()
        {
            var device = TestImageBuilder.MbrWithLogicals(32 << 20, 512,
                new TestPartition(1 << 20, 4 << 20, 0x0C),
                extendedOffset: 8 << 20,
                extendedLength: 20 << 20,
                logicals: new[]
                {
                    new TestPartition(9 << 20, 4 << 20, 0x07),
                    new TestPartition(15 << 20, 6 << 20, 0x83)
                });

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Mbr, table.Style);
            Assert.Equal(3, table.Partitions.Count);
            Assert.Equal(1, table.Partitions[0].Number);
            Assert.Equal(5, table.Partitions[1].Number);
            Assert.Equal(9 << 20, table.Partitions[1].Offset);
            Assert.Equal(6, table.Partitions[2].Number);
            Assert.Equal(15 << 20, table.Partitions[2].Offset);
            Assert.Equal(6 << 20, table.Partitions[2].Length);
        }

        [Fact]
        public void ProtectiveMbr_IsDetected()
        {
            var device = TestImageBuilder.Gpt(16 << 20, 512, new TestPartition(1 << 20, 8 << 20, 0));
            var sector0 = new byte[512];
            device.Read(0, sector0, 0, 512);

            Assert.True(MbrParser.HasSignature(sector0));
            Assert.True(MbrParser.IsProtective(sector0));
        }

        [Fact]
        public void PartitionBeyondDeviceEnd_IsKept_AndFlagged()
        {
            // Like an old card reader that reports 8 MiB for a larger card.
            var device = TestImageBuilder.Mbr(8 << 20, 512,
                new TestPartition(1 << 20, 4 << 20, 0x0C),
                new TestPartition(5 << 20, 64 << 20, 0x83));

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Mbr, table.Style);
            Assert.True(table.ExtendsBeyondDevice);
            Assert.Equal(2, table.Partitions.Count);
        }

        [Fact]
        public void PartitionsWithinDevice_AreNotFlagged()
        {
            var device = TestImageBuilder.Mbr(8 << 20, 512, new TestPartition(1 << 20, 4 << 20, 0x0C));

            Assert.False(PartitionTableReader.Read(device).ExtendsBeyondDevice);
        }

        [Fact]
        public void InvalidStatusByte_MakesTableUnknown()
        {
            var device = TestImageBuilder.Mbr(8 << 20, 512, new TestPartition(1 << 20, 4 << 20, 0x0C));
            device.Data[446] = 0x12; // neither 0x00 nor 0x80
            for (int i = 3; i < 90; i++)
            {
                device.Data[i] = 0; // no file system signature either
            }

            Assert.Equal(TableStyle.Unknown, PartitionTableReader.Read(device).Style);
        }

        [Fact]
        public void FileSystemsAreProbed()
        {
            var device = TestImageBuilder.Mbr(16 << 20, 512,
                new TestPartition(1 << 20, 4 << 20, 0x0C) { FileSystemSignature = "FAT32" },
                new TestPartition(5 << 20, 4 << 20, 0x07) { FileSystemSignature = "exFAT" },
                new TestPartition(9 << 20, 4 << 20, 0x07) { FileSystemSignature = "NTFS" });

            var table = PartitionTableReader.Read(device);

            Assert.Equal("FAT32", table.Partitions[0].FileSystem);
            Assert.Equal("exFAT", table.Partitions[1].FileSystem);
            Assert.Equal("NTFS", table.Partitions[2].FileSystem);
        }
    }
}
