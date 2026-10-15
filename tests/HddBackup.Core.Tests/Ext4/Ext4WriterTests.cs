using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class Ext4WriterTests
    {
        /// <summary>
        /// Rebuilds the fixture's ext4 into a new file (partition only). blocksFactor = new size
        /// relative to the source block count; 0 ⇒ minimum (data + 25%).
        /// </summary>
        internal static string Rebuild(string fixture, double blocksFactor)
        {
            var output = Path.Combine(TestPaths.NewTempDirectory(), fixture + "-" + blocksFactor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".ext4");
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                var sb = reader.Superblock;
                var plan = Ext4CopyPlan.Build(reader, CancellationToken.None);
                long blocks = blocksFactor > 0
                    ? (long)(sb.BlocksCount * blocksFactor)
                    : (long)((plan.DataBlocks + plan.DirectoryBlocks + plan.SourceJournalBlocks) * 1.25) + 4096;
                var layout = Ext4Layout.Create(
                    blocks, reader.BlockSize, sb.InodeSize, 16384, plan.HighestInode + 16,
                    sb.DescriptorSize, sb.HasResizeInode, sb.LogGroupsPerFlex);
                long journal = Math.Min(plan.SourceJournalBlocks, Ext4Layout.DefaultJournalBlocks(layout.BlocksCount));
                using (var target = FileBlockDevice.Create(output, layout.BlocksCount * layout.BlockSize))
                {
                    new Ext4Writer(target, layout, reader, plan).Build(journal, null, CancellationToken.None);
                }
            }

            var export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (!string.IsNullOrEmpty(export))
            {
                Directory.CreateDirectory(export);
                File.Copy(output, Path.Combine(export, Path.GetFileName(output)), true);
            }

            return output;
        }

        public static TheoryData<string, double> Cases()
        {
            var data = new TheoryData<string, double>();
            foreach (var f in new[] { "rpi-like-4k", "rpi-like-1k", "rpi-legacy", "rpi-like-gpt" })
            {
                data.Add(f, 1.0);
                data.Add(f, 0);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Rebuilt_FileSystem_ReproducesTheManifest(string fixture, double factor)
        {
            var path = Rebuild(fixture, factor);
            using (var device = FileBlockDevice.OpenRead(path))
            using (var reader = new Ext4Reader(device))
            {
                Assert.Empty(reader.Validate());
                ManifestAssert.Matches(reader, FixtureImages.Manifest(fixture));
            }
        }
    }

    /// <summary>Shared manifest comparison (used by reader and writer tests).</summary>
    internal static class ManifestAssert
    {
        public static void Matches(Ext4Reader reader, IReadOnlyList<ManifestEntry> manifest)
        {
            var nodes = Ext4TestHelpers.Walk(reader).ToDictionary(n => n.Path);
            var hardLinks = new Dictionary<string, uint>();
            foreach (var m in manifest)
            {
                Assert.True(nodes.ContainsKey(m.Path), "missing " + m.Path);
                var inode = nodes[m.Path].Inode;
                Assert.True(m.Type == Ext4TestHelpers.TypeName(inode), m.Path + " type");
                Assert.True(m.Mode == (inode.Mode & 0xFFF), m.Path + " mode");
                Assert.True(m.Uid == inode.Uid && m.Gid == inode.Gid, m.Path + " owner");
                Assert.True(m.Mtime == inode.Mtime, m.Path + " mtime");
                if (m.Type == "file")
                {
                    Assert.True(m.Size == inode.Size, m.Path + " size");
                    Assert.True(m.Sha256 == Ext4TestHelpers.Sha256(reader, inode), m.Path + " content");
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
                Assert.True(m.Xattrs.Count == xattrs.Count, m.Path + " xattr count");
                foreach (var kv in m.Xattrs)
                {
                    Assert.True(xattrs.ContainsKey(kv.Key) && Ext4TestHelpers.SameBytes(kv.Value, xattrs[kv.Key]), m.Path + " xattr " + kv.Key);
                }

                if (m.HardLinkKey != null)
                {
                    if (hardLinks.TryGetValue(m.HardLinkKey, out var ino))
                    {
                        Assert.True(ino == inode.Number, m.Path + " hard link group");
                    }
                    else
                    {
                        hardLinks[m.HardLinkKey] = inode.Number;
                    }
                }
            }

            var expected = new HashSet<string>(manifest.Select(x => x.Path)) { "lost+found" };
            var extra = nodes.Keys.Where(p => !expected.Contains(p)).ToList();
            Assert.True(extra.Count == 0, "unexpected: " + string.Join(", ", extra));
        }
    }
}
