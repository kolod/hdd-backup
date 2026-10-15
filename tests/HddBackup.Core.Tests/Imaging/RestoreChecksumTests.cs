using System.IO;
using System.Linq;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class RestoreChecksumTests
    {
        private static (FakeDeviceAccess Access, string Path, MemoryBlockDevice Source) Prepare(string extension)
        {
            var source = BackupDeviceTests.BuildDevice("GPT", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);
            EngineHarness.Backup(access, access.Add(1, source), path);
            return (access, path, source);
        }

        [Theory]
        [InlineData(".img", 1)]
        [InlineData(".img.gz", 2)]
        public void MatchingSidecars_AreVerified(string extension, int expectedChecks)
        {
            var (access, path, _) = Prepare(extension);
            var target = new MemoryBlockDevice(6 << 20, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: false);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(expectedChecks, result.Verifications.Count);
            Assert.All(result.Verifications, v => Assert.Equal(VerificationStatus.Match, v.Status));
        }

        [Theory]
        [InlineData(".img")]
        [InlineData(".img.gz")]
        public void SingleByteCorruption_IsDetected_AndDeviceIsReleased(string extension)
        {
            var (access, path, _) = Prepare(extension);
            var bytes = File.ReadAllBytes(path);
            // For gzip, flip a byte inside the stored trailer CRC so decompression still succeeds;
            // for raw, flip a data byte.
            int index = extension == ".img" ? bytes.Length / 2 : bytes.Length - 6;
            bytes[index] ^= 0x01;
            File.WriteAllBytes(path, bytes);
            var target = new MemoryBlockDevice(6 << 20, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: false);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.True(
                result.ErrorCode == ErrorCode.ChecksumMismatch || result.ErrorCode == ErrorCode.CorruptImage,
                "Unexpected error " + result.ErrorCode);
            Assert.False(result.ContentComplete);
            Assert.True(result.SafeToRemove);
            Assert.Equal(1, access.ExclusiveReleases);
            Assert.Equal(target.WriteCalls, target.WritesAtLastFlush);
        }

        [Fact]
        public void RawDataCorruption_ReportsChecksumMismatch()
        {
            var (access, path, _) = Prepare(".img");
            var bytes = File.ReadAllBytes(path);
            bytes[12345] ^= 0x10;
            File.WriteAllBytes(path, bytes);

            var result = EngineHarness.Restore(access, access.Add(2, new MemoryBlockDevice(6 << 20, 512)), path, verify: false);

            Assert.Equal(ErrorCode.ChecksumMismatch, result.ErrorCode);
            Assert.Equal(VerificationStatus.Mismatch, result.Verifications.Single().Status);
        }

        [Fact]
        public void NoSidecars_RestoreProceeds_WithNotAvailable()
        {
            var (access, path, source) = Prepare(".img.gz");
            File.Delete(path + ".sha256");
            File.Delete(Path.Combine(Path.GetDirectoryName(path), "disk.img.sha256"));
            var target = new MemoryBlockDevice(6 << 20, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.All(result.Verifications, v => Assert.Equal(VerificationStatus.NotAvailable, v.Status));
            Assert.Equal(EngineHarness.Sha(source.Data), result.ContentSha256);
        }

        [Fact]
        public void OnlyContentSidecarPresent_IsStillChecked()
        {
            var (access, path, _) = Prepare(".img.gz");
            File.Delete(path + ".sha256");
            var contentSidecar = Path.Combine(Path.GetDirectoryName(path), "disk.img.sha256");
            File.WriteAllText(contentSidecar, new string('0', 64) + " *disk.img\n");

            var result = EngineHarness.Restore(access, access.Add(2, new MemoryBlockDevice(6 << 20, 512)), path, verify: false);

            Assert.Equal(ErrorCode.ChecksumMismatch, result.ErrorCode);
            Assert.Equal(VerificationStatus.NotAvailable, result.Verifications.Single(v => v.Kind == ChecksumKind.File).Status);
            Assert.Equal(VerificationStatus.Mismatch, result.Verifications.Single(v => v.Kind == ChecksumKind.Content).Status);
        }
    }
}
