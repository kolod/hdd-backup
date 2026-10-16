using System;
using System.IO;
using System.Linq;
using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// What can be learned from the start and the end of an image file without reading it all:
    /// the partition table and, for gzip, an estimate of the uncompressed size.
    /// </summary>
    public static class ImageHead
    {
        private const int HeadBytes = 1024 * 1024;
        /// <summary>Backup GPT (header plus entries) at the end of a disk, in 512-byte sectors.</summary>
        public const int GptTailSectors = 34;
        private const long FourGiB = 1L << 32;

        /// <summary>Logical size an image needs on a device: end of the last partition, plus the backup GPT; 0 without partitions.</summary>
        public static long RequiredDeviceBytes(string imagePath)
        {
            var table = ReadTable(imagePath);
            if (table.Partitions.Count == 0)
            {
                return 0;
            }

            return table.Partitions.Max(p => p.End) + (table.Style == TableStyle.Gpt ? GptTailSectors * 512L : 0);
        }

        /// <summary>
        /// Uncompressed size of a gzip image: the smallest size that is at least
        /// <paramref name="lowerBound"/> and matches the gzip trailer (size modulo 4 GiB). Exact when
        /// the image ends less than 4 GiB after <paramref name="lowerBound"/> and is a single gzip
        /// member; null when not a gzip file or no lower bound is known.
        /// </summary>
        public static long? EstimateGzipSize(string imagePath, long lowerBound)
        {
            uint? trailer = ReadGzipTrailerSize(imagePath);
            if (trailer == null || lowerBound <= 0)
            {
                return null;
            }

            return FromTrailer(trailer.Value, lowerBound);
        }

        /// <summary>The smallest value ≥ <paramref name="lowerBound"/> that is congruent to <paramref name="trailer"/> modulo 4 GiB.</summary>
        public static long FromTrailer(uint trailer, long lowerBound)
        {
            long size = trailer;
            if (size < lowerBound)
            {
                size += (lowerBound - size + FourGiB - 1) / FourGiB * FourGiB;
            }

            return size;
        }

        /// <summary>The ISIZE field of the last gzip member (uncompressed size modulo 4 GiB), or null.</summary>
        public static uint? ReadGzipTrailerSize(string imagePath)
        {
            using (var file = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 18 || file.ReadByte() != 0x1F || file.ReadByte() != 0x8B)
                {
                    return null;
                }

                var bytes = new byte[4];
                file.Seek(-4, SeekOrigin.End);
                if (file.Read(bytes, 0, 4) != 4)
                {
                    return null;
                }

                return BitConverter.ToUInt32(bytes, 0); // little-endian, as Windows on x86/x64
            }
        }

        private static PartitionTable ReadTable(string imagePath)
        {
            bool gzip;
            using (var probe = File.OpenRead(imagePath))
            {
                gzip = probe.Length >= 18 && probe.ReadByte() == 0x1F && probe.ReadByte() == 0x8B;
            }

            if (!gzip)
            {
                using (var device = FileBlockDevice.OpenRead(imagePath))
                {
                    return PartitionTableReader.Read(device);
                }
            }

            using (var reader = new ImageReader(imagePath))
            {
                var head = new byte[HeadBytes];
                int n = reader.ReadFull(head, 0, head.Length);
                return PartitionTableReader.Read(new HeadDevice(head, n));
            }
        }

        /// <summary>The first megabyte of an image; everything after it reads as zeros.</summary>
        private sealed class HeadDevice : IBlockDevice
        {
            private readonly byte[] _head;
            private readonly int _length;

            public HeadDevice(byte[] head, int length)
            {
                _head = head;
                _length = length;
            }

            public long Size => long.MaxValue / 2 / 512 * 512;

            public int SectorSize => 512;

            public bool CanWrite => false;

            public int Read(long offset, byte[] buffer, int index, int count)
            {
                Array.Clear(buffer, index, count);
                if (offset < _length)
                {
                    Buffer.BlockCopy(_head, (int)offset, buffer, index, (int)Math.Min(count, _length - offset));
                }

                return count;
            }

            public void Write(long offset, byte[] buffer, int index, int count) => throw new InvalidOperationException();

            public void Flush()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
