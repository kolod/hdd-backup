using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// Writes an image to <c>&lt;path&gt;.partial</c>, optionally gzip-compressed, hashing the
    /// bytes that reach the file. The engine renames the file only after success.
    /// </summary>
    internal sealed class ImageWriter : IDisposable
    {
        private const int FileBufferSize = 1 << 20;
        private const string PartialSuffix = ".partial";

        private readonly FileStream _file;
        private readonly HashingStream _hashing;
        private readonly Stream _top;
        private bool _completed;
        private bool _disposed;

        public ImageWriter(string finalPath, bool gzip, CompressionLevel level)
        {
            FinalPath = finalPath;
            PartialPath = finalPath + PartialSuffix;
            try
            {
                _file = new FileStream(PartialPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileBufferSize);
            }
            catch (IOException ex)
            {
                throw MapIo(ex, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Cannot create image file: " + ex.Message);
            }

            _hashing = new HashingStream(_file, leaveOpen: true);
            _top = gzip ? new GZipStream(_hashing, level, leaveOpen: true) : (Stream)_hashing;
        }

        public string FinalPath { get; }

        public string PartialPath { get; }

        /// <summary>SHA-256 of the bytes written to the file; available after <see cref="Complete"/>.</summary>
        public string FileHash { get; private set; }

        public long FileLength { get; private set; }

        public void Write(byte[] buffer, int offset, int count, long sourceOffset)
        {
            try
            {
                _top.Write(buffer, offset, count);
            }
            catch (IOException ex)
            {
                throw MapIo(ex, sourceOffset);
            }
        }

        /// <summary>Finishes compression and flushes the file to storage.</summary>
        public void Complete()
        {
            try
            {
                if (!ReferenceEquals(_top, _hashing))
                {
                    _top.Dispose(); // writes the gzip trailer
                }

                _hashing.Flush();
                _file.Flush(true);
                FileLength = _file.Length;
                FileHash = _hashing.FinishHash();
                _completed = true;
                CloseStreams();
            }
            catch (IOException ex)
            {
                throw MapIo(ex, null);
            }
        }

        /// <summary>Closes and deletes the partial file. Never throws.</summary>
        public void Abort()
        {
            try
            {
                CloseStreams();
            }
            catch (Exception)
            {
                // The file is being discarded; close errors (e.g. disk full on flush) do not matter.
            }

            try
            {
                if (File.Exists(PartialPath))
                {
                    File.Delete(PartialPath);
                }
            }
            catch (Exception)
            {
                // Best effort; the ".partial" suffix still marks the file as incomplete.
            }
        }

        public void Dispose()
        {
            if (!_completed)
            {
                Abort();
            }
        }

        internal static ImagingException MapIo(IOException ex, long? offset)
        {
            int code = ex.HResult & 0xFFFF;
            string detail = "Image file: " + ex.Message + " (Win32 error " + code.ToString(CultureInfo.InvariantCulture) + ")";
            switch (code)
            {
                case 112: // ERROR_DISK_FULL
                case 39: // ERROR_HANDLE_DISK_FULL
                    return new ImagingException(ErrorCode.DestinationFull, detail, offset, ex);
                case 223: // ERROR_FILE_TOO_LARGE
                    return new ImagingException(ErrorCode.FileTooLargeForFs, detail, offset, ex);
                default:
                    return new ImagingException(ErrorCode.WriteError, detail, offset, ex);
            }
        }

        private void CloseStreams()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _top.Dispose();
                _hashing.Dispose();
            }
            finally
            {
                _file.Dispose();
            }
        }
    }
}
