using System;
using System.Collections.Generic;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Read-only window over a device that reads in large aligned chunks and keeps the last ones. A raw disk behind a
    /// USB reader answers every small read with its own slow request; the file system check reads inodes and
    /// directories a few hundred bytes at a time, so without this it takes minutes on a card. Never writes.
    /// </summary>
    internal sealed class CachedReadDevice : IBlockDevice
    {
        private const int ChunkSize = 256 * 1024;
        private const int MaxChunks = 256; // 64 MiB

        private readonly IBlockDevice _inner;
        private readonly Dictionary<long, LinkedListNode<Chunk>> _chunks = new Dictionary<long, LinkedListNode<Chunk>>();
        private readonly LinkedList<Chunk> _order = new LinkedList<Chunk>();
        private readonly object _sync = new object();

        private sealed class Chunk
        {
            public long Index;
            public byte[] Data;
            public int Length;
        }

        public CachedReadDevice(IBlockDevice inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public long Size => _inner.Size;

        public int SectorSize => _inner.SectorSize;

        public bool CanWrite => false;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            lock (_sync)
            {
                int done = 0;
                while (done < count)
                {
                    long position = offset + done;
                    var chunk = Get(position / ChunkSize);
                    int within = (int)(position - (chunk.Index * ChunkSize));
                    int take = Math.Min(count - done, chunk.Length - within);
                    if (take <= 0)
                    {
                        throw new ArgumentOutOfRangeException(nameof(offset), "Range is outside the device.");
                    }

                    Buffer.BlockCopy(chunk.Data, within, buffer, index + done, take);
                    done += take;
                }

                return done;
            }
        }

        private Chunk Get(long chunkIndex)
        {
            if (_chunks.TryGetValue(chunkIndex, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value;
            }

            long start = chunkIndex * ChunkSize;
            int length = (int)Math.Min(ChunkSize, _inner.Size - start);
            var chunk = new Chunk { Index = chunkIndex, Data = new byte[length], Length = length };
            _inner.Read(start, chunk.Data, 0, length);
            _chunks[chunkIndex] = _order.AddFirst(chunk);
            if (_chunks.Count > MaxChunks)
            {
                var last = _order.Last;
                _order.RemoveLast();
                _chunks.Remove(last.Value.Index);
            }

            return chunk;
        }

        public void Write(long offset, byte[] buffer, int index, int count) =>
            throw new InvalidOperationException("The cached view is read-only.");

        public void Flush()
        {
        }

        public void Dispose()
        {
            // The inner device belongs to the caller.
        }
    }
}
