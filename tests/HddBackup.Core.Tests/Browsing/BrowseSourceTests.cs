using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public sealed class BrowseSourceTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        internal static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream));
            }
        }

        [Fact]
        public void WholeDeviceImage_ListsOnlyTheExt4PartitionAsBrowsable()
        {
            using (var source = BrowseSource.OpenImage(FixtureImages.RawPath("rpi-like-4k")))
            {
                Assert.Equal(BrowseProblem.None, source.Problem);
                Assert.Equal(2, source.Partitions.Count);
                var boot = source.Partitions[0];
                Assert.False(boot.IsBrowsable);
                Assert.Contains("FAT32", boot.Unsupported);
                var root = Assert.Single(source.Browsable);
                Assert.Equal(2, root.Number);
                Assert.Equal("ext4", root.FileSystem);
                Assert.Equal(FileSystemCondition.Clean, root.State.Condition);
                Assert.Same(root, source.Partitions[1]);
            }
        }

        [Fact]
        public void SinglePartitionImage_OpensDirectly()
        {
            using (var source = BrowseSource.OpenImage(FixtureImages.RawPath("browse-names")))
            {
                Assert.Equal(BrowseProblem.None, source.Problem);
                var only = Assert.Single(source.Partitions);
                Assert.True(only.IsBrowsable);
                Assert.Equal(1, only.Number);
                Assert.Equal(0, only.Offset);
                using (var view = source.OpenFileSystem(only))
                {
                    Assert.Equal("/", view.Root.Path);
                    Assert.True(view.Root.IsDirectory);
                }
            }
        }

        [Fact]
        public void ImageWithoutExt4_ReportsNoExt4()
        {
            var path = Path.Combine(_temp.New(), "empty.img");
            File.WriteAllBytes(path, new byte[1 << 20]);
            using (var source = BrowseSource.OpenImage(path))
            {
                Assert.Equal(BrowseProblem.NoExt4, source.Problem);
                Assert.Empty(source.Browsable);
            }
        }

        [Fact]
        public void CompressedImage_NeedsConversion_AndCreatesNothing()
        {
            var dir = _temp.New();
            var path = Path.Combine(dir, "backup.img.gz");
            File.Copy(FixtureImages.CompressedPath("browse-names"), path);
            using (var source = BrowseSource.OpenImage(path))
            {
                Assert.Equal(BrowseProblem.NeedsConversion, source.Problem);
                Assert.Empty(source.Partitions);
            }

            Assert.Equal(new[] { path }, Directory.GetFileSystemEntries(dir));
        }

        [Fact]
        public void MissingFile_ThrowsReadError()
        {
            var ex = Assert.Throws<ImagingException>(() => BrowseSource.OpenImage(Path.Combine(_temp.New(), "nope.img")));
            Assert.Equal(ErrorCode.ReadError, ex.Code);
        }

        [Fact]
        public void UnsupportedFeature_NamesTheFeature()
        {
            var path = Path.Combine(_temp.New(), "meta-bg.img");
            File.Copy(FixtureImages.RawPath("browse-names"), path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                var raw = new byte[Ext4Superblock.Size];
                stream.Position = Ext4Superblock.ByteOffset;
                stream.Read(raw, 0, raw.Length);
                var sb = new Ext4Superblock(raw);
                sb.FeatureIncompat |= Ext4Features.IncompatMetaBg;
                sb.Checksum = sb.ComputeChecksum();
                stream.Position = Ext4Superblock.ByteOffset;
                stream.Write(sb.Raw, 0, sb.Raw.Length);
            }

            using (var source = BrowseSource.OpenImage(path))
            {
                Assert.Equal(BrowseProblem.NoExt4, source.Problem);
                var p = Assert.Single(source.Partitions);
                Assert.False(p.IsBrowsable);
                Assert.Contains("meta_bg", p.Unsupported);
                Assert.Throws<ImagingException>(() => source.OpenFileSystem(p));
            }
        }

        [Fact]
        public void BrowsingAReadOnlyDevice_NeverWrites_AndLeavesTheImageUnchanged()
        {
            var path = FixtureImages.RawPath("rpi-like-4k");
            string before = Sha256Of(path);
            var device = new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(path));
            using (var source = BrowseSource.Open(device))
            {
                using (var view = source.OpenFileSystem(source.Browsable.Single()))
                {
                    foreach (var child in view.GetChildren(view.Root, default(System.Threading.CancellationToken)))
                    {
                        view.Describe(child);
                    }
                }
            }

            Assert.Equal(0, device.WriteCalls);
            Assert.True(device.ReadCalls > 0);
            Assert.Equal(before, Sha256Of(path));
        }
    }
}
