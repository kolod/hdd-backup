using System;

namespace HddBackup.Core.Progress
{
    /// <summary>
    /// Progress units for reading an image file. Counted in uncompressed bytes against the known or
    /// estimated uncompressed size, but never behind the share of the file already read, so a
    /// wrong estimate cannot hold progress back. Without a size, the share of the file is used.
    /// </summary>
    public sealed class ImageProgress
    {
        private readonly long _fileLength;
        private readonly long? _dataSize;

        /// <param name="fileLength">Length of the image file (compressed for gzip).</param>
        /// <param name="dataSize">Exact or estimated uncompressed size; null when unknown.</param>
        public ImageProgress(long fileLength, long? dataSize)
        {
            _fileLength = Math.Max(0, fileLength);
            _dataSize = dataSize > 0 ? dataSize : null;
        }

        public long Total => _dataSize ?? _fileLength;

        /// <param name="filePosition">Bytes of the file read so far.</param>
        /// <param name="dataPosition">Uncompressed bytes produced so far.</param>
        public long Units(long filePosition, long dataPosition)
        {
            if (_dataSize == null)
            {
                return filePosition;
            }

            long size = _dataSize.Value;
            long fileShare = _fileLength > 0 ? (long)((double)filePosition / _fileLength * size) : 0;
            return Math.Min(size, Math.Max(dataPosition, fileShare));
        }
    }
}
