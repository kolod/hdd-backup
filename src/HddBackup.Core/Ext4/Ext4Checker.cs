using System;
using System.Collections.Generic;
using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Structural consistency check of an ext4 file system (a small e2fsck): checksums, block and
    /// inode bitmaps against actual use, free counts, directory structure, link counts, i_blocks,
    /// xattr block reference counts, journal superblock and resize inode. Returns findings; an
    /// empty list means consistent.
    /// </summary>
    internal static class Ext4Checker
    {
        private const int MaxFindings = 50;

        public static List<string> Check(IBlockDevice partition)
        {
            var findings = new List<string>();
            try
            {
                using (var reader = new Ext4Reader(partition))
                {
                    new Run(reader, findings).Execute();
                }
            }
            catch (Ext4FormatException ex)
            {
                findings.Add(ex.Message);
            }

            return findings;
        }

        private sealed class Run
        {
            private readonly Ext4Reader _r;
            private readonly Ext4Superblock _sb;
            private readonly List<string> _findings;
            private readonly int _bs;
            private readonly ulong[] _claimed;
            private readonly Dictionary<uint, int> _linkRefs = new Dictionary<uint, int>();
            private readonly Dictionary<uint, int> _subdirs = new Dictionary<uint, int>();
            private readonly Dictionary<long, int> _xattrRefs = new Dictionary<long, int>();
            private readonly HashSet<uint> _reachable = new HashSet<uint>();
            private readonly int[] _dirsPerGroup;

            public Run(Ext4Reader reader, List<string> findings)
            {
                _r = reader;
                _sb = reader.Superblock;
                _findings = findings;
                _bs = reader.BlockSize;
                _claimed = new ulong[(_sb.BlocksCount + 63) / 64];
                _dirsPerGroup = new int[reader.GroupCount];
            }

            public void Execute()
            {
                if ((long)_r.GroupCount * _sb.InodesPerGroup != _sb.InodesCount)
                {
                    Add("Inode count does not match groups × inodes per group.");
                }

                ClaimMetadata();
                WalkTree();
                CheckReservedInodes();
                CheckInodeBitmaps();
                CheckBlockBitmaps();
                CheckXattrRefs();
            }

            private void Add(string finding)
            {
                if (_findings.Count < MaxFindings)
                {
                    _findings.Add(finding);
                }
            }

            private void Claim(long block, long count, string owner)
            {
                for (long b = block; b < block + count; b++)
                {
                    if (b < _sb.FirstDataBlock || b >= _sb.BlocksCount)
                    {
                        Add(owner + " uses block " + b + " outside the file system.");
                        return;
                    }

                    ulong bit = 1UL << (int)(b & 63);
                    if ((_claimed[b >> 6] & bit) != 0)
                    {
                        Add("Block " + b + " is used twice (" + owner + ").");
                        return;
                    }

                    _claimed[b >> 6] |= bit;
                }
            }

            private bool IsClaimed(long b) => (_claimed[b >> 6] & (1UL << (int)(b & 63))) != 0;

            private static bool HasSuper(int g)
            {
                if (g <= 1)
                {
                    return true;
                }

                foreach (int p in new[] { 3, 5, 7 })
                {
                    long v = p;
                    while (v < g)
                    {
                        v *= p;
                    }

                    if (v == g)
                    {
                        return true;
                    }
                }

                return false;
            }

            private void ClaimMetadata()
            {
                int descPerBlock = _bs / _sb.DescriptorSize;
                int gdtBlocks = (_r.GroupCount + descPerBlock - 1) / descPerBlock;
                long itableBlocks = ((long)_sb.InodesPerGroup * _sb.InodeSize + _bs - 1) / _bs;
                for (int g = 0; g < _r.GroupCount; g++)
                {
                    long start = _sb.FirstDataBlock + ((long)g * _sb.BlocksPerGroup);
                    if (HasSuper(g))
                    {
                        Claim(start, 1 + gdtBlocks + _sb.ReservedGdtBlocks, "superblock area of group " + g);
                    }

                    var d = _r.Groups[g];
                    Claim(d.BlockBitmap, 1, "block bitmap " + g);
                    Claim(d.InodeBitmap, 1, "inode bitmap " + g);
                    Claim(d.InodeTable, itableBlocks, "inode table " + g);
                }
            }

            private void WalkTree()
            {
                var queue = new Queue<(uint Ino, uint Parent)>();
                queue.Enqueue((Ext4CopyPlan.RootInode, Ext4CopyPlan.RootInode));
                _reachable.Add(Ext4CopyPlan.RootInode);
                ClaimInode(_r.ReadInode(Ext4CopyPlan.RootInode));

                while (queue.Count > 0)
                {
                    var (ino, parent) = queue.Dequeue();
                    var dir = _r.ReadInode(ino);
                    _dirsPerGroup[(ino - 1) / _sb.InodesPerGroup]++;
                    var entries = _r.ReadDirectory(dir);
                    if (entries.Count < 2 || !entries[0].IsDot || entries[0].Inode != ino || !entries[1].IsDotDot || entries[1].Inode != parent)
                    {
                        Add("Directory " + ino + " does not start with correct '.' and '..'.");
                    }

                    int subdirs = 0;
                    foreach (var e in entries)
                    {
                        if (e.IsDot || e.IsDotDot)
                        {
                            continue;
                        }

                        if (e.Inode < 1 || e.Inode > _sb.InodesCount)
                        {
                            Add("Directory " + ino + " entry '" + e.NameText + "' has an invalid inode.");
                            continue;
                        }

                        if (!_r.IsInodeInUse(e.Inode))
                        {
                            Add("Directory " + ino + " entry '" + e.NameText + "' points to free inode " + e.Inode + ".");
                        }

                        var child = _r.ReadInode(e.Inode);
                        if (_sb.HasFileType && e.FileType != Ext4DirEntry.FileTypeFor(child.Type))
                        {
                            Add("Directory " + ino + " entry '" + e.NameText + "' has a wrong file type.");
                        }

                        _linkRefs.TryGetValue(e.Inode, out int refs);
                        _linkRefs[e.Inode] = refs + 1;
                        if (_reachable.Add(e.Inode))
                        {
                            ClaimInode(child);
                            if (child.IsDirectory)
                            {
                                queue.Enqueue((e.Inode, ino));
                            }
                        }
                        else if (child.IsDirectory)
                        {
                            Add("Directory inode " + e.Inode + " is linked more than once.");
                        }

                        if (child.IsDirectory)
                        {
                            subdirs++;
                        }
                    }

                    _subdirs[ino] = subdirs;
                }

                foreach (var ino in _reachable)
                {
                    var inode = _r.ReadInode(ino);
                    int expected;
                    if (inode.IsDirectory)
                    {
                        _subdirs.TryGetValue(ino, out int subs);
                        expected = 2 + subs;
                        if (inode.LinksCount == 1 && _sb.HasDirNlink && expected > 65000)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        _linkRefs.TryGetValue(ino, out expected);
                    }

                    if (inode.LinksCount != expected)
                    {
                        Add("Inode " + ino + " link count " + inode.LinksCount + " should be " + expected + ".");
                    }
                }
            }

            private void ClaimInode(Ext4Inode inode)
            {
                string owner = "inode " + inode.Number;
                long blocks = 0;
                if (!_r.IsFastSymlinkOrSpecial(inode))
                {
                    foreach (var e in _r.MapFile(inode))
                    {
                        Claim(e.Physical, e.Count, owner);
                        blocks += e.Count;
                    }

                    foreach (var t in _r.ExtentTreeBlocks(inode))
                    {
                        Claim(t, 1, owner + " extent tree");
                        blocks++;
                    }
                }

                if (inode.FileAcl != 0)
                {
                    _r.ReadXattrs(inode); // verifies magic and checksum
                    if (_xattrRefs.TryGetValue(inode.FileAcl, out int refs))
                    {
                        _xattrRefs[inode.FileAcl] = refs + 1;
                    }
                    else
                    {
                        _xattrRefs[inode.FileAcl] = 1;
                        Claim(inode.FileAcl, 1, owner + " xattr block");
                    }

                    blocks++;
                }

                if (inode.AllocatedBlocks(_sb) != blocks)
                {
                    Add("Inode " + inode.Number + " i_blocks " + inode.AllocatedBlocks(_sb) + " should be " + blocks + ".");
                }

                if (inode.IsDirectory)
                {
                    long end = 0;
                    foreach (var e in _r.MapFile(inode))
                    {
                        end = Math.Max(end, e.Logical + e.Count);
                    }

                    if (inode.Size != end * _bs)
                    {
                        Add("Directory " + inode.Number + " size " + inode.Size + " does not match its blocks.");
                    }
                }
            }

            private void CheckReservedInodes()
            {
                if (_sb.HasJournal)
                {
                    var journal = _r.ReadInode(_sb.JournalInode);
                    ClaimInode(journal);
                    var map = _r.MapFile(journal);
                    if (map.Count == 0)
                    {
                        Add("Journal inode has no blocks.");
                    }
                    else
                    {
                        var jsb = _r.ReadBlock(map[0].Physical);
                        if (BinaryHelpers.U32BE(jsb, 0) != 0xC03B3998)
                        {
                            Add("Journal superblock magic is invalid.");
                        }
                        else if (BinaryHelpers.U32BE(jsb, 0x10) != journal.Size / _bs || BinaryHelpers.U32BE(jsb, 0x0C) != _bs)
                        {
                            Add("Journal superblock size does not match the journal inode.");
                        }
                        else if (BinaryHelpers.U32BE(jsb, 0x1C) != 0)
                        {
                            Add("Journal is not empty.");
                        }
                    }

                    for (int i = 0; i < 15; i++)
                    {
                        if (_sb.JournalBackupType == 1 && _sb.GetJournalBackup(i) != BinaryHelpers.U32(journal.Raw, Ext4Inode.BlockArrayOffset + (i * 4)))
                        {
                            Add("Superblock journal backup does not match the journal inode.");
                            break;
                        }
                    }
                }

                if (_sb.HasResizeInode)
                {
                    CheckResizeInode();
                }
            }

            private void CheckResizeInode()
            {
                var inode = _r.ReadInode(Ext4CopyPlan.ResizeInode);
                long dind = BinaryHelpers.U32(inode.Raw, Ext4Inode.BlockArrayOffset + (13 * 4));
                for (int i = 0; i < 15; i++)
                {
                    if (i != 13 && BinaryHelpers.U32(inode.Raw, Ext4Inode.BlockArrayOffset + (i * 4)) != 0)
                    {
                        Add("Resize inode has unexpected block pointers.");
                    }
                }

                if (dind == 0)
                {
                    Add("Resize inode has no double-indirect block.");
                    return;
                }

                Claim(dind, 1, "resize inode");
                var dindData = _r.ReadBlock(dind);
                int apb = _bs / 4;
                int descPerBlock = _bs / _sb.DescriptorSize;
                int gdtBlocks = (_r.GroupCount + descPerBlock - 1) / descPerBlock;
                long expectedBlocks = 1;
                for (int r = 0; r < _sb.ReservedGdtBlocks; r++)
                {
                    long primary = _sb.FirstDataBlock + 1 + gdtBlocks + r;
                    if (BinaryHelpers.U32(dindData, ((gdtBlocks + r) % apb) * 4) != primary)
                    {
                        Add("Resize inode does not reference reserved GDT block " + primary + ".");
                        return;
                    }

                    var list = _r.ReadBlock(primary);
                    int k = 0;
                    expectedBlocks++;
                    for (int g = 1; g < _r.GroupCount; g++)
                    {
                        if (!HasSuper(g))
                        {
                            continue;
                        }

                        expectedBlocks++;
                        if (k < apb && BinaryHelpers.U32(list, k++ * 4) != primary + ((long)g * _sb.BlocksPerGroup))
                        {
                            Add("Reserved GDT block " + primary + " lists a wrong backup location.");
                            return;
                        }
                    }
                }

                if (inode.AllocatedBlocks(_sb) != expectedBlocks)
                {
                    Add("Resize inode i_blocks " + inode.AllocatedBlocks(_sb) + " should be " + expectedBlocks + ".");
                }
            }

            private void CheckInodeBitmaps()
            {
                long freeTotal = 0;
                for (int g = 0; g < _r.GroupCount; g++)
                {
                    uint free = 0;
                    for (uint i = 0; i < _sb.InodesPerGroup; i++)
                    {
                        uint ino = ((uint)g * _sb.InodesPerGroup) + i + 1;
                        bool expected = ino < _sb.FirstInode || _reachable.Contains(ino);
                        bool actual = _r.IsInodeInUse(ino);
                        if (expected != actual)
                        {
                            Add("Inode bitmap: inode " + ino + " is " + (actual ? "marked used but unreachable" : "in use but marked free") + ".");
                        }

                        if (!actual)
                        {
                            free++;
                        }
                    }

                    var d = _r.Groups[g];
                    if (d.FreeInodes != free)
                    {
                        Add("Group " + g + " free inodes " + d.FreeInodes + " should be " + free + ".");
                    }

                    if (d.UsedDirs != (uint)_dirsPerGroup[g])
                    {
                        Add("Group " + g + " directory count " + d.UsedDirs + " should be " + _dirsPerGroup[g] + ".");
                    }

                    if ((_sb.HasMetadataCsum || _sb.HasGdtCsum) && (d.Flags & Ext4GroupDescriptor.FlagInodeUninit) == 0)
                    {
                        if (_sb.HasMetadataCsum)
                        {
                            var bitmap = _r.ReadBlock(d.InodeBitmap);
                            uint csum = Ext4Csum.Bitmap(_sb, bitmap, (int)(_sb.InodesPerGroup / 8));
                            if ((d.InodeBitmapChecksum & Mask()) != (csum & Mask()))
                            {
                                Add("Group " + g + " inode bitmap checksum mismatch.");
                            }
                        }
                    }

                    freeTotal += free;
                }

                if (_sb.FreeInodesCount != freeTotal)
                {
                    Add("Superblock free inodes " + _sb.FreeInodesCount + " should be " + freeTotal + ".");
                }
            }

            private void CheckBlockBitmaps()
            {
                long freeTotal = 0;
                int reported = 0;
                for (int g = 0; g < _r.GroupCount; g++)
                {
                    var d = _r.Groups[g];
                    if ((d.Flags & Ext4GroupDescriptor.FlagBlockUninit) != 0 && (_sb.HasMetadataCsum || _sb.HasGdtCsum))
                    {
                        // Lazily initialised group (mke2fs): the bitmap block is not authoritative.
                        freeTotal += d.FreeBlocks;
                        continue;
                    }

                    var bitmap = _r.ReadBlock(d.BlockBitmap);
                    long start = _sb.FirstDataBlock + ((long)g * _sb.BlocksPerGroup);
                    long end = Math.Min(start + _sb.BlocksPerGroup, _sb.BlocksCount);
                    uint free = 0;
                    for (long b = start; b < end; b++)
                    {
                        long i = b - start;
                        bool actual = (bitmap[i >> 3] & (1 << (int)(i & 7))) != 0;
                        bool expected = IsClaimed(b);
                        if (actual != expected && reported++ < 10)
                        {
                            Add("Block bitmap: block " + b + " is " + (actual ? "marked used but not owned" : "in use but marked free") + ".");
                        }

                        if (!actual)
                        {
                            free++;
                        }
                    }

                    if (d.FreeBlocks != free)
                    {
                        Add("Group " + g + " free blocks " + d.FreeBlocks + " should be " + free + ".");
                    }

                    if (_sb.HasMetadataCsum)
                    {
                        uint csum = Ext4Csum.Bitmap(_sb, bitmap, (int)(_sb.BlocksPerGroup / 8));
                        if ((d.BlockBitmapChecksum & Mask()) != (csum & Mask()))
                        {
                            Add("Group " + g + " block bitmap checksum mismatch.");
                        }
                    }

                    freeTotal += free;
                }

                if (_sb.FreeBlocksCount != freeTotal)
                {
                    Add("Superblock free blocks " + _sb.FreeBlocksCount + " should be " + freeTotal + ".");
                }
            }

            private void CheckXattrRefs()
            {
                foreach (var kv in _xattrRefs)
                {
                    var block = _r.ReadBlock(kv.Key);
                    uint refcount = BinaryHelpers.U32(block, 4);
                    if (refcount != kv.Value)
                    {
                        Add("Xattr block " + kv.Key + " reference count " + refcount + " should be " + kv.Value + ".");
                    }
                }
            }

            private uint Mask() => _sb.DescriptorSize >= 64 ? 0xFFFFFFFFu : 0xFFFFu;
        }
    }
}
