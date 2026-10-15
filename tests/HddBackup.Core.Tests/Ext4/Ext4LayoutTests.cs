using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    /// <summary>Our geometry must equal what mke2fs produced (layout-reference.txt from dumpe2fs).</summary>
    public class Ext4LayoutTests
    {
        private sealed class Reference
        {
            public long Blocks;
            public long Inodes;
            public long Ipg;
            public int ReservedGdt;
            public readonly Dictionary<int, (long Sb, long Bb, long Ib, long It)> Groups = new Dictionary<int, (long, long, long, long)>();
        }

        private static Dictionary<string, Reference> Load()
        {
            var result = new Dictionary<string, Reference>();
            Reference current = null;
            int group = -1;
            long sb = -1, bb = 0, ib = 0;
            foreach (var line in File.ReadAllLines(Path.Combine(FixtureImages.Directory, "layout-reference.txt")))
            {
                var m = Regex.Match(line, @"^=== size=(\d+)MiB block=(\d+)");
                if (m.Success)
                {
                    current = new Reference();
                    result[m.Groups[1].Value + ":" + m.Groups[2].Value] = current;
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                if ((m = Regex.Match(line, @"^Block count:\s+(\d+)")).Success) current.Blocks = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"^Inode count:\s+(\d+)")).Success) current.Inodes = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"^Inodes per group:\s+(\d+)")).Success) current.Ipg = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"^Reserved GDT blocks:\s+(\d+)")).Success) current.ReservedGdt = int.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"^Group (\d+):")).Success) { group = int.Parse(m.Groups[1].Value); sb = -1; }
                if ((m = Regex.Match(line, @"superblock at (\d+)")).Success) sb = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"Block bitmap at (\d+)")).Success) bb = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"Inode bitmap at (\d+)")).Success) ib = long.Parse(m.Groups[1].Value);
                if ((m = Regex.Match(line, @"Inode table at (\d+)")).Success)
                {
                    current.Groups[group] = (sb, bb, ib, long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }

            return result;
        }

        [Theory]
        [InlineData(64, 4096)]
        [InlineData(448, 4096)]
        [InlineData(4096, 4096)]
        [InlineData(16384, 4096)]
        [InlineData(128, 1024)]
        [InlineData(448, 1024)]
        public void Geometry_MatchesMke2fs(int mib, int blockSize)
        {
            var reference = Load()[mib + ":" + blockSize];
            var layout = Ext4Layout.Create((long)mib * 1024 * 1024 / blockSize, blockSize, 256, 16384, 0, 64, true, 4);

            Assert.Equal(reference.Blocks, layout.BlocksCount);
            Assert.Equal(reference.Ipg, layout.InodesPerGroup);
            Assert.Equal(reference.Inodes, layout.InodesCount);
            Assert.Equal(reference.ReservedGdt, layout.ReservedGdtBlocks);
            Assert.Equal(reference.Groups.Count, layout.GroupCount);
            foreach (var kv in reference.Groups)
            {
                int g = kv.Key;
                Assert.Equal(kv.Value.Sb >= 0, layout.HasSuperblock(g));
                if (kv.Value.Sb >= 0)
                {
                    Assert.Equal(kv.Value.Sb, layout.GroupStart(g));
                }

                Assert.True(kv.Value.Bb == layout.BlockBitmap[g], $"group {g} block bitmap {layout.BlockBitmap[g]} vs {kv.Value.Bb}");
                Assert.True(kv.Value.Ib == layout.InodeBitmap[g], $"group {g} inode bitmap {layout.InodeBitmap[g]} vs {kv.Value.Ib}");
                Assert.True(kv.Value.It == layout.InodeTable[g], $"group {g} inode table {layout.InodeTable[g]} vs {kv.Value.It}");
            }
        }

        [Fact]
        public void TinyLastGroup_IsDropped()
        {
            // 4 KiB blocks: 32768 per group; 10 blocks into a second group is too small to keep.
            var layout = Ext4Layout.Create(32768 + 10, 4096, 256, 16384, 0, 64, true, 4);
            Assert.Equal(32768, layout.BlocksCount);
            Assert.Equal(1, layout.GroupCount);
        }

        [Fact]
        public void MinimumInodes_AreHonoured()
        {
            var layout = Ext4Layout.Create(65536, 4096, 256, 16384, 50000, 64, true, 4);
            Assert.True(layout.InodesCount >= 50000);
        }
    }
}
