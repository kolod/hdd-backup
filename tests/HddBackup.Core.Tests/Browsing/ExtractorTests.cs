using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public sealed class ExtractorTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static readonly PathInfo Ntfs = new PathInfo { FreeBytes = long.MaxValue, FileSystem = "NTFS" };

        internal static ExtractionSummary Extract(
            IFileTree tree,
            FsEntry selection,
            string dest,
            ConflictPolicy policy = ConflictPolicy.Replace,
            CancellationToken ct = default(CancellationToken),
            Action<long> progress = null,
            string protectedPath = null)
        {
            var plan = ExtractionPlanner.Plan(tree, selection, dest, Ntfs, policy, protectedPath, ct);
            return Ext4Extractor.Run(tree, plan, policy, protectedPath, progress, ct);
        }

        private static string Ext(string path) => WindowsNames.ToExtendedPath(path);

        private static string FileSha(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(Ext(path), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return Sha256Accumulator.ToHex(sha.ComputeHash(stream));
            }
        }

        private static long UnixSeconds(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

        private static byte[] Bytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static IEnumerable<string> PartFiles(string dir)
        {
            return Directory.EnumerateFiles(Ext(dir), Ext4Extractor.PartPrefix + "*", SearchOption.AllDirectories);
        }

        [Theory]
        [InlineData("rpi-like-4k")]
        [InlineData("rpi-like-1k")]
        public void WholeFileSystem_IsIdenticalToTheManifest(string fixture)
        {
            var (source, view) = FileSystemViewTests.Open(fixture);
            using (source)
            using (view)
            {
                var dest = _temp.New();
                var summary = Extract(view, view.Root, dest);
                var manifest = FixtureImages.Manifest(fixture);
                string root = Path.Combine(dest, "root");

                foreach (var m in manifest)
                {
                    string path = Path.Combine(root, m.Path.Replace('/', '\\'));
                    switch (m.Type)
                    {
                        case "file":
                            Assert.True(File.Exists(Ext(path)), "missing " + m.Path);
                            Assert.True(m.Size == new FileInfo(Ext(path)).Length, m.Path + " size");
                            Assert.True(m.Sha256 == FileSha(path), m.Path + " content");
                            Assert.True(m.Mtime == UnixSeconds(File.GetLastWriteTimeUtc(Ext(path))), m.Path + " mtime");
                            break;
                        case "dir":
                            Assert.True(Directory.Exists(Ext(path)), "missing directory " + m.Path);
                            Assert.True(m.Mtime == UnixSeconds(Directory.GetLastWriteTimeUtc(Ext(path))), m.Path + " directory mtime");
                            break;
                        default: // symbolic links, device files, FIFOs: not created on Windows
                            Assert.False(File.Exists(Ext(path)) || Directory.Exists(Ext(path)), m.Path + " must not be created");
                            break;
                    }
                }

                Assert.Equal(manifest.Count(m => m.Type == "file"), summary.FilesExtracted);
                Assert.Equal(manifest.Count(m => m.Type == "dir") + 2, summary.DirectoriesCreated); // + root and lost+found
                Assert.Equal(manifest.Where(m => m.Type == "file").Sum(m => m.Size), summary.BytesExtracted);
                Assert.Empty(PartFiles(dest));
                ExportExtracted(fixture, root);
            }
        }

        [Fact]
        public void TrickyNames_AreRenamedPredictably_AndContentSurvives()
        {
            var (source, view) = FileSystemViewTests.Open("browse-names");
            using (source)
            using (view)
            {
                var dest = _temp.New();
                var summary = Extract(view, view.Root, dest);
                var manifest = FixtureImages.Manifest("browse-names").ToDictionary(m => m.Path);
                string root = Path.Combine(dest, "root");

                var expected = new Dictionary<string, string>
                {
                    { "tricky/CON", @"tricky\_CON" },
                    { "tricky/README", @"tricky\README" },
                    { "tricky/Readme", @"tricky\Readme (2)" },
                    { "tricky/a:b", @"tricky\a_b" },
                    { "tricky/nul.txt", @"tricky\_nul.txt" },
                    { "tricky/trailing ", @"tricky\trailing_" },
                    { "tricky/trailing.", @"tricky\trailing_ (2)" },
                    { "tricky/what?.txt", @"tricky\what_.txt" },
                    { "tricky/with space.txt", @"tricky\with space.txt" },
                    { "tricky/Ünï cödé.txt", @"tricky\Ünï cödé.txt" },
                    { "tricky/日本語.txt", @"tricky\日本語.txt" },
                    { "etc/hostname", @"etc\hostname" },
                    { "tricky/hard-a", @"tricky\hard-a" },
                    { "tricky/hard-b", @"tricky\hard-b" },
                    { "etc/hard-c", @"etc\hard-c" }
                };
                foreach (var pair in expected)
                {
                    string path = Path.Combine(root, pair.Value);
                    Assert.True(File.Exists(Ext(path)), "missing " + pair.Value);
                    Assert.Equal(manifest[pair.Key].Sha256, FileSha(path));
                }

                // The invalid UTF-8 name became "bad-__-name".
                Assert.True(File.Exists(Ext(Path.Combine(root, @"tricky\bad-__-name"))));

                // The path of more than 300 characters.
                var deep = manifest.Values.Single(m => m.Path.EndsWith("/file.txt", StringComparison.Ordinal) && m.Path.StartsWith("deep/", StringComparison.Ordinal));
                string deepPath = Path.Combine(root, deep.Path.Replace('/', '\\'));
                Assert.True(deepPath.Length > 300);
                Assert.Equal(deep.Sha256, FileSha(deepPath));

                // Not created: symbolic links, FIFO, socket.
                foreach (var name in new[] { "fast-link", "slow-link", "fifo", "app.sock" })
                {
                    Assert.False(File.Exists(Ext(Path.Combine(root, "tricky", name))), name);
                }

                Assert.Equal(8, summary.Issues.Count(i => i.Kind == ExtractionIssueKind.Renamed));
                Assert.Equal(4, summary.Issues.Count(i => i.Kind == ExtractionIssueKind.Skipped));
                Assert.DoesNotContain(summary.Issues, i => i.Kind == ExtractionIssueKind.Failed);
            }
        }

        [Fact]
        public void AFileOfSeveralChunks_IsCopiedExactly_WithMonotonicProgress()
        {
            var tree = new FakeFileTree();
            var data = Bytes((9 << 20) + 123, 7);
            var file = tree.AddFile(tree.Root, "big.bin", data);
            var seen = new List<long>();
            var summary = Extract(tree, file, _temp.New(), progress: seen.Add);
            Assert.Equal(data.Length, summary.BytesExtracted);
            Assert.True(seen.Count >= 3);
            Assert.Equal(seen.OrderBy(x => x).ToList(), seen);
            Assert.Equal(data.Length, seen.Last());
        }

        [Fact]
        public void EmptyFilesAndDirectories_AreCreated_WithTheirTimes()
        {
            var tree = new FakeFileTree();
            var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var dir = tree.AddDirectory(tree.Root, "d", time);
            tree.AddFile(dir, "empty", new byte[0], time.AddDays(1));
            tree.AddDirectory(dir, "sub", time.AddDays(2));
            var dest = _temp.New();
            var summary = Extract(tree, dir, dest);
            Assert.Equal(1, summary.FilesExtracted);
            Assert.Equal(2, summary.DirectoriesCreated);
            Assert.Equal(0, new FileInfo(Path.Combine(dest, "d", "empty")).Length);
            Assert.Equal(UnixSeconds(time.AddDays(1)), UnixSeconds(File.GetLastWriteTimeUtc(Path.Combine(dest, "d", "empty"))));
            Assert.Equal(UnixSeconds(time.AddDays(2)), UnixSeconds(Directory.GetLastWriteTimeUtc(Path.Combine(dest, "d", "sub"))));
            // The directory time is set after its files were written.
            Assert.Equal(UnixSeconds(time), UnixSeconds(Directory.GetLastWriteTimeUtc(Path.Combine(dest, "d"))));
        }

        [Fact]
        public void CancellingBeforeStart_WritesNothing()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "a", new byte[10]);
            var dest = _temp.New();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => Extract(tree, tree.Root, dest, ct: cts.Token));
            Assert.Empty(Directory.GetFileSystemEntries(dest));
        }

        [Fact]
        public void CancellingInTheMiddleOfAFile_LeavesNothingThatLooksComplete()
        {
            var (source, view) = FileSystemViewTests.Open("browse-large");
            using (source)
            using (view)
            {
                var dest = _temp.New();
                var cts = new CancellationTokenSource();
                var five = view.GetEntry("/data/five.bin");
                Assert.True(five.Size > 4 << 20); // more than one 4 MiB chunk
                Assert.Throws<OperationCanceledException>(() => Extract(view, five, dest, ct: cts.Token, progress: done => cts.Cancel()));
                Assert.Empty(Directory.GetFileSystemEntries(dest));
            }
        }

        [Fact]
        public void CancellingBetweenFiles_KeepsTheCompletedFilesWhole()
        {
            var tree = new FakeFileTree();
            var a = Bytes(1000, 1);
            tree.AddFile(tree.Root, "a", a);
            tree.AddFile(tree.Root, "b", Bytes(1000, 2));
            tree.AddFile(tree.Root, "c", Bytes(1000, 3));
            var dest = _temp.New();
            var cts = new CancellationTokenSource();
            int files = 0;
            Assert.Throws<OperationCanceledException>(() => Extract(tree, tree.Root, dest, ct: cts.Token, progress: done =>
            {
                if (++files == 1)
                {
                    cts.Cancel();
                }
            }));
            Assert.Empty(PartFiles(dest));
            string root = Path.Combine(dest, "root");
            foreach (var f in Directory.GetFiles(root))
            {
                Assert.Equal(1000, new FileInfo(f).Length);
            }
        }

        [Fact]
        public void AnUnreadableItem_IsReported_AndTheRestIsExtracted()
        {
            var tree = new FakeFileTree();
            var good = Bytes(500, 4);
            tree.AddFile(tree.Root, "a-good", good);
            var bad = tree.AddFile(tree.Root, "b-bad", Bytes(500, 5));
            tree.AddFile(tree.Root, "c-good", good);
            tree.OnRead = f =>
            {
                if (ReferenceEquals(f, bad))
                {
                    throw new ImagingException(ErrorCode.CorruptImage, "/b-bad: extent tree is damaged");
                }
            };
            var dest = _temp.New();
            var summary = Extract(tree, tree.Root, dest);
            Assert.Equal(2, summary.FilesExtracted);
            var issue = Assert.Single(summary.Issues);
            Assert.Equal(ExtractionIssueKind.Failed, issue.Kind);
            Assert.Equal(ExtractionIssueReason.Unreadable, issue.Reason);
            Assert.Equal("/b-bad", issue.Path);
            Assert.False(File.Exists(Path.Combine(dest, "root", "b-bad")));
            Assert.Empty(PartFiles(dest));
        }

        [Fact]
        public void ASourceReadError_StopsTheExtraction_AndLeavesNoPartFile()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "a", Bytes(500, 4));
            var b = tree.AddFile(tree.Root, "b", Bytes(500, 5));
            tree.OnRead = f =>
            {
                if (ReferenceEquals(f, b))
                {
                    throw new ImagingException(ErrorCode.DeviceRemoved, "device removed", 1234);
                }
            };
            var dest = _temp.New();
            var ex = Assert.Throws<ImagingException>(() => Extract(tree, tree.Root, dest));
            Assert.Equal(ErrorCode.DeviceRemoved, ex.Code);
            Assert.Empty(PartFiles(dest));
        }

        [Fact]
        public void ADestinationWriteProblem_FailsOnlyThatItem()
        {
            var tree = new FakeFileTree();
            tree.AddFile(tree.Root, "blocked.txt", Bytes(100, 1));
            tree.AddFile(tree.Root, "fine.txt", Bytes(100, 2));
            var dest = _temp.New();
            Directory.CreateDirectory(Path.Combine(dest, "root"));
            string blocked = Path.Combine(dest, "root", "blocked.txt");
            File.WriteAllBytes(blocked, new byte[] { 5 });
            ExtractionSummary summary;
            // An existing file that another program holds open cannot be replaced.
            using (new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                summary = Extract(tree, tree.Root, dest);
            }

            Assert.Equal(1, summary.FilesExtracted);
            var issue = Assert.Single(summary.Issues, i => i.Kind == ExtractionIssueKind.Failed);
            Assert.Equal(ExtractionIssueReason.WriteFailed, issue.Reason);
            Assert.Equal("/blocked.txt", issue.Path);
            Assert.True(File.Exists(Path.Combine(dest, "root", "fine.txt")));
            Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(blocked));
            Assert.Empty(PartFiles(dest));
        }

        [Theory]
        [InlineData(unchecked((int)0x80070070), true)] // ERROR_DISK_FULL
        [InlineData(unchecked((int)0x80070027), true)] // ERROR_HANDLE_DISK_FULL
        [InlineData(unchecked((int)0x80070020), false)] // ERROR_SHARING_VIOLATION
        [InlineData(unchecked((int)0x80070005), false)]
        public void DiskFullIsRecognisedFromTheErrorCode(int hresult, bool full)
        {
            Assert.Equal(full, Ext4Extractor.IsDiskFull(new IOException("x", hresult)));
        }

        [Fact]
        public void ExistingFiles_AreKeptUnderSkip_AndReplacedUnderReplace()
        {
            var tree = new FakeFileTree();
            var file = tree.AddFile(tree.Root, "f.txt", new byte[] { 1, 2, 3 });
            var dest = _temp.New();
            string target = Path.Combine(dest, "f.txt");
            File.WriteAllBytes(target, new byte[] { 9, 9 });

            var skipped = Extract(tree, file, dest, ConflictPolicy.Skip);
            Assert.Equal(0, skipped.FilesExtracted);
            Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(target));
            Assert.Contains(skipped.Issues, i => i.Reason == ExtractionIssueReason.AlreadyExists);

            File.SetAttributes(target, FileAttributes.ReadOnly);
            var replaced = Extract(tree, file, dest, ConflictPolicy.Replace);
            Assert.Equal(1, replaced.FilesExtracted);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
            Assert.Empty(PartFiles(dest));
        }

        [Fact]
        public void WritingOverTheProtectedImage_IsRefused()
        {
            var tree = new FakeFileTree();
            var file = tree.AddFile(tree.Root, "disk.img", new byte[] { 1 });
            var dest = _temp.New();
            string image = Path.Combine(dest, "disk.img");
            File.WriteAllBytes(image, new byte[] { 7, 7, 7 });
            var plan = ExtractionPlanner.Plan(tree, file, dest, Ntfs, ConflictPolicy.Replace, image, CancellationToken.None);
            Assert.NotNull(plan.ProtectedPathHit);
            var ex = Assert.Throws<ImagingException>(() => Ext4Extractor.Run(tree, plan, ConflictPolicy.Replace, image, null, CancellationToken.None));
            Assert.Equal(ErrorCode.DestinationIsSource, ex.Code);
            Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(image));
        }

        /// <summary>
        /// Exports a listing of the extracted tree (directories and the SHA-256 of every file, UTF-8,
        /// paths relative to the extracted root) for the independent debugfs comparison in CI
        /// (tools/ci/Invoke-Ext4Checks.sh). A listing, not the files: it compares byte for byte
        /// without character set conversions and without paths that are too long for artifact tools.
        /// </summary>
        private static void ExportExtracted(string fixture, string root)
        {
            var export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (string.IsNullOrEmpty(export))
            {
                return;
            }

            var lines = new List<string>();
            Describe(Ext(root), string.Empty, lines);
            var dir = Path.Combine(export, "extracted");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fixture + ".tsv"), string.Join("\n", lines) + "\n", new System.Text.UTF8Encoding(false));
        }

        private static void Describe(string directory, string prefix, List<string> lines)
        {
            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                string name = prefix + Path.GetFileName(sub);
                lines.Add("D\t" + name);
                Describe(sub, name + "/", lines);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    lines.Add("F\t" + Sha256Accumulator.ToHex(sha.ComputeHash(stream)) + "\t" + prefix + Path.GetFileName(file));
                }
            }
        }
    }
}
