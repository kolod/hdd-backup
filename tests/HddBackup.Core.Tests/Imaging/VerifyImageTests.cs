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
    public class VerifyImageTests
    {
        private static OperationResult Verify(FakeDeviceAccess access, string path)
        {
            return EngineHarness.Run(access, new OperationRequest { Kind = OperationKind.VerifyImage, ImagePath = path });
        }

        private static string Prepare(FakeDeviceAccess access, string extension)
        {
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);
            EngineHarness.Backup(access, access.Add(1, BackupDeviceTests.BuildDevice("MBR", 512)), path);
            return path;
        }

        [Theory]
        [InlineData(".img", 1)]
        [InlineData(".img.gz", 2)]
        public void GoodImage_AllMatch_AndNoDeviceIsOpened(string extension, int checks)
        {
            var access = new FakeDeviceAccess();
            var path = Prepare(access, extension);
            int readOpens = access.ReadOpens;

            var result = Verify(access, path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(checks, result.Verifications.Count);
            Assert.All(result.Verifications, v => Assert.Equal(VerificationStatus.Match, v.Status));
            Assert.Equal(readOpens, access.ReadOpens);
            Assert.Equal(0, access.ExclusiveOpens);
        }

        [Fact]
        public void CorruptedImage_IsMismatch()
        {
            var access = new FakeDeviceAccess();
            var path = Prepare(access, ".img");
            var bytes = File.ReadAllBytes(path);
            bytes[4321] ^= 1;
            File.WriteAllBytes(path, bytes);

            var result = Verify(access, path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ChecksumMismatch, result.ErrorCode);
            Assert.Equal(VerificationStatus.Mismatch, result.Verifications.Single().Status);
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".img.gz")]
        public void WithoutSidecars_SavedChecksumsMatchBackupLayout(string extension)
        {
            var access = new FakeDeviceAccess();
            var path = Prepare(access, extension);
            var dir = Path.GetDirectoryName(path);
            var originals = Directory.GetFiles(dir, "*.sha256").ToDictionary(Path.GetFileName, File.ReadAllText);
            foreach (var f in Directory.GetFiles(dir, "*.sha256"))
            {
                File.Delete(f);
            }

            var result = Verify(access, path);
            Assert.Equal(OperationState.Completed, result.State);
            Assert.All(result.Verifications, v => Assert.Equal(VerificationStatus.NotAvailable, v.Status));

            ImagingEngine.SaveChecksumFiles(path, result.IsGzipImage, result.FileSha256, result.ContentSha256);

            var saved = Directory.GetFiles(dir, "*.sha256").ToDictionary(Path.GetFileName, File.ReadAllText);
            Assert.Equal(originals.OrderBy(kv => kv.Key), saved.OrderBy(kv => kv.Key));
        }
    }
}
