using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class ConvertImageTests
    {
        private static OperationResult Convert(FakeDeviceAccess access, string source, string output, CompressionChoice compression = CompressionChoice.Optimal, CancellationToken token = default(CancellationToken))
        {
            return EngineHarness.Run(access, new OperationRequest
            {
                Kind = OperationKind.ConvertImage,
                ImagePath = source,
                OutputPath = output,
                Compression = compression,
                EjectAfter = false
            }, token);
        }

        /// <summary>A whole-device backup made by the engine, with its sidecars.</summary>
        private static string Backup(FakeDeviceAccess access, string extension, out byte[] content)
        {
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            content = device.Data.ToArray();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);
            Assert.Equal(OperationState.Completed, EngineHarness.Backup(access, access.Add(1, device), path).State);
            return path;
        }

        private static string[] Snapshot(string directory) =>
            Directory.GetFiles(directory).OrderBy(f => f).Select(f => Path.GetFileName(f) + ":" + EngineHarness.ShaOfFile(f) + ":" + File.GetLastWriteTimeUtc(f).Ticks).ToArray();

        // US1: compressed → uncompressed

        [Fact]
        public void GzipToRaw_EqualsContent_WithChecksum_SourceUnchanged()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img.gz", out var content);
            var dir = Path.GetDirectoryName(source);
            var before = Snapshot(dir);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "disk.img");

            var result = Convert(access, source, output);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(output, result.OutputPath);
            Assert.Equal(content, File.ReadAllBytes(output));
            var sidecar = ChecksumFile.Read(ImageDescriptor.FileSidecarPath(output), ChecksumKind.File);
            Assert.Equal(EngineHarness.Sha(content), sidecar.Sha256);
            Assert.Equal(new[] { "disk.img.sha256" }, result.SavedChecksumFiles);
            Assert.Equal(before, Snapshot(dir));
            Assert.Equal(1, access.ReadOpens); // only the backup opened the device
            Assert.Equal(0, access.ExclusiveOpens);
        }

        [Theory]
        [InlineData("rpi-like-4k")]
        [InlineData("rpi-like-gpt")]
        public void GzipFixtureToRaw_EqualsRawFixture(string name)
        {
            var dir = TestPaths.NewTempDirectory();
            var source = Path.Combine(dir, name + ".img.gz");
            File.Copy(FixtureImages.CompressedPath(name), source);
            var output = Path.Combine(dir, name + ".img");

            var result = Convert(new FakeDeviceAccess(), source, output);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(EngineHarness.ShaOfFile(FixtureImages.RawPath(name)), EngineHarness.ShaOfFile(output));
        }

        [Fact]
        public void SameFolder_SharedContentSidecar_IsNotRewritten()
        {
            // disk.img.gz → disk.img next to it: disk.img.sha256 already holds the content checksum.
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img.gz", out _);
            var shared = ImageDescriptor.ContentSidecarPath(source);
            var stamp = File.GetLastWriteTimeUtc(shared);
            var output = Path.Combine(Path.GetDirectoryName(source), "disk.img");

            var result = Convert(access, source, output);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Empty(result.SavedChecksumFiles);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(shared));
            Assert.Equal(EngineHarness.ShaOfFile(output), ChecksumFile.Read(shared, ChecksumKind.File).Sha256);
        }

        [Fact]
        public void SourceContentMismatch_FailsAndLeavesNothing()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img.gz", out _);
            ChecksumFile.Write(ImageDescriptor.ContentSidecarPath(source), new string('0', 64), "disk.img");
            var outDir = TestPaths.NewTempDirectory();

            var result = Convert(access, source, Path.Combine(outDir, "disk.img"));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ChecksumMismatch, result.ErrorCode);
            Assert.Empty(Directory.GetFiles(outDir));
        }

        [Fact]
        public void Cancelled_LeavesNothing()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img", out _);
            var outDir = TestPaths.NewTempDirectory();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var result = Convert(access, source, Path.Combine(outDir, "disk.img.gz"), token: cts.Token);
                Assert.Equal(OperationState.Cancelled, result.State);
            }

            Assert.Empty(Directory.GetFiles(outDir));
        }

        [Theory]
        [InlineData(".img.gz", ".img")]
        [InlineData(".img", ".img.gz")]
        public void PartitionImage_CopiesMetadata(string from, string to)
        {
            var device = BackupDeviceTests.BuildDevice("GPT", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, device);
            var source = Path.Combine(TestPaths.NewTempDirectory(), "part2" + from);
            Assert.Equal(OperationState.Completed, PartitionBackupTests.BackupPartition(access, info, 1, source).State);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "part2" + to);

            var result = Convert(access, source, output);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(
                File.ReadAllBytes(PartitionMetadataFile.PathFor(source)),
                File.ReadAllBytes(PartitionMetadataFile.PathFor(output)));
            Assert.Equal(ImageKind.Partition, ImageDescriptor.Load(output).Kind);
            Assert.Equal(EngineHarness.ReadImageContent(source), EngineHarness.ReadImageContent(output));
        }

        [Fact]
        public void Validate_RejectsSamePathAndSameFormat()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img", out _);
            var engine = EngineHarness.NewEngine(access);

            var same = Assert.Throws<ImagingException>(() => engine.Validate(new OperationRequest { Kind = OperationKind.ConvertImage, ImagePath = source, OutputPath = source }));
            Assert.Equal(ErrorCode.InvalidRequest, same.Code);
            var sameFormat = Assert.Throws<ImagingException>(() => engine.Validate(new OperationRequest
            {
                Kind = OperationKind.ConvertImage,
                ImagePath = source,
                OutputPath = Path.Combine(TestPaths.NewTempDirectory(), "copy.img")
            }));
            Assert.Equal(ErrorCode.InvalidRequest, sameFormat.Code);
        }

        [Fact]
        public void RawOutput_NotEnoughSpace_RefusedBeforeWriting()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img.gz", out _);
            access.PathInfo = new Devices.PathInfo { FreeBytes = 1024, FileSystem = "NTFS" };
            var outDir = TestPaths.NewTempDirectory();

            var result = Convert(access, source, Path.Combine(outDir, "disk.img"));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DestinationFull, result.ErrorCode);
            Assert.Empty(Directory.GetFiles(outDir));
        }

        // US2: uncompressed → compressed

        [Theory]
        [InlineData(CompressionChoice.Optimal)]
        [InlineData(CompressionChoice.Fastest)]
        public void RawToGzip_RoundTrip_WithBothSidecars(CompressionChoice compression)
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img", out var content);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");

            var result = Convert(access, source, output, compression);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(content, EngineHarness.ReadImageContent(output));
            Assert.Equal(EngineHarness.ShaOfFile(output), ChecksumFile.Read(ImageDescriptor.FileSidecarPath(output), ChecksumKind.File).Sha256);
            Assert.Equal(EngineHarness.Sha(content), ChecksumFile.Read(ImageDescriptor.ContentSidecarPath(output), ChecksumKind.Content).Sha256);
            Assert.Equal(new[] { "disk.img.gz.sha256", "disk.img.sha256" }, result.SavedChecksumFiles);
        }

        [Fact]
        public void RawToGzip_SameFolder_SourceSidecarUntouched()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img", out _);
            var sidecar = ImageDescriptor.FileSidecarPath(source);
            var bytes = File.ReadAllBytes(sidecar);
            var stamp = File.GetLastWriteTimeUtc(sidecar);

            var result = Convert(access, source, source + ".gz");

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(new[] { "disk.img.gz.sha256" }, result.SavedChecksumFiles);
            Assert.Equal(bytes, File.ReadAllBytes(sidecar));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(sidecar));
        }

        [Fact]
        public void RawToGzip_OnFat_WarnsMayExceed()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img", out _);
            access.PathInfo = new Devices.PathInfo { FreeBytes = long.MaxValue, FileSystem = "FAT32" };

            var warnings = EngineHarness.NewEngine(access).Validate(new OperationRequest
            {
                Kind = OperationKind.ConvertImage,
                ImagePath = source,
                OutputPath = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz")
            });

            Assert.Contains(ValidationWarning.MayExceedFat32Limit, warnings);
        }

        [Fact]
        public async Task Progress_ReachesHundredPercent()
        {
            var access = new FakeDeviceAccess();
            var source = Backup(access, ".img.gz", out _);
            var engine = EngineHarness.NewEngine(access);
            var result = await engine.RunAsync(new OperationRequest
            {
                Kind = OperationKind.ConvertImage,
                ImagePath = source,
                OutputPath = Path.Combine(TestPaths.NewTempDirectory(), "disk.img")
            }, CancellationToken.None);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(100.0, engine.CurrentProgress.Percent);
        }
    }
}
