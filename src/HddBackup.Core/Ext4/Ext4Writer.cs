using System;
using System.Collections.Generic;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Builds a new ext4 file system on <paramref name="target"/> (a partition-sized device that
    /// reads as zeros) with the given layout, copying every reachable inode of the source.
    /// Features, UUID, label, hash seed and checksum seed come from the source superblock.
    /// </summary>
    internal sealed class Ext4Writer
    {
        private const int MaxExtentLength = 32768;
        private const int MaxUninitExtentLength = 32767;
        private const ushort ExtentMagic = 0xF30A;
        private const int CopyChunkBlocks = 256;

        private readonly IBlockDevice _target;
        private readonly Ext4Layout _layout;
        private readonly Ext4Reader _source;
        private readonly Ext4CopyPlan _plan;
        private readonly Ext4Superblock _sb;
        private readonly int _bs;
        private readonly bool _csum;
        private readonly ulong[] _usedBlocks;
        private readonly Dictionary<long, long> _xattrBlockMap = new Dictionary<long, long>();
        private readonly HashSet<uint> _usedInodes = new HashSet<uint>();
        private readonly int[] _dirsPerGroup;
        private long _cursor;

        // Inode table buffering: one group at a time (inodes are written in increasing order).
        private int _itableGroup = -1;
        private byte[] _itableBuffer;

        public Ext4Writer(IBlockDevice target, Ext4Layout layout, Ext4Reader source, Ext4CopyPlan plan)
        {
            if (target.Size < layout.BlocksCount * layout.BlockSize)
            {
                throw new ArgumentException("Target is smaller than the layout.");
            }

            if (layout.BlockSize != source.BlockSize || layout.InodeSize != source.Superblock.InodeSize)
            {
                throw new ArgumentException("Layout block or inode size differs from the source.");
            }

            if (plan.HighestInode > layout.InodesCount)
            {
                throw new ArgumentException("Layout has too few inodes.");
            }

            _target = target;
            _layout = layout;
            _source = source;
            _plan = plan;
            _bs = layout.BlockSize;
            _sb = source.Superblock.Clone();
            _csum = _sb.HasMetadataCsum;
            _usedBlocks = new ulong[(layout.BlocksCount + 63) / 64];
            _dirsPerGroup = new int[layout.GroupCount];
            // Block 0 of a 4 KiB file system is the primary superblock block, already in the metadata.
            // With 1 KiB blocks, block 0 lies before the file system (first data block 1).
            foreach (var (start, count) in layout.MetadataExtents)
            {
                MarkUsed(start, count);
            }

            _cursor = layout.FirstDataBlock;
        }

        /// <summary>Bytes of file data copied so far.</summary>
        public long BytesCopied { get; private set; }

        public void Build(long journalBlocks, Action<long> progress, CancellationToken cancel)
        {
            PrepareSuperblock();

            uint[] journalBlockArray = null;
            if (_sb.HasJournal)
            {
                journalBlockArray = WriteJournal(journalBlocks);
            }

            if (_sb.HasResizeInode)
            {
                WriteResizeInode();
            }

            CopyReservedInodes();

            foreach (var node in _plan.Nodes)
            {
                cancel.ThrowIfCancellationRequested();
                WriteNode(node);
                progress?.Invoke(BytesCopied);
            }

            FlushInodeTable();
            FinishMetadata(journalBlockArray);
            _target.Flush();
        }

        // ---------------------------------------------------------------- superblock

        private void PrepareSuperblock()
        {
            var src = _source.Superblock;
            _sb.InodesCount = (uint)_layout.InodesCount;
            _sb.BlocksCount = _layout.BlocksCount;
            _sb.ReservedBlocksCount = src.BlocksCount == 0 ? 0 : (long)Math.Round((double)_layout.BlocksCount * src.ReservedBlocksCount / src.BlocksCount);
            _sb.FirstDataBlock = _layout.FirstDataBlock;
            _sb.BlocksPerGroup = _layout.BlocksPerGroup;
            _sb.ClustersPerGroup = _layout.BlocksPerGroup;
            _sb.InodesPerGroup = _layout.InodesPerGroup;
            _sb.ReservedGdtBlocks = (ushort)_layout.ReservedGdtBlocks;
            _sb.LastOrphan = 0;
            _sb.State = Ext4Superblock.StateValid;
            _sb.FeatureIncompat &= ~Ext4Features.IncompatRecover; // the new journal is empty
            _sb.FirstMetaBg = 0;

            // Error history, overhead hint, snapshot and backup-group fields do not carry over.
            Array.Clear(_sb.Raw, 0x194, 0x200 - 0x194);
            Array.Clear(_sb.Raw, 0x180, 0x14);           // snapshot fields
            BinaryHelpers.PutU32(_sb.Raw, 0x248, 0);      // s_overhead_clusters
            Array.Clear(_sb.Raw, 0x24C, 8);              // s_backup_bgs (sparse_super2 only)
        }

        // ---------------------------------------------------------------- journal and resize inode

        private uint[] WriteJournal(long journalBlocks)
        {
            var sourceJournal = _source.ReadInode(Ext4CopyPlan.JournalInode);
            var sourceMap = _source.MapFile(sourceJournal);
            var jsb = _source.ReadBlock(sourceMap[0].Physical);
            if (BinaryHelpers.U32BE(jsb, 0) != 0xC03B3998)
            {
                throw new Ext4FormatException("Source journal superblock has a bad magic.");
            }

            var extents = AllocateExtents(0, journalBlocks, false);
            BinaryHelpers.PutU32BE(jsb, 0x10, (uint)journalBlocks); // s_maxlen
            BinaryHelpers.PutU32BE(jsb, 0x1C, 0);                    // s_start: empty journal
            BinaryHelpers.PutU32BE(jsb, 0x20, 0);                    // s_errno
            uint incompat = BinaryHelpers.U32BE(jsb, 0x28);
            if ((incompat & (0x8 | 0x10)) != 0)
            {
                BinaryHelpers.PutU32BE(jsb, 0xFC, 0);
                BinaryHelpers.PutU32BE(jsb, 0xFC, Crc32c.Append(~0u, jsb, 0, 1024));
            }

            WriteBlock(extents[0].Physical, jsb);

            var inode = sourceJournal.CloneAs(Ext4CopyPlan.JournalInode);
            inode.Size = journalBlocks * _bs;
            inode.LinksCount = 1;
            inode.Dtime = 0;
            inode.FileAcl = 0;
            inode.Flags = (inode.Flags | Ext4Inode.FlagExtents) & ~(Ext4Inode.FlagHugeFile | Ext4Inode.FlagInlineData);
            long treeBlocks = WriteExtentTree(inode, extents);
            inode.BlocksRaw = (journalBlocks + treeBlocks) * (_bs / 512);
            StoreInode(inode);

            var words = new uint[17];
            for (int i = 0; i < 15; i++)
            {
                words[i] = BinaryHelpers.U32(inode.Raw, Ext4Inode.BlockArrayOffset + (i * 4));
            }

            words[15] = (uint)(inode.Size >> 32);
            words[16] = (uint)inode.Size;
            return words;
        }

        private void WriteResizeInode()
        {
            int apb = _bs / 4;
            int rsv = _layout.ReservedGdtBlocks;
            var backups = new List<int>(_layout.BackupGroups());
            long dind = AllocateBlock();
            var dindData = new byte[_bs];
            for (int r = 0; r < rsv; r++)
            {
                long primary = _layout.ReservedGdtBlock(0, r);
                BinaryHelpers.PutU32(dindData, ((_layout.GdtBlocks + r) % apb) * 4, (uint)primary);
                var list = new byte[_bs];
                int k = 0;
                foreach (int g in backups)
                {
                    if (k >= apb)
                    {
                        break;
                    }

                    BinaryHelpers.PutU32(list, k++ * 4, (uint)(primary + ((long)g * _layout.BlocksPerGroup)));
                }

                WriteBlock(primary, list);
            }

            WriteBlock(dind, dindData);

            Ext4Inode inode;
            if (_source.Superblock.HasResizeInode)
            {
                inode = _source.ReadInode(Ext4CopyPlan.ResizeInode).CloneAs(Ext4CopyPlan.ResizeInode);
            }
            else
            {
                inode = new Ext4Inode(Ext4CopyPlan.ResizeInode, new byte[_sb.InodeSize]);
                inode.Mode = Ext4Inode.TypeFile | 0x180;
            }

            inode.SetBlockArray(new byte[Ext4Inode.BlockArraySize]);
            BinaryHelpers.PutU32(inode.Raw, Ext4Inode.BlockArrayOffset + (13 * 4), (uint)dind);
            inode.Size = ((long)apb * apb + apb + 12) * _bs;
            inode.LinksCount = 1;
            inode.Flags = 0;
            inode.FileAcl = 0;
            inode.BlocksRaw = (1 + ((long)rsv * (1 + backups.Count))) * (_bs / 512);
            StoreInode(inode);
        }

        private void CopyReservedInodes()
        {
            for (uint ino = 1; ino < _sb.FirstInode; ino++)
            {
                if (ino == Ext4CopyPlan.RootInode || ino == Ext4CopyPlan.JournalInode || (ino == Ext4CopyPlan.ResizeInode && _sb.HasResizeInode))
                {
                    continue;
                }

                var inode = _source.ReadInode(ino).CloneAs(ino);
                if (ino == 1)
                {
                    // Bad blocks inode: the list does not apply to the new layout.
                    inode.SetBlockArray(new byte[Ext4Inode.BlockArraySize]);
                    inode.BlocksRaw = 0;
                    inode.Size = 0;
                }

                if (!IsZero(inode.Raw, 0, 0x7C) || !IsZero(inode.Raw, 0x7E, inode.Raw.Length - 0x7E))
                {
                    StoreInode(inode);
                }
                else
                {
                    StoreInode(new Ext4Inode(ino, new byte[_sb.InodeSize]), forceChecksum: _csum);
                }
            }
        }

        // ---------------------------------------------------------------- inodes

        private void WriteNode(Ext4CopyPlan.Node node)
        {
            var inode = node.Inode.CloneAs(node.NewInode);
            inode.Dtime = 0;
            long extraBlocks = 0;

            if (inode.FileAcl != 0)
            {
                inode.FileAcl = CopyXattrBlock(node.Inode.FileAcl);
                extraBlocks++;
            }

            if (inode.IsDirectory)
            {
                var blocks = BuildDirectoryBlocks(node, inode);
                var extents = AllocateExtents(0, blocks.Count, false);
                int i = 0;
                foreach (var e in extents)
                {
                    for (int k = 0; k < e.Count; k++)
                    {
                        WriteBlock(e.Physical + k, blocks[i++]);
                    }
                }

                inode.Flags = (inode.Flags | Ext4Inode.FlagExtents) & ~(Ext4Inode.FlagIndex | Ext4Inode.FlagHugeFile | Ext4Inode.FlagInlineData);
                long tree = WriteExtentTree(inode, extents);
                inode.Size = (long)blocks.Count * _bs;
                inode.BlocksRaw = (blocks.Count + tree + extraBlocks) * (_bs / 512);
                int links = 2 + node.SubdirCount;
                inode.LinksCount = (ushort)(links > 65000 && _sb.HasDirNlink ? 1 : links);
                _dirsPerGroup[GroupOfInode(node.NewInode)]++;
            }
            else if (node.IsFastOrSpecial)
            {
                inode.BlocksRaw = extraBlocks * (_bs / 512);
                inode.LinksCount = (ushort)node.LinkCount;
            }
            else
            {
                var newExtents = new List<Ext4Extent>();
                long dataBlocks = 0;
                foreach (var e in _plan.MapForCopy(_source, node))
                {
                    foreach (var ne in AllocateExtents(e.Logical, e.Count, e.Uninitialized))
                    {
                        if (!e.Uninitialized)
                        {
                            CopyBlocks(e.Physical + (ne.Logical - e.Logical), ne.Physical, ne.Count);
                        }

                        newExtents.Add(ne);
                        dataBlocks += ne.Count;
                    }
                }

                inode.Flags = (inode.Flags | Ext4Inode.FlagExtents) & ~(Ext4Inode.FlagHugeFile | Ext4Inode.FlagInlineData);
                long tree = WriteExtentTree(inode, newExtents);
                inode.BlocksRaw = (dataBlocks + tree + extraBlocks) * (_bs / 512);
                inode.LinksCount = (ushort)node.LinkCount;
            }

            StoreInode(inode);
        }

        private List<byte[]> BuildDirectoryBlocks(Ext4CopyPlan.Node node, Ext4Inode inode)
        {
            int limit = _bs - (_csum ? 12 : 0);
            var entries = new List<(uint Ino, byte Type, byte[] Name)>
            {
                (node.NewInode, (byte)(_sb.HasFileType ? 2 : 0), new[] { (byte)'.' }),
                (node.NewParent, (byte)(_sb.HasFileType ? 2 : 0), new[] { (byte)'.', (byte)'.' })
            };
            foreach (var c in node.Children)
            {
                entries.Add((_plan.BySource[c.SourceInode].NewInode, c.FileType, c.Name));
            }

            var blocks = new List<byte[]>();
            byte[] block = null;
            int used = 0;
            int lastOffset = -1;
            foreach (var (ino, type, name) in entries)
            {
                int size = (8 + name.Length + 3) & ~3;
                if (block == null || used + size > limit)
                {
                    if (block != null)
                    {
                        CloseDirectoryBlock(block, lastOffset, limit, node.NewInode, inode);
                    }

                    block = new byte[_bs];
                    blocks.Add(block);
                    used = 0;
                }

                BinaryHelpers.PutU32(block, used, ino);
                BinaryHelpers.PutU16(block, used + 4, size);
                block[used + 6] = (byte)name.Length;
                block[used + 7] = type;
                Buffer.BlockCopy(name, 0, block, used + 8, name.Length);
                lastOffset = used;
                used += size;
            }

            CloseDirectoryBlock(block, lastOffset, limit, node.NewInode, inode);

            long wanted = Ext4CopyPlan.DirectoryBlockCount(node, _bs, _csum);
            while (blocks.Count < wanted)
            {
                var empty = new byte[_bs];
                BinaryHelpers.PutU16(empty, 4, limit);
                CloseDirectoryBlock(empty, -1, limit, node.NewInode, inode);
                blocks.Add(empty);
            }

            return blocks;
        }

        private void CloseDirectoryBlock(byte[] block, int lastOffset, int limit, uint ino, Ext4Inode inode)
        {
            if (lastOffset >= 0)
            {
                BinaryHelpers.PutU16(block, lastOffset + 4, limit - lastOffset);
            }

            if (_csum)
            {
                int t = _bs - 12;
                BinaryHelpers.PutU32(block, t, 0);
                BinaryHelpers.PutU16(block, t + 4, 12);
                block[t + 6] = 0;
                block[t + 7] = 0xDE;
                uint seed = Crc32c.AppendU32(Crc32c.AppendU32(_sb.MetadataSeed, ino), inode.Generation);
                BinaryHelpers.PutU32(block, t + 8, Crc32c.Append(seed, block, 0, t));
            }
        }

        private long CopyXattrBlock(long sourceBlock)
        {
            if (_xattrBlockMap.TryGetValue(sourceBlock, out long existing))
            {
                return existing;
            }

            var data = _source.ReadBlock(sourceBlock);
            long target = AllocateBlock();
            BinaryHelpers.PutU32(data, 4, (uint)_plan.XattrBlockRefs[sourceBlock]); // h_refcount
            if (_csum)
            {
                BinaryHelpers.PutU32(data, 0x10, Ext4Csum.XattrBlock(_sb, target, data));
            }

            WriteBlock(target, data);
            _xattrBlockMap[sourceBlock] = target;
            return target;
        }

        /// <summary>Writes the extent tree into the inode (and tree blocks); returns tree blocks used.</summary>
        private long WriteExtentTree(Ext4Inode inode, List<Ext4Extent> extents)
        {
            var root = new byte[Ext4Inode.BlockArraySize];
            int perBlock = (_bs - 12) / 12;
            long treeBlocks = 0;

            // Level entries: (first logical, child block or extent).
            if (extents.Count <= 4)
            {
                WriteNodeHeader(root, 0, extents.Count, 4, 0);
                for (int i = 0; i < extents.Count; i++)
                {
                    WriteLeafEntry(root, 12 + (i * 12), extents[i]);
                }

                inode.SetBlockArray(root);
                return 0;
            }

            var level = new List<(long Logical, long Block)>();
            for (int start = 0; start < extents.Count; start += perBlock)
            {
                int count = Math.Min(perBlock, extents.Count - start);
                var block = new byte[_bs];
                WriteNodeHeader(block, 0, count, perBlock, 0);
                for (int i = 0; i < count; i++)
                {
                    WriteLeafEntry(block, 12 + (i * 12), extents[start + i]);
                }

                long location = AllocateBlock();
                WriteTreeBlock(inode, block, location, perBlock);
                level.Add((extents[start].Logical, location));
                treeBlocks++;
            }

            int depth = 1;
            while (level.Count > 4)
            {
                var next = new List<(long, long)>();
                for (int start = 0; start < level.Count; start += perBlock)
                {
                    int count = Math.Min(perBlock, level.Count - start);
                    var block = new byte[_bs];
                    WriteNodeHeader(block, 0, count, perBlock, depth);
                    for (int i = 0; i < count; i++)
                    {
                        WriteIndexEntry(block, 12 + (i * 12), level[start + i].Logical, level[start + i].Block);
                    }

                    long location = AllocateBlock();
                    WriteTreeBlock(inode, block, location, perBlock);
                    next.Add((level[start].Logical, location));
                    treeBlocks++;
                }

                level = next;
                depth++;
            }

            WriteNodeHeader(root, 0, level.Count, 4, depth);
            for (int i = 0; i < level.Count; i++)
            {
                WriteIndexEntry(root, 12 + (i * 12), level[i].Logical, level[i].Block);
            }

            inode.SetBlockArray(root);
            return treeBlocks;
        }

        private void WriteTreeBlock(Ext4Inode inode, byte[] block, long location, int max)
        {
            if (_csum)
            {
                int tail = 12 + (12 * max);
                BinaryHelpers.PutU32(block, tail, Crc32c.Append(inode.ChecksumSeed(_sb), block, 0, tail));
            }

            WriteBlock(location, block);
        }

        private static void WriteNodeHeader(byte[] node, int offset, int entries, int max, int depth)
        {
            BinaryHelpers.PutU16(node, offset, ExtentMagic);
            BinaryHelpers.PutU16(node, offset + 2, entries);
            BinaryHelpers.PutU16(node, offset + 4, max);
            BinaryHelpers.PutU16(node, offset + 6, depth);
            BinaryHelpers.PutU32(node, offset + 8, 0);
        }

        private static void WriteLeafEntry(byte[] node, int o, Ext4Extent e)
        {
            BinaryHelpers.PutU32(node, o, (uint)e.Logical);
            BinaryHelpers.PutU16(node, o + 4, e.Uninitialized ? e.Count + 32768 : e.Count);
            BinaryHelpers.PutU16(node, o + 6, (ushort)(e.Physical >> 32));
            BinaryHelpers.PutU32(node, o + 8, (uint)e.Physical);
        }

        private static void WriteIndexEntry(byte[] node, int o, long logical, long block)
        {
            BinaryHelpers.PutU32(node, o, (uint)logical);
            BinaryHelpers.PutU32(node, o + 4, (uint)block);
            BinaryHelpers.PutU16(node, o + 8, (ushort)(block >> 32));
            BinaryHelpers.PutU16(node, o + 10, 0);
        }

        // ---------------------------------------------------------------- allocation and I/O

        private List<Ext4Extent> AllocateExtents(long logical, long count, bool uninitialized)
        {
            var result = new List<Ext4Extent>();
            int max = uninitialized ? MaxUninitExtentLength : MaxExtentLength;
            while (count > 0)
            {
                long start = FindFree(_cursor);
                long run = 1;
                while (run < count && run < max && start + run < _layout.BlocksCount && !IsUsed(start + run))
                {
                    run++;
                }

                MarkUsed(start, run);
                _cursor = start + run;
                result.Add(new Ext4Extent(logical, start, (int)run, uninitialized));
                logical += run;
                count -= run;
            }

            return result;
        }

        private long AllocateBlock()
        {
            long block = FindFree(_cursor);
            MarkUsed(block, 1);
            _cursor = block + 1;
            return block;
        }

        private long FindFree(long from)
        {
            for (long b = Math.Max(from, _layout.FirstDataBlock); b < _layout.BlocksCount; b++)
            {
                ulong word = _usedBlocks[b >> 6];
                if (word == ulong.MaxValue && (b & 63) == 0)
                {
                    b += 63;
                    continue;
                }

                if (!IsUsed(b))
                {
                    return b;
                }
            }

            throw new Ext4FormatException("The new file system is full.");
        }

        private bool IsUsed(long block) => (_usedBlocks[block >> 6] & (1UL << (int)(block & 63))) != 0;

        private void MarkUsed(long start, long count)
        {
            for (long b = start; b < start + count; b++)
            {
                if (IsUsed(b))
                {
                    throw new InvalidOperationException("Block " + b + " allocated twice.");
                }

                _usedBlocks[b >> 6] |= 1UL << (int)(b & 63);
            }
        }

        private void CopyBlocks(long sourceBlock, long targetBlock, int count)
        {
            var buffer = new byte[Math.Min(count, CopyChunkBlocks) * _bs];
            while (count > 0)
            {
                int n = Math.Min(count, CopyChunkBlocks);
                _source.ReadBlocks(sourceBlock, n, buffer, 0);
                _target.Write(targetBlock * _bs, buffer, 0, n * _bs);
                BytesCopied += (long)n * _bs;
                sourceBlock += n;
                targetBlock += n;
                count -= n;
            }
        }

        private void WriteBlock(long block, byte[] data) => _target.Write(block * _bs, data, 0, _bs);

        private int GroupOfInode(uint ino) => (int)((ino - 1) / _layout.InodesPerGroup);

        private void StoreInode(Ext4Inode inode, bool forceChecksum = false)
        {
            if (inode.Raw.Length != _sb.InodeSize)
            {
                throw new InvalidOperationException("Inode size mismatch.");
            }

            if (_csum && (forceChecksum || !inode.IsAllZero))
            {
                inode.UpdateChecksum(_sb);
            }

            int group = GroupOfInode(inode.Number);
            if (group != _itableGroup)
            {
                if (group < _itableGroup)
                {
                    throw new InvalidOperationException("Inodes must be written in increasing order.");
                }

                FlushInodeTable();
                _itableGroup = group;
                _itableBuffer = new byte[_layout.InodeTableBlocks * _bs];
            }

            int index = (int)((inode.Number - 1) % _layout.InodesPerGroup);
            Buffer.BlockCopy(inode.Raw, 0, _itableBuffer, index * _sb.InodeSize, _sb.InodeSize);
            _usedInodes.Add(inode.Number);
        }

        private void FlushInodeTable()
        {
            if (_itableBuffer != null)
            {
                _target.Write(_layout.InodeTable[_itableGroup] * _bs, _itableBuffer, 0, _itableBuffer.Length);
                _itableBuffer = null;
            }
        }

        private static bool IsZero(byte[] data, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
            {
                if (data[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------- bitmaps, descriptors, superblocks

        private void FinishMetadata(uint[] journalBlockArray)
        {
            int groups = _layout.GroupCount;
            uint ipg = _layout.InodesPerGroup;
            var descriptors = new Ext4GroupDescriptor[groups];
            long freeBlocksTotal = 0;
            long freeInodesTotal = 0;

            for (int g = 0; g < groups; g++)
            {
                // Block bitmap: one bit per block of the group; bits past the file system end are set.
                var blockBitmap = new byte[_bs];
                long start = _layout.GroupStart(g);
                long end = _layout.GroupEnd(g);
                uint freeBlocks = 0;
                for (long i = 0; i < _layout.BlocksPerGroup; i++)
                {
                    long b = start + i;
                    if (b >= end || IsUsed(b))
                    {
                        blockBitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
                    }
                    else
                    {
                        freeBlocks++;
                    }
                }

                // Inode bitmap: ipg bits, padding up to the block end set.
                var inodeBitmap = new byte[_bs];
                uint freeInodes = 0;
                uint highestUsed = 0;
                for (uint i = 0; i < ipg; i++)
                {
                    uint ino = ((uint)g * ipg) + i + 1;
                    if (ino < _sb.FirstInode || _usedInodes.Contains(ino))
                    {
                        inodeBitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
                        highestUsed = i + 1;
                    }
                    else
                    {
                        freeInodes++;
                    }
                }

                for (long i = ipg; i < (long)_bs * 8; i++)
                {
                    inodeBitmap[i >> 3] |= (byte)(1 << (int)(i & 7));
                }

                WriteBlock(_layout.BlockBitmap[g], blockBitmap);
                WriteBlock(_layout.InodeBitmap[g], inodeBitmap);

                var d = new Ext4GroupDescriptor(new byte[_layout.DescriptorSize])
                {
                    BlockBitmap = _layout.BlockBitmap[g],
                    InodeBitmap = _layout.InodeBitmap[g],
                    InodeTable = _layout.InodeTable[g],
                    FreeBlocks = freeBlocks,
                    FreeInodes = freeInodes,
                    UsedDirs = (uint)_dirsPerGroup[g]
                };

                if (_csum || _sb.HasGdtCsum)
                {
                    ushort flags = Ext4GroupDescriptor.FlagItableZeroed;
                    if (highestUsed == 0)
                    {
                        flags |= Ext4GroupDescriptor.FlagInodeUninit;
                    }

                    d.Flags = flags;
                    d.ItableUnused = ipg - highestUsed;
                }

                if (_csum)
                {
                    d.BlockBitmapChecksum = Ext4Csum.Bitmap(_sb, blockBitmap, (int)(_layout.BlocksPerGroup / 8));
                    d.InodeBitmapChecksum = Ext4Csum.Bitmap(_sb, inodeBitmap, (int)(ipg / 8));
                    if (_layout.DescriptorSize < 64)
                    {
                        d.BlockBitmapChecksum &= 0xFFFF;
                        d.InodeBitmapChecksum &= 0xFFFF;
                    }
                }

                d.Checksum = d.ComputeChecksum(_sb, (uint)g);
                descriptors[g] = d;
                freeBlocksTotal += freeBlocks;
                freeInodesTotal += freeInodes;
            }

            _sb.FreeBlocksCount = freeBlocksTotal;
            _sb.FreeInodesCount = (uint)freeInodesTotal;
            if (journalBlockArray != null)
            {
                _sb.JournalBackupType = 1;
                for (int i = 0; i < 17; i++)
                {
                    _sb.SetJournalBackup(i, journalBlockArray[i]);
                }
            }

            // Group descriptor table (primary and every backup carry the same table).
            var gdt = new byte[_layout.GdtBlocks * _bs];
            for (int g = 0; g < groups; g++)
            {
                Buffer.BlockCopy(descriptors[g].Raw, 0, gdt, g * _layout.DescriptorSize, _layout.DescriptorSize);
            }

            for (int g = 0; g < groups; g++)
            {
                if (!_layout.HasSuperblock(g))
                {
                    continue;
                }

                long sbBlock = _layout.GroupStart(g);
                _target.Write((sbBlock + 1) * _bs, gdt, 0, gdt.Length);

                var copy = _sb.Clone();
                copy.BlockGroupNumber = (ushort)g;
                if (_csum)
                {
                    copy.Checksum = copy.ComputeChecksum();
                }

                if (g == 0)
                {
                    // Primary superblock at byte 1024; keep the first 1024 bytes of the source area.
                    // Bytes 0-1023 (boot area) come from the source; the rest of a 4 KiB block 0 stays zero.
                    var head = new byte[Math.Max(2048, _bs)];
                    var sourceHead = new byte[1024];
                    ReadSourceBytes(0, sourceHead);
                    Buffer.BlockCopy(sourceHead, 0, head, 0, 1024);
                    Buffer.BlockCopy(copy.Raw, 0, head, 1024, 1024);
                    _target.Write(0, head, 0, head.Length);
                }
                else
                {
                    var block = new byte[_bs];
                    Buffer.BlockCopy(copy.Raw, 0, block, 0, 1024);
                    WriteBlock(sbBlock, block);
                }
            }
        }

        private void ReadSourceBytes(long offset, byte[] buffer)
        {
            var block = new byte[Math.Max(_bs, 1024)];
            _source.ReadBlocks(offset / _bs, Math.Max(1, 1024 / _bs), block, 0);
            Buffer.BlockCopy(block, (int)(offset % _bs), buffer, 0, buffer.Length);
        }
    }
}
