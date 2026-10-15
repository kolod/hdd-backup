using System;
using System.IO;
using HddBackup.Core.Formats;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// Pass-through stream that SHA-256 hashes every byte exactly once, in stream order.
    /// Seeking backwards over already hashed bytes is allowed (they are not hashed again);
    /// seeking past the hashed region is rejected because it would leave a gap.
    /// </summary>
    public sealed class HashingStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _leaveOpen;
        private readonly Sha256Accumulator _hash = new Sha256Accumulator();
        private long _position;

        public HashingStream(Stream inner, bool leaveOpen)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _leaveOpen = leaveOpen;
        }

        /// <summary>Number of leading stream bytes already included in the hash.</summary>
        public long HashedUpTo { get; private set; }

        public long BytesProcessed => HashedUpTo;

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => _inner.CanWrite;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > HashedUpTo)
                {
                    throw new InvalidOperationException("Cannot seek past the hashed region.");
                }

                _inner.Position = value;
                _position = value;
            }
        }

        public string FinishHash() => _hash.Finish();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            Account(buffer, offset, n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Account(buffer, offset, count);
        }

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    target = offset;
                    break;
                case SeekOrigin.Current:
                    target = _position + offset;
                    break;
                default:
                    target = _inner.Length + offset;
                    break;
            }

            Position = target;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        private void Account(byte[] buffer, int offset, int count)
        {
            long start = _position;
            long end = start + count;
            if (end > HashedUpTo)
            {
                long from = Math.Max(start, HashedUpTo);
                _hash.Append(buffer, offset + (int)(from - start), (int)(end - from));
                HashedUpTo = end;
            }

            _position = end;
        }
    }
}
