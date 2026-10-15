using System.IO;
using HddBackup.Core.Errors;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Safety
{
    public class DeviceRemovedTests
    {
        [Fact]
        public void SourceRemovedDuringBackup_FailsWithOffset_AndLeavesNoFiles()
        {
            long failAt = 2L << 20;
            var device = new FaultyBlockDevice(6 << 20, 512, failAt, ErrorCode.DeviceRemoved, failReads: true, failWrites: false);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();

            var result = EngineHarness.Backup(access, info, Path.Combine(dir, "disk.img.gz"));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DeviceRemoved, result.ErrorCode);
            Assert.Equal(failAt, result.ErrorOffset);
            Assert.False(result.SafeToRemove); // already gone: the UI says "already removed"
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void TargetRemovedDuringRestore_FailsWithOffset()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img");
            EngineHarness.Backup(access, access.Add(1, source), path);
            long failAt = 3L << 20;
            var target = new FaultyBlockDevice(source.Size, 512, failAt, ErrorCode.DeviceRemoved, failReads: false, failWrites: true);

            var result = EngineHarness.Restore(access, access.Add(2, target), path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DeviceRemoved, result.ErrorCode);
            Assert.Equal(failAt, result.ErrorOffset);
            Assert.False(result.SafeToRemove);
            Assert.False(result.ContentComplete);
            Assert.Equal(1, access.ExclusiveReleases);
        }
    }
}
