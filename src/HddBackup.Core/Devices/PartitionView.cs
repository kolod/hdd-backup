using System;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// A window over a byte range of another device. Offsets are relative to the window;
    /// any access outside it is rejected, so a partition operation can never touch the
    /// partition table or neighbouring partitions.
    /// </summary>
    public sealed class PartitionView : IBlockDevice
    {
        private readonly IBlockDevice _inner;
        private readonly long _offset;
        private readonly bool _ownsInner;

        public PartitionView(IBlockDevice inner, long offset, long length, bool ownsInner = false)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (offset < 0 || length <= 0 || offset + length > inner.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(length), "Partition lies outside the device.");
            }

            if (offset % inner.SectorSize != 0 || length % inner.SectorSize != 0)
            {
                throw new ArgumentException("Partition bounds must be sector aligned.");
            }

            _offset = offset;
            Size = length;
            _ownsInner = ownsInner;
        }

        public long Size { get; }

        public int SectorSize => _inner.SectorSize;

        public bool CanWrite => _inner.CanWrite;

        /// <summary>Absolute offset of the window on the underlying device.</summary>
        public long BaseOffset => _offset;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            return _inner.Read(_offset + offset, buffer, index, count);
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            _inner.Write(_offset + offset, buffer, index, count);
        }

        public void Flush() => _inner.Flush();

        public void Dispose()
        {
            if (_ownsInner)
            {
                _inner.Dispose();
            }
        }
    }
}
