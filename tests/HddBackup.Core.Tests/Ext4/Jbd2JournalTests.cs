using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Partitioning;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class Jbd2JournalTests
    {
        private const uint Magic = 0xC03B3998;

        internal static JournalOverlay ReplayFixture(string name)
        {
            using (var root = FixtureImages.OpenRoot(name, out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                return Jbd2Journal.Open(reader).Replay();
            }
        }

        /// <summary>The fixture's root seen through its replayed journal, compared with the e2fsck reference.</summary>
        internal static List<string> CompareRecoveredWithReference(string name)
        {
            using (var root = FixtureImages.OpenRoot(name, out var image))
            using (image)
            using (var refRoot = FixtureImages.OpenRoot(name + ".reference", out var refImage))
            using (refImage)
            {
                JournalOverlay overlay;
                using (var raw = new Ext4Reader(root))
                {
                    overlay = Jbd2Journal.Open(raw).Replay();
                }

                using (var recovered = new Ext4Reader(new OverlayBlockDevice(root, overlay, 4096)))
                using (var reference = new Ext4Reader(refRoot))
                {
                    return Ext4TreeComparer.Compare(recovered, reference, CancellationToken.None);
                }
            }
        }

        [Fact]
        public void CleanFileSystem_JournalIsEmpty()
        {
            var overlay = ReplayFixture("rpi-like-4k");
            Assert.True(overlay.WasEmpty);
            Assert.Empty(overlay.Blocks);
        }

        [Fact]
        public void DirtyJournal_ReplaysThreeTransactions_TreeEqualsReference()
        {
            var overlay = ReplayFixture("dirty-journal");
            Assert.Equal(3, overlay.Replayed);
            Assert.Equal(0, overlay.Discarded);
            Assert.NotEmpty(overlay.Blocks);
            Assert.Empty(CompareRecoveredWithReference("dirty-journal"));
        }

        [Fact]
        public void DirtyJournal_WithoutReplay_DiffersFromReference()
        {
            // Guards the comparison above: the on-disk (old) tree must not equal the recovered one.
            using (var root = FixtureImages.OpenRoot("dirty-journal", out var image))
            using (image)
            using (var refRoot = FixtureImages.OpenRoot("dirty-journal.reference", out var refImage))
            using (refImage)
            using (var raw = new Ext4Reader(root))
            using (var reference = new Ext4Reader(refRoot))
            {
                Assert.NotEmpty(Ext4TreeComparer.Compare(raw, reference, CancellationToken.None));
            }
        }

        [Fact]
        public void TornJournal_AppliesOnlyTheCommittedTransaction()
        {
            var overlay = ReplayFixture("dirty-journal-torn");
            Assert.Equal(1, overlay.Replayed);
            Assert.Equal(1, overlay.Discarded);
            Assert.Empty(CompareRecoveredWithReference("dirty-journal-torn"));
        }

        // ------------------------------------------------------------- synthetic journals

        private sealed class JournalBuilder
        {
            private readonly MemoryBlockDevice _device;
            private readonly List<long> _map = new List<long>();
            private readonly int _bs;
            private readonly uint _incompat;
            private readonly uint _seed;
            private readonly List<byte[]> _log = new List<byte[]>();
            private uint _sequence = 1;

            public JournalBuilder(uint incompat)
            {
                using (var root = FixtureImages.OpenRoot("dirty-journal", out var image))
                using (image)
                {
                    _device = new MemoryBlockDevice(root.Size, 512);
                    var buffer = new byte[1 << 20];
                    for (long o = 0; o < root.Size; o += buffer.Length)
                    {
                        int n = (int)Math.Min(buffer.Length, root.Size - o);
                        root.Read(o, buffer, 0, n);
                        Buffer.BlockCopy(buffer, 0, _device.Data, (int)o, n);
                    }
                }

                using (var reader = new Ext4Reader(_device))
                {
                    _bs = reader.BlockSize;
                    BlocksCount = reader.Superblock.BlocksCount;
                    foreach (var e in reader.MapFile(reader.ReadInode(reader.Superblock.JournalInode)))
                    {
                        for (int i = 0; i < e.Count; i++)
                        {
                            _map.Add(e.Physical + i);
                        }
                    }
                }

                _incompat = incompat;
                var uuid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
                _seed = Crc32c.Append(~0u, uuid, 0, 16);
                var jsb = new byte[_bs];
                BinaryHelpers.PutU32BE(jsb, 0, Magic);
                BinaryHelpers.PutU32BE(jsb, 4, 4);
                BinaryHelpers.PutU32BE(jsb, 0x0C, (uint)_bs);
                BinaryHelpers.PutU32BE(jsb, 0x10, (uint)_map.Count);
                BinaryHelpers.PutU32BE(jsb, 0x14, 1);
                BinaryHelpers.PutU32BE(jsb, 0x18, 1);
                BinaryHelpers.PutU32BE(jsb, 0x1C, 1);
                BinaryHelpers.PutU32BE(jsb, 0x28, incompat);
                Buffer.BlockCopy(uuid, 0, jsb, 0x30, 16);
                _log.Add(jsb);
            }

            public long BlocksCount { get; }

            private bool V3 => (_incompat & 0x10) != 0;

            private bool B64 => (_incompat & 0x2) != 0;

            /// <summary>Adds a transaction; data blocks in order; revoked blocks; optional corruptions.</summary>
            public void Transaction(IList<(long Block, byte[] Data)> blocks, IList<long> revoked = null, bool corruptData = false, bool noCommit = false)
            {
                if (revoked != null && revoked.Count > 0)
                {
                    var r = Header(5);
                    int recordSize = B64 ? 8 : 4;
                    int offset = 16;
                    foreach (var b in revoked)
                    {
                        if (B64)
                        {
                            BinaryHelpers.PutU32BE(r, offset, (uint)(b >> 32));
                            BinaryHelpers.PutU32BE(r, offset + 4, (uint)b);
                        }
                        else
                        {
                            BinaryHelpers.PutU32BE(r, offset, (uint)b);
                        }

                        offset += recordSize;
                    }

                    BinaryHelpers.PutU32BE(r, 12, (uint)offset);
                    Tail(r);
                    _log.Add(r);
                }

                if (blocks.Count > 0)
                {
                    var d = Header(1);
                    int tagSize = V3 ? 16 : (B64 ? 12 : 8);
                    int o = 12;
                    var payload = new List<byte[]>();
                    for (int i = 0; i < blocks.Count; i++)
                    {
                        var data = (byte[])blocks[i].Data.Clone();
                        uint flags = i == 0 ? 0u : 0x2u; // SAME_UUID after the first tag
                        if (BinaryHelpers.U32BE(data, 0) == Magic)
                        {
                            BinaryHelpers.PutU32BE(data, 0, 0);
                            flags |= 0x1; // ESCAPE
                        }

                        if (i == blocks.Count - 1)
                        {
                            flags |= 0x8; // LAST_TAG
                        }

                        uint csum = DataChecksum(data);
                        if (corruptData)
                        {
                            data[100] ^= 0xFF;
                        }

                        BinaryHelpers.PutU32BE(d, o, (uint)blocks[i].Block);
                        if (V3)
                        {
                            BinaryHelpers.PutU32BE(d, o + 4, flags);
                            BinaryHelpers.PutU32BE(d, o + 8, (uint)(blocks[i].Block >> 32));
                            BinaryHelpers.PutU32BE(d, o + 12, csum);
                        }
                        else
                        {
                            d[o + 6] = (byte)(flags >> 8);
                            d[o + 7] = (byte)flags;
                            if (B64)
                            {
                                BinaryHelpers.PutU32BE(d, o + 8, (uint)(blocks[i].Block >> 32));
                            }
                        }

                        o += tagSize + (i == 0 ? 16 : 0);
                        if (i == 0)
                        {
                            Buffer.BlockCopy(Enumerable.Range(1, 16).Select(x => (byte)x).ToArray(), 0, d, o - 16, 16);
                        }

                        payload.Add(data);
                    }

                    Tail(d);
                    _log.Add(d);
                    _log.AddRange(payload);
                }

                if (!noCommit)
                {
                    var c = Header(2);
                    if (V3)
                    {
                        BinaryHelpers.PutU32BE(c, 16, 0);
                        BinaryHelpers.PutU32BE(c, 16, Crc32c.Append(_seed, c, 0, _bs));
                    }

                    _log.Add(c);
                }

                _sequence++;
            }

            public JournalOverlay Replay()
            {
                for (int i = 0; i < _log.Count; i++)
                {
                    Buffer.BlockCopy(_log[i], 0, _device.Data, (int)(_map[i] * _bs), _bs);
                }

                // The block after the log must not look like a continuation.
                Array.Clear(_device.Data, (int)(_map[_log.Count] * _bs), _bs);
                using (var reader = new Ext4Reader(_device))
                {
                    return Jbd2Journal.Open(reader).Replay();
                }
            }

            private byte[] Header(int type)
            {
                var b = new byte[_bs];
                BinaryHelpers.PutU32BE(b, 0, Magic);
                BinaryHelpers.PutU32BE(b, 4, (uint)type);
                BinaryHelpers.PutU32BE(b, 8, _sequence);
                return b;
            }

            private void Tail(byte[] b)
            {
                if (V3)
                {
                    BinaryHelpers.PutU32BE(b, _bs - 4, 0);
                    BinaryHelpers.PutU32BE(b, _bs - 4, Crc32c.Append(_seed, b, 0, _bs));
                }
            }

            private uint DataChecksum(byte[] data)
            {
                var seq = new byte[4];
                BinaryHelpers.PutU32BE(seq, 0, _sequence);
                return Crc32c.Append(Crc32c.Append(_seed, seq, 0, 4), data, 0, data.Length);
            }
        }

        private static byte[] Block(byte fill, uint? firstWord = null)
        {
            var b = Enumerable.Repeat(fill, 4096).ToArray();
            if (firstWord.HasValue)
            {
                BinaryHelpers.PutU32BE(b, 0, firstWord.Value);
            }

            return b;
        }

        private const uint V3With64Bit = 0x1 | 0x2 | 0x10;

        [Fact]
        public void Revoke_SuppressesEarlierCopies_ButNotLaterOnes()
        {
            var j = new JournalBuilder(V3With64Bit);
            long x = j.BlocksCount - 10, y = j.BlocksCount - 11, z = j.BlocksCount - 12;
            j.Transaction(new[] { (x, Block(0xA1)), (z, Block(0xD1)) });
            j.Transaction(new (long, byte[])[0], revoked: new[] { x, z });
            j.Transaction(new[] { (y, Block(0xB2)), (z, Block(0xD3)) });

            var overlay = j.Replay();

            Assert.Equal(3, overlay.Replayed);
            Assert.False(overlay.Blocks.ContainsKey(x));
            Assert.Equal(0xB2, overlay.Blocks[y][0]);
            Assert.Equal(0xD3, overlay.Blocks[z][0]);
        }

        [Fact]
        public void EscapedBlock_GetsItsMagicBack()
        {
            var j = new JournalBuilder(V3With64Bit);
            long x = j.BlocksCount - 10;
            j.Transaction(new[] { (x, Block(0x11, Magic)) });

            var overlay = j.Replay();

            Assert.Equal(Magic, BinaryHelpers.U32BE(overlay.Blocks[x], 0));
            Assert.Equal(0x11, overlay.Blocks[x][100]);
        }

        [Fact]
        public void DamagedDataBlock_EndsTheLog()
        {
            var j = new JournalBuilder(V3With64Bit);
            long x = j.BlocksCount - 10, y = j.BlocksCount - 11;
            j.Transaction(new[] { (x, Block(0x21)) });
            j.Transaction(new[] { (y, Block(0x22)) }, corruptData: true);

            var overlay = j.Replay();

            Assert.Equal(1, overlay.Replayed);
            Assert.Equal(1, overlay.Discarded);
            Assert.True(overlay.Blocks.ContainsKey(x));
            Assert.False(overlay.Blocks.ContainsKey(y));
        }

        [Fact]
        public void MissingCommit_DiscardsTheLastTransaction()
        {
            var j = new JournalBuilder(V3With64Bit);
            long x = j.BlocksCount - 10, y = j.BlocksCount - 11;
            j.Transaction(new[] { (x, Block(0x31)) });
            j.Transaction(new[] { (y, Block(0x32)) }, noCommit: true);

            var overlay = j.Replay();

            Assert.Equal(1, overlay.Replayed);
            Assert.False(overlay.Blocks.ContainsKey(y));
        }

        [Theory]
        [InlineData(0x1u)]          // revoke only: 8-byte tags, no checksums (ext3/ext4 without metadata_csum)
        [InlineData(0x1u | 0x2u)]   // 64-bit block numbers, no checksums
        [InlineData(0x1u | 0x10u)]  // checksums v3 with 32-bit block numbers
        public void TagFormats(uint incompat)
        {
            var j = new JournalBuilder(incompat);
            long x = j.BlocksCount - 10, y = j.BlocksCount - 20;
            j.Transaction(new[] { (x, Block(0x41)), (y, Block(0x42)) }, revoked: null);
            j.Transaction(new (long, byte[])[0], revoked: new[] { y });

            var overlay = j.Replay();

            Assert.Equal(2, overlay.Replayed);
            Assert.Equal(0x41, overlay.Blocks[x][0]);
            Assert.False(overlay.Blocks.ContainsKey(y));
        }
    }
}
