using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public sealed class ExtractionPlannerTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static PathInfo Info(long? free = null, string fileSystem = "NTFS")
        {
            return new PathInfo { FreeBytes = free, FileSystem = fileSystem };
        }

        private static ExtractionPlan Plan(IFileTree tree, FsEntry selection, string dest, PathInfo info = null, ConflictPolicy policy = ConflictPolicy.Replace, string protectedPath = null)
        {
            return ExtractionPlanner.Plan(tree, selection, dest, info ?? Info(), policy, protectedPath, CancellationToken.None);
        }

        [Fact]
        public void AFile_IsOneCopy()
        {
            var tree = new FakeFileTree();
            var file = tree.AddFile(tree.Root, "hostname", new byte[12]);
            var plan = Plan(tree, file, _temp.New());
            var item = Assert.Single(plan.Items);
            Assert.Equal(ExtractionAction.Copy, item.Action);
            Assert.Equal("hostname", item.RelativePath);
            Assert.Equal(1, plan.Files);
            Assert.Equal(0, plan.Directories);
            Assert.Equal(12, plan.TotalBytes);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void TheRootDirectory_GoesIntoAFolderNamedRoot()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "a", new byte[1]);
            var plan = Plan(tree, tree.Root, _temp.New());
            Assert.Equal("root", plan.Items[0].RelativePath);
            Assert.Equal(@"root\a", plan.Items[1].RelativePath);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void ATrickyDirectory_GetsPredictableNamesAndSkipsWhatWindowsCannotHold()
        {
            var (source, view) = FileSystemViewTests.Open("browse-names");
            using (source)
            using (view)
            {
                var plan = Plan(view, view.GetEntry("/tricky"), _temp.New());
                var manifest = FixtureImages.Manifest("browse-names");

                var copies = plan.Items.Where(i => i.Action == ExtractionAction.Copy).ToList();
                Assert.Equal(14, copies.Count);
                Assert.Equal(1, plan.Directories);
                Assert.Equal(14, plan.Files);
                long expectedBytes = manifest.Where(m => m.Type == "file" && m.Path.StartsWith("tricky/", StringComparison.Ordinal)).Sum(m => m.Size);
                Assert.Equal(expectedBytes, plan.TotalBytes);

                var names = copies.Select(c => c.RelativePath.Substring(@"tricky\".Length)).ToList();
                Assert.Equal(
                    new[]
                    {
                        "_CON", "README", "Readme (2)", "a_b", "bad-__-name", "hard-a", "hard-b", "_nul.txt",
                        "trailing_", "trailing_ (2)", "what_.txt", "with space.txt", "Ünï cödé.txt", "日本語.txt"
                    },
                    names);

                var renamed = plan.Issues.Where(i => i.Kind == ExtractionIssueKind.Renamed).ToDictionary(i => i.Path, i => i.ResultName);
                Assert.Equal(8, renamed.Count);
                Assert.Equal("_CON", renamed["/tricky/CON"]);
                Assert.Equal("Readme (2)", renamed["/tricky/Readme"]);
                Assert.Equal("trailing_ (2)", renamed["/tricky/trailing."]);

                var skipped = plan.Issues.Where(i => i.Kind == ExtractionIssueKind.Skipped).ToList();
                Assert.Equal(2, skipped.Count(i => i.Reason == ExtractionIssueReason.SymbolicLink));
                Assert.Equal(2, skipped.Count(i => i.Reason == ExtractionIssueReason.SpecialFile));
                Assert.DoesNotContain(plan.Issues, i => i.Kind == ExtractionIssueKind.Failed);
                Assert.Equal(1 + 14 + 4, plan.Items.Count);
            }
        }

        [Fact]
        public void ADeepPath_IsNotAProblem_BecauseOfExtendedPaths()
        {
            var (source, view) = FileSystemViewTests.Open("browse-names");
            using (source)
            using (view)
            {
                var plan = Plan(view, view.GetEntry("/deep"), _temp.New());
                Assert.Equal(1, plan.Files);
                Assert.DoesNotContain(plan.Issues, i => i.Kind == ExtractionIssueKind.Failed);
                Assert.True(plan.Items.Last().RelativePath.Length > 300);
            }
        }

        [Fact]
        public void PathsBeyondTheWindowsLimit_AreReportedAsFailed()
        {
            var tree = new FakeFileTree();
            var dir = tree.Root;
            string segment = new string('d', 200);
            for (int i = 0; i < 170; i++)
            {
                dir = tree.AddDirectory(dir, segment);
            }

            tree.AddFile(dir, "deep.txt", new byte[5]);
            var first = tree.GetChildren(tree.Root, CancellationToken.None)[0];
            var plan = Plan(tree, first, _temp.New());
            Assert.Contains(plan.Issues, i => i.Kind == ExtractionIssueKind.Failed && i.Reason == ExtractionIssueReason.PathTooLong);
            Assert.Equal(0, plan.Files);
            Assert.True(plan.Directories > 0);
        }

        [Fact]
        public void ExistingFiles_AreConflicts_SkippedOrKeptForReplacing()
        {
            var (source, view) = FileSystemViewTests.Open("browse-names");
            using (source)
            using (view)
            {
                var dest = _temp.New();
                Directory.CreateDirectory(Path.Combine(dest, "tricky"));
                File.WriteAllText(Path.Combine(dest, "tricky", "_CON"), "old");
                File.WriteAllText(Path.Combine(dest, "tricky", "README"), "old");

                var skip = Plan(view, view.GetEntry("/tricky"), dest, policy: ConflictPolicy.Skip);
                Assert.Equal(2, skip.Conflicts);
                Assert.Equal(2, skip.Items.Count(i => i.Action == ExtractionAction.SkipExisting));
                Assert.Equal(12, skip.Files);
                Assert.Equal(2, skip.Issues.Count(i => i.Reason == ExtractionIssueReason.AlreadyExists));

                var replace = Plan(view, view.GetEntry("/tricky"), dest, policy: ConflictPolicy.Replace);
                Assert.Equal(2, replace.Conflicts);
                Assert.Equal(14, replace.Files);
                Assert.DoesNotContain(replace.Items, i => i.Action == ExtractionAction.SkipExisting);
            }
        }

        [Fact]
        public void ADirectoryWhereAFileGoes_IsAFailedItem()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "name", new byte[3]);
            var dest = _temp.New();
            Directory.CreateDirectory(Path.Combine(dest, "name"));
            var plan = Plan(tree, tree.GetChildren(tree.Root, CancellationToken.None)[0], dest);
            Assert.Equal(ExtractionAction.Fail, Assert.Single(plan.Items).Action);
            Assert.Equal(ExtractionIssueReason.WriteFailed, Assert.Single(plan.Issues).Reason);
            Assert.Equal(0, plan.Files);
        }

        [Fact]
        public void NotEnoughFreeSpace_IsReportedWithBothSizes()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "a", new byte[1000]);
            tree.AddFile(tree.Root, "b", new byte[2000]);
            var tight = Plan(tree, tree.Root, _temp.New(), Info(free: 2999));
            Assert.True(tight.InsufficientSpace);
            Assert.Equal(3000, tight.TotalBytes);
            Assert.Equal(2999, tight.FreeBytes);
            Assert.False(Plan(tree, tree.Root, _temp.New(), Info(free: 3000)).InsufficientSpace);
            Assert.False(Plan(tree, tree.Root, _temp.New(), Info(free: null)).InsufficientSpace);
        }

        [Fact]
        public void FilesOf4GiBOrMore_FailOnFat32_ButNotOnNtfs()
        {
            var tree = new FakeFileTree();
            tree.AddSparseFile(tree.Root, "huge.bin", 5L << 30);
            tree.AddFile(tree.Root, "small.txt", new byte[10]);
            var fat = Plan(tree, tree.Root, _temp.New(), Info(fileSystem: "FAT32"));
            Assert.Equal(1, fat.Files);
            Assert.Equal(10, fat.TotalBytes);
            var issue = Assert.Single(fat.Issues);
            Assert.Equal(ExtractionIssueReason.FatLimit, issue.Reason);
            Assert.Equal("/huge.bin", issue.Path);

            var ntfs = Plan(tree, tree.Root, _temp.New(), Info(fileSystem: "NTFS"));
            Assert.Equal(2, ntfs.Files);
            Assert.Empty(ntfs.Issues);
        }

        [Fact]
        public void UnreadableAndNotViewableEntries_AreFailedItems_AndTheRestIsPlanned()
        {
            var tree = new FakeFileTree();
            var bad = tree.AddFile(tree.Root, "bad", new byte[4]);
            bad.Unreadable = "inode checksum mismatch";
            var enc = tree.AddFile(tree.Root, "secret", new byte[4]);
            enc.NotViewable = "the content is encrypted";
            tree.AddFile(tree.Root, "good", new byte[4]);
            var plan = Plan(tree, tree.Root, _temp.New());
            Assert.Equal(1, plan.Files);
            Assert.Contains(plan.Issues, i => i.Reason == ExtractionIssueReason.Unreadable && i.Path == "/bad" && i.Detail.Contains("checksum"));
            Assert.Contains(plan.Issues, i => i.Reason == ExtractionIssueReason.NotExtractable && i.Path == "/secret");
        }

        [Fact]
        public void ATargetThatIsTheProtectedImage_IsFlagged()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "disk.img", new byte[4]);
            var dest = _temp.New();
            var image = Path.Combine(dest, "disk.img");
            File.WriteAllBytes(image, new byte[8]);
            var plan = Plan(tree, tree.GetChildren(tree.Root, CancellationToken.None)[0], dest, protectedPath: image);
            Assert.Equal(image, plan.ProtectedPathHit);
            Assert.Null(Plan(tree, tree.GetChildren(tree.Root, CancellationToken.None)[0], _temp.New(), protectedPath: image).ProtectedPathHit);
        }

        [Fact]
        public void SymbolicLinksAndSpecialFiles_AreSkippedEvenWhenSelectedDirectly()
        {
            var tree = new FakeFileTree();
            var link = tree.AddSymlink(tree.Root, "l", "/x");
            var fifo = tree.AddSpecial(tree.Root, "f", SpecialKind.Fifo);
            Assert.Equal(ExtractionAction.SkipSymlink, Assert.Single(Plan(tree, link, _temp.New()).Items).Action);
            Assert.Equal(ExtractionAction.SkipSpecial, Assert.Single(Plan(tree, fifo, _temp.New()).Items).Action);
        }

        [Fact]
        public void Cancellation_IsHonoured()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "a", new byte[1]);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(
                () => ExtractionPlanner.Plan(tree, tree.Root, _temp.New(), Info(), ConflictPolicy.Replace, null, cts.Token));
        }
    }
}
