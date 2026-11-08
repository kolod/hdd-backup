using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Editing
{
    /// <summary>Names are checked at once, before anything is recorded (spec FR-022).</summary>
    public static class EditNames
    {
        public const int MaxBytes = 255;

        /// <summary>Null when the name is acceptable, else <see cref="EditError.BadName"/>.</summary>
        public static EditError? Validate(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "." || name == ".." || name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0)
            {
                return EditError.BadName;
            }

            return Encoding.UTF8.GetByteCount(name) > MaxBytes ? EditError.BadName : (EditError?)null;
        }
    }

    internal sealed partial class Ext4Editor
    {
        private const int FileModeDefault = 0x81A4;
        private const int DirModeDefault = 0x41ED;

        private sealed class DirBlock
        {
            public long Logical;
            public long Physical;
            public byte[] Data;
        }

        private int DirLimit => _bs - (_sb.HasMetadataCsum ? 12 : 0);

        // ------------------------------------------------------------------------- creating

        public void CreateFile(string parentPath, string name, byte[] content)
        {
            CheckName(name);
            var parent = ResolveDirectory(parentPath);
            RequireNoEntry(parent, name);
            int goal = _alloc.GroupOfInode(parent.Number);
            long blocksNeeded = (content.Length + _bs - 1) / _bs;
            uint ino = _alloc.AllocateInode(goal, false);
            var runs = blocksNeeded == 0 ? new List<(long Start, int Count)>() : _alloc.AllocateBlocks(blocksNeeded, goal);
            var extents = new List<Ext4Extent>();
            long logical = 0;
            foreach (var (start, count) in runs)
            {
                extents.Add(new Ext4Extent(logical, start, count, false));
                logical += count;
            }

            if (extents.Count > (_bs - 12) / 12)
            {
                throw new EditException(EditError.TooFragmented, name);
            }

            long leaf = extents.Count > 4 ? _alloc.AllocateOneBlock(goal) : 0;
            var inode = NewInode(ino, FileModeDefault, parent);
            _io.Phase = WritePhase.Data;
            long pos = 0;
            foreach (var e in extents)
            {
                long length = (long)e.Count * _bs;
                var buffer = new byte[length];
                Buffer.BlockCopy(content, (int)pos, buffer, 0, (int)Math.Min(length, content.Length - pos));
                _io.WriteBlocks(e.Physical, buffer, buffer.Length);
                pos += length;
            }

            if (leaf != 0)
            {
                WriteLeaf(inode, leaf, extents);
            }

            _alloc.Commit(WritePhase.Allocation);
            SetExtentRoot(inode, extents, leaf);
            inode.Size = content.Length;
            inode.LinksCount = 1;
            SetBlockCount(inode, extents.Sum(e => (long)e.Count) + (leaf != 0 ? 1 : 0));
            StoreInode(inode, WritePhase.Inode);
            AddEntry(parent, name, ino, 1);
            _alloc.Commit(WritePhase.Counters);
        }

        public void CreateDirectory(string parentPath, string name)
        {
            CheckName(name);
            var parent = ResolveDirectory(parentPath);
            RequireNoEntry(parent, name);
            int goal = _alloc.GroupOfInode(parent.Number);
            uint ino = _alloc.AllocateInode(goal, true);
            long block = _alloc.AllocateOneBlock(goal);
            var inode = NewInode(ino, DirModeDefault, parent);

            var data = new byte[_bs];
            int limit = DirLimit;
            WriteEntry(data, 0, ino, 12, 2, new[] { (byte)'.' });
            WriteEntry(data, 12, parent.Number, limit - 12, 2, new[] { (byte)'.', (byte)'.' });
            _io.Phase = WritePhase.Data;
            SealBlock(data, inode);
            _io.WriteBlock(block, data);
            _alloc.Commit(WritePhase.Allocation);

            var extents = new List<Ext4Extent> { new Ext4Extent(0, block, 1, false) };
            SetExtentRoot(inode, extents, 0);
            inode.Size = _bs;
            inode.LinksCount = 2;
            SetBlockCount(inode, 1);
            StoreInode(inode, WritePhase.Inode);

            AddEntry(parent, name, ino, 2);
            parent.LinksCount = (ushort)(parent.LinksCount + 1);
            TouchDirectory(parent, WritePhase.Link);
            _alloc.Commit(WritePhase.Counters);
        }

        // ------------------------------------------------------------------------- deleting

        /// <summary>Deletes an entry; a directory goes with everything in it. The entry is unlinked first, so nothing refers to what is freed.</summary>
        public void Delete(string path)
        {
            string trimmed = path.TrimEnd('/');
            if (trimmed.Length == 0)
            {
                throw new EditException(EditError.IsRoot, path);
            }

            int slash = trimmed.LastIndexOf('/');
            string parentPath = slash <= 0 ? "/" : trimmed.Substring(0, slash);
            string name = trimmed.Substring(slash + 1);
            var parent = ResolveDirectory(parentPath);
            var target = Resolve(trimmed);
            if (parentPath == "/" && name == "lost+found")
            {
                throw new EditException(EditError.Protected, path);
            }

            // 1. unlink
            RemoveEntry(parent, name);
            if (target.IsDirectory)
            {
                parent.LinksCount = (ushort)Math.Max(2, parent.LinksCount - 1);
            }

            TouchDirectory(parent, WritePhase.Link);

            // 2. the subtree, bottom up
            var order = new List<Ext4Inode>();
            Collect(target, order);
            var remaining = new Dictionary<uint, int>();
            var runs = new List<(long Start, int Count)>();
            var toFree = new List<Ext4Inode>();
            foreach (var inode in order)
            {
                if (!inode.IsDirectory)
                {
                    int left = remaining.TryGetValue(inode.Number, out int r) ? r : inode.LinksCount;
                    left--;
                    remaining[inode.Number] = left;
                    if (left > 0)
                    {
                        if (left != inode.LinksCount)
                        {
                            // other names stay: only the link count drops
                            inode.LinksCount = (ushort)left;
                            inode.Dtime = 0;
                            StoreInode(inode, WritePhase.Inode);
                        }

                        continue;
                    }
                }

                toFree.Add(inode);
                AddFreedBlocks(inode, runs);
            }

            _io.Phase = WritePhase.Free;
            foreach (var inode in toFree)
            {
                _io.Patch(_alloc.InodeOffset(inode.Number), new byte[_sb.InodeSize]);
                _alloc.FreeInode(inode.Number, inode.IsDirectory);
            }

            _alloc.FreeBlocks(runs);
            _alloc.Commit(WritePhase.Free);
        }

        private void Collect(Ext4Inode inode, List<Ext4Inode> order)
        {
            if (inode.IsDirectory)
            {
                foreach (var e in _reader.ReadDirectory(inode))
                {
                    if (!e.IsDot && !e.IsDotDot)
                    {
                        Collect(_reader.ReadInode(e.Inode), order);
                    }
                }
            }

            order.Add(inode);
        }

        private void AddFreedBlocks(Ext4Inode inode, List<(long Start, int Count)> runs)
        {
            if (!_reader.IsFastSymlinkOrSpecial(inode))
            {
                foreach (var e in _reader.MapFile(inode))
                {
                    runs.Add((e.Physical, e.Count));
                }

                foreach (long b in _reader.ExtentTreeBlocks(inode))
                {
                    runs.Add((b, 1));
                }
            }

            if (inode.FileAcl != 0)
            {
                long block = inode.FileAcl;
                var data = _io.ReadBlock(block);
                uint refs = BinaryHelpers.U32(data, 4);
                if (refs > 1)
                {
                    BinaryHelpers.PutU32(data, 4, refs - 1);
                    if (_sb.HasMetadataCsum)
                    {
                        BinaryHelpers.PutU32(data, 0x10, Ext4Csum.XattrBlock(_sb, block, data));
                    }

                    _io.Phase = WritePhase.Free;
                    _io.WriteBlock(block, data);
                }
                else
                {
                    runs.Add((block, 1));
                }
            }
        }

        // ------------------------------------------------------------------------- renaming

        /// <summary>Changes the name of an entry inside its directory.</summary>
        public void Rename(string path, string newName)
        {
            CheckName(newName);
            string trimmed = path.TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            if (trimmed.Length == 0 || slash < 0)
            {
                throw new EditException(EditError.IsRoot, path);
            }

            string parentPath = slash == 0 ? "/" : trimmed.Substring(0, slash);
            string oldName = trimmed.Substring(slash + 1);
            var parent = ResolveDirectory(parentPath);
            var target = Resolve(trimmed);
            if (parentPath == "/" && oldName == "lost+found")
            {
                throw new EditException(EditError.Protected, path);
            }

            if (oldName == newName)
            {
                return;
            }

            RequireNoEntry(parent, newName);
            byte type = Ext4DirEntry.FileTypeFor(target.Type);
            AddEntry(parent, newName, target.Number, type);
            RemoveEntry(parent, oldName);
            TouchDirectory(parent, WritePhase.Link);
            SetTimes(target, ctime: true, mtime: false);
            StoreInode(target, WritePhase.Inode);
            _alloc.Commit(WritePhase.Counters);
        }

        // ------------------------------------------------------------------------- directory entries

        private Ext4Inode ResolveDirectory(string path)
        {
            var dir = Resolve(path);
            if (!dir.IsDirectory)
            {
                throw new EditException(EditError.NotDirectory, path);
            }

            return dir;
        }

        private static void CheckName(string name)
        {
            var bad = EditNames.Validate(name);
            if (bad.HasValue)
            {
                throw new EditException(bad.Value, name);
            }
        }

        private void RequireNoEntry(Ext4Inode dir, string name)
        {
            byte[] wanted = Encoding.UTF8.GetBytes(name);
            if (_reader.ReadDirectory(dir).Any(e => e.Name.SequenceEqual(wanted)))
            {
                throw new EditException(EditError.NameExists, name);
            }
        }

        private List<DirBlock> LoadDirectory(Ext4Inode dir)
        {
            long blocks = (dir.Size + _bs - 1) / _bs;
            var list = new List<DirBlock>();
            foreach (var e in _reader.MapFile(dir))
            {
                for (int i = 0; i < e.Count && e.Logical + i < blocks; i++)
                {
                    list.Add(new DirBlock { Logical = e.Logical + i, Physical = e.Physical + i, Data = _io.ReadBlock(e.Physical + i) });
                }
            }

            list.Sort((a, b) => a.Logical.CompareTo(b.Logical));
            return list;
        }

        private void AddEntry(Ext4Inode dir, string name, uint ino, byte type)
        {
            if ((dir.Flags & Ext4Inode.FlagIndex) != 0)
            {
                Linearize(dir);
            }

            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            int need = (8 + nameBytes.Length + 3) & ~3;
            byte fileType = _sb.HasFileType ? type : (byte)0;
            var blocks = LoadDirectory(dir);
            foreach (var b in blocks)
            {
                int off = 0;
                while (off < DirLimit)
                {
                    uint eIno = BinaryHelpers.U32(b.Data, off);
                    int rec = BinaryHelpers.U16(b.Data, off + 4);
                    int used = eIno == 0 ? 0 : (8 + b.Data[off + 6] + 3) & ~3;
                    if (rec >= 8 && rec - used >= need)
                    {
                        if (eIno == 0)
                        {
                            WriteEntry(b.Data, off, ino, rec, fileType, nameBytes);
                        }
                        else
                        {
                            BinaryHelpers.PutU16(b.Data, off + 4, used);
                            WriteEntry(b.Data, off + used, ino, rec - used, fileType, nameBytes);
                        }

                        SealBlock(b.Data, dir);
                        _io.Phase = WritePhase.Link;
                        _io.WriteBlock(b.Physical, b.Data);
                        TouchDirectory(dir, WritePhase.Link);
                        return;
                    }

                    if (rec < 8)
                    {
                        break;
                    }

                    off += rec;
                }
            }

            // No room: a new block at the end of the directory
            long newBlock = _alloc.AllocateOneBlock(_alloc.GroupOfInode(dir.Number));
            var data = new byte[_bs];
            WriteEntry(data, 0, ino, DirLimit, fileType, nameBytes);
            SealBlock(data, dir);
            _io.Phase = WritePhase.Data;
            _io.WriteBlock(newBlock, data);
            _alloc.Commit(WritePhase.Allocation);

            var extents = _reader.MapFile(dir);
            long logical = (dir.Size + _bs - 1) / _bs;
            var last = extents.Count > 0 ? extents[extents.Count - 1] : default(Ext4Extent);
            if (extents.Count > 0 && last.Physical + last.Count == newBlock && last.Logical + last.Count == logical && last.Count < MaxExtentLength)
            {
                extents[extents.Count - 1] = new Ext4Extent(last.Logical, last.Physical, last.Count + 1, false);
            }
            else
            {
                extents.Add(new Ext4Extent(logical, newBlock, 1, false));
            }

            RewriteExtents(dir, extents);
            dir.Size += _bs;
            TouchDirectory(dir, WritePhase.Link);
        }

        private void RemoveEntry(Ext4Inode dir, string name)
        {
            byte[] wanted = Encoding.UTF8.GetBytes(name);
            foreach (var b in LoadDirectory(dir))
            {
                int off = 0;
                int prev = -1;
                while (off < DirLimit)
                {
                    uint eIno = BinaryHelpers.U32(b.Data, off);
                    int rec = BinaryHelpers.U16(b.Data, off + 4);
                    int nlen = b.Data[off + 6];
                    if (rec < 8)
                    {
                        break;
                    }

                    bool match = eIno != 0 && nlen == wanted.Length && !(nlen <= 2 && b.Data[off + 8] == (byte)'.');
                    if (match)
                    {
                        for (int i = 0; i < nlen && match; i++)
                        {
                            match = b.Data[off + 8 + i] == wanted[i];
                        }
                    }

                    if (match)
                    {
                        if (prev >= 0)
                        {
                            BinaryHelpers.PutU16(b.Data, prev + 4, BinaryHelpers.U16(b.Data, prev + 4) + rec);
                        }
                        else
                        {
                            BinaryHelpers.PutU32(b.Data, off, 0);
                        }

                        SealBlock(b.Data, dir);
                        _io.Phase = WritePhase.Link;
                        _io.WriteBlock(b.Physical, b.Data);
                        return;
                    }

                    prev = off;
                    off += rec;
                }
            }

            throw new EditException(EditError.NotFound, name);
        }

        /// <summary>Rewrites an indexed (htree) directory as a plain list of entries and clears the index flag (research R-4).</summary>
        private void Linearize(Ext4Inode dir)
        {
            var entries = _reader.ReadDirectory(dir);
            var blocks = LoadDirectory(dir);
            uint parent = entries.FirstOrDefault(e => e.IsDotDot)?.Inode ?? 2;
            int limit = DirLimit;
            var packed = new List<byte[]>();
            byte[] cur = null;
            int used = 0;
            int lastOff = -1;
            var all = new List<(uint Ino, byte Type, byte[] Name)>
            {
                (dir.Number, (byte)(_sb.HasFileType ? 2 : 0), new[] { (byte)'.' }),
                (parent, (byte)(_sb.HasFileType ? 2 : 0), new[] { (byte)'.', (byte)'.' })
            };
            foreach (var e in entries)
            {
                if (!e.IsDot && !e.IsDotDot)
                {
                    all.Add((e.Inode, e.FileType, e.Name));
                }
            }

            foreach (var (ino, type, name) in all)
            {
                int size = (8 + name.Length + 3) & ~3;
                if (cur == null || used + size > limit)
                {
                    if (cur != null)
                    {
                        BinaryHelpers.PutU16(cur, lastOff + 4, limit - lastOff);
                    }

                    cur = new byte[_bs];
                    packed.Add(cur);
                    used = 0;
                }

                WriteEntry(cur, used, ino, size, type, name);
                lastOff = used;
                used += size;
            }

            BinaryHelpers.PutU16(cur, lastOff + 4, limit - lastOff);
            if (packed.Count > blocks.Count)
            {
                throw new EditException(EditError.TooFragmented, "directory grew while being rewritten");
            }

            while (packed.Count < blocks.Count)
            {
                var empty = new byte[_bs];
                BinaryHelpers.PutU16(empty, 4, limit);
                packed.Add(empty);
            }

            _io.Phase = WritePhase.Link;
            for (int i = 0; i < blocks.Count; i++)
            {
                SealBlock(packed[i], dir);
                _io.WriteBlock(blocks[i].Physical, packed[i]);
            }

            dir.Flags &= ~Ext4Inode.FlagIndex;
            StoreInode(dir, WritePhase.Link);
        }

        private void TouchDirectory(Ext4Inode dir, WritePhase phase)
        {
            SetTimes(dir, ctime: true, mtime: true);
            SetBlockCount(dir, (dir.Size / _bs) + _reader.ExtentTreeBlocks(dir).Count);
            StoreInode(dir, phase);
        }

        private void WriteEntry(byte[] block, int offset, uint ino, int recLen, byte type, byte[] name)
        {
            BinaryHelpers.PutU32(block, offset, ino);
            BinaryHelpers.PutU16(block, offset + 4, recLen);
            block[offset + 6] = (byte)name.Length;
            block[offset + 7] = type;
            Buffer.BlockCopy(name, 0, block, offset + 8, name.Length);
        }

        /// <summary>The directory entry tail with its checksum (metadata_csum only).</summary>
        private void SealBlock(byte[] block, Ext4Inode dir)
        {
            if (!_sb.HasMetadataCsum)
            {
                return;
            }

            int t = _bs - 12;
            BinaryHelpers.PutU32(block, t, 0);
            BinaryHelpers.PutU16(block, t + 4, 12);
            block[t + 6] = 0;
            block[t + 7] = 0xDE;
            BinaryHelpers.PutU32(block, t + 8, Crc32c.Append(dir.ChecksumSeed(_sb), block, 0, t));
        }

        // ------------------------------------------------------------------------- new inodes and extent rewrites

        private Ext4Inode NewInode(uint number, int mode, Ext4Inode parent)
        {
            var raw = new byte[_sb.InodeSize];
            var inode = new Ext4Inode(number, raw);
            inode.Mode = (ushort)mode;
            BinaryHelpers.PutU16(raw, 0x02, (ushort)parent.Uid);
            BinaryHelpers.PutU16(raw, 0x78, (ushort)(parent.Uid >> 16));
            BinaryHelpers.PutU16(raw, 0x18, (ushort)parent.Gid);
            BinaryHelpers.PutU16(raw, 0x7A, (ushort)(parent.Gid >> 16));
            uint now = _alloc.Now;
            BinaryHelpers.PutU32(raw, 0x08, now);
            BinaryHelpers.PutU32(raw, 0x0C, now);
            BinaryHelpers.PutU32(raw, 0x10, now);
            BinaryHelpers.PutU32(raw, 0x64, (uint)new Random().Next());
            inode.Flags = Ext4Inode.FlagExtents;
            if (_sb.InodeSize > 128)
            {
                int extra = Math.Min(32, _sb.InodeSize - 128);
                BinaryHelpers.PutU16(raw, 0x80, extra);
                if (extra >= 24)
                {
                    BinaryHelpers.PutU32(raw, 0x90, now);
                }
            }

            return inode;
        }

        /// <summary>Replaces an inode's extent list, reusing or freeing the old tree block (a leaf at most, research R-6).</summary>
        private void RewriteExtents(Ext4Inode inode, List<Ext4Extent> extents)
        {
            var oldTree = _reader.ExtentTreeBlocks(inode);
            if (extents.Count > (_bs - 12) / 12)
            {
                throw new EditException(EditError.TooFragmented);
            }

            long leaf = 0;
            if (extents.Count > 4)
            {
                leaf = oldTree.Count > 0 ? oldTree[0] : _alloc.AllocateOneBlock(_alloc.GroupOfInode(inode.Number));
            }

            _io.Phase = WritePhase.Data;
            if (leaf != 0)
            {
                WriteLeaf(inode, leaf, extents);
            }

            _alloc.Commit(WritePhase.Allocation);
            SetExtentRoot(inode, extents, leaf);
            var surplus = oldTree.Skip(leaf != 0 && oldTree.Count > 0 ? 1 : 0).Select(b => (b, 1)).ToList();
            if (surplus.Count > 0)
            {
                _alloc.FreeBlocks(surplus);
            }
        }
    }
}
