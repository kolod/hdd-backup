using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Progress;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Ext4;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Shrinking
{
    public class ShrinkPlannerTests
    {
        [Theory]
        [InlineData("rpi-like-4k", false)]
        [InlineData("rpi-like-4k", true)]
        [InlineData("rpi-like-1k", false)]
        [InlineData("rpi-legacy", true)]
        [InlineData("rpi-like-gpt", false)]
        public void Analyze_FindsLayoutAndMinimum(string fixture, bool gzip)
        {
            string path = gzip ? FixtureImages.CompressedPath(fixture) : FixtureImages.RawPath(fixture);
            var plan = ShrinkPlanner.Analyze(path, CancellationToken.None);

            Assert.True(plan.IsShrinkable, string.Join("; ", plan.Blockers));
            Assert.Equal(gzip, plan.SourceIsGzip);
            Assert.Equal(2, plan.Partitions.Count);
            Assert.StartsWith("FAT", plan.Partitions.OrderBy(p => p.Offset).First().FileSystem);
            // Required size: end of the root partition (plus backup GPT); the file may have spare room.
            long fileLength = new FileInfo(FixtureImages.RawPath(fixture)).Length;
            Assert.True(plan.SourceImageBytes <= fileLength && plan.SourceImageBytes >= plan.RootPartition.End);
            Assert.True(plan.UsedDataBytes > 0);
            Assert.True(plan.MinimumImageBytes < plan.SourceImageBytes);
            Assert.Equal(0, plan.MinimumImageBytes % ShrinkPlanner.Alignment);

            // SC-005: minimum ≤ used data + max(10 %, 256 MiB) on top of the prefix.
            long allowance = Math.Max(plan.UsedDataBytes / 10, 256L << 20);
            Assert.True(plan.MinimumImageBytes <= plan.RootPartition.Offset + plan.UsedDataBytes + allowance);
            Assert.True(plan.Fits(plan.MinimumImageBytes));
            Assert.False(plan.Fits(plan.MinimumImageBytes - ShrinkPlanner.Alignment));
        }

        [Fact]
        public void Describe_ListsLayoutFeaturesAndBlockers()
        {
            string ok = ShrinkPlanner.Analyze(FixtureImages.CompressedPath("rpi-like-4k"), CancellationToken.None).Describe();
            Assert.Contains("[root]", ok);
            Assert.Contains("metadata_csum", ok);
            Assert.Contains("Blockers: none", ok);

            string blocked = ShrinkPlanner.Analyze(FixtureImages.CompressedPath("inline"), CancellationToken.None).Describe();
            Assert.Contains("UnsupportedFeature: inline_data", blocked);
        }

        [Fact]
        public void RequiredDeviceBytes_IsKnownForGzip()
        {
            Assert.Equal(new FileInfo(FixtureImages.RawPath("rpi-like-4k")).Length, ShrinkPlanner.RequiredDeviceBytes(FixtureImages.CompressedPath("rpi-like-4k")));
        }
    }

    public class ImageShrinkerTests
    {
        internal static OperationResult Shrink(string source, string output, long? target = null, CancellationToken cancel = default(CancellationToken))
        {
            var engine = new ImagingEngine(new FakeDeviceAccess(), EngineHarness.BufferSize, 4);
            return engine.RunAsync(
                new OperationRequest { Kind = OperationKind.ShrinkImage, ImagePath = source, ShrinkOutputPath = output, ShrinkTargetBytes = target, Compression = CompressionChoice.Fastest },
                cancel).GetAwaiter().GetResult();
        }

        public static TheoryData<string, bool, string> Cases()
        {
            var data = new TheoryData<string, bool, string>();
            data.Add("rpi-like-4k", false, ".img");
            data.Add("rpi-like-4k", true, ".img.gz");
            data.Add("rpi-like-1k", true, ".img");
            data.Add("rpi-legacy", false, ".img");
            data.Add("rpi-like-gpt", false, ".img");
            return data;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Shrink_ProducesVerifiedSmallerImage(string fixture, bool gzipSource, string outputExtension)
        {
            string source = gzipSource ? FixtureImages.CompressedPath(fixture) : FixtureImages.RawPath(fixture);
            string sourceHashBefore = EngineHarness.ShaOfFile(source);
            var dir = TestPaths.NewTempDirectory();
            var output = Path.Combine(dir, "shrunk" + outputExtension);

            var result = Shrink(source, output);

            Assert.True(result.State == OperationState.Completed, result.ErrorCode + " " + result.ErrorDetail);
            Assert.Equal(sourceHashBefore, EngineHarness.ShaOfFile(source));
            Assert.DoesNotContain(Directory.GetFiles(dir), f => f.EndsWith(".partial", StringComparison.Ordinal));

            // Checksums next to the result, like a backup.
            Assert.Equal(EngineHarness.ShaOfFile(output), ChecksumFile.Read(output + ".sha256", ChecksumKind.File).Sha256);
            var raw = EngineHarness.ReadImageContent(output);
            Assert.Equal(EngineHarness.Sha(raw), result.ContentSha256);
            Assert.True(raw.Length < new FileInfo(FixtureImages.RawPath(fixture)).Length);

            // Prefix identical except the partition table; root file system complete and consistent.
            var original = File.ReadAllBytes(FixtureImages.RawPath(fixture));
            var rawPath = Path.Combine(dir, "check.img");
            File.WriteAllBytes(rawPath, raw);
            using (var image = FileBlockDevice.OpenRead(rawPath))
            {
                var table = PartitionTableReader.Read(image);
                var root = table.Partitions.OrderBy(p => p.Offset).Last();
                Assert.True(table.Partitions.Max(p => p.End) <= raw.Length);
                long tableEnd = table.Style == TableStyle.Gpt ? 34 * 512 : 512;
                for (long i = tableEnd; i < root.Offset; i++)
                {
                    if (original[i] != raw[i])
                    {
                        Assert.Fail("prefix differs at " + i);
                    }
                }

                using (var view = new PartitionView(image, root.Offset, root.Length))
                {
                    Assert.Empty(Ext4Checker.Check(view));
                    using (var reader = new Ext4Reader(view))
                    {
                        ManifestAssert.Matches(reader, FixtureImages.Manifest(fixture));
                        Assert.Equal(root.Length, reader.Superblock.BlocksCount * reader.BlockSize);
                    }
                }
            }

            var export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (!string.IsNullOrEmpty(export))
            {
                Directory.CreateDirectory(export);
                File.Copy(rawPath, Path.Combine(export, "shrunk-" + fixture + ".img"), true);
            }
        }

        [Fact]
        public void TargetSize_IsRespected()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("rpi-like-4k"), CancellationToken.None);
            long target = plan.MinimumImageBytes + (32L << 20);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "fit.img");

            var result = Shrink(FixtureImages.RawPath("rpi-like-4k"), output, target);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.True(new FileInfo(output).Length <= target);
            Assert.True(new FileInfo(output).Length > plan.MinimumImageBytes);
        }

        [Fact]
        public void GzipOutput_DecompressesToRawOutput()
        {
            var dir = TestPaths.NewTempDirectory();
            var raw = Path.Combine(dir, "a.img");
            var gz = Path.Combine(dir, "b.img.gz");

            Assert.Equal(OperationState.Completed, Shrink(FixtureImages.RawPath("rpi-legacy"), raw).State);
            Assert.Equal(OperationState.Completed, Shrink(FixtureImages.RawPath("rpi-legacy"), gz).State);

            Assert.True(File.ReadAllBytes(raw).SequenceEqual(EngineHarness.ReadImageContent(gz)), "gzip output differs from raw output");
            Assert.Equal(ChecksumFile.Read(raw + ".sha256", ChecksumKind.File).Sha256, ChecksumFile.Read(gz.Substring(0, gz.Length - 3) + ".sha256", ChecksumKind.Content).Sha256);
        }

        [Fact]
        public void TargetBelowMinimum_IsRefusedWithoutFiles()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("rpi-like-4k"), CancellationToken.None);
            var dir = TestPaths.NewTempDirectory();

            var result = Shrink(FixtureImages.RawPath("rpi-like-4k"), Path.Combine(dir, "x.img"), plan.MinimumImageBytes - (64L << 20));

            Assert.Equal(ErrorCode.ShrinkNotPossible, result.ErrorCode);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public async Task NotEnoughFreeSpace_IsRefusedWithoutFiles()
        {
            var access = new FakeDeviceAccess { PathInfo = new PathInfo { FreeBytes = 16L << 20, FileSystem = "NTFS" } };
            var engine = new ImagingEngine(access, EngineHarness.BufferSize, 4);
            var dir = TestPaths.NewTempDirectory();

            var result = await engine.RunAsync(
                new OperationRequest { Kind = OperationKind.ShrinkImage, ImagePath = FixtureImages.CompressedPath("rpi-like-4k"), ShrinkOutputPath = Path.Combine(dir, "x.img") },
                CancellationToken.None);

            Assert.Equal(ErrorCode.DestinationFull, result.ErrorCode);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(40)]
        [InlineData(150)]
        public void Cancellation_LeavesNoFiles_AndSourceUntouched(int afterMs)
        {
            var source = FixtureImages.CompressedPath("rpi-like-4k");
            string before = EngineHarness.ShaOfFile(source);
            var dir = TestPaths.NewTempDirectory();
            using (var cts = new CancellationTokenSource(afterMs))
            {
                var result = Shrink(source, Path.Combine(dir, "c.img"), null, cts.Token);
                if (result.State == OperationState.Completed)
                {
                    return; // finished before the cancellation fired
                }

                Assert.Equal(OperationState.Cancelled, result.State);
            }

            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal(before, EngineHarness.ShaOfFile(source));
        }

        [Fact]
        public async Task Progress_StaysBelow100UntilCommit()
        {
            var engine = new ImagingEngine(new FakeDeviceAccess(), EngineHarness.BufferSize, 4);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "p.img");
            var task = engine.RunAsync(new OperationRequest { Kind = OperationKind.ShrinkImage, ImagePath = FixtureImages.RawPath("rpi-legacy"), ShrinkOutputPath = output }, CancellationToken.None);
            double last = 0;
            while (!task.IsCompleted)
            {
                var p = engine.CurrentProgress;
                Assert.True(p.Percent >= last);
                last = p.Percent;
                if (p.State != OperationState.Completed)
                {
                    Assert.True(p.Percent < 100.0);
                }

                await Task.Delay(1);
            }

            Assert.Equal(OperationState.Completed, (await task).State);
            Assert.Equal(100.0, engine.CurrentProgress.Percent);
        }
    }

    /// <summary>US3: unsafe sources are refused with a specific reason and nothing is written (T035/T036).</summary>
    public class ShrinkRefusalTests
    {
        [Theory]
        [InlineData("dirty", ShrinkBlocker.JournalNeedsRecovery, null)]
        [InlineData("inline", ShrinkBlocker.UnsupportedFeature, "inline_data")]
        [InlineData("root-not-last", ShrinkBlocker.RootNotLastPartition, null)]
        public void NegativeFixtures_AreRefused(string fixture, ShrinkBlocker expected, string detail)
        {
            AssertRefused(FixtureImages.CompressedPath(fixture), expected, detail);
        }

        [Theory]
        [InlineData("errors", ShrinkBlocker.FsHasErrors, null)]
        [InlineData("error-count", ShrinkBlocker.FsHasErrors, null)]
        [InlineData("not-clean", ShrinkBlocker.FsNotClean, null)]
        [InlineData("orphans", ShrinkBlocker.OrphanInodes, null)]
        [InlineData("block-8k", ShrinkBlocker.UnsupportedBlockSize, "8192")]
        [InlineData("inode-512", ShrinkBlocker.UnsupportedInodeSize, "512")]
        [InlineData("bad-checksum", ShrinkBlocker.ChecksumMismatch, "superblock")]
        public void PatchedSuperblock_IsRefused(string patch, ShrinkBlocker expected, string detail)
        {
            var source = FixtureImages.RawPath("rpi-like-4k");
            long rootOffset = ShrinkPlanner.Analyze(source, CancellationToken.None).RootPartition.Offset;
            var copy = Path.Combine(TestPaths.NewTempDirectory(), patch + ".img");
            File.Copy(source, copy);

            using (var stream = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                var raw = new byte[Ext4Superblock.Size];
                stream.Position = rootOffset + Ext4Superblock.ByteOffset;
                stream.Read(raw, 0, raw.Length);
                var sb = new Ext4Superblock(raw);
                switch (patch)
                {
                    case "errors": sb.State |= Ext4Superblock.StateErrors; break;
                    case "error-count": sb.ErrorCount = 3; break;
                    case "not-clean": sb.State &= unchecked((ushort)~Ext4Superblock.StateValid); break;
                    case "orphans": sb.LastOrphan = 12; break;
                    case "block-8k": raw[0x18] = 3; break; // s_log_block_size
                    case "inode-512": raw[0x58] = 0x00; raw[0x59] = 0x02; break; // s_inode_size
                }

                sb.Checksum = sb.ComputeChecksum();
                if (patch == "bad-checksum")
                {
                    raw[0x78] ^= 0xFF; // s_volume_name[0]: harmless field, now with a stale checksum
                }

                stream.Position = rootOffset + Ext4Superblock.ByteOffset;
                stream.Write(raw, 0, raw.Length);
            }

            AssertRefused(copy, expected, detail);
        }

        private static void AssertRefused(string source, ShrinkBlocker expected, string detail)
        {
            string before = EngineHarness.ShaOfFile(source);
            var plan = ShrinkPlanner.Analyze(source, CancellationToken.None);
            Assert.False(plan.IsShrinkable);
            var blocker = Assert.Single(plan.Blockers, b => b.Blocker == expected);
            if (detail != null)
            {
                Assert.Contains(detail, blocker.Detail);
            }

            // The engine refuses before creating any file, and the source stays untouched.
            var dir = TestPaths.NewTempDirectory();
            var result = ImageShrinkerTests.Shrink(source, Path.Combine(dir, "out.img"));
            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ShrinkNotPossible, result.ErrorCode);
            Assert.Contains(expected.ToString(), result.ErrorDetail);
            Assert.Empty(Directory.GetFiles(dir));
            Assert.Equal(before, EngineHarness.ShaOfFile(source));
        }
    }

    public class ShrinkAndWriteTests
    {
        [Fact]
        public void ShrunkImage_WrittenToSmallerDevice_EqualsShrunkImage()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("rpi-like-4k"), CancellationToken.None);
            var output = Path.Combine(TestPaths.NewTempDirectory(), "for-card.img");
            long cardSize = plan.MinimumImageBytes + (16L << 20);
            Assert.True(cardSize < plan.SourceImageBytes);

            var shrink = ImageShrinkerTests.Shrink(FixtureImages.RawPath("rpi-like-4k"), output, cardSize);
            Assert.Equal(OperationState.Completed, shrink.State);

            var access = new FakeDeviceAccess();
            var card = new MemoryBlockDevice(cardSize, 512);
            var info = access.Add(5, card);
            var restore = EngineHarness.Restore(access, info, output, verify: true);

            Assert.Equal(OperationState.Completed, restore.State);
            var image = File.ReadAllBytes(output);
            Assert.True(image.SequenceEqual(card.Data.Take(image.Length)), "card content differs from the shrunk image");
        }
    }
}
