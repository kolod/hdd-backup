using System;
using System.IO;
using System.IO.Compression;
using HddBackup.Core.Errors;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// Streams the uncompressed content of a raw or gzip image while hashing the file bytes.
    /// <para>
    /// .NET Framework's GZipStream stops after the first gzip member and silently returns short
    /// data for truncated files. This reader therefore locates each member's trailer itself
    /// (matching ISIZE) and continues with the next member; a missing trailer is reported as a
    /// corrupt image.
    /// </para>
    /// </summary>
    internal sealed class ImageReader : IDisposable
    {
        private const int FileBufferSize = 1 << 20;
        private const int BoundarySearchWindow = 256 * 1024;
        private const int MinimumMemberSize = 18;

        private readonly FileStream _file;
        private readonly HashingStream _hashing;
        private GZipStream _gzip;
        private long _memberStart;
        private long _memberSize;
        private bool _endOfData;

        public ImageReader(string path)
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileBufferSize, FileOptions.SequentialScan);
            IsGzip = _file.Length >= MinimumMemberSize && _file.ReadByte() == 0x1F && _file.ReadByte() == 0x8B;
            _file.Position = 0;
            _hashing = new HashingStream(_file, leaveOpen: true);
            if (IsGzip)
            {
                StartMember(0);
            }
        }

        public bool IsGzip { get; }

        public long FileLength => _file.Length;

        /// <summary>File bytes consumed so far (monotonic; drives progress for gzip images).</summary>
        public long FilePosition => _hashing.HashedUpTo;

        /// <summary>Uncompressed bytes returned so far.</summary>
        public long Position { get; private set; }

        /// <summary>SHA-256 of the whole file; valid once all data has been read.</summary>
        public string FinishFileHash()
        {
            if (!_endOfData)
            {
                throw new InvalidOperationException("Image not fully read.");
            }

            return _hashing.FinishHash();
        }

        /// <summary>Fills the buffer completely unless the end of the image is reached.</summary>
        public int ReadFull(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count && !_endOfData)
            {
                int n = IsGzip ? ReadGzip(buffer, offset + total, count - total) : _hashing.Read(buffer, offset + total, count - total);
                if (n == 0)
                {
                    if (!IsGzip || !TryStartNextMember())
                    {
                        _endOfData = true;
                        DrainForHash();
                    }

                    continue;
                }

                total += n;
                Position += n;
                _memberSize += n;
            }

            return total;
        }

        public void Dispose()
        {
            _gzip?.Dispose();
            _hashing.Dispose();
            _file.Dispose();
        }

        private int ReadGzip(byte[] buffer, int offset, int count)
        {
            try
            {
                return _gzip.Read(buffer, offset, count);
            }
            catch (InvalidDataException ex)
            {
                throw new ImagingException(ErrorCode.CorruptImage, "Gzip data error: " + ex.Message, Position, ex);
            }
        }

        private void StartMember(long position)
        {
            _gzip?.Dispose();
            _hashing.Position = position;
            _memberStart = position;
            _memberSize = 0;
            _gzip = new GZipStream(_hashing, CompressionMode.Decompress, leaveOpen: true);
        }

        /// <summary>
        /// Called when the current member is exhausted. Finds its trailer, then either starts the
        /// next member (returns true) or confirms the end of file (returns false).
        /// </summary>
        private bool TryStartNextMember()
        {
            long delivered = _hashing.Position;
            long fileLength = _file.Length;
            long windowStart = Math.Max(_memberStart + 10, delivered - BoundarySearchWindow);
            long windowEnd = Math.Min(fileLength, delivered + 16);
            var window = new byte[windowEnd - windowStart];
            _hashing.Position = windowStart;
            int read = 0;
            while (read < window.Length)
            {
                int n = _hashing.Read(window, read, window.Length - read);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            uint isize = unchecked((uint)_memberSize);
            for (int i = 8; i <= read; i++)
            {
                if (BinaryHelpers.U32(window, i - 4) != isize)
                {
                    continue;
                }

                long p = windowStart + i;
                if (p == fileLength)
                {
                    return false;
                }

                if (i + 3 <= read && window[i] == 0x1F && window[i + 1] == 0x8B && window[i + 2] == 0x08)
                {
                    StartMember(p);
                    return true;
                }

                // Some tools pad gzip files with zeros (e.g. to a block size); gzip ignores them.
                if (i < read && window[i] == 0 && RestOfFileIsZero(p))
                {
                    return false;
                }
            }

            throw new ImagingException(
                ErrorCode.CorruptImage, "Gzip member trailer not found; the file is truncated or damaged.", Position);
        }

        private bool RestOfFileIsZero(long from)
        {
            _hashing.Position = from;
            var scratch = new byte[64 * 1024];
            int n;
            while ((n = _hashing.Read(scratch, 0, scratch.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                {
                    if (scratch[i] != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void DrainForHash()
        {
            _gzip?.Dispose();
            _gzip = null;
            _hashing.Position = _hashing.HashedUpTo;
            var scratch = new byte[64 * 1024];
            while (_hashing.Read(scratch, 0, scratch.Length) > 0)
            {
            }
        }
    }
}
