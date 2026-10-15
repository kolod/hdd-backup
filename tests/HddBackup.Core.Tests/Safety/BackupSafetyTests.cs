using System.IO;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Safety
{
    public class BackupSafetyTests
    {
        [Fact]
        public void Source_IsNeverWritten()
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var before = (byte[])device.Data.Clone();
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);

            var result = EngineHarness.Backup(access, info, Path.Combine(TestPaths.NewTempDirectory(), "x.img.gz"), verify: true);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.False(access.AnyWriteAttemptOnReadHandle);
            Assert.Equal(0, device.WriteCalls);
            Assert.Equal(before, device.Data);
            Assert.Equal(0, access.ExclusiveOpens);
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".img.gz")]
        public void ReadError_ReportsOffset_AndLeavesNoFiles(string extension)
        {
            long failAt = (3 << 20) + 4096;
            var device = new FaultyBlockDevice(6 << 20, 512, failAt, ErrorCode.ReadError, failReads: true, failWrites: false);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();

            var result = EngineHarness.Backup(access, info, Path.Combine(dir, "disk" + extension));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ReadError, result.ErrorCode);
            Assert.Equal(failAt, result.ErrorOffset);
            Assert.False(result.ContentComplete);
            Assert.True(result.SafeToRemove);
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal(1, access.ReadReleases);
            Assert.Equal(0, access.EjectCalls);
        }

        [Fact]
        public void DestinationTooSmallForRawImage_IsRefusedBeforeReading()
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess { PathInfo = new PathInfo { FreeBytes = 1 << 20, FileSystem = "NTFS" } };
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();

            var result = EngineHarness.Backup(access, info, Path.Combine(dir, "disk.img"));

            Assert.Equal(ErrorCode.DestinationFull, result.ErrorCode);
            Assert.Equal(0, access.ReadOpens);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void ImageOnSourceDevice_IsRefused()
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess { PathInfo = new PathInfo { DiskNumbers = new[] { 1 }, FreeBytes = long.MaxValue } };
            var info = access.Add(1, device);

            var engine = EngineHarness.NewEngine(access);
            var ex = Assert.Throws<ImagingException>(() => engine.Validate(new OperationRequest
            {
                Kind = OperationKind.BackupDevice,
                Device = info,
                ImagePath = Path.Combine(TestPaths.NewTempDirectory(), "disk.img")
            }));
            Assert.Equal(ErrorCode.ImageOnSource, ex.Code);
        }

        [Fact]
        public void LargeRawImageOnFat32_IsRefused_GzipOnlyWarns()
        {
            var info = new DeviceInfo { DiskNumber = 1, Size = 8L << 30, LogicalSectorSize = 512 };
            var fat = new PathInfo { FreeBytes = long.MaxValue, FileSystem = "FAT32" };
            var dir = TestPaths.NewTempDirectory();

            var ex = Assert.Throws<ImagingException>(() => DestinationChecks.Check(
                new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = Path.Combine(dir, "a.img") }, fat));
            Assert.Equal(ErrorCode.FileTooLargeForFs, ex.Code);

            var warnings = DestinationChecks.Check(
                new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = Path.Combine(dir, "a.img.gz") }, fat);
            Assert.Contains(ValidationWarning.MayExceedFat32Limit, warnings);
        }

        [Fact]
        public void ReaderWithMisreportedCapacity_IsRefusedForEveryOperation()
        {
            // Like an old built-in reader that exposes 8 MiB of a larger card.
            var device = TestImageBuilder.Mbr(8 << 20, 512,
                new TestPartition(1 << 20, 4 << 20, 0x0C),
                new TestPartition(5 << 20, 64 << 20, 0x83));
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            Assert.True(info.IsCapacityMisreported);
            var dir = TestPaths.NewTempDirectory();
            var before = (byte[])device.Data.Clone();

            var image = Path.Combine(dir, "small.img");
            File.WriteAllBytes(image, new byte[1 << 20]);

            foreach (var request in new[]
            {
                new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = Path.Combine(dir, "a.img") },
                new OperationRequest { Kind = OperationKind.BackupPartition, Device = info, Partition = info.PartitionTable.Partitions[0], ImagePath = Path.Combine(dir, "p.img") },
                new OperationRequest { Kind = OperationKind.RestoreDevice, Device = info, ImagePath = image }
            })
            {
                var ex = Assert.Throws<ImagingException>(() => EngineHarness.NewEngine(access).Validate(request));
                Assert.Equal(ErrorCode.UnsuitableReader, ex.Code);
                var result = EngineHarness.Run(access, request);
                Assert.Equal(ErrorCode.UnsuitableReader, result.ErrorCode);
            }

            Assert.Equal(0, access.ReadOpens);
            Assert.Equal(0, access.ExclusiveOpens);
            Assert.Equal(before, device.Data);
            Assert.Equal(new[] { image }, Directory.GetFiles(dir));
        }

        [Fact]
        public void UnsuitableReader_WithExplicitOverride_BacksUpWhatIsVisible_ButNeverRestores()
        {
            var device = TestImageBuilder.Mbr(8 << 20, 512,
                new TestPartition(1 << 20, 4 << 20, 0x0C),
                new TestPartition(5 << 20, 64 << 20, 0x83));
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "card_incomplete.img");
            var backup = new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = path, AllowIncompleteBackup = true };

            Assert.Contains(ValidationWarning.IncompleteBackup, EngineHarness.NewEngine(access).Validate(backup));
            var result = EngineHarness.Run(access, backup);

            Assert.Equal(Core.Progress.OperationState.Completed, result.State);
            Assert.True(result.ImageIncomplete);
            Assert.Equal(device.Data, File.ReadAllBytes(path));

            var restore = new OperationRequest { Kind = OperationKind.RestoreDevice, Device = info, ImagePath = path, AllowIncompleteBackup = true };
            var ex = Assert.Throws<ImagingException>(() => EngineHarness.NewEngine(access).Validate(restore));
            Assert.Equal(ErrorCode.UnsuitableReader, ex.Code);
            Assert.Equal(0, access.ExclusiveOpens);
        }

        [Fact]
        public void SmallCardWhosePartitionsFit_RemainsUsable()
        {
            var device = TestImageBuilder.Mbr(8 << 20, 512, new TestPartition(1 << 20, 6 << 20, 0x0C));
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            Assert.False(info.IsCapacityMisreported);

            var result = EngineHarness.Backup(access, info, Path.Combine(TestPaths.NewTempDirectory(), "small.img"));

            Assert.Equal(Core.Progress.OperationState.Completed, result.State);
        }

        [Fact]
        public void ExistingImageAndSidecars_SurviveAFailedBackup()
        {
            var good = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var goodInfo = access.Add(1, good);
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "disk.img.gz");
            EngineHarness.Backup(access, goodInfo, path);
            var originalSha = File.ReadAllText(path + ".sha256");

            var faulty = new FaultyBlockDevice(6 << 20, 512, 2 << 20, ErrorCode.ReadError, true, false);
            var faultyInfo = access.Add(2, faulty);
            var result = EngineHarness.Backup(access, faultyInfo, path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(originalSha, File.ReadAllText(path + ".sha256"));
            Assert.Equal(EngineHarness.Sha(good.Data), EngineHarness.Sha(EngineHarness.ReadImageContent(path)));
        }
    }
}
