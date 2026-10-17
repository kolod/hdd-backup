using System;
using System.Collections.Generic;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>The file system blocks recovered from the journal (research R-2).</summary>
    internal sealed class JournalOverlay
    {
        public static readonly JournalOverlay Empty = new JournalOverlay(new Dictionary<long, byte[]>(), 0, 0, true);

        public JournalOverlay(IReadOnlyDictionary<long, byte[]> blocks, int replayed, int discarded, bool wasEmpty)
        {
            Blocks = blocks;
            Replayed = replayed;
            Discarded = discarded;
            WasEmpty = wasEmpty;
        }

        /// <summary>File system block number → newest complete, not revoked content.</summary>
        public IReadOnlyDictionary<long, byte[]> Blocks { get; }

        /// <summary>Complete transactions applied.</summary>
        public int Replayed { get; }

        /// <summary>1 when the journal ended in an incomplete or damaged transaction that was not applied.</summary>
        public int Discarded { get; }

        public bool WasEmpty { get; }
    }

    /// <summary>
    /// Reads an ext4 internal journal (jbd2) and computes what recovery would write, without
    /// writing anything: the same three passes as Linux recovery (find the end of the log, collect
    /// revoke records, replay complete transactions), implemented from the jbd2 on-disk format
    /// description. A transaction counts only with its commit block and, when the journal has
    /// checksums, with all checksums correct; the first incomplete or damaged one ends the log.
    /// </summary>
    internal sealed class Jbd2Journal
    {
        private const uint Magic = 0xC03B3998;
        private const int TypeDescriptor = 1;
        private const int TypeCommit = 2;
        private const int TypeSuperblockV1 = 3;
        private const int TypeSuperblockV2 = 4;
        private const int TypeRevoke = 5;

        private const uint IncompatRevoke = 0x1;
        private const uint Incompat64Bit = 0x2;
        private const uint IncompatAsyncCommit = 0x4;
        private const uint IncompatCsumV2 = 0x8;
        private const uint IncompatCsumV3 = 0x10;
        private const uint IncompatFastCommit = 0x20;
        private const uint KnownIncompat = IncompatRevoke | Incompat64Bit | IncompatAsyncCommit | IncompatCsumV2 | IncompatCsumV3 | IncompatFastCommit;

        private const uint FlagEscape = 0x1;
        private const uint FlagSameUuid = 0x2;
        private const uint FlagLastTag = 0x8;

        private readonly Ext4Reader _reader;
        private readonly long[] _map; // journal block → file system block
        private readonly int _bs;
        private readonly uint _first;
        private readonly uint _last;
        private readonly uint _sequence;
        private readonly uint _start;
        private readonly uint _incompat;
        private readonly uint _seed;

        private Jbd2Journal(Ext4Reader reader, long[] map, byte[] sb)
        {
            _reader = reader;
            _map = map;
            _bs = reader.BlockSize;
            int type = (int)BinaryHelpers.U32BE(sb, 4);
            uint blockSize = BinaryHelpers.U32BE(sb, 0x0C);
            uint maxLen = BinaryHelpers.U32BE(sb, 0x10);
            _first = BinaryHelpers.U32BE(sb, 0x14);
            _sequence = BinaryHelpers.U32BE(sb, 0x18);
            _start = BinaryHelpers.U32BE(sb, 0x1C);
            _incompat = type == TypeSuperblockV2 ? BinaryHelpers.U32BE(sb, 0x28) : 0;
            if (blockSize != _bs)
            {
                throw new Ext4FormatException("Journal block size " + blockSize + " differs from the file system block size.");
            }

            if (maxLen > map.Length || _first == 0 || _first >= maxLen)
            {
                throw new Ext4FormatException("Journal superblock geometry is invalid.");
            }

            if ((_incompat & ~KnownIncompat) != 0)
            {
                throw new Ext4FormatException("Journal has unknown incompatible features 0x" + (_incompat & ~KnownIncompat).ToString("X") + ".");
            }

            _last = maxLen;
            if ((_incompat & IncompatFastCommit) != 0)
            {
                uint fastCommitBlocks = BinaryHelpers.U32BE(sb, 0x54);
                _last = maxLen - (fastCommitBlocks == 0 ? 256u : fastCommitBlocks);
            }

            if (HasCsum)
            {
                var uuid = new byte[16];
                Buffer.BlockCopy(sb, 0x30, uuid, 0, 16);
                _seed = Crc32c.Append(~0u, uuid, 0, 16);
            }
        }

        private bool HasCsum => (_incompat & (IncompatCsumV2 | IncompatCsumV3)) != 0;

        private bool Has64Bit => (_incompat & Incompat64Bit) != 0;

        /// <summary>Opens the internal journal of the file system read by <paramref name="reader"/>.</summary>
        public static Jbd2Journal Open(Ext4Reader reader)
        {
            var sb = reader.Superblock;
            if (!sb.HasJournal)
            {
                return null;
            }

            if (sb.JournalInode == 0 || (sb.FeatureIncompat & Ext4Features.IncompatJournalDev) != 0 || sb.JournalDevice != 0)
            {
                throw new Ext4FormatException("External journals are not supported.");
            }

            var inode = reader.ReadInode(sb.JournalInode);
            long blocks = (inode.Size + reader.BlockSize - 1) / reader.BlockSize;
            var map = new long[blocks];
            for (long i = 0; i < blocks; i++)
            {
                map[i] = -1;
            }

            foreach (var extent in reader.MapFile(inode))
            {
                for (int i = 0; i < extent.Count && extent.Logical + i < blocks; i++)
                {
                    map[extent.Logical + i] = extent.Physical + i;
                }
            }

            if (blocks == 0 || map[0] < 0)
            {
                throw new Ext4FormatException("Journal inode has no blocks.");
            }

            var jsb = reader.ReadBlock(map[0]);
            int type = (int)BinaryHelpers.U32BE(jsb, 4);
            if (BinaryHelpers.U32BE(jsb, 0) != Magic || (type != TypeSuperblockV1 && type != TypeSuperblockV2))
            {
                throw new Ext4FormatException("Journal superblock magic is invalid.");
            }

            return new Jbd2Journal(reader, map, jsb);
        }

        public JournalOverlay Replay()
        {
            if (_start == 0)
            {
                return JournalOverlay.Empty;
            }

            if ((_incompat & IncompatFastCommit) != 0)
            {
                // Fast-commit blocks would have to be applied after the full commits; not supported.
                throw new Ext4FormatException("Journals with fast commits are not supported.");
            }

            var transactions = Scan(out bool damagedTail);
            var revoked = new Dictionary<long, uint>();
            foreach (var t in transactions)
            {
                foreach (var block in t.Revoked)
                {
                    if (!revoked.TryGetValue(block, out uint seq) || SequenceAfter(t.Sequence, seq))
                    {
                        revoked[block] = t.Sequence;
                    }
                }
            }

            var overlay = new Dictionary<long, byte[]>();
            foreach (var t in transactions)
            {
                foreach (var tag in t.Tags)
                {
                    if (revoked.TryGetValue(tag.FsBlock, out uint revokedIn) && !SequenceAfter(t.Sequence, revokedIn))
                    {
                        continue; // revoked in this or a later transaction
                    }

                    var data = ReadJournalBlock(tag.JournalBlock);
                    if ((tag.Flags & FlagEscape) != 0)
                    {
                        BinaryHelpers.PutU32BE(data, 0, Magic);
                    }

                    overlay[tag.FsBlock] = data;
                }
            }

            return new JournalOverlay(overlay, transactions.Count, damagedTail ? 1 : 0, false);
        }

        /// <summary>True when <paramref name="a"/> is a later transaction than <paramref name="b"/> (wrap-around safe).</summary>
        private static bool SequenceAfter(uint a, uint b) => (int)(a - b) > 0;

        private List<Transaction> Scan(out bool damagedTail)
        {
            var complete = new List<Transaction>();
            uint expected = _sequence;
            uint position = _start;
            Transaction current = null;
            damagedTail = false;
            for (long guard = 0; guard <= _last; guard++)
            {
                var block = ReadJournalBlock(position);
                if (BinaryHelpers.U32BE(block, 0) != Magic || BinaryHelpers.U32BE(block, 8) != expected)
                {
                    break; // end of the log
                }

                int type = (int)BinaryHelpers.U32BE(block, 4);
                if (current == null)
                {
                    current = new Transaction(expected);
                }

                if (type == TypeDescriptor)
                {
                    if (HasCsum && !TailChecksumMatches(block))
                    {
                        damagedTail = true;
                        break;
                    }

                    uint next = Advance(position, 1);
                    if (!ReadTags(block, current, ref next))
                    {
                        damagedTail = true;
                        break;
                    }

                    position = next;
                    continue;
                }

                if (type == TypeRevoke)
                {
                    if (HasCsum && !TailChecksumMatches(block))
                    {
                        damagedTail = true;
                        break;
                    }

                    ReadRevokes(block, current);
                    position = Advance(position, 1);
                    continue;
                }

                if (type == TypeCommit)
                {
                    if (HasCsum && !CommitChecksumMatches(block))
                    {
                        damagedTail = true;
                        break;
                    }

                    complete.Add(current);
                    current = null;
                    expected++;
                    position = Advance(position, 1);
                    continue;
                }

                break; // unknown block type ends the log
            }

            if (current != null && (current.Tags.Count > 0 || current.Revoked.Count > 0))
            {
                damagedTail = true; // started but never committed
            }

            return complete;
        }

        private bool ReadTags(byte[] block, Transaction t, ref uint next)
        {
            bool v3 = (_incompat & IncompatCsumV3) != 0;
            bool v2 = (_incompat & IncompatCsumV2) != 0;
            int tagSize = v3 ? 16 : (Has64Bit ? 12 : 8) + (v2 ? 2 : 0);
            int end = _bs - (HasCsum ? 4 : 0);
            int offset = 12;
            while (offset + tagSize <= end)
            {
                long fsBlock = BinaryHelpers.U32BE(block, offset);
                uint flags;
                uint checksum;
                if (v3)
                {
                    flags = BinaryHelpers.U32BE(block, offset + 4);
                    if (Has64Bit)
                    {
                        fsBlock |= (long)BinaryHelpers.U32BE(block, offset + 8) << 32;
                    }

                    checksum = BinaryHelpers.U32BE(block, offset + 12);
                }
                else
                {
                    checksum = (uint)((block[offset + 4] << 8) | block[offset + 5]);
                    flags = (uint)((block[offset + 6] << 8) | block[offset + 7]);
                    if (Has64Bit)
                    {
                        fsBlock |= (long)BinaryHelpers.U32BE(block, offset + 8) << 32;
                    }
                }

                offset += tagSize;
                if ((flags & FlagSameUuid) == 0)
                {
                    offset += 16; // the journal UUID follows the first tag
                }

                uint journalBlock = next;
                if (HasCsum && !DataChecksumMatches(ReadJournalBlock(journalBlock), t.Sequence, checksum, v3))
                {
                    return false;
                }

                if (fsBlock < 0 || fsBlock >= _reader.Superblock.BlocksCount)
                {
                    return false;
                }

                t.Tags.Add(new Tag(fsBlock, journalBlock, flags));
                next = Advance(next, 1);
                if ((flags & FlagLastTag) != 0)
                {
                    break;
                }
            }

            return true;
        }

        private void ReadRevokes(byte[] block, Transaction t)
        {
            int count = (int)BinaryHelpers.U32BE(block, 12); // bytes used, header included
            int recordSize = Has64Bit ? 8 : 4;
            int limit = Math.Min(count, _bs - (HasCsum ? 4 : 0));
            for (int offset = 16; offset + recordSize <= limit; offset += recordSize)
            {
                long fsBlock = Has64Bit
                    ? ((long)BinaryHelpers.U32BE(block, offset) << 32) | BinaryHelpers.U32BE(block, offset + 4)
                    : BinaryHelpers.U32BE(block, offset);
                t.Revoked.Add(fsBlock);
            }
        }

        // Descriptor and revoke blocks end with a 4-byte checksum of the block (that field zeroed).
        private bool TailChecksumMatches(byte[] block)
        {
            uint stored = BinaryHelpers.U32BE(block, _bs - 4);
            var copy = (byte[])block.Clone();
            BinaryHelpers.PutU32BE(copy, _bs - 4, 0);
            return Crc32c.Append(_seed, copy, 0, _bs) == stored;
        }

        // Commit blocks keep their checksum in h_chksum[0] (offset 16), zeroed for the computation.
        private bool CommitChecksumMatches(byte[] block)
        {
            uint stored = BinaryHelpers.U32BE(block, 16);
            var copy = (byte[])block.Clone();
            BinaryHelpers.PutU32BE(copy, 16, 0);
            return Crc32c.Append(_seed, copy, 0, _bs) == stored;
        }

        // Data block checksum: crc32c over the big-endian sequence number, then the block as logged.
        private bool DataChecksumMatches(byte[] data, uint sequence, uint stored, bool v3)
        {
            var seq = new byte[4];
            BinaryHelpers.PutU32BE(seq, 0, sequence);
            uint csum = Crc32c.Append(Crc32c.Append(_seed, seq, 0, 4), data, 0, data.Length);
            return v3 ? csum == stored : (csum & 0xFFFF) == stored;
        }

        private uint Advance(uint position, uint count)
        {
            uint next = position + count;
            if (next >= _last)
            {
                next -= _last - _first;
            }

            return next;
        }

        private byte[] ReadJournalBlock(uint journalBlock)
        {
            if (journalBlock >= _map.Length || _map[journalBlock] < 0)
            {
                throw new Ext4FormatException("Journal block " + journalBlock + " is not mapped.");
            }

            return _reader.ReadBlock(_map[journalBlock]);
        }

        private struct Tag
        {
            public Tag(long fsBlock, uint journalBlock, uint flags)
            {
                FsBlock = fsBlock;
                JournalBlock = journalBlock;
                Flags = flags;
            }

            public long FsBlock { get; }

            public uint JournalBlock { get; }

            public uint Flags { get; }
        }

        private sealed class Transaction
        {
            public Transaction(uint sequence)
            {
                Sequence = sequence;
            }

            public uint Sequence { get; }

            public List<Tag> Tags { get; } = new List<Tag>();

            public List<long> Revoked { get; } = new List<long>();
        }
    }
}
