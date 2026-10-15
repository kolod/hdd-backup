using System;
using System.IO;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// An image file seen as a block device with 512-byte sectors. Used for image analysis and for
    /// building shrunk images; never for physical disks.
    /// </summary>
    public sealed class FileBlockDevice : IBlockDevice
    {
        public const int Sector = 512;
        private readonly FileStream _stream;
        private readonly object _sync = new object();

        private FileBlockDevice(FileStream stream, bool writable, long size)
        {
            _stream = stream;
            CanWrite = writable;
            Size = size;
        }

        public long Size { get; }

        public int SectorSize => Sector;

        public bool CanWrite { get; }

        public string Path => _stream.Name;

        public static FileBlockDevice OpenRead(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            return new FileBlockDevice(stream, false, stream.Length - (stream.Length % Sector));
        }

        /// <summary>Creates (or truncates) a file of the given size; unwritten areas read as zeros.</summary>
        public static FileBlockDevice Create(string path, long size)
        {
            if (size <= 0 || size % Sector != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            stream.SetLength(size);
            return new FileBlockDevice(stream, true, size);
        }

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            lock (_sync)
            {
                _stream.Position = offset;
                int done = 0;
                while (done < count)
                {
                    int n = _stream.Read(buffer, index + done, count - done);
                    if (n == 0)
                    {
                        Array.Clear(buffer, index + done, count - done);
                        break;
                    }

                    done += n;
                }

                return count;
            }
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            if (!CanWrite)
            {
                throw new InvalidOperationException("Image opened read-only.");
            }

            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            lock (_sync)
            {
                _stream.Position = offset;
                _stream.Write(buffer, index, count);
            }
        }

        public void Flush()
        {
            if (CanWrite)
            {
                lock (_sync)
                {
                    _stream.Flush(true);
                }
            }
        }

        public void Dispose() => _stream.Dispose();
    }
}
