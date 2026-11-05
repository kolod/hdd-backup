using System;
using System.Threading;
using HddBackup.Core.Devices;

namespace HddBackup.Core.Tests.Fakes
{
    /// <summary>
    /// Wraps a device and fails on any write or flush, counting the attempts, so tests can prove
    /// that browsing and extracting never touch the source (Principle I).
    /// </summary>
    public sealed class ReadOnlyBlockDevice : IBlockDevice
    {
        private readonly IBlockDevice _inner;
        private readonly bool _ownsInner;
        private int _writeCalls;
        private int _reads;

        public ReadOnlyBlockDevice(IBlockDevice inner, bool ownsInner = true)
        {
            _inner = inner;
            _ownsInner = ownsInner;
        }

        /// <summary>Number of write or flush attempts.</summary>
        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public int ReadCalls => Volatile.Read(ref _reads);

        /// <summary>When set, every read after this many reads throws it (device removal).</summary>
        public int? FailAfterReads { get; set; }

        public Exception FailWith { get; set; }

        public long Size => _inner.Size;

        public int SectorSize => _inner.SectorSize;

        public bool CanWrite => false;

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            int n = Interlocked.Increment(ref _reads);
            if (FailAfterReads.HasValue && n > FailAfterReads.Value && FailWith != null)
            {
                throw FailWith;
            }

            return _inner.Read(offset, buffer, index, count);
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            Interlocked.Increment(ref _writeCalls);
            throw new InvalidOperationException("A write reached a device that must stay read-only.");
        }

        public void Flush()
        {
            Interlocked.Increment(ref _writeCalls);
            throw new InvalidOperationException("A flush reached a device that must stay read-only.");
        }

        public void Dispose()
        {
            if (_ownsInner)
            {
                _inner.Dispose();
            }
        }
    }
}
