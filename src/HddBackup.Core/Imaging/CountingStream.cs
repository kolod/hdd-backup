using System;
using System.IO;
using System.Threading;

namespace HddBackup.Core.Imaging
{
    /// <summary>Pass-through stream that counts bytes read and written (thread-safe counters).</summary>
    public sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private long _bytesRead;
        private long _bytesWritten;

        public CountingStream(Stream inner, bool leaveOpen)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _leaveOpen = leaveOpen;
        }

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            Interlocked.Add(ref _bytesRead, n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Interlocked.Add(ref _bytesWritten, count);
        }

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_leaveOpen)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
