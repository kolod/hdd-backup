using System.IO;
using HddBackup.Core.Errors;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Safety
{
    public class LockFailureTests
    {
        [Fact]
        public void LockFailure_WritesNothing()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img");
            EngineHarness.Backup(access, access.Add(1, source), path);
            var target = new MemoryBlockDevice(source.Size, 512);
            target.Fill(3);
            var before = (byte[])target.Data.Clone();
            var info = access.Add(2, target);
            access.FailLock = true;

            var result = EngineHarness.Restore(access, info, path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.LockFailed, result.ErrorCode);
            Assert.Equal(0, target.WriteCalls);
            Assert.Equal(before, target.Data);
            Assert.True(result.SafeToRemove);
        }
    }
}
