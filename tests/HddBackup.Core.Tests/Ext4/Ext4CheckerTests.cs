using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public sealed class Ext4CheckerTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        [Theory]
        [MemberData(nameof(Ext4WriterTests.Cases), MemberType = typeof(Ext4WriterTests))]
        public void RebuiltFileSystems_AreConsistent(string fixture, double factor)
        {
            var path = Ext4WriterTests.Rebuild(fixture, factor, _temp);
            using (var device = FileBlockDevice.OpenRead(path))
            {
                var findings = Ext4Checker.Check(device);
                Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
            }
        }

        [Theory]
        [InlineData("rpi-like-4k")]
        [InlineData("rpi-legacy")]
        public void Mke2fsSources_AreConsistent(string fixture)
        {
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            {
                var findings = Ext4Checker.Check(root);
                Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
            }
        }

        /// <summary>Copy of a rebuilt legacy file system (no metadata checksums to fix up).</summary>
        private string LegacyCopy()
        {
            var path = Ext4WriterTests.Rebuild("rpi-legacy", 0, _temp);
            var copy = Path.Combine(Path.GetDirectoryName(path), "corrupt.ext4");
            File.Copy(path, copy, true);
            return copy;
        }

        private static void Patch(string path, long offset, Func<byte, byte> change)
        {
            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = offset;
                int b = f.ReadByte();
                f.Position = offset;
                f.WriteByte(change((byte)b));
            }
        }

        private static (long InodeOffset, long DataBlock) Locate(string path, string name)
        {
            using (var device = FileBlockDevice.OpenRead(path))
            using (var reader = new Ext4Reader(device))
            {
                var node = Ext4TestHelpers.Walk(reader).Single(n => n.Path == name);
                uint index = node.Inode.Number - 1;
                int group = (int)(index / reader.Superblock.InodesPerGroup);
                long inodeOffset = (reader.Groups[group].InodeTable * reader.BlockSize) + ((index % reader.Superblock.InodesPerGroup) * reader.Superblock.InodeSize);
                var map = reader.MapFile(node.Inode);
                return (inodeOffset, map.Count > 0 ? map[0].Physical : -1);
            }
        }

        private static string Findings(string path)
        {
            using (var device = FileBlockDevice.OpenRead(path))
            {
                return string.Join(" | ", Ext4Checker.Check(device));
            }
        }

        [Fact]
        public void UsedBlockMarkedFree_IsReported()
        {
            var path = LegacyCopy();
            var (_, block) = Locate(path, "usr/lib/libc.so.6");
            long bitmapBlock;
            using (var device = FileBlockDevice.OpenRead(path))
            using (var reader = new Ext4Reader(device))
            {
                bitmapBlock = reader.Groups[(int)(block / reader.Superblock.BlocksPerGroup)].BlockBitmap * reader.BlockSize;
            }

            long bit = block % 32768;
            Patch(path, bitmapBlock + (bit >> 3), b => (byte)(b & ~(1 << (int)(bit & 7))));

            Assert.Contains("in use but marked free", Findings(path));
        }

        [Fact]
        public void WrongLinkCount_IsReported()
        {
            var path = LegacyCopy();
            var (inodeOffset, _) = Locate(path, "home/pi/hard-a");
            Patch(path, inodeOffset + 0x1A, b => 7);

            Assert.Contains("link count", Findings(path));
        }

        [Fact]
        public void WrongBlockCount_IsReported()
        {
            var path = LegacyCopy();
            var (inodeOffset, _) = Locate(path, "etc/passwd");
            Patch(path, inodeOffset + 0x1C, b => (byte)(b + 8));

            Assert.Contains("i_blocks", Findings(path));
        }

        [Fact]
        public void CorruptInodeChecksum_IsReported()
        {
            var path = Ext4WriterTests.Rebuild("rpi-like-4k", 0, _temp);
            var copy = Path.Combine(Path.GetDirectoryName(path), "corrupt-csum.ext4");
            File.Copy(path, copy, true);
            var (inodeOffset, _) = Locate(copy, "etc/passwd");
            Patch(copy, inodeOffset + 0x10, b => (byte)(b ^ 1));

            Assert.Contains("checksum", Findings(copy));
        }
    }

    public sealed class Ext4TreeComparerTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static System.Collections.Generic.List<string> CompareWithSource(string fixture, string rebuilt)
        {
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            using (var a = new Ext4Reader(root))
            using (var device = FileBlockDevice.OpenRead(rebuilt))
            using (var b = new Ext4Reader(device))
            {
                return Ext4TreeComparer.Compare(a, b, CancellationToken.None);
            }
        }

        [Theory]
        [MemberData(nameof(Ext4WriterTests.Cases), MemberType = typeof(Ext4WriterTests))]
        public void Rebuilt_IsIdenticalToSource(string fixture, double factor)
        {
            var findings = CompareWithSource(fixture, Ext4WriterTests.Rebuild(fixture, factor, _temp));
            Assert.True(findings.Count == 0, string.Join(Environment.NewLine, findings));
        }

        [Fact]
        public void ChangedContent_IsReported()
        {
            var path = Ext4WriterTests.Rebuild("rpi-legacy", 0, _temp);
            long block;
            int blockSize;
            using (var device = FileBlockDevice.OpenRead(path))
            using (var reader = new Ext4Reader(device))
            {
                var node = Ext4TestHelpers.Walk(reader).Single(n => n.Path == "usr/bin/bash");
                block = reader.MapFile(node.Inode)[0].Physical;
                blockSize = reader.BlockSize;
            }

            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = (block * blockSize) + 100;
                f.WriteByte(0x42);
            }

            var findings = CompareWithSource("rpi-legacy", path);
            Assert.Contains(findings, f => f.Contains("content"));
        }

        [Fact]
        public void ChangedMode_IsReported()
        {
            var path = Ext4WriterTests.Rebuild("rpi-legacy", 0, _temp);
            long inodeOffset;
            using (var device = FileBlockDevice.OpenRead(path))
            using (var reader = new Ext4Reader(device))
            {
                var node = Ext4TestHelpers.Walk(reader).Single(n => n.Path == "etc/fstab");
                uint index = node.Inode.Number - 1;
                inodeOffset = (reader.Groups[0].InodeTable * reader.BlockSize) + (index * reader.Superblock.InodeSize);
            }

            using (var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = inodeOffset;
                f.WriteByte(0xFF);
            }

            var findings = CompareWithSource("rpi-legacy", path);
            Assert.Contains(findings, f => f.Contains("mode"));
        }
    }
}
