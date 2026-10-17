using System;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Read-only view of a partition with the blocks recovered from the journal laid over it, so
    /// everything above (superblock, descriptors, inodes, directories) sees the recovered file
    /// system. Nothing is ever written to the partition.
    /// </summary>
    internal sealed class OverlayBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner;
        private readonly JournalOverlay _overlay;
        private readonly int _blockSize;

        public OverlayBlockDevice(IBlockDevice inner, JournalOverlay overlay, int blockSize)
        {
            _inner = inner;
            _overlay = overlay;
            _blockSize = blockSize;
        }

        public long Size => _inner.Size;

        public int SectorSize => _inner.SectorSize;

        public bool CanWrite => false;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            int read = _inner.Read(offset, buffer, index, count);
            if (_overlay.Blocks.Count == 0 || read <= 0)
            {
                return read;
            }

            long end = offset + read;
            for (long block = offset / _blockSize; block * _blockSize < end; block++)
            {
                if (!_overlay.Blocks.TryGetValue(block, out var data))
                {
                    continue;
                }

                long blockStart = block * _blockSize;
                long from = Math.Max(offset, blockStart);
                long to = Math.Min(end, blockStart + _blockSize);
                Buffer.BlockCopy(data, (int)(from - blockStart), buffer, index + (int)(from - offset), (int)(to - from));
            }

            return read;
        }

        public void Write(long offset, byte[] buffer, int index, int count) =>
            throw new InvalidOperationException("The recovered file system view is read-only.");

        public void Flush()
        {
        }

        public void Dispose()
        {
            // The inner device belongs to the caller.
        }
    }
}
