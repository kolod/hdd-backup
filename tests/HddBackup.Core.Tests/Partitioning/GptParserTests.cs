using System;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Partitioning
{
    public class GptParserTests
    {
        [Theory]
        [InlineData(512)]
        [InlineData(4096)]
        public void ValidGpt_IsParsed(int sectorSize)
        {
            var device = TestImageBuilder.Gpt(32 << 20, sectorSize,
                new TestPartition(1 << 20, 8 << 20, 0) { Name = "EFI" },
                new TestPartition(9 << 20, 16 << 20, 0) { Name = "Data" });

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Gpt, table.Style);
            Assert.Equal(TestImageBuilder.DiskGuid, table.DiskGuid);
            Assert.Equal(2, table.Partitions.Count);
            Assert.Equal("EFI", table.Partitions[0].Name);
            Assert.Equal(1 << 20, table.Partitions[0].Offset);
            Assert.Equal(8 << 20, table.Partitions[0].Length);
            Assert.Equal(TestImageBuilder.BasicDataType, table.Partitions[1].GptType);
            Assert.NotEqual(Guid.Empty, table.Partitions[1].GptId.Value);
        }

        [Fact]
        public void CorruptPrimaryHeader_FallsBackToBackupHeader()
        {
            var device = TestImageBuilder.Gpt(32 << 20, 512, new TestPartition(1 << 20, 8 << 20, 0) { Name = "Data" });
            device.CorruptByte(512 + 20); // inside primary header CRC field

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Gpt, table.Style);
            Assert.Single(table.Partitions);
        }

        [Fact]
        public void BothHeadersCorrupt_TableIsUnknown()
        {
            var device = TestImageBuilder.Gpt(32 << 20, 512, new TestPartition(1 << 20, 8 << 20, 0));
            device.CorruptByte(512 + 20);
            device.CorruptByte(device.Size - 512 + 20);

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Unknown, table.Style);
        }

        [Fact]
        public void CorruptEntryArray_UsesBackupCopy()
        {
            var device = TestImageBuilder.Gpt(32 << 20, 512, new TestPartition(1 << 20, 8 << 20, 0));
            device.CorruptByte(2 * 512 + 40); // first entry, primary array

            var table = PartitionTableReader.Read(device);

            Assert.Equal(TableStyle.Gpt, table.Style);
            Assert.Equal(1 << 20, table.Partitions[0].Offset);
        }
    }
}
