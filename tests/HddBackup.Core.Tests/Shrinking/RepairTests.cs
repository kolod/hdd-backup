using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Imaging;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Progress;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Shrinking
{
    /// <summary>Feature 006: repair after an unclean shutdown, checked against e2fsck -fy references.</summary>
    public sealed class RepairTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static OperationResult Run(string source, string output, long? target, bool repair, CancellationToken cancel = default(CancellationToken))
        {
            var engine = new ImagingEngine(new FakeDeviceAccess(), EngineHarness.BufferSize, 4);
            return engine.RunAsync(
                new OperationRequest
                {
                    Kind = OperationKind.ShrinkImage,
                    ImagePath = source,
                    ShrinkOutputPath = output,
                    ShrinkTargetBytes = target,
                    RepairFileSystem = repair,
                    Compression = CompressionChoice.Fastest,
                    EjectAfter = false
                },
                cancel).GetAwaiter().GetResult();
        }

        /// <summary>The uncompressed content of an output image in a file of its own.</summary>
        private string RawOf(string output)
        {
            if (!output.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                return output;
            }

            var raw = Path.Combine(_temp.New(), "content.img");
            File.WriteAllBytes(raw, EngineHarness.ReadImageContent(output));
            return raw;
        }

        /// <summary>Own full check of the output root, its tree against the reference, the prefix against the source.</summary>
        private void AssertRepaired(string fixture, string output)
        {
            using (var image = FileBlockDevice.OpenRead(RawOf(output)))
            using (var refRoot = FixtureImages.OpenRoot(fixture + ".reference", out var refImage))
            using (refImage)
            {
                var table = PartitionTableReader.Read(image);
                var root = table.Partitions.OrderBy(p => p.Offset).Last();
                using (var outRoot = new PartitionView(image, root.Offset, root.Length))
                {
                    Assert.Empty(Ext4Checker.Check(outRoot));
                    using (var a = new Ext4Reader(outRoot))
                    using (var b = new Ext4Reader(refRoot))
                    {
                        Assert.False(a.Superblock.NeedsRecovery);
                        Assert.Equal(0u, a.Superblock.LastOrphan);
                        var diffs = Ext4TreeComparer.Compare(a, b, CancellationToken.None);
                        Assert.True(diffs.Count == 0, string.Join(Environment.NewLine, diffs.Take(10)));
                    }
                }

                // The boot partition (everything between the table and the root) is unchanged.
                using (var source = FixtureImages.Open(fixture))
                {
                    var x = new byte[1 << 20];
                    var y = new byte[1 << 20];
                    for (long o = 1 << 20; o < root.Offset; o += x.Length)
                    {
                        int n = (int)Math.Min(x.Length, root.Offset - o);
                        source.Read(o, x, 0, n);
                        image.Read(o, y, 0, n);
                        Assert.True(x.Take(n).SequenceEqual(y.Take(n)), "prefix differs at " + o);
                    }
                }
            }
        }

        [Theory]
        [InlineData("dirty-journal", 3, 0)]
        [InlineData("dirty-journal-torn", 1, 0)]
        [InlineData("orphans", 0, 3)]
        [InlineData("not-clean", 0, 0)]
        public void SameSizeRepair_EqualsLinuxRecovery(string fixture, int transactions, int orphans)
        {
            string source = FixtureImages.RawPath(fixture);
            string before = EngineHarness.ShaOfFile(source);
            long size = ShrinkPlanner.Analyze(source, CancellationToken.None).SourceImageBytes;
            var output = Path.Combine(_temp.New(), fixture + "_repaired.img");

            var result = Run(source, output, size, repair: true);

            Assert.True(result.State == OperationState.Completed, result.ErrorCode + " " + result.ErrorDetail);
            Assert.True(result.Repaired);
            Assert.Equal(transactions, result.RepairJournalTransactions);
            Assert.Equal(orphans, result.RepairOrphansRemoved);
            Assert.Equal(size, new FileInfo(output).Length);
            AssertRepaired(fixture, output);
            Assert.Equal(before, EngineHarness.ShaOfFile(source));

            // CI checks every repaired image again with e2fsck against the e2fsck -fy reference.
            var export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (!string.IsNullOrEmpty(export))
            {
                Directory.CreateDirectory(export);
                File.Copy(output, Path.Combine(export, "repaired-" + fixture + ".img"), true);
            }
        }

        [Fact]
        public void CompressedSourceAndOutput()
        {
            string source = FixtureImages.CompressedPath("dirty-journal");
            long size = ShrinkPlanner.Analyze(FixtureImages.RawPath("dirty-journal"), CancellationToken.None).SourceImageBytes;
            var output = Path.Combine(_temp.New(), "repaired.img.gz");

            var result = Run(source, output, size, repair: true);

            Assert.True(result.State == OperationState.Completed, result.ErrorCode + " " + result.ErrorDetail);
            AssertRepaired("dirty-journal", output);
        }

        [Theory]
        [InlineData("dirty-journal")]
        [InlineData("orphans")]
        [InlineData("not-clean")]
        public void WithoutRepair_NeedsRepairIsRefused(string fixture)
        {
            var dir = _temp.New();
            var result = Run(FixtureImages.RawPath(fixture), Path.Combine(dir, "out.img"), null, repair: false);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ShrinkNotPossible, result.ErrorCode);
            Assert.Contains("NeedsRepair", result.ErrorDetail);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void Analysis_ReportsNeedsRepairWithReasons()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("orphans"), CancellationToken.None);
            Assert.True(plan.IsRepairable);
            Assert.False(plan.IsShrinkable);
            var root = plan.RootFileSystem;
            Assert.Equal(FileSystemCondition.NeedsRepair, root.Condition);
            Assert.True(root.Reasons.HasFlag(RepairReasons.OrphanInodes));
            Assert.Equal(3, root.OrphanCount);
            Assert.True(plan.MinimumImageBytes > 0 && plan.MinimumImageBytes < plan.SourceImageBytes);

            var gz = ShrinkPlanner.Analyze(FixtureImages.CompressedPath("dirty-journal"), CancellationToken.None);
            Assert.True(gz.RootFileSystem.Reasons.HasFlag(RepairReasons.JournalNeedsRecovery));
            Assert.Equal(0, gz.MinimumImageBytes); // measured during the operation
        }

        [Fact]
        public void NotClean_MinimumIsFromTheTree_NotFromStaleCounters()
        {
            var clean = ShrinkPlanner.Analyze(FixtureImages.RawPath("not-clean.reference"), CancellationToken.None);
            var dirty = ShrinkPlanner.Analyze(FixtureImages.RawPath("not-clean"), CancellationToken.None);
            Assert.True(dirty.MinimumImageBytes > 0 && dirty.MinimumImageBytes <= clean.MinimumImageBytes + ShrinkPlanner.Alignment);
        }

        [Fact]
        public void Cancelled_LeavesNothing()
        {
            var dir = _temp.New();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var result = Run(FixtureImages.RawPath("dirty-journal"), Path.Combine(dir, "out.img"), null, repair: true, cts.Token);
                Assert.Equal(OperationState.Cancelled, result.State);
            }

            Assert.Empty(Directory.GetFiles(dir));
        }

        // US2: damage beyond an unclean shutdown

        [Fact]
        public void StructuralDamage_IsRefused_AndNothingWritten()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("damaged-dirent"), CancellationToken.None);
            Assert.Equal(FileSystemCondition.Damaged, plan.RootFileSystem.Condition);
            Assert.Contains(plan.Blockers, b => b.Blocker == ShrinkBlocker.StructuralDamage);

            var dir = _temp.New();
            var result = Run(FixtureImages.RawPath("damaged-dirent"), Path.Combine(dir, "out.img"), plan.SourceImageBytes, repair: true);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.ShrinkNotPossible, result.ErrorCode);
            Assert.Contains("StructuralDamage", result.ErrorDetail);
            Assert.Empty(Directory.GetFiles(dir));
        }

        [Fact]
        public void CompressedStructuralDamage_IsRefusedDuringTheOperation()
        {
            var dir = _temp.New();
            long size = ShrinkPlanner.Analyze(FixtureImages.RawPath("damaged-dirent"), CancellationToken.None).SourceImageBytes;
            var result = Run(FixtureImages.CompressedPath("damaged-dirent"), Path.Combine(dir, "out.img"), size, repair: true);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Contains("StructuralDamage", result.ErrorDetail);
            Assert.Empty(Directory.GetFiles(dir));
        }

        // US3: repair and shrink in one operation

        [Theory]
        [InlineData("dirty-journal", null)]
        [InlineData("orphans", null)]
        [InlineData("dirty-journal", 1L << 30)]
        public void RepairAndShrink(string fixture, long? target)
        {
            string source = FixtureImages.RawPath(fixture);
            var plan = ShrinkPlanner.Analyze(source, CancellationToken.None);
            var output = Path.Combine(_temp.New(), "small.img");
            long? t = target.HasValue ? Math.Min(target.Value, plan.SourceImageBytes - ShrinkPlanner.Alignment) : (long?)null;

            var result = Run(source, output, t, repair: true);

            Assert.True(result.State == OperationState.Completed, result.ErrorCode + " " + result.ErrorDetail);
            Assert.True(new FileInfo(output).Length < plan.SourceImageBytes);
            AssertRepaired(fixture, output);
        }

        [Fact]
        public void ImageFit_TreatsRepairablePlanAsShrinkable()
        {
            var plan = ShrinkPlanner.Analyze(FixtureImages.RawPath("orphans"), CancellationToken.None);
            Assert.True(plan.Fits(plan.MinimumImageBytes, repair: true));
            Assert.False(plan.Fits(plan.MinimumImageBytes));
        }
    }
}
