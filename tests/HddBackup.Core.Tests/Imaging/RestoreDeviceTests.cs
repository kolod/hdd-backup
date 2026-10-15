using System.IO;
using System.Linq;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class RestoreDeviceTests
    {
        [Theory]
        [MemberData(nameof(BackupDeviceTests.Matrix), MemberType = typeof(BackupDeviceTests))]
        public void Restore_OntoEqualAndLargerDevices_IsByteIdentical(string extension, string table, int sectorSize)
        {
            var source = BackupDeviceTests.BuildDevice(table, sectorSize);
            var access = new FakeDeviceAccess();
            var sourceInfo = access.Add(1, source);
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "disk" + extension);
            Assert.Equal(OperationState.Completed, EngineHarness.Backup(access, sourceInfo, path).State);
            var filesBefore = Directory.GetFiles(dir).OrderBy(f => f).ToArray();

            var equal = new MemoryBlockDevice(source.Size, sectorSize);
            var equalInfo = access.Add(2, equal);
            var larger = new MemoryBlockDevice(source.Size + (2 << 20), sectorSize);
            larger.Fill(99);
            var largerTail = larger.Data.Skip((int)source.Size).ToArray();
            var largerInfo = access.Add(3, larger);

            var r1 = EngineHarness.Restore(access, equalInfo, path);
            var r2 = EngineHarness.Restore(access, largerInfo, path);

            Assert.Equal(OperationState.Completed, r1.State);
            Assert.Equal(OperationState.Completed, r2.State);
            Assert.True(r1.SafeToRemove && r1.ContentComplete);
            Assert.Equal(source.Data, equal.Data);
            Assert.Equal(source.Data, larger.Data.Take((int)source.Size).ToArray());
            Assert.Equal(largerTail, larger.Data.Skip((int)source.Size).ToArray());

            // Streaming restore: no temporary files appear next to the image.
            Assert.Equal(filesBefore, Directory.GetFiles(dir).OrderBy(f => f).ToArray());
            Assert.Equal(2, access.ExclusiveReleases);
        }

        [Fact]
        public void Restore_FlushesBeforeReleasing()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            EngineHarness.Backup(access, access.Add(1, source), path);
            var target = new MemoryBlockDevice(source.Size, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: false);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.True(target.FlushCalls >= 1);
            Assert.Equal(target.WriteCalls, target.WritesAtLastFlush);
        }

        [Fact]
        public void RawImageWithOddSize_IsPaddedToSectorBoundary()
        {
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "odd.img");
            var content = new byte[(1 << 20) + 100];
            new System.Random(5).NextBytes(content);
            File.WriteAllBytes(path, content);
            var access = new FakeDeviceAccess();
            var target = new MemoryBlockDevice(2 << 20, 512);
            target.Fill(1);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: true);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(content, target.Data.Take(content.Length).ToArray());
            Assert.All(target.Data.Skip(content.Length).Take(512 - 100), b => Assert.Equal(0, b));
        }
    }
}
