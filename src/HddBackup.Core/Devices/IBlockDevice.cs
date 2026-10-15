using System;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Sector-addressed storage. Offsets and counts passed to <see cref="Read"/> and
    /// <see cref="Write"/> must be multiples of <see cref="SectorSize"/>.
    /// </summary>
    public interface IBlockDevice : IDisposable
    {
        /// <summary>Size in bytes; always a multiple of <see cref="SectorSize"/>.</summary>
        long Size { get; }

        int SectorSize { get; }

        bool CanWrite { get; }

        /// <summary>Reads exactly <paramref name="count"/> bytes; returns the count read.</summary>
        int Read(long offset, byte[] buffer, int index, int count);

        void Write(long offset, byte[] buffer, int index, int count);

        /// <summary>Commits operating system and device write caches.</summary>
        void Flush();
    }

    internal static class BlockDeviceGuard
    {
        public static void CheckRange(IBlockDevice device, long offset, byte[] buffer, int index, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (index < 0 || count < 0 || index + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (offset < 0 || offset + count > device.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Range is outside the device.");
            }

            if (offset % device.SectorSize != 0 || count % device.SectorSize != 0)
            {
                throw new ArgumentException("Offset and count must be sector aligned.");
            }
        }
    }
}
