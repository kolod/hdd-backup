using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Editing
{
    /// <summary>What the user saw of an entry when they changed it; a mismatch at save time stops the change.</summary>
    public sealed class OriginalStamp
    {
        public OriginalStamp(uint inode, long size, uint mtime)
        {
            Inode = inode;
            Size = size;
            Mtime = mtime;
        }

        public uint Inode { get; }

        public long Size { get; }

        public uint Mtime { get; }
    }

    /// <summary>
    /// Changes one ext4 file system in place (research R-1): every change is recorded as ordered writes on a
    /// <see cref="RecordingBlockDevice"/>, so that it can be checked before anything reaches the real device.
    /// Instances are single use: build the changes, take <see cref="Writes"/>, discard.
    /// </summary>
    internal sealed partial class Ext4Editor
    {
        private const ushort ExtentMagic = 0xF30A;
        private const int MaxExtentLength = 32768;

        private readonly RecordingBlockDevice _dev;
        private readonly Ext4Reader _reader;
        private readonly EditIo _io;
        private readonly Ext4Allocator _alloc;
        private readonly Ext4Superblock _sb;
        private readonly int _bs;

        public Ext4Editor(IBlockDevice partition, uint? nowSeconds = null)
        {
            _dev = new RecordingBlockDevice(partition);
            _reader = new Ext4Reader(_dev);
            _sb = _reader.Superblock;
            var support = Ext4EditSupportCheck.Check(_sb);
            if (!support.Editable)
            {
                throw new EditException(EditError.NotEditable, support.Reason.ToString());
            }

            _bs = _reader.BlockSize;
            _io = new EditIo(_dev, _bs);
            uint now = nowSeconds ?? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _alloc = new Ext4Allocator(_reader, _io, now);
        }

        /// <summary>Everything recorded so far, in order.</summary>
        public WriteSet Writes => _dev.Set;

        public Ext4Reader Reader => _reader;

        public Ext4Superblock Superblock => _alloc.Superblock;

        // ------------------------------------------------------------------------- content

        /// <summary>
        /// Replaces the content of a regular file, copy on write: new blocks and data first, then their
        /// allocation, then one inode write, then the freeing of the old blocks (research R-5): a file is
        /// entirely old or entirely new if the writing stops anywhere.
        /// </summary>
        public void ReplaceContent(string path, byte[] content, OriginalStamp original = null)
        {
            if (content.Length > 64 * 1024 * 1024)
            {
                throw new EditException(EditError.TooLarge);
            }

            var inode = Resolve(path);
            if (!inode.IsRegular)
            {
                throw new EditException(EditError.NotRegularFile, path);
            }

            CheckOriginal(inode, original, path);
            var oldMap = _reader.MapFile(inode);
            var oldBlocks = new List<(long Start, int Count)>();
            foreach (var e in oldMap)
            {
                oldBlocks.Add((e.Physical, e.Count));
            }

            foreach (long b in _reader.ExtentTreeBlocks(inode))
            {
                oldBlocks.Add((b, 1));
            }

            int goal = Math.Max(0, _alloc.GroupOfInode(inode.Number));
            long blocksNeeded = (content.Length + _bs - 1) / _bs;
            var runs = blocksNeeded == 0 ? new List<(long Start, int Count)>() : _alloc.AllocateBlocks(blocksNeeded, goal);
            var extents = new List<Ext4Extent>();
            long logical = 0;
            foreach (var (start, count) in runs)
            {
                extents.Add(new Ext4Extent(logical, start, count, false));
                logical += count;
            }

            int perLeaf = (_bs - 12) / 12;
            if (extents.Count > perLeaf)
            {
                throw new EditException(EditError.TooFragmented, path);
            }

            long leaf = 0;
            if (extents.Count > 4)
            {
                leaf = _alloc.AllocateOneBlock(goal);
            }

            // 1. data and the extent leaf, durable before anything refers to them
            _io.Phase = WritePhase.Data;
            long pos = 0;
            foreach (var e in extents)
            {
                long length = (long)e.Count * _bs;
                var buffer = new byte[length];
                int copy = (int)Math.Min(length, content.Length - pos);
                Buffer.BlockCopy(content, (int)pos, buffer, 0, copy);
                _io.WriteBlocks(e.Physical, buffer, buffer.Length);
                pos += length;
            }

            if (leaf != 0)
            {
                WriteLeaf(inode, leaf, extents);
            }

            // 2. their allocation
            _alloc.Commit(WritePhase.Allocation);

            // 3. one inode write that switches the file to the new blocks
            SetExtentRoot(inode, extents, leaf);
            inode.Size = content.Length;
            inode.Flags |= Ext4Inode.FlagExtents;
            SetBlockCount(inode, extents.Sum(e => (long)e.Count) + (leaf != 0 ? 1 : 0));
            SetTimes(inode, ctime: true, mtime: true);
            StoreInode(inode, WritePhase.Inode);

            // 4. the old blocks are free only now
            _alloc.FreeBlocks(oldBlocks);
            _alloc.Commit(WritePhase.Free);
        }

        // ------------------------------------------------------------------------- lookups

        /// <summary>Reads the inode of an absolute path (no links followed); throws <see cref="EditError.NotFound"/>.</summary>
        public Ext4Inode Resolve(string path)
        {
            var inode = _reader.ReadInode(2);
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!inode.IsDirectory)
                {
                    throw new EditException(EditError.NotFound, path);
                }

                byte[] wanted = Encoding.UTF8.GetBytes(part);
                Ext4DirEntry hit = null;
                foreach (var e in _reader.ReadDirectory(inode))
                {
                    if (!e.IsDot && !e.IsDotDot && e.Name.SequenceEqual(wanted))
                    {
                        hit = e;
                        break;
                    }
                }

                if (hit == null)
                {
                    throw new EditException(EditError.NotFound, path);
                }

                inode = _reader.ReadInode(hit.Inode);
            }

            return inode;
        }

        private static void CheckOriginal(Ext4Inode inode, OriginalStamp original, string path)
        {
            if (original != null && (original.Inode != inode.Number || original.Size != inode.Size || original.Mtime != inode.Mtime))
            {
                throw new EditException(EditError.EntryChanged, path);
            }
        }

        // ------------------------------------------------------------------------- inode helpers

        private void StoreInode(Ext4Inode inode, WritePhase phase)
        {
            _io.Phase = phase;
            if (_sb.HasMetadataCsum)
            {
                inode.UpdateChecksum(_sb);
            }

            _io.Patch(_alloc.InodeOffset(inode.Number), inode.Raw);
        }

        private void SetTimes(Ext4Inode inode, bool ctime, bool mtime)
        {
            uint now = _alloc.Now;
            if (ctime)
            {
                BinaryHelpers.PutU32(inode.Raw, 0x0C, now);
                if (inode.ExtraIsize >= 8)
                {
                    BinaryHelpers.PutU32(inode.Raw, 0x84, 0);
                }
            }

            if (mtime)
            {
                BinaryHelpers.PutU32(inode.Raw, 0x10, now);
                if (inode.ExtraIsize >= 12)
                {
                    BinaryHelpers.PutU32(inode.Raw, 0x88, 0);
                }
            }
        }

        private void SetBlockCount(Ext4Inode inode, long dataAndTreeBlocks)
        {
            long total = dataAndTreeBlocks + (inode.FileAcl != 0 ? 1 : 0);
            inode.BlocksRaw = (inode.Flags & Ext4Inode.FlagHugeFile) != 0 ? total : total * (_bs / 512);
        }

        // ------------------------------------------------------------------------- extent trees

        private void SetExtentRoot(Ext4Inode inode, List<Ext4Extent> extents, long leaf)
        {
            var root = new byte[Ext4Inode.BlockArraySize];
            if (leaf == 0)
            {
                WriteHeader(root, 0, extents.Count, 4, 0);
                for (int i = 0; i < extents.Count; i++)
                {
                    WriteLeafEntry(root, 12 + (i * 12), extents[i]);
                }
            }
            else
            {
                WriteHeader(root, 0, 1, 4, 1);
                BinaryHelpers.PutU32(root, 12, (uint)extents[0].Logical);
                BinaryHelpers.PutU32(root, 16, (uint)leaf);
                BinaryHelpers.PutU16(root, 20, (ushort)(leaf >> 32));
            }

            inode.SetBlockArray(root);
        }

        private void WriteLeaf(Ext4Inode inode, long location, List<Ext4Extent> extents)
        {
            int max = (_bs - 12) / 12;
            var block = new byte[_bs];
            WriteHeader(block, 0, extents.Count, max, 0);
            for (int i = 0; i < extents.Count; i++)
            {
                WriteLeafEntry(block, 12 + (i * 12), extents[i]);
            }

            if (_sb.HasMetadataCsum)
            {
                int tail = 12 + (12 * max);
                BinaryHelpers.PutU32(block, tail, Crc32c.Append(inode.ChecksumSeed(_sb), block, 0, tail));
            }

            _io.WriteBlock(location, block);
        }

        private static void WriteHeader(byte[] node, int offset, int entries, int max, int depth)
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
    }
}
