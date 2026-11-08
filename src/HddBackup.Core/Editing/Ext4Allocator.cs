using System;
using System.Collections.Generic;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Editing
{
    public enum EditError
    {
        NoSpace,
        NoInodes,
        TooFragmented,
        NotFound,
        NotRegularFile,
        NotDirectory,
        EntryChanged,
        NameExists,
        BadName,
        IsRoot,
        Protected,
        NotEditable,
        TooLarge,
        TooLong,
        BadValue,
        Internal
    }

    /// <summary>A change cannot be made; <see cref="Code"/> says why, nothing has been written.</summary>
    public sealed class EditException : Exception
    {
        public EditException(EditError code, string detail = null, long needed = 0, long available = 0)
            : base(code + (detail == null ? string.Empty : ": " + detail))
        {
            Code = code;
            Detail = detail ?? string.Empty;
            Needed = needed;
            Available = available;
        }

        public EditError Code { get; }

        public string Detail { get; }

        public long Needed { get; }

        public long Available { get; }
    }

    /// <summary>Sector-aligned access to the recording device: whole blocks and patches of a few bytes.</summary>
    internal sealed class EditIo
    {
        private readonly RecordingBlockDevice _dev;

        public EditIo(RecordingBlockDevice dev, int blockSize)
        {
            _dev = dev;
            BlockSize = blockSize;
            if (dev.SectorSize > blockSize || dev.SectorSize > 1024)
            {
                throw new EditException(EditError.NotEditable, "sector size " + dev.SectorSize);
            }
        }

        public int BlockSize { get; }

        public WritePhase Phase
        {
            get => _dev.Phase;
            set => _dev.Phase = value;
        }

        public void WriteBlock(long block, byte[] data) => _dev.Write(block * BlockSize, data, 0, BlockSize);

        public void WriteBlocks(long block, byte[] data, int length) => _dev.Write(block * BlockSize, data, 0, length);

        public byte[] ReadBlock(long block)
        {
            var data = new byte[BlockSize];
            _dev.Read(block * BlockSize, data, 0, BlockSize);
            return data;
        }

        /// <summary>Changes a few bytes at an arbitrary offset (read, patch and write back whole sectors).</summary>
        public void Patch(long offset, byte[] data)
        {
            int sector = _dev.SectorSize;
            long start = offset / sector * sector;
            long end = (offset + data.Length + sector - 1) / sector * sector;
            var buffer = new byte[end - start];
            _dev.Read(start, buffer, 0, buffer.Length);
            Buffer.BlockCopy(data, 0, buffer, (int)(offset - start), data.Length);
            _dev.Write(start, buffer, 0, buffer.Length);
        }
    }

    /// <summary>
    /// Block and inode allocation on a file system in place (research R-3): bitmaps read on demand, uninitialised
    /// groups initialised as the kernel does, group descriptors, bitmap checksums and the superblock counters
    /// kept right, all written by <see cref="Commit"/> in the phase the caller names.
    /// </summary>
    internal sealed class Ext4Allocator
    {
        private readonly Ext4Reader _reader;
        private readonly EditIo _io;
        private readonly Ext4Superblock _sb;
        private readonly Ext4GroupDescriptor[] _gd;
        private readonly int _bs;
        private readonly int _groups;
        private readonly Dictionary<int, byte[]> _blockBitmaps = new Dictionary<int, byte[]>();
        private readonly Dictionary<int, byte[]> _inodeBitmaps = new Dictionary<int, byte[]>();
        private readonly HashSet<int> _dirtyBlockBitmaps = new HashSet<int>();
        private readonly HashSet<int> _dirtyInodeBitmaps = new HashSet<int>();
        private readonly HashSet<int> _dirtyDescriptors = new HashSet<int>();
        private bool _superblockDirty;

        public Ext4Allocator(Ext4Reader reader, EditIo io, uint now)
        {
            _reader = reader;
            _io = io;
            _sb = reader.Superblock.Clone();
            _bs = reader.BlockSize;
            _groups = reader.GroupCount;
            _gd = new Ext4GroupDescriptor[_groups];
            for (int g = 0; g < _groups; g++)
            {
                _gd[g] = new Ext4GroupDescriptor((byte[])reader.Groups[g].Raw.Clone());
            }

            Now = now;
        }

        public uint Now { get; }

        public Ext4Superblock Superblock => _sb;

        public int GroupCount => _groups;

        public bool UsesGroupChecksums => _sb.HasMetadataCsum || _sb.HasGdtCsum;

        public long FreeBlockCount => _sb.FreeBlocksCount;

        public long GroupStart(int group) => _sb.FirstDataBlock + ((long)group * _sb.BlocksPerGroup);

        public int GroupOfBlock(long block) => (int)((block - _sb.FirstDataBlock) / _sb.BlocksPerGroup);

        public int GroupOfInode(uint inode) => (int)((inode - 1) / _sb.InodesPerGroup);

        public long InodeOffset(uint inode)
        {
            int group = GroupOfInode(inode);
            return (_gd[group].InodeTable * _bs) + ((long)((inode - 1) % _sb.InodesPerGroup) * _sb.InodeSize);
        }

        // ------------------------------------------------------------------------- blocks

        /// <summary>Allocates <paramref name="count"/> blocks as few runs as possible, near <paramref name="goalGroup"/>.</summary>
        public List<(long Start, int Count)> AllocateBlocks(long count, int goalGroup)
        {
            if (count > _sb.FreeBlocksCount)
            {
                throw new EditException(EditError.NoSpace, null, count * _bs, _sb.FreeBlocksCount * _bs);
            }

            var runs = new List<(long, int)>();
            long left = count;
            for (int i = 0; i < _groups && left > 0; i++)
            {
                int g = (goalGroup + i) % _groups;
                if (_gd[g].FreeBlocks == 0)
                {
                    continue;
                }

                var bm = BlockBitmap(g);
                int bits = GroupBlockCount(g);
                int idx = 0;
                while (left > 0 && idx < bits)
                {
                    if (IsSet(bm, idx))
                    {
                        idx++;
                        continue;
                    }

                    int run = 0;
                    while (idx + run < bits && !IsSet(bm, idx + run) && run < left && run < 32768)
                    {
                        run++;
                    }

                    for (int k = 0; k < run; k++)
                    {
                        SetBit(bm, idx + k);
                    }

                    runs.Add((GroupStart(g) + idx, run));
                    _gd[g].FreeBlocks -= (uint)run;
                    _gd[g].Flags = (ushort)(_gd[g].Flags & ~Ext4GroupDescriptor.FlagBlockUninit);
                    _dirtyBlockBitmaps.Add(g);
                    _dirtyDescriptors.Add(g);
                    left -= run;
                    idx += run;
                }
            }

            if (left > 0)
            {
                // The counters said there was room but the bitmaps did not agree: stop before anything is written.
                throw new EditException(EditError.NoSpace, "bitmaps disagree with the counters", count * _bs, (count - left) * _bs);
            }

            _sb.FreeBlocksCount -= count;
            _superblockDirty = true;
            return runs;
        }

        public long AllocateOneBlock(int goalGroup) => AllocateBlocks(1, goalGroup)[0].Start;

        public void FreeBlocks(IEnumerable<(long Start, int Count)> runs)
        {
            foreach (var (start, count) in runs)
            {
                for (int i = 0; i < count; i++)
                {
                    long block = start + i;
                    int g = GroupOfBlock(block);
                    var bm = BlockBitmap(g);
                    int idx = (int)(block - GroupStart(g));
                    if (!IsSet(bm, idx))
                    {
                        continue; // already free: do not count it twice
                    }

                    ClearBit(bm, idx);
                    _gd[g].FreeBlocks++;
                    _sb.FreeBlocksCount++;
                    _dirtyBlockBitmaps.Add(g);
                    _dirtyDescriptors.Add(g);
                    _superblockDirty = true;
                }
            }
        }

        public bool IsBlockUsed(long block)
        {
            int g = GroupOfBlock(block);
            return IsSet(BlockBitmap(g), (int)(block - GroupStart(g)));
        }

        // ------------------------------------------------------------------------- inodes

        public uint AllocateInode(int goalGroup, bool directory)
        {
            if (_sb.FreeInodesCount == 0)
            {
                throw new EditException(EditError.NoInodes, null, 1, 0);
            }

            for (int i = 0; i < _groups; i++)
            {
                int g = (goalGroup + i) % _groups;
                if (_gd[g].FreeInodes == 0)
                {
                    continue;
                }

                var bm = InodeBitmap(g);
                int ipg = (int)_sb.InodesPerGroup;
                for (int idx = 0; idx < ipg; idx++)
                {
                    if (IsSet(bm, idx))
                    {
                        continue;
                    }

                    SetBit(bm, idx);
                    var gd = _gd[g];
                    gd.FreeInodes--;
                    if (directory)
                    {
                        gd.UsedDirs++;
                    }

                    gd.Flags = (ushort)(gd.Flags & ~Ext4GroupDescriptor.FlagInodeUninit);
                    if (UsesGroupChecksums && gd.ItableUnused > (uint)(ipg - (idx + 1)))
                    {
                        gd.ItableUnused = (uint)(ipg - (idx + 1));
                    }

                    _sb.FreeInodesCount--;
                    _dirtyInodeBitmaps.Add(g);
                    _dirtyDescriptors.Add(g);
                    _superblockDirty = true;
                    return (uint)((g * ipg) + idx + 1);
                }
            }

            throw new EditException(EditError.NoInodes, null, 1, 0);
        }

        public void FreeInode(uint inode, bool directory)
        {
            int g = GroupOfInode(inode);
            var bm = InodeBitmap(g);
            int idx = (int)((inode - 1) % _sb.InodesPerGroup);
            if (!IsSet(bm, idx))
            {
                return;
            }

            ClearBit(bm, idx);
            _gd[g].FreeInodes++;
            if (directory && _gd[g].UsedDirs > 0)
            {
                _gd[g].UsedDirs--;
            }

            _sb.FreeInodesCount++;
            _dirtyInodeBitmaps.Add(g);
            _dirtyDescriptors.Add(g);
            _superblockDirty = true;
        }

        // ------------------------------------------------------------------------- writing it all out

        /// <summary>Writes the changed bitmaps, group descriptors and the superblock, in this order, in <paramref name="phase"/>.</summary>
        public void Commit(WritePhase phase)
        {
            _io.Phase = phase;
            int bytesPerBlockBitmap = (int)(_sb.BlocksPerGroup / 8);
            int bytesPerInodeBitmap = (int)(_sb.InodesPerGroup / 8);
            foreach (int g in _dirtyBlockBitmaps)
            {
                var bm = _blockBitmaps[g];
                if (_sb.HasMetadataCsum)
                {
                    _gd[g].BlockBitmapChecksum = Ext4Csum.Bitmap(_sb, bm, bytesPerBlockBitmap);
                }

                _io.WriteBlock(_gd[g].BlockBitmap, bm);
            }

            foreach (int g in _dirtyInodeBitmaps)
            {
                var bm = _inodeBitmaps[g];
                if (_sb.HasMetadataCsum)
                {
                    _gd[g].InodeBitmapChecksum = Ext4Csum.Bitmap(_sb, bm, bytesPerInodeBitmap);
                }

                _io.WriteBlock(_gd[g].InodeBitmap, bm);
            }

            if (_dirtyDescriptors.Count > 0)
            {
                WriteDescriptors();
            }

            if (_superblockDirty)
            {
                _sb.WriteTime = Now;
                if (_sb.HasMetadataCsum)
                {
                    _sb.Checksum = _sb.ComputeChecksum();
                }

                _io.Patch(Ext4Superblock.ByteOffset, _sb.Raw);
            }

            _dirtyBlockBitmaps.Clear();
            _dirtyInodeBitmaps.Clear();
            _dirtyDescriptors.Clear();
            _superblockDirty = false;
        }

        private void WriteDescriptors()
        {
            int descSize = _sb.DescriptorSize;
            long gdtBlock = _sb.FirstDataBlock + 1;
            var blocks = new Dictionary<long, byte[]>();
            foreach (int g in _dirtyDescriptors)
            {
                if (UsesGroupChecksums)
                {
                    _gd[g].Checksum = _gd[g].ComputeChecksum(_sb, (uint)g);
                }

                long byteOffset = (long)g * descSize;
                long block = gdtBlock + (byteOffset / _bs);
                if (!blocks.TryGetValue(block, out var data))
                {
                    data = _io.ReadBlock(block);
                    blocks[block] = data;
                }

                Buffer.BlockCopy(_gd[g].Raw, 0, data, (int)(byteOffset % _bs), descSize);
            }

            foreach (var pair in blocks)
            {
                _io.WriteBlock(pair.Key, pair.Value);
            }
        }

        // ------------------------------------------------------------------------- bitmaps

        private byte[] BlockBitmap(int g)
        {
            if (_blockBitmaps.TryGetValue(g, out var bm))
            {
                return bm;
            }

            if ((_gd[g].Flags & Ext4GroupDescriptor.FlagBlockUninit) != 0 && UsesGroupChecksums)
            {
                bm = InitBlockBitmap(g);
            }
            else
            {
                bm = _io.ReadBlock(_gd[g].BlockBitmap);
            }

            _blockBitmaps[g] = bm;
            return bm;
        }

        private byte[] InodeBitmap(int g)
        {
            if (_inodeBitmaps.TryGetValue(g, out var bm))
            {
                return bm;
            }

            if ((_gd[g].Flags & Ext4GroupDescriptor.FlagInodeUninit) != 0 && UsesGroupChecksums)
            {
                bm = new byte[_bs];
                for (int i = (int)(_sb.InodesPerGroup / 8); i < _bs; i++)
                {
                    bm[i] = 0xFF;
                }
            }
            else
            {
                bm = _io.ReadBlock(_gd[g].InodeBitmap);
            }

            _inodeBitmaps[g] = bm;
            return bm;
        }

        /// <summary>The bitmap a kernel would build for a BLOCK_UNINIT group: copies of the superblock and descriptors, and the metadata of any group that lives here.</summary>
        private byte[] InitBlockBitmap(int g)
        {
            var bm = new byte[_bs];
            long start = GroupStart(g);
            int bits = GroupBlockCount(g);
            if (HasSuperBackup(g))
            {
                int gdtBlocks = (int)(((long)_groups * _sb.DescriptorSize + _bs - 1) / _bs);
                int used = 1 + gdtBlocks + _sb.ReservedGdtBlocks;
                for (int i = 0; i < used && i < bits; i++)
                {
                    SetBit(bm, i);
                }
            }

            int tableBlocks = (int)(((long)_sb.InodesPerGroup * _sb.InodeSize + _bs - 1) / _bs);
            for (int h = 0; h < _groups; h++)
            {
                Mark(bm, start, bits, _gd[h].BlockBitmap, 1);
                Mark(bm, start, bits, _gd[h].InodeBitmap, 1);
                Mark(bm, start, bits, _gd[h].InodeTable, tableBlocks);
            }

            for (int i = bits; i < _bs * 8; i++)
            {
                SetBit(bm, i);
            }

            return bm;
        }

        private static void Mark(byte[] bm, long groupStart, int bits, long block, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long idx = block + i - groupStart;
                if (idx >= 0 && idx < bits)
                {
                    SetBit(bm, (int)idx);
                }
            }
        }

        private int GroupBlockCount(int g)
        {
            long start = GroupStart(g);
            long left = _sb.BlocksCount - start;
            return (int)Math.Min(_sb.BlocksPerGroup, left);
        }

        private bool HasSuperBackup(int g)
        {
            if (g == 0 || g == 1)
            {
                return true;
            }

            foreach (int b in new[] { 3, 5, 7 })
            {
                long p = b;
                while (p < g)
                {
                    p *= b;
                }

                if (p == g)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSet(byte[] bm, int idx) => (bm[idx >> 3] & (1 << (idx & 7))) != 0;

        private static void SetBit(byte[] bm, int idx) => bm[idx >> 3] |= (byte)(1 << (idx & 7));

        private static void ClearBit(byte[] bm, int idx) => bm[idx >> 3] &= (byte)~(1 << (idx & 7));
    }
}
