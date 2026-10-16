using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class ChecksumCalculationTests
    {
        private static OperationResult Verify(FakeDeviceAccess access, string path, bool save = true, CancellationToken token = default(CancellationToken))
        {
            return EngineHarness.Run(access, new OperationRequest
            {
                Kind = OperationKind.VerifyImage,
                ImagePath = path,
                SaveMissingChecksums = save,
                EjectAfter = false
            }, token);
        }

        private static string BackupWithoutSidecars(FakeDeviceAccess access, string extension, out byte[] content)
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            content = device.Data.ToArray();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);
            EngineHarness.Backup(access, access.Add(1, device), path);
            File.Delete(ImageDescriptor.FileSidecarPath(path));
            if (extension == ".img.gz")
            {
                File.Delete(ImageDescriptor.ContentSidecarPath(path));
            }

            return path;
        }

        [Fact]
        public void Raw_WithoutSidecar_SavesIt()
        {
            var access = new FakeDeviceAccess();
            var path = BackupWithoutSidecars(access, ".img", out var content);

            var result = Verify(access, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img.sha256" }, result.SavedChecksumFiles);
            Assert.Equal(EngineHarness.Sha(content), ChecksumFile.Read(ImageDescriptor.FileSidecarPath(path), ChecksumKind.File).Sha256);
        }

        [Fact]
        public void Gzip_WithoutSidecars_SavesBoth()
        {
            var access = new FakeDeviceAccess();
            var path = BackupWithoutSidecars(access, ".img.gz", out var content);

            var result = Verify(access, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img.gz.sha256", "disk.img.sha256" }, result.SavedChecksumFiles);
            Assert.Equal(EngineHarness.ShaOfFile(path), ChecksumFile.Read(ImageDescriptor.FileSidecarPath(path), ChecksumKind.File).Sha256);
            Assert.Equal(EngineHarness.Sha(content), ChecksumFile.Read(ImageDescriptor.ContentSidecarPath(path), ChecksumKind.Content).Sha256);
        }

        [Fact]
        public void Gzip_WithFileSidecarOnly_AddsContentSidecar_KeepsExisting()
        {
            var access = new FakeDeviceAccess();
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            EngineHarness.Backup(access, access.Add(1, device), path);
            File.Delete(ImageDescriptor.ContentSidecarPath(path));
            var fileSidecar = ImageDescriptor.FileSidecarPath(path);
            var stamp = File.GetLastWriteTimeUtc(fileSidecar);

            var result = Verify(access, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img.sha256" }, result.SavedChecksumFiles);
            Assert.True(File.Exists(ImageDescriptor.ContentSidecarPath(path)));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(fileSidecar));
        }

        [Fact]
        public void Mismatch_SavesNothing()
        {
            var access = new FakeDeviceAccess();
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            EngineHarness.Backup(access, access.Add(1, device), path);
            File.Delete(ImageDescriptor.ContentSidecarPath(path));
            ChecksumFile.Write(ImageDescriptor.FileSidecarPath(path), new string('0', 64), "disk.img.gz");

            var result = Verify(access, path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ChecksumMismatch, result.ErrorCode);
            Assert.Empty(result.SavedChecksumFiles);
            Assert.False(File.Exists(ImageDescriptor.ContentSidecarPath(path)));
        }

        [Fact]
        public void Cancelled_SavesNothing()
        {
            var access = new FakeDeviceAccess();
            var path = BackupWithoutSidecars(access, ".img.gz", out _);
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                Assert.Equal(OperationState.Cancelled, Verify(access, path, token: cts.Token).State);
            }

            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)));
        }

        [Fact]
        public void WithoutFlag_SavesNothing()
        {
            var access = new FakeDeviceAccess();
            var path = BackupWithoutSidecars(access, ".img", out _);

            var result = Verify(access, path, save: false);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Empty(result.SavedChecksumFiles);
            Assert.False(File.Exists(ImageDescriptor.FileSidecarPath(path)));
        }
    }
}
