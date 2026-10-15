using System.IO;
using HddBackup.Core.Formats;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class BackupChecksumTests
    {
        [Fact]
        public void RawBackup_WritesOnlyImgSha256_WithFileHash()
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "disk.img");

            var result = EngineHarness.Backup(access, info, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img", "disk.img.sha256" }, SortedNames(dir));
            var sidecar = ChecksumFile.Read(path + ".sha256", ChecksumKind.File);
            Assert.Equal("disk.img", sidecar.Label);
            Assert.Equal(EngineHarness.ShaOfFile(path), sidecar.Sha256);
            Assert.Equal(EngineHarness.Sha(device.Data), result.ContentSha256);
            Assert.Equal(result.ContentSha256, result.FileSha256);
        }

        [Fact]
        public void GzipBackup_WritesFileAndContentSidecars()
        {
            var device = BackupDeviceTests.BuildDevice("GPT", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "disk.img.gz");

            var result = EngineHarness.Backup(access, info, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img.gz", "disk.img.gz.sha256", "disk.img.sha256" }, SortedNames(dir));

            var fileSidecar = ChecksumFile.Read(path + ".sha256", ChecksumKind.File);
            Assert.Equal("disk.img.gz", fileSidecar.Label);
            Assert.Equal(EngineHarness.ShaOfFile(path), fileSidecar.Sha256);
            Assert.Equal(fileSidecar.Sha256, result.FileSha256);

            var contentSidecar = ChecksumFile.Read(Path.Combine(dir, "disk.img.sha256"), ChecksumKind.Content);
            Assert.Equal("disk.img", contentSidecar.Label);
            Assert.Equal(EngineHarness.Sha(device.Data), contentSidecar.Sha256);
            Assert.Equal(contentSidecar.Sha256, result.ContentSha256);
            Assert.NotEqual(result.FileSha256, result.ContentSha256);
        }

        [Fact]
        public void ChecksumFileFormat_IsSha256sumCompatible()
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");

            EngineHarness.Backup(access, info, path);

            var text = File.ReadAllText(path + ".sha256");
            Assert.Matches("^[0-9a-f]{64} \\*disk\\.img\\.gz\n$", text);
        }

        private static string[] SortedNames(string dir)
        {
            var names = Directory.GetFiles(dir);
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = Path.GetFileName(names[i]);
            }

            System.Array.Sort(names, System.StringComparer.Ordinal);
            return names;
        }
    }
}
