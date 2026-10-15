using System.IO;
using System.Linq;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class PartitionRestoreTests
    {
        private static OperationResult RestorePartition(FakeDeviceAccess access, Devices.DeviceInfo info, int index, string path)
        {
            return EngineHarness.Run(access, new OperationRequest
            {
                Kind = OperationKind.RestorePartition,
                Device = info,
                Partition = info.PartitionTable.Partitions[index],
                ImagePath = path,
                VerifyAfter = true
            });
        }

        [Theory]
        [MemberData(nameof(PartitionBackupTests.Matrix), MemberType = typeof(PartitionBackupTests))]
        public void PartitionRestore_ChangesOnlyTargetRange(string extension, string table)
        {
            var source = BackupDeviceTests.BuildDevice(table, 512);
            var access = new FakeDeviceAccess();
            var sourceInfo = access.Add(1, source);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "p" + extension);
            Assert.Equal(OperationState.Completed, PartitionBackupTests.BackupPartition(access, sourceInfo, 1, path).State);

            // Same layout, different content.
            var target = BackupDeviceTests.BuildDevice(table, 512);
            target.Fill(12345);
            var layoutSource = BackupDeviceTests.BuildDevice(table, 512);
            var tableBytes = table == "MBR" ? 512 : 34 * 512;
            System.Buffer.BlockCopy(layoutSource.Data, 0, target.Data, 0, tableBytes);
            System.Buffer.BlockCopy(layoutSource.Data, (int)(target.Size - (33 * 512)), target.Data, (int)(target.Size - (33 * 512)), 33 * 512);
            var targetInfo = access.Add(2, target);
            var before = (byte[])target.Data.Clone();
            var p = targetInfo.PartitionTable.Partitions[1];

            var result = RestorePartition(access, targetInfo, 1, path);

            Assert.Equal(OperationState.Completed, result.State);
            var sourcePart = source.Data.Skip((int)p.Offset).Take((int)p.Length).ToArray();
            Assert.Equal(sourcePart, target.Data.Skip((int)p.Offset).Take((int)p.Length).ToArray());
            Assert.Equal(before.Take((int)p.Offset).ToArray(), target.Data.Take((int)p.Offset).ToArray());
            Assert.Equal(before.Skip((int)p.End).ToArray(), target.Data.Skip((int)p.End).ToArray());
        }

        [Fact]
        public void SmallerTargetPartition_IsRefused()
        {
            var source = TestImageBuilder.Mbr(8 << 20, 512, new TestPartition(1 << 20, 4 << 20, 0x07));
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "p.img");
            PartitionBackupTests.BackupPartition(access, access.Add(1, source), 0, path);
            var target = TestImageBuilder.Mbr(8 << 20, 512, new TestPartition(1 << 20, 2 << 20, 0x07));
            var info = access.Add(2, target);
            var before = (byte[])target.Data.Clone();

            var result = RestorePartition(access, info, 0, path);

            Assert.Equal(ErrorCode.SizeMismatch, result.ErrorCode);
            Assert.Equal(before, target.Data);
        }

        [Fact]
        public void MissingMetadata_IsRefused()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, source);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "p.img");
            PartitionBackupTests.BackupPartition(access, info, 1, path);
            File.Delete(PartitionMetadataFile.PathFor(path));

            var result = RestorePartition(access, access.Add(2, BackupDeviceTests.BuildDevice("MBR", 512)), 1, path);

            Assert.Equal(ErrorCode.MissingPartitionMetadata, result.ErrorCode);
        }

        [Fact]
        public void NewerMetadataVersion_IsRefused()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, source);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "p.img");
            PartitionBackupTests.BackupPartition(access, info, 1, path);
            var metaPath = PartitionMetadataFile.PathFor(path);
            var json = File.ReadAllText(metaPath);
            var bumped = System.Text.RegularExpressions.Regex.Replace(json, "\"formatVersion\"\\s*:\\s*1", "\"formatVersion\": 2");
            Assert.NotEqual(json, bumped);
            File.WriteAllText(metaPath, bumped);

            var result = RestorePartition(access, access.Add(2, BackupDeviceTests.BuildDevice("MBR", 512)), 1, path);

            Assert.Equal(ErrorCode.UnsupportedMetadata, result.ErrorCode);
        }

        [Fact]
        public void PartitionImage_CannotBeRestoredToWholeDevice()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, source);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "p.img");
            PartitionBackupTests.BackupPartition(access, info, 1, path);
            var target = new MemoryBlockDevice(source.Size, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path);

            Assert.Equal(ErrorCode.InvalidRequest, result.ErrorCode);
            Assert.Equal(0, target.WriteCalls);
        }
    }
}
