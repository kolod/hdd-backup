using System;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;

namespace HddBackup.Core.Tests.Fakes
{
    /// <summary>In-memory disk. Dispose does nothing, so tests can inspect it after an operation.</summary>
    public class MemoryBlockDevice : IBlockDevice
    {
        private int _writeCalls;
        private int _flushCalls;

        public MemoryBlockDevice(long size, int sectorSize, bool writeProtected = false)
        {
            if (size % sectorSize != 0)
            {
                throw new ArgumentException("Size must be a multiple of the sector size.");
            }

            Data = new byte[size];
            SectorSize = sectorSize;
            WriteProtected = writeProtected;
        }

        public byte[] Data { get; }

        public long Size => Data.LongLength;

        public int SectorSize { get; }

        public bool WriteProtected { get; set; }

        public bool CanWrite => !WriteProtected;

        public int WriteCalls => Volatile.Read(ref _writeCalls);

        public int FlushCalls => Volatile.Read(ref _flushCalls);

        /// <summary>Number of write calls at the time of the most recent flush.</summary>
        public int WritesAtLastFlush { get; private set; } = -1;

        public virtual int Read(long offset, byte[] buffer, int index, int count)
        {
            Check(offset, buffer, index, count);
            Buffer.BlockCopy(Data, (int)offset, buffer, index, count);
            return count;
        }

        public virtual void Write(long offset, byte[] buffer, int index, int count)
        {
            Check(offset, buffer, index, count);
            if (WriteProtected)
            {
                throw new ImagingException(ErrorCode.WriteProtected, "fake write protect", offset);
            }

            Interlocked.Increment(ref _writeCalls);
            Buffer.BlockCopy(buffer, index, Data, (int)offset, count);
        }

        public virtual void Flush()
        {
            Interlocked.Increment(ref _flushCalls);
            WritesAtLastFlush = WriteCalls;
        }

        public void Dispose()
        {
        }

        public void Fill(int seed)
        {
            new Random(seed).NextBytes(Data);
        }

        public void CorruptByte(long offset)
        {
            Data[offset] ^= 0xFF;
        }

        private void Check(long offset, byte[] buffer, int index, int count)
        {
            if (offset < 0 || count < 0 || offset + count > Size || index + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (offset % SectorSize != 0 || count % SectorSize != 0)
            {
                throw new ArgumentException("Unaligned access.");
            }
        }
    }

    /// <summary>Fails (or "disappears") once an access reaches a given offset.</summary>
    public sealed class FaultyBlockDevice : MemoryBlockDevice
    {
        public FaultyBlockDevice(long size, int sectorSize, long failAt, ErrorCode code, bool failReads, bool failWrites)
            : base(size, sectorSize)
        {
            FailAt = failAt;
            Code = code;
            FailReads = failReads;
            FailWrites = failWrites;
        }

        public long FailAt { get; }

        public ErrorCode Code { get; }

        public bool FailReads { get; }

        public bool FailWrites { get; }

        public bool Removed { get; private set; }

        /// <summary>Undoes a "removal" triggered while the test fixture probed the device.</summary>
        public void Reset() => Removed = false;

        public override int Read(long offset, byte[] buffer, int index, int count)
        {
            if (Removed || (FailReads && offset + count > FailAt))
            {
                throw Fail(Math.Max(offset, Math.Min(FailAt, offset + count)));
            }

            return base.Read(offset, buffer, index, count);
        }

        public override void Write(long offset, byte[] buffer, int index, int count)
        {
            if (Removed || (FailWrites && offset + count > FailAt))
            {
                throw Fail(offset);
            }

            base.Write(offset, buffer, index, count);
        }

        public override void Flush()
        {
            if (Removed)
            {
                throw new ImagingException(ErrorCode.DeviceRemoved, "fake device gone");
            }

            base.Flush();
        }

        private ImagingException Fail(long offset)
        {
            if (Code == ErrorCode.DeviceRemoved)
            {
                Removed = true;
            }

            return new ImagingException(Code, "fake failure", offset);
        }
    }

    /// <summary>A large device that discards writes (for multi-gigabyte streaming tests).</summary>
    public sealed class NullBlockDevice : IBlockDevice
    {
        private long _written;

        public NullBlockDevice(long size)
        {
            Size = size;
        }

        public long Size { get; }

        public int SectorSize => 512;

        public bool CanWrite => true;

        public long BytesWritten => Interlocked.Read(ref _written);

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            Array.Clear(buffer, index, count);
            return count;
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            Interlocked.Add(ref _written, count);
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
