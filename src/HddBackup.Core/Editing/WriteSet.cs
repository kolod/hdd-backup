using System;
using System.Collections.Generic;
using System.Threading;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Editing
{
    /// <summary>
    /// The step of a change a write belongs to (research R-5). A device flush separates two steps when a
    /// write set is replayed, so that data and allocation are durable before anything refers to them and
    /// references are removed before things are freed.
    /// </summary>
    public enum WritePhase
    {
        Data,
        Allocation,
        Inode,
        Link,
        Free,
        Counters
    }

    /// <summary>One write, in partition coordinates (offset and length are multiples of the sector size).</summary>
    public sealed class BlockWrite
    {
        public BlockWrite(long offset, byte[] data, WritePhase phase)
        {
            Offset = offset;
            Data = data;
            Phase = phase;
        }

        public long Offset { get; }

        public byte[] Data { get; }

        public WritePhase Phase { get; }
    }

    /// <summary>
    /// The ordered writes an edit would make. Replayed in order, later writes win over earlier ones at the
    /// same place, so replaying the same set twice gives the same result.
    /// </summary>
    public sealed class WriteSet
    {
        private readonly List<BlockWrite> _writes = new List<BlockWrite>();

        public IReadOnlyList<BlockWrite> Writes => _writes;

        public long TotalBytes { get; private set; }

        public void Add(BlockWrite write)
        {
            _writes.Add(write);
            TotalBytes += write.Data.Length;
        }

        /// <summary>
        /// Writes everything to <paramref name="target"/> in order, flushing when the phase changes and at the
        /// end. <paramref name="progress"/> receives the bytes written so far; cancellation is honoured between writes.
        /// </summary>
        public void ApplyTo(IBlockDevice target, Action<long> progress, CancellationToken cancellationToken)
        {
            long done = 0;
            WritePhase? current = null;
            foreach (var w in _writes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (current.HasValue && current.Value != w.Phase)
                {
                    target.Flush();
                }

                current = w.Phase;
                target.Write(w.Offset, w.Data, 0, w.Data.Length);
                done += w.Data.Length;
                progress?.Invoke(done);
            }

            target.Flush();
        }
    }

    /// <summary>
    /// A device over a partition whose reads see the writes recorded so far and whose <see cref="Write"/> only
    /// records: nothing ever reaches the inner device, so an edit can be built, checked and only then replayed.
    /// </summary>
    public sealed class RecordingBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner;
        private readonly Dictionary<long, byte[]> _sectors = new Dictionary<long, byte[]>();

        public RecordingBlockDevice(IBlockDevice inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public WriteSet Set { get; } = new WriteSet();

        /// <summary>The phase attached to the writes recorded from now on.</summary>
        public WritePhase Phase { get; set; }

        public long Size => _inner.Size;

        public int SectorSize => _inner.SectorSize;

        public bool CanWrite => true;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            int read = _inner.Read(offset, buffer, index, count);
            if (_sectors.Count == 0 || read <= 0)
            {
                return read;
            }

            int sector = SectorSize;
            long end = offset + read;
            for (long s = offset / sector; s * sector < end; s++)
            {
                if (!_sectors.TryGetValue(s, out var data))
                {
                    continue;
                }

                long start = s * sector;
                long from = Math.Max(offset, start);
                long to = Math.Min(end, start + sector);
                Buffer.BlockCopy(data, (int)(from - start), buffer, index + (int)(from - offset), (int)(to - from));
            }

            return read;
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            int sector = SectorSize;
            if (offset < 0 || count <= 0 || offset % sector != 0 || count % sector != 0 || offset + count > Size)
            {
                throw new ArgumentException("A write must be sector aligned and inside the partition.");
            }

            var copy = new byte[count];
            Buffer.BlockCopy(buffer, index, copy, 0, count);
            Set.Add(new BlockWrite(offset, copy, Phase));
            for (int i = 0; i < count / sector; i++)
            {
                var s = new byte[sector];
                Buffer.BlockCopy(copy, i * sector, s, 0, sector);
                _sectors[(offset / sector) + i] = s;
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
            // The inner device belongs to the caller.
        }
    }
}
