using System.IO;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class BackupDeviceTests
    {
        public static TheoryData<string, string, int> Matrix()
        {
            var data = new TheoryData<string, string, int>();
            foreach (var ext in new[] { ".img", ".img.gz" })
            {
                foreach (var table in new[] { "MBR", "GPT" })
                {
                    foreach (var sector in new[] { 512, 4096 })
                    {
                        data.Add(ext, table, sector);
                    }
                }
            }

            return data;
        }

        internal static MemoryBlockDevice BuildDevice(string table, int sectorSize, long size = 6 << 20)
        {
            return table == "MBR"
                ? TestImageBuilder.Mbr(size, sectorSize, new TestPartition(1 << 20, 2 << 20, 0x0C), new TestPartition(3 << 20, 2 << 20, 0x07))
                : TestImageBuilder.Gpt(size, sectorSize, new TestPartition(1 << 20, 2 << 20, 0), new TestPartition(3 << 20, 2 << 20, 0));
        }

        [Theory]
        [MemberData(nameof(Matrix))]
        public void FullBackup_IsByteIdenticalToDevice(string extension, string table, int sectorSize)
        {
            // 6 MiB + one odd chunk tail exercises partial last buffers.
            var device = BuildDevice(table, sectorSize, (6 << 20) + (sectorSize * 3));
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);

            var result = EngineHarness.Backup(access, info, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.True(result.ContentComplete);
            Assert.True(result.SafeToRemove);
            Assert.Equal(device.Data, EngineHarness.ReadImageContent(path));
            Assert.False(File.Exists(path + ".partial"));
            Assert.Equal(1, access.ReadReleases);
            Assert.Equal(1, access.EjectCalls);
            Assert.True(result.Ejected);
        }

        [Fact]
        public void EjectCanBeTurnedOff_AndVetoIsReportedWithoutFailing()
        {
            var device = BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess { EjectResult = new HddBackup.Core.Devices.EjectResult(HddBackup.Core.Devices.EjectStatus.Vetoed, "explorer.exe") };
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();

            var noEject = EngineHarness.Run(access, new HddBackup.Core.Imaging.OperationRequest
            {
                Kind = HddBackup.Core.Imaging.OperationKind.BackupDevice,
                Device = info,
                ImagePath = Path.Combine(dir, "a.img"),
                EjectAfter = false
            });
            Assert.Equal(0, access.EjectCalls);
            Assert.Equal(OperationState.Completed, noEject.State);

            var vetoed = EngineHarness.Backup(access, info, Path.Combine(dir, "b.img"));
            Assert.Equal(OperationState.Completed, vetoed.State);
            Assert.True(vetoed.SafeToRemove);
            Assert.Equal("explorer.exe", vetoed.EjectVetoedBy);
        }

        [Fact]
        public void VerifyAfterBackup_Passes()
        {
            var device = BuildDevice("GPT", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");

            var result = EngineHarness.Backup(access, info, path, verify: true);

            Assert.Equal(OperationState.Completed, result.State);
        }
    }
}
