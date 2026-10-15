using System;
using System.IO;
using System.Linq;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class PartitionBackupTests
    {
        public static TheoryData<string, string> Matrix()
        {
            var data = new TheoryData<string, string>();
            foreach (var ext in new[] { ".img", ".img.gz" })
            {
                foreach (var table in new[] { "MBR", "GPT" })
                {
                    data.Add(ext, table);
                }
            }

            return data;
        }

        internal static OperationResult BackupPartition(FakeDeviceAccess access, Devices.DeviceInfo info, int index, string path)
        {
            return EngineHarness.Run(access, new OperationRequest
            {
                Kind = OperationKind.BackupPartition,
                Device = info,
                Partition = info.PartitionTable.Partitions[index],
                ImagePath = path,
                Compression = CompressionChoice.Fastest
            });
        }

        [Theory]
        [MemberData(nameof(Matrix))]
        public void PartitionImage_EqualsSectorRange_AndHasMetadata(string extension, string table)
        {
            var device = BackupDeviceTests.BuildDevice(table, 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var partition = info.PartitionTable.Partitions[1];
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "part2" + extension);

            var result = BackupPartition(access, info, 1, path);

            Assert.Equal(OperationState.Completed, result.State);
            var expected = device.Data.Skip((int)partition.Offset).Take((int)partition.Length).ToArray();
            Assert.Equal(expected, EngineHarness.ReadImageContent(path));

            var metadata = PartitionMetadataFile.Read(PartitionMetadataFile.PathFor(path));
            Assert.Equal(1, metadata.FormatVersion);
            Assert.Equal(table, metadata.TableStyle);
            Assert.Equal(partition.Number, metadata.PartitionNumber);
            Assert.Equal(partition.Offset, metadata.Offset);
            Assert.Equal(partition.Length, metadata.Length);
            Assert.Equal(512, metadata.SectorSize);
            Assert.Equal(EngineHarness.Sha(expected), metadata.Sha256);
            Assert.Equal(device.Size, metadata.SourceSize);
            if (table == "GPT")
            {
                Assert.Equal(TestImageBuilder.BasicDataType, Guid.Parse(metadata.GptType));
            }
            else
            {
                Assert.Equal(0x07, metadata.MbrType);
            }

            var json = File.ReadAllText(PartitionMetadataFile.PathFor(path));
            Assert.Contains("\"formatVersion\"", json);
            Assert.Contains("\"sha256\"", json);
        }
    }
}
