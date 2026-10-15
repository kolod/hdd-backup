using System;
using System.Collections.Generic;
using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Read-only ext4 access over a partition. Verifies metadata checksums (metadata_csum) and
    /// throws <see cref="Ext4FormatException"/> on any structural problem.
    /// </summary>
    internal sealed class Ext4Reader : IDisposable
    {
        private const ushort ExtentMagic = 0xF30A;
        private const uint XattrMagic = 0xEA020000;
        private const int MaxExtentDepth = 5;

        private readonly IBlockDevice _device;
        private readonly bool _ownsDevice;
        private readonly Ext4GroupDescriptor[] _groups;
        private readonly Dictionary<int, byte[]> _inodeBitmaps = new Dictionary<int, byte[]>();

        public Ext4Reader(IBlockDevice partition, bool ownsDevice = false)
        {
            _device = partition;
            _ownsDevice = ownsDevice;
            var raw = new byte[Ext4Superblock.Size];
            ReadBytes(Ext4Superblock.ByteOffset, raw, 0, raw.Length);
            Superblock = new Ext4Superblock(raw);
            if (Superblock.Magic != Ext4Superblock.MagicValue)
            {
                throw new Ext4FormatException("Not an ext2/3/4 file system (bad magic).");
            }

            if (Superblock.LogBlockSize > 2)
            {
                throw new Ext4FormatException("Unsupported block size " + (1024L << Superblock.LogBlockSize) + ".");
            }

            if (Superblock.HasMetadataCsum && Superblock.Checksum != Superblock.ComputeChecksum())
            {
                throw new Ext4FormatException("Superblock checksum mismatch.");
            }

            BlockSize = Superblock.BlockSize;
            if (Superblock.BlocksPerGroup == 0 || Superblock.InodesPerGroup == 0
                || (long)Superblock.BlocksCount * BlockSize > partition.Size + BlockSize)
            {
                throw new Ext4FormatException("Superblock geometry does not match the partition.");
            }

            GroupCount = (int)((Superblock.BlocksCount - Superblock.FirstDataBlock + Superblock.BlocksPerGroup - 1) / Superblock.BlocksPerGroup);
            _groups = ReadGroupDescriptors();
        }

        public Ext4Superblock Superblock { get; }

        public int BlockSize { get; }

        public int GroupCount { get; }

        public IReadOnlyList<Ext4GroupDescriptor> Groups => _groups;

        public long DeviceSize => _device.Size;

        public void Dispose()
        {
            if (_ownsDevice)
            {
                _device.Dispose();
            }
        }

        public void ReadBlocks(long block, int count, byte[] buffer, int offset)
        {
            ReadBytes(block * BlockSize, buffer, offset, count * BlockSize);
        }

        public byte[] ReadBlock(long block)
        {
            var data = new byte[BlockSize];
            ReadBlocks(block, 1, data, 0);
            return data;
        }

        public Ext4Inode ReadInode(uint number)
        {
            if (number == 0 || number > Superblock.InodesCount)
            {
                throw new Ext4FormatException("Inode number " + number + " out of range.");
            }

            uint index = number - 1;
            int group = (int)(index / Superblock.InodesPerGroup);
            long offsetInTable = (long)(index % Superblock.InodesPerGroup) * Superblock.InodeSize;
            long byteOffset = (_groups[group].InodeTable * BlockSize) + offsetInTable;
            var raw = new byte[Superblock.InodeSize];
            ReadBytes(byteOffset, raw, 0, raw.Length);
            var inode = new Ext4Inode(number, raw);
            if (Superblock.HasMetadataCsum && !inode.IsAllZero && inode.StoredChecksum != inode.ComputeChecksum(Superblock))
            {
                throw new Ext4FormatException("Inode " + number + " checksum mismatch.");
            }

            return inode;
        }

        public bool IsInodeInUse(uint number)
        {
            uint index = number - 1;
            int group = (int)(index / Superblock.InodesPerGroup);
            if ((_groups[group].Flags & Ext4GroupDescriptor.FlagInodeUninit) != 0 && (Superblock.HasMetadataCsum || Superblock.HasGdtCsum))
            {
                return false;
            }

            if (!_inodeBitmaps.TryGetValue(group, out var bitmap))
            {
                bitmap = ReadBlock(_groups[group].InodeBitmap);
                _inodeBitmaps[group] = bitmap;
            }

            uint bit = index % Superblock.InodesPerGroup;
            return (bitmap[bit >> 3] & (1 << (int)(bit & 7))) != 0;
        }

        /// <summary>Physical mapping of a file's data, in logical order; holes are omitted.</summary>
        public List<Ext4Extent> MapFile(Ext4Inode inode)
        {
            var result = new List<Ext4Extent>();
            if ((inode.Flags & Ext4Inode.FlagInlineData) != 0)
            {
                throw new Ext4FormatException("Inode " + inode.Number + " uses inline data.");
            }

            if ((inode.Flags & Ext4Inode.FlagExtents) != 0)
            {
                WalkExtentNode(inode, inode.GetBlockArray(), 0, MaxExtentDepth + 1, result, null);
            }
            else
            {
                MapBlockMap(inode, result);
            }

            return result;
        }

        /// <summary>Blocks used by the extent tree itself (index and leaf blocks, not data).</summary>
        public List<long> ExtentTreeBlocks(Ext4Inode inode)
        {
            var blocks = new List<long>();
            if ((inode.Flags & Ext4Inode.FlagExtents) != 0)
            {
                WalkExtentNode(inode, inode.GetBlockArray(), 0, MaxExtentDepth + 1, new List<Ext4Extent>(), blocks);
            }
            else if (!IsFastSymlinkOrSpecial(inode))
            {
                CollectBlockMapMeta(inode, blocks);
            }

            return blocks;
        }

        /// <summary>Reads a byte range of a file into the buffer (holes and unwritten extents read as zeros).</summary>
        public void ReadFile(Ext4Inode inode, List<Ext4Extent> map, long offset, byte[] buffer, int index, int count)
        {
            Array.Clear(buffer, index, count);
            long end = offset + count;
            foreach (var e in map)
            {
                long eStart = e.Logical * BlockSize;
                long eEnd = eStart + ((long)e.Count * BlockSize);
                if (eEnd <= offset || eStart >= end || e.Uninitialized)
                {
                    continue;
                }

                long from = Math.Max(offset, eStart);
                long to = Math.Min(end, eEnd);
                ReadBytes((e.Physical * BlockSize) + (from - eStart), buffer, index + (int)(from - offset), (int)(to - from));
            }
        }

        public List<Ext4DirEntry> ReadDirectory(Ext4Inode dir)
        {
            if (!dir.IsDirectory)
            {
                throw new Ext4FormatException("Inode " + dir.Number + " is not a directory.");
            }

            var entries = new List<Ext4DirEntry>();
            var map = MapFile(dir);
            long blocks = (dir.Size + BlockSize - 1) / BlockSize;
            var block = new byte[BlockSize];
            foreach (var e in map)
            {
                for (int i = 0; i < e.Count && e.Logical + i < blocks; i++)
                {
                    ReadBlocks(e.Physical + i, 1, block, 0);
                    ParseDirectoryBlock(dir, block, entries);
                }
            }

            return entries;
        }

        public string ReadSymlink(Ext4Inode inode)
        {
            var bytes = ReadSymlinkBytes(inode);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        public byte[] ReadSymlinkBytes(Ext4Inode inode)
        {
            long size = inode.Size;
            if (IsFastSymlinkOrSpecial(inode))
            {
                var fast = new byte[size];
                Buffer.BlockCopy(inode.Raw, Ext4Inode.BlockArrayOffset, fast, 0, (int)size);
                return fast;
            }

            var data = new byte[size];
            ReadFile(inode, MapFile(inode), 0, data, 0, (int)size);
            return data;
        }

        /// <summary>A fast symlink keeps its target in i_block; devices/FIFOs/sockets keep device numbers there.</summary>
        public bool IsFastSymlinkOrSpecial(Ext4Inode inode)
        {
            if (inode.IsSymlink)
            {
                long xattrBlocks = inode.FileAcl != 0 ? BlockSize / 512 : 0;
                return (inode.Flags & Ext4Inode.FlagExtents) == 0 && inode.BlocksRaw - xattrBlocks == 0 && inode.Size < Ext4Inode.BlockArraySize;
            }

            return !inode.IsRegular && !inode.IsDirectory;
        }

        public List<Ext4Xattr> ReadXattrs(Ext4Inode inode)
        {
            var result = new List<Ext4Xattr>();
            int inodeSize = Superblock.InodeSize;
            int ibody = 128 + inode.ExtraIsize;
            if (inodeSize > 128 && ibody + 4 <= inodeSize && BinaryHelpers.U32(inode.Raw, ibody) == XattrMagic)
            {
                ParseXattrEntries(inode.Raw, ibody + 4, inodeSize, ibody + 4, true, result);
            }

            if (inode.FileAcl != 0)
            {
                var block = ReadBlock(inode.FileAcl);
                if (BinaryHelpers.U32(block, 0) != XattrMagic)
                {
                    throw new Ext4FormatException("Inode " + inode.Number + " xattr block has a bad magic.");
                }

                if (Superblock.HasMetadataCsum && BinaryHelpers.U32(block, 0x10) != Ext4Csum.XattrBlock(Superblock, inode.FileAcl, block))
                {
                    throw new Ext4FormatException("Xattr block " + inode.FileAcl + " checksum mismatch.");
                }

                ParseXattrEntries(block, 32, BlockSize, 0, false, result);
            }

            return result;
        }

        /// <summary>Precondition checks (research R-3). Empty list ⇒ the file system may be rebuilt.</summary>
        public List<string> Validate()
        {
            var problems = new List<string>();
            var sb = Superblock;
            foreach (var feature in Ext4Features.Unsupported(sb))
            {
                problems.Add("feature:" + feature);
            }

            if (sb.NeedsRecovery)
            {
                problems.Add("needs_recovery");
            }

            if ((sb.State & Ext4Superblock.StateValid) == 0)
            {
                problems.Add("not_clean");
            }

            if ((sb.State & Ext4Superblock.StateErrors) != 0 || sb.ErrorCount != 0)
            {
                problems.Add("errors");
            }

            if (sb.LastOrphan != 0 || (sb.State & Ext4Superblock.StateOrphans) != 0)
            {
                problems.Add("orphans");
            }

            if (sb.InodeSize != 128 && sb.InodeSize != 256)
            {
                problems.Add("inode_size:" + sb.InodeSize);
            }

            if (sb.LogClusterSize != sb.LogBlockSize)
            {
                problems.Add("feature:bigalloc");
            }

            return problems;
        }

        private Ext4GroupDescriptor[] ReadGroupDescriptors()
        {
            int descSize = Superblock.DescriptorSize;
            long gdtBlock = Superblock.FirstDataBlock + 1;
            int perBlock = BlockSize / descSize;
            int blocks = (GroupCount + perBlock - 1) / perBlock;
            var data = new byte[blocks * BlockSize];
            ReadBlocks(gdtBlock, blocks, data, 0);
            var groups = new Ext4GroupDescriptor[GroupCount];
            for (int g = 0; g < GroupCount; g++)
            {
                var raw = new byte[descSize];
                Buffer.BlockCopy(data, g * descSize, raw, 0, descSize);
                groups[g] = new Ext4GroupDescriptor(raw);
                if ((Superblock.HasMetadataCsum || Superblock.HasGdtCsum) && groups[g].Checksum != groups[g].ComputeChecksum(Superblock, (uint)g))
                {
                    throw new Ext4FormatException("Group descriptor " + g + " checksum mismatch.");
                }
            }

            return groups;
        }

        private void WalkExtentNode(Ext4Inode inode, byte[] node, int nodeOffset, int depthBudget, List<Ext4Extent> extents, List<long> treeBlocks)
        {
            if (BinaryHelpers.U16(node, nodeOffset) != ExtentMagic)
            {
                throw new Ext4FormatException("Inode " + inode.Number + " has a bad extent header.");
            }

            int entries = BinaryHelpers.U16(node, nodeOffset + 2);
            int max = BinaryHelpers.U16(node, nodeOffset + 4);
            int depth = BinaryHelpers.U16(node, nodeOffset + 6);
            if (entries > max || depth >= depthBudget)
            {
                throw new Ext4FormatException("Inode " + inode.Number + " has an invalid extent tree.");
            }

            for (int i = 0; i < entries; i++)
            {
                int o = nodeOffset + 12 + (i * 12);
                if (depth == 0)
                {
                    uint logical = BinaryHelpers.U32(node, o);
                    int len = BinaryHelpers.U16(node, o + 4);
                    long physical = ((long)BinaryHelpers.U16(node, o + 6) << 32) | BinaryHelpers.U32(node, o + 8);
                    bool uninit = len > 32768;
                    if (uninit)
                    {
                        len -= 32768;
                    }

                    if (len == 0 || physical + len > Superblock.BlocksCount)
                    {
                        throw new Ext4FormatException("Inode " + inode.Number + " has an extent outside the file system.");
                    }

                    extents.Add(new Ext4Extent(logical, physical, len, uninit));
                }
                else
                {
                    long child = ((long)BinaryHelpers.U16(node, o + 8) << 32) | BinaryHelpers.U32(node, o + 4);
                    if (child == 0 || child >= Superblock.BlocksCount)
                    {
                        throw new Ext4FormatException("Inode " + inode.Number + " has an extent index outside the file system.");
                    }

                    var block = ReadBlock(child);
                    if (Superblock.HasMetadataCsum)
                    {
                        int max2 = BinaryHelpers.U16(block, 4);
                        int tail = 12 + (12 * max2);
                        if (tail + 4 <= BlockSize && BinaryHelpers.U32(block, tail) != Crc32c.Append(inode.ChecksumSeed(Superblock), block, 0, tail))
                        {
                            throw new Ext4FormatException("Inode " + inode.Number + " extent block " + child + " checksum mismatch.");
                        }
                    }

                    treeBlocks?.Add(child);
                    WalkExtentNode(inode, block, 0, depth, extents, treeBlocks);
                }
            }
        }

        private void MapBlockMap(Ext4Inode inode, List<Ext4Extent> result)
        {
            if (IsFastSymlinkOrSpecial(inode))
            {
                return;
            }

            var array = inode.GetBlockArray();
            long logical = 0;
            for (int i = 0; i < 12; i++, logical++)
            {
                AddBlock(result, logical, BinaryHelpers.U32(array, i * 4));
            }

            long perBlock = BlockSize / 4;
            for (int level = 1; level <= 3; level++)
            {
                uint root = BinaryHelpers.U32(array, (11 + level) * 4);
                long span = 1;
                for (int k = 0; k < level; k++)
                {
                    span *= perBlock;
                }

                if (root != 0)
                {
                    WalkIndirect(root, level, logical, result, null);
                }

                logical += span;
            }
        }

        private void CollectBlockMapMeta(Ext4Inode inode, List<long> blocks)
        {
            var array = inode.GetBlockArray();
            for (int level = 1; level <= 3; level++)
            {
                uint root = BinaryHelpers.U32(array, (11 + level) * 4);
                if (root != 0)
                {
                    WalkIndirect(root, level, 0, new List<Ext4Extent>(), blocks);
                }
            }
        }

        private void WalkIndirect(long block, int level, long logicalStart, List<Ext4Extent> result, List<long> metaBlocks)
        {
            if (block >= Superblock.BlocksCount)
            {
                throw new Ext4FormatException("Indirect block outside the file system.");
            }

            metaBlocks?.Add(block);
            var data = ReadBlock(block);
            long perBlock = BlockSize / 4;
            long span = 1;
            for (int k = 1; k < level; k++)
            {
                span *= perBlock;
            }

            for (int i = 0; i < perBlock; i++)
            {
                uint child = BinaryHelpers.U32(data, i * 4);
                if (child == 0)
                {
                    continue;
                }

                if (level == 1)
                {
                    AddBlock(result, logicalStart + i, child);
                }
                else
                {
                    WalkIndirect(child, level - 1, logicalStart + (i * span), result, metaBlocks);
                }
            }
        }

        private void AddBlock(List<Ext4Extent> result, long logical, uint physical)
        {
            if (physical == 0)
            {
                return;
            }

            if (physical >= Superblock.BlocksCount)
            {
                throw new Ext4FormatException("Block map entry outside the file system.");
            }

            if (result.Count > 0)
            {
                var last = result[result.Count - 1];
                if (last.Logical + last.Count == logical && last.Physical + last.Count == physical && last.Count < 32768)
                {
                    result[result.Count - 1] = new Ext4Extent(last.Logical, last.Physical, last.Count + 1, false);
                    return;
                }
            }

            result.Add(new Ext4Extent(logical, physical, 1, false));
        }

        private void ParseDirectoryBlock(Ext4Inode dir, byte[] block, List<Ext4DirEntry> entries)
        {
            int limit = BlockSize;
            bool hasTail = BinaryHelpers.U16(block, BlockSize - 8) == 12 && block[BlockSize - 5] == 0xDE && BinaryHelpers.U32(block, BlockSize - 12) == 0;
            if (hasTail)
            {
                limit = BlockSize - 12;
                if (Superblock.HasMetadataCsum && BinaryHelpers.U32(block, BlockSize - 4) != Crc32c.Append(dir.ChecksumSeed(Superblock), block, 0, BlockSize - 12))
                {
                    throw new Ext4FormatException("Directory " + dir.Number + " block checksum mismatch.");
                }
            }

            int offset = 0;
            while (offset + 8 <= limit)
            {
                uint inode = BinaryHelpers.U32(block, offset);
                int recLen = BinaryHelpers.U16(block, offset + 4);
                if (recLen == 0 && BlockSize == 65536)
                {
                    recLen = 65536;
                }

                int nameLen = block[offset + 6];
                if (recLen < 8 || recLen % 4 != 0 || offset + recLen > BlockSize || 8 + nameLen > recLen)
                {
                    throw new Ext4FormatException("Directory " + dir.Number + " has a corrupt entry.");
                }

                if (inode != 0 && nameLen > 0)
                {
                    var name = new byte[nameLen];
                    Buffer.BlockCopy(block, offset + 8, name, 0, nameLen);
                    entries.Add(new Ext4DirEntry(inode, block[offset + 7], name));
                }

                offset += recLen;
            }
        }

        private static void ParseXattrEntries(byte[] data, int start, int end, int valueBase, bool inInode, List<Ext4Xattr> result)
        {
            int o = start;
            while (o + 4 <= end && BinaryHelpers.U32(data, o) != 0)
            {
                if (o + 16 > end)
                {
                    throw new Ext4FormatException("Corrupt xattr entry.");
                }

                int nameLen = data[o];
                byte index = data[o + 1];
                int valueOffset = BinaryHelpers.U16(data, o + 2);
                uint valueInode = BinaryHelpers.U32(data, o + 4);
                int valueSize = (int)BinaryHelpers.U32(data, o + 8);
                if (valueInode != 0)
                {
                    throw new Ext4FormatException("Xattr values stored in inodes (ea_inode) are not supported.");
                }

                if (o + 16 + nameLen > end || valueBase + valueOffset + valueSize > data.Length)
                {
                    throw new Ext4FormatException("Corrupt xattr entry.");
                }

                var name = new byte[nameLen];
                Buffer.BlockCopy(data, o + 16, name, 0, nameLen);
                var value = new byte[valueSize];
                Buffer.BlockCopy(data, valueBase + valueOffset, value, 0, valueSize);
                result.Add(new Ext4Xattr(index, name, value, inInode));
                o += (16 + nameLen + 3) & ~3;
            }
        }

        private void ReadBytes(long offset, byte[] buffer, int index, int count)
        {
            int sector = _device.SectorSize;
            long alignedStart = offset - (offset % sector);
            long alignedEnd = (offset + count + sector - 1) / sector * sector;
            if (alignedStart == offset && alignedEnd == offset + count)
            {
                _device.Read(offset, buffer, index, count);
                return;
            }

            var temp = new byte[alignedEnd - alignedStart];
            _device.Read(alignedStart, temp, 0, temp.Length);
            Buffer.BlockCopy(temp, (int)(offset - alignedStart), buffer, index, count);
        }
    }

    /// <summary>Checksum helpers shared by reader, writer and checker.</summary>
    internal static class Ext4Csum
    {
        public static uint XattrBlock(Ext4Superblock sb, long blockNumber, byte[] block)
        {
            var copy = (byte[])block.Clone();
            BinaryHelpers.PutU32(copy, 0x10, 0);
            return Crc32c.Append(Crc32c.AppendU64(sb.MetadataSeed, (ulong)blockNumber), copy, 0, copy.Length);
        }

        public static uint Bitmap(Ext4Superblock sb, byte[] bitmap, int bytes) => Crc32c.Append(sb.MetadataSeed, bitmap, 0, bytes);
    }
}
