using System;
using System.Collections.Generic;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// File system geometry equivalent to what mke2fs creates for the same parameters: groups,
    /// sparse superblock backups, group descriptor and reserved GDT blocks, and flex_bg placement of
    /// bitmaps and inode tables (all of a flex group's metadata packed into its first group).
    /// </summary>
    internal sealed class Ext4Layout
    {
        private readonly List<(long Start, long Count)> _metadata = new List<(long, long)>();

        private Ext4Layout()
        {
        }

        public int BlockSize { get; private set; }

        public long BlocksCount { get; private set; }

        public uint FirstDataBlock { get; private set; }

        public uint BlocksPerGroup { get; private set; }

        public int GroupCount { get; private set; }

        public uint InodesPerGroup { get; private set; }

        public int InodeSize { get; private set; }

        public int InodeTableBlocks { get; private set; }

        public int DescriptorSize { get; private set; }

        public int GdtBlocks { get; private set; }

        public int ReservedGdtBlocks { get; private set; }

        public int FlexSize { get; private set; }

        public long[] BlockBitmap { get; private set; }

        public long[] InodeBitmap { get; private set; }

        public long[] InodeTable { get; private set; }

        public long InodesCount => (long)InodesPerGroup * GroupCount;

        /// <summary>All metadata block ranges (superblocks, GDT, reserved GDT, bitmaps, inode tables).</summary>
        public IReadOnlyList<(long Start, long Count)> MetadataExtents => _metadata;

        /// <summary>
        /// Builds the layout. <paramref name="blocksCount"/> may be reduced (a tiny last group is
        /// dropped, like mke2fs does); the final value is <see cref="BlocksCount"/>.
        /// </summary>
        public static Ext4Layout Create(
            long blocksCount, int blockSize, int inodeSize, long inodeRatio, long minInodes,
            int descriptorSize, bool resizeInode, int logGroupsPerFlex)
        {
            if (blockSize != 1024 && blockSize != 2048 && blockSize != 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(blockSize));
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                var layout = new Ext4Layout
                {
                    BlockSize = blockSize,
                    BlocksCount = blocksCount,
                    FirstDataBlock = blockSize == 1024 ? 1u : 0u,
                    BlocksPerGroup = (uint)(8 * blockSize),
                    InodeSize = inodeSize,
                    DescriptorSize = descriptorSize,
                    FlexSize = 1 << logGroupsPerFlex
                };

                long dataBlocks = blocksCount - layout.FirstDataBlock;
                layout.GroupCount = (int)((dataBlocks + layout.BlocksPerGroup - 1) / layout.BlocksPerGroup);
                if (layout.GroupCount < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(blocksCount), "File system too small.");
                }

                layout.ComputeInodes(blocksCount, inodeRatio, minInodes);
                int descPerBlock = blockSize / descriptorSize;
                layout.GdtBlocks = (layout.GroupCount + descPerBlock - 1) / descPerBlock;
                layout.ReservedGdtBlocks = resizeInode ? layout.ComputeReservedGdt(descPerBlock) : 0;

                // Drop a last group too small for its own metadata (mke2fs does the same).
                long rem = dataBlocks % layout.BlocksPerGroup;
                long overhead = 3 + layout.InodeTableBlocks + layout.ReservedGdtBlocks;
                if (layout.HasSuperblock(layout.GroupCount - 1))
                {
                    overhead += 1 + layout.GdtBlocks + layout.ReservedGdtBlocks;
                }

                if (rem != 0 && rem < overhead + 50)
                {
                    if (layout.GroupCount == 1)
                    {
                        throw new ArgumentOutOfRangeException(nameof(blocksCount), "File system too small.");
                    }

                    blocksCount -= rem;
                    continue;
                }

                layout.PlaceMetadata();
                return layout;
            }

            throw new InvalidOperationException("Could not compute a valid layout.");
        }

