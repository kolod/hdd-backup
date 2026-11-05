using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Errors;
using HddBackup.Core.Formats;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public class FileSystemViewTests
    {
        internal static (BrowseSource Source, Ext4FileSystemView View) Open(string fixture)
        {
            var source = BrowseSource.OpenImage(FixtureImages.RawPath(fixture));
            return (source, source.OpenFileSystem(source.Browsable.Last()));
        }

        /// <summary>Walks the whole tree (describing every entry); keys are paths.</summary>
        internal static Dictionary<string, FsEntry> Walk(IFileTree view)
        {
            var all = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
            var stack = new Stack<FsEntry>();
            stack.Push(view.Root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var c in view.GetChildren(dir, CancellationToken.None))
                {
                    view.Describe(c);
                    all[c.Path.TrimStart('/')] = c;
                    if (c.IsDirectory)
                    {
                        stack.Push(c);
                    }
                }
            }

            return all;
        }

        internal static string Sha256(IFileTree view, FsEntry file)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 20];
                long offset = 0;
                int n;
                while ((n = view.ReadRange(file, offset, buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    offset += n;
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Sha256Accumulator.ToHex(sha.Hash);
            }
        }

        [Theory]
        [InlineData("browse-names")]
        [InlineData("rpi-like-4k")]
        public void TreeMatchesTheManifest(string fixture)
        {
            var (source, view) = Open(fixture);
            using (source)
            using (view)
            {
                var tree = Walk(view);
                var manifest = FixtureImages.Manifest(fixture);
                foreach (var m in manifest)
                {
                    Assert.True(tree.ContainsKey(m.Path), "missing " + m.Path);
                    var e = tree[m.Path];
                    Assert.Null(e.Unreadable);
                    Assert.True(m.Mode == (e.Mode & 0xFFF), m.Path + " mode");
                    Assert.True(m.Uid == e.Uid && m.Gid == e.Gid, m.Path + " owner");
                    Assert.True(m.Mtime == new DateTimeOffset(e.Modified, TimeSpan.Zero).ToUnixTimeSeconds(), m.Path + " mtime");
                    switch (m.Type)
                    {
                        case "dir":
                            Assert.Equal(FsEntryKind.Directory, e.Kind);
                            break;
                        case "file":
                            Assert.Equal(FsEntryKind.File, e.Kind);
                            Assert.True(m.Size == e.Size, m.Path + " size");
                            if (m.Size < 8 << 20)
                            {
                                Assert.True(m.Sha256 == Sha256(view, e), m.Path + " content");
                            }

                            break;
                        case "symlink":
                            Assert.Equal(FsEntryKind.Symlink, e.Kind);
                            Assert.Equal(m.LinkTarget, e.LinkTarget);
                            break;
                        case "fifo":
                            Assert.Equal(SpecialKind.Fifo, e.Special);
                            break;
                        case "sock":
                            Assert.Equal(SpecialKind.Socket, e.Special);
                            break;
                        case "chr":
                            Assert.Equal(SpecialKind.CharDevice, e.Special);
                            break;
                        case "blk":
                            Assert.Equal(SpecialKind.BlockDevice, e.Special);
                            break;
                    }
                }

                // Only the entries that make does not list (lost+found) are extra.
                var extra = tree.Keys.Except(manifest.Select(m => m.Path)).ToList();
                Assert.All(extra, p => Assert.StartsWith("lost+found", p));
            }
        }

        [Fact]
        public void Children_AreSortedDirectoriesFirstThenOrdinal()
        {
            var (source, view) = Open("browse-names");
            using (source)
            using (view)
            {
                foreach (var dir in new[] { view.Root, view.GetEntry("/tricky") })
                {
                    var kids = view.GetChildren(dir, CancellationToken.None);
                    var expected = kids.OrderBy(k => k.IsDirectory ? 0 : 1).ThenBy(k => k.Name, StringComparer.Ordinal).ToList();
                    Assert.Equal(expected.Select(k => k.Path), kids.Select(k => k.Path));
                }
            }
        }

        [Fact]
        public void InvalidUtf8Names_ShowReplacementCharacters_AndHardLinksShareTheInode()
        {
            var (source, view) = Open("browse-names");
            using (source)
            using (view)
            {
                var tricky = view.GetChildren(view.GetEntry("/tricky"), CancellationToken.None);
                Assert.Contains(tricky, e => e.Name.Contains('�'));
                var a = tricky.Single(e => e.Name == "hard-a");
                var b = tricky.Single(e => e.Name == "hard-b");
                Assert.Equal(a.Inode, b.Inode);
                Assert.Equal(a.Inode, view.GetEntry("/etc/hard-c").Inode);
            }
        }

        [Fact]
        public void GetEntry_FindsByPath_AndByInodeHint()
        {
            var (source, view) = Open("browse-names");
            using (source)
            using (view)
            {
                var host = view.GetEntry("/etc/hostname");
                Assert.NotNull(host);
                Assert.Equal(12, host.Size);
                Assert.Same(view.Root, view.GetEntry("/"));
                Assert.Null(view.GetEntry("/etc/missing"));
                Assert.Null(view.GetEntry("/etc/hostname", host.Inode + 1000));
                Assert.NotNull(view.GetEntry("/etc/hostname", host.Inode));
            }
        }

        [Fact]
        public void Counts_MatchAWalkOfTheTree()
        {
            foreach (var fixture in new[] { "browse-names", "rpi-like-4k" })
            {
                var (source, view) = Open(fixture);
                using (source)
                using (view)
                {
                    var tree = Walk(view);
                    long dirs = tree.Values.Count(e => e.IsDirectory);
                    long files = tree.Values.Where(e => !e.IsDirectory).Select(e => e.Inode).Distinct().Count();
                    Assert.Equal(dirs, view.Counts.Directories);
                    Assert.Equal(files, view.Counts.Files);
                    Assert.False(view.Counts.Approximate);
                }
            }
        }

        [Fact]
        public void CancelledListing_Throws()
        {
            var (source, view) = Open("browse-names");
            using (source)
            using (view)
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                Assert.Throws<OperationCanceledException>(() => view.GetChildren(view.GetEntry("/tricky"), cts.Token));
            }
        }

        [Fact]
        public void LargeDirectory_ListsQuickly_AndTheSparseFileReportsItsSizeWithoutReadingIt()
        {
            var (source, view) = Open("browse-large");
            using (source)
            using (view)
            {
                var many = view.GetEntry("/many");
                var watch = Stopwatch.StartNew();
                var kids = view.GetChildren(many, CancellationToken.None);
                watch.Stop();
                Assert.Equal(20000, kids.Count);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "listing took " + watch.Elapsed);

                var hole = view.GetEntry("/data/hole.bin");
                Assert.Equal(2147483648L, hole.Size);
                var buffer = new byte[1 << 20];
                Assert.Equal(buffer.Length, view.ReadRange(hole, 1L << 30, buffer, 0, buffer.Length));
                Assert.All(buffer, b => Assert.Equal(0, b));
                var tail = new byte[4096];
                Assert.Equal(4096, view.ReadRange(hole, hole.Size - 4096, tail, 0, tail.Length));
                Assert.StartsWith("hole-tail", System.Text.Encoding.ASCII.GetString(tail, 0, 9));
                Assert.Equal(0, view.ReadRange(hole, hole.Size, tail, 0, tail.Length));
            }
        }

        [Fact]
        public void OpeningTheRoot_ReadsOnlyTheSuperblockAndGroupDescriptors()
        {
            // SC-001: showing the root must not depend on the size of the file system.
            var path = FixtureImages.RawPath("browse-large");
            var device = new HddBackup.Core.Tests.Fakes.ReadOnlyBlockDevice(HddBackup.Core.Devices.FileBlockDevice.OpenRead(path));
            var watch = Stopwatch.StartNew();
            using (var source = BrowseSource.Open(device))
            using (var view = source.OpenFileSystem(source.Browsable.Single()))
            {
                var kids = view.GetChildren(view.Root, CancellationToken.None);
                Assert.NotEmpty(kids);
                var counts = view.Counts;
                Assert.True(counts.Files > 20000);
            }

            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "opening took " + watch.Elapsed);
            Assert.True(device.ReadCalls < 200, "the root took " + device.ReadCalls + " reads");
        }

        [Fact]
        public void ReadingADirectoryAsAFile_IsRejected()
        {
            var (source, view) = Open("browse-names");
            using (source)
            using (view)
            {
                var buffer = new byte[16];
                var ex = Assert.Throws<ImagingException>(() => view.ReadRange(view.Root, 0, buffer, 0, buffer.Length));
                Assert.Equal(ErrorCode.InvalidRequest, ex.Code);
            }
        }
    }
}
