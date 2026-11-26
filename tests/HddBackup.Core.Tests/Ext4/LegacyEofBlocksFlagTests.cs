using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    /// <summary>
    /// 0x200000 in an inode is EA_INODE_FL only on a file system with the ea_inode feature; older kernels left it set
    /// (as EOFBLOCKS_FL) on ordinary files, and a shrink of such a card must not fail on it.
    /// </summary>
    public sealed class LegacyEofBlocksFlagTests : IDisposable
    {
        private const uint LegacyFlag = 0x200000;
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private string SetFlagOnAFile(string fixture)
        {
            string image = Path.Combine(_temp.New(), fixture + ".img");
            File.Copy(FixtureImages.RawPath(fixture), image);
            byte[] raw;
            long offset;
            using (var device = FileBlockDevice.OpenRead(image))
            using (var window = new PartitionView(device, 0, device.Size))
            using (var reader = new Ext4Reader(window))
            {
                // a regular file inside the first folder of the root (the fixture has no regular file in the root itself)
                var folder = reader.ReadDirectory(reader.ReadInode(Ext4CopyPlan.RootInode))
                    .Where(e => !e.IsDot && !e.IsDotDot && e.NameText != "lost+found" && reader.ReadInode(e.Inode).IsDirectory)
                    .Select(e => reader.ReadInode(e.Inode)).First(d => reader.ReadDirectory(d).Any(c => !c.IsDot && !c.IsDotDot && reader.ReadInode(c.Inode).IsRegular));
                var entry = reader.ReadDirectory(folder).First(c => !c.IsDot && !c.IsDotDot && reader.ReadInode(c.Inode).IsRegular);
                var node = reader.ReadInode(entry.Inode);
                node.Flags |= LegacyFlag;
                node.UpdateChecksum(reader.Superblock);
                raw = node.Raw;
                uint index = entry.Inode - 1;
                int group = (int)(index / reader.Superblock.InodesPerGroup);
                offset = (reader.Groups[group].InodeTable * reader.BlockSize) + ((long)(index % reader.Superblock.InodesPerGroup) * reader.Superblock.InodeSize);
            }

            using (var stream = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = offset;
                stream.Write(raw, 0, raw.Length);
            }

            return image;
        }

        [Fact]
        public void AFileWithTheOldEofBlocksFlagIsCopiedByThePlan()
        {
            string image = SetFlagOnAFile("browse-names");
            using (var device = FileBlockDevice.OpenRead(image))
            using (var window = new PartitionView(device, 0, device.Size))
            using (var reader = new Ext4Reader(window))
            {
                Assert.Equal(0u, reader.Superblock.FeatureIncompat & Ext4Features.IncompatEaInode);
                var plan = Ext4CopyPlan.Build(reader, CancellationToken.None);
                Assert.True(plan.Nodes.Count > 2);
            }
        }
    }
}