/// <summary>mke2fs default internal journal size (in blocks) for a file system of this size.</summary>
        public static long DefaultJournalBlocks(long blocksCount)
        {
            if (blocksCount < 2048) return 0;
            if (blocksCount < 32768) return 1024;
            if (blocksCount < 256 * 1024) return 4096;
            if (blocksCount < 512 * 1024) return 8192;
            if (blocksCount < 4096 * 1024) return 16384;
            if (blocksCount < 8192 * 1024) return 32768;
            if (blocksCount < 16384 * 1024) return 65536;
            if (blocksCount < 32768 * 1024) return 131072;
            return 262144;
        }

        public long GroupStart(int group) => FirstDataBlock + ((long)group * BlocksPerGroup);

        public long GroupEnd(int group) => Math.Min(GroupStart(group) + BlocksPerGroup, BlocksCount);

        public int GroupOf(long block) => (int)((block - FirstDataBlock) / BlocksPerGroup);

        public bool HasSuperblock(int group)
        {
            if (group <= 1)
            {
                return true;
            }

            return IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);
        }

        /// <summary>Groups 1.. that carry superblock/GDT backups, in order.</summary>
        public IEnumerable<int> BackupGroups()
        {
            for (int g = 1; g < GroupCount; g++)
            {
                if (HasSuperblock(g))
                {
                    yield return g;
                }
            }
        }

        /// <summary>Block of reserved GDT block <paramref name="index"/> in a group with a superblock.</summary>
        public long ReservedGdtBlock(int group, int index) => GroupStart(group) + 1 + GdtBlocks + index;

        private static bool IsPowerOf(int value, int b)
        {
            long p = b;
            while (p < value)
            {
                p *= b;
            }

            return p == value;
        }

        private void ComputeInodes(long blocksCount, long inodeRatio, long minInodes)
        {
            long wanted = Math.Max(blocksCount * BlockSize / inodeRatio, minInodes);
            long ipg = (wanted + GroupCount - 1) / GroupCount;
            ipg = Math.Max(ipg, 16);
            int perBlock = BlockSize / InodeSize;
            long tableBlocks = ((ipg * InodeSize) + BlockSize - 1) / BlockSize;
            ipg = tableBlocks * perBlock;
            ipg &= ~7L;
            if (ipg > 8L * BlockSize)
            {
                ipg = 8L * BlockSize;
            }

            InodesPerGroup = (uint)ipg;
            InodeTableBlocks = (int)(((ipg * InodeSize) + BlockSize - 1) / BlockSize);
        }

        private int ComputeReservedGdt(int descPerBlock)
        {
            ulong maxBlocks = 0xFFFFFFFFUL;
            if ((ulong)BlocksCount < maxBlocks / 1024)
            {
                maxBlocks = (ulong)BlocksCount * 1024;
            }

            ulong rsvGroups = (maxBlocks - FirstDataBlock + BlocksPerGroup - 1) / BlocksPerGroup;
            long rsv = (long)((rsvGroups + (ulong)descPerBlock - 1) / (ulong)descPerBlock) - GdtBlocks;
            rsv = Math.Max(0, Math.Min(rsv, BlockSize / 4));
            return (int)rsv;
        }

        private void PlaceMetadata()
        {
            BlockBitmap = new long[GroupCount];
            InodeBitmap = new long[GroupCount];
            InodeTable = new long[GroupCount];

            for (int g = 0; g < GroupCount; g++)
            {
                if (HasSuperblock(g))
                {
                    _metadata.Add((GroupStart(g), 1 + GdtBlocks + ReservedGdtBlocks));
                }
            }

            for (int g0 = 0; g0 < GroupCount; g0 += FlexSize)
            {
                int rem = Math.Min(FlexSize, GroupCount - g0);

                // mke2fs reserves FlexSize slots per table type, except in the last flex group where
                // it keeps the tables contiguous ((last group & (FlexSize - 1)) + 1, or FlexSize if 0).
                int slots = rem == 1 ? FlexSize : rem;
                long base0 = GroupStart(g0) + (HasSuperblock(g0) ? 1 + GdtBlocks + ReservedGdtBlocks : 0);
                long needed = (2L * slots) + ((long)rem * InodeTableBlocks);
                if (base0 + needed <= GroupEnd(g0))
                {
                    for (int i = 0; i < rem; i++)
                    {
                        BlockBitmap[g0 + i] = base0 + i;
                        InodeBitmap[g0 + i] = base0 + slots + i;
                        InodeTable[g0 + i] = base0 + (2L * slots) + ((long)i * InodeTableBlocks);
                    }

                    _metadata.Add((base0, rem));
                    _metadata.Add((base0 + slots, rem));
                    _metadata.Add((base0 + (2L * slots), (long)rem * InodeTableBlocks));
                    continue;
                }

                // Very large inode tables: spill sequentially, skipping backup superblock areas.
                long cursor = base0;
                for (int i = 0; i < rem; i++)
                {
                    BlockBitmap[g0 + i] = Take(ref cursor, 1);
                }

                for (int i = 0; i < rem; i++)
                {
                    InodeBitmap[g0 + i] = Take(ref cursor, 1);
                }

                for (int i = 0; i < rem; i++)
                {
                    InodeTable[g0 + i] = Take(ref cursor, InodeTableBlocks);
                }
            }
        }

        /// <summary>Takes a contiguous run at the cursor, skipping superblock backup areas.</summary>
        private long Take(ref long cursor, int count)
        {
            while (true)
            {
                if (cursor + count > BlocksCount)
                {
                    throw new InvalidOperationException("Metadata does not fit the file system.");
                }

                bool moved = false;
                for (int g = GroupOf(cursor); g <= GroupOf(cursor + count - 1); g++)
                {
                    if (g > 0 && HasSuperblock(g))
                    {
                        long s = GroupStart(g);
                        long e = s + 1 + GdtBlocks + ReservedGdtBlocks;
                        if (cursor < e && cursor + count > s)
                        {
                            cursor = e;
                            moved = true;
                            break;
                        }
                    }
                }

                if (!moved)
                {
                    long start = cursor;
                    cursor += count;
                    _metadata.Add((start, count));
                    return start;
                }
            }
        }
    }
}
