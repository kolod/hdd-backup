using System.Collections.Generic;
using System.IO;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class Ext4ReaderTests
    {
        public static TheoryData<string> PositiveFixtures() => new TheoryData<string> { "rpi-like-4k", "rpi-like-1k", "rpi-legacy", "rpi-like-gpt" };

        [Theory]
        [MemberData(nameof(PositiveFixtures))]
        public void Reader_ReproducesTheManifest(string fixture)
        {
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                Assert.Empty(reader.Validate());
                var nodes = Ext4TestHelpers.Walk(reader).ToDictionary(n => n.Path);
                var manifest = FixtureImages.Manifest(fixture);
                var hardLinkInodes = new Dictionary<string, uint>();

                foreach (var m in manifest)
                {
                    Assert.True(nodes.ContainsKey(m.Path), "missing " + m.Path);
                    var inode = nodes[m.Path].Inode;
                    Assert.Equal(m.Type, Ext4TestHelpers.TypeName(inode));
                    Assert.Equal(m.Mode, inode.Mode & 0xFFF);
                    Assert.Equal(m.Uid, inode.Uid);
                    Assert.Equal(m.Gid, inode.Gid);
                    Assert.Equal(m.Mtime, (long)inode.Mtime);
                    if (m.Type == "file")
                    {
                        Assert.Equal(m.Size, inode.Size);
                        Assert.Equal(m.Sha256, Ext4TestHelpers.Sha256(reader, inode));
                    }
                    else if (m.Type == "symlink")
                    {
                        Assert.Equal(m.LinkTarget, reader.ReadSymlink(inode));
                    }
                    else if (m.Type == "chr" || m.Type == "blk")
                    {
                        Assert.Equal(m.Rdev, Ext4TestHelpers.Rdev(inode));
                    }

                    var xattrs = Ext4TestHelpers.XattrsAsTools(reader, inode);
                    Assert.Equal(m.Xattrs.Keys.OrderBy(k => k), xattrs.Keys.OrderBy(k => k));
                    foreach (var kv in m.Xattrs)
                    {
                        Assert.True(Ext4TestHelpers.SameBytes(kv.Value, xattrs[kv.Key]), m.Path + " xattr " + kv.Key);
                    }

                    if (m.HardLinkKey != null)
                    {
                        if (hardLinkInodes.TryGetValue(m.HardLinkKey, out var ino))
                        {
                            Assert.Equal(ino, inode.Number);
                        }
                        else
                        {
                            hardLinkInodes[m.HardLinkKey] = inode.Number;
                        }
                    }
                }

                // Nothing extra besides lost+found.
                var expected = new HashSet<string>(manifest.Select(m => m.Path)) { "lost+found" };
                var extra = nodes.Keys.Where(p => !expected.Contains(p)).ToList();
                Assert.True(extra.Count == 0, "unexpected: " + string.Join(", ", extra));
            }
        }

        [Fact]
        public void Features_AreReportedCorrectly()
        {
            using (var root = FixtureImages.OpenRoot("rpi-like-4k", out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                Assert.True(reader.Superblock.HasMetadataCsum);
                Assert.True(reader.Superblock.Is64Bit);
                Assert.True(reader.Superblock.HasResizeInode);
                Assert.Equal(4096, reader.BlockSize);
                Assert.Equal("rootfs", reader.Superblock.VolumeName);
            }

            using (var root = FixtureImages.OpenRoot("rpi-legacy", out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                Assert.False(reader.Superblock.HasMetadataCsum);
                Assert.True(reader.Superblock.HasGdtCsum);
                Assert.False(reader.Superblock.Is64Bit);
            }
        }

        [Theory]
        [InlineData("dirty", "needs_recovery")]
        [InlineData("inline", "feature:inline_data")]
        public void NegativeFixtures_AreFlagged(string fixture, string problem)
        {
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                Assert.Contains(problem, reader.Validate());
            }
        }

        [Fact]
        public void CorruptedInode_FailsChecksum()
        {
            var copy = Path.Combine(TestPaths.NewTempDirectory(), "corrupt.img");
            File.Copy(FixtureImages.RawPath("rpi-like-4k"), copy);
            long rootStart;
            long inodeTableByte;
            using (var image = FileBlockDevice.OpenRead(copy))
            {
                var p = PartitionTableReader.Read(image).Partitions.Last();
                rootStart = p.Offset;
                using (var reader = new Ext4Reader(new PartitionView(image, p.Offset, p.Length)))
                {
                    // Inode 2 (root directory) lives in the first inode table slot after inode 1.
                    inodeTableByte = rootStart + (reader.Groups[0].InodeTable * reader.BlockSize) + reader.Superblock.InodeSize + 0x10;
                }
            }

            using (var f = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite))
            {
                f.Position = inodeTableByte;
                int b = f.ReadByte();
                f.Position = inodeTableByte;
                f.WriteByte((byte)(b ^ 0x01));
            }

            using (var image = FileBlockDevice.OpenRead(copy))
            {
                var p = PartitionTableReader.Read(image).Partitions.Last();
                using (var reader = new Ext4Reader(new PartitionView(image, p.Offset, p.Length)))
                {
                    var ex = Assert.Throws<Ext4FormatException>(() => reader.ReadInode(2));
                    Assert.Contains("checksum", ex.Message);
                }
            }
        }
    }
}
