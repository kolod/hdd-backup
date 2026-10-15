using System;
using System.ComponentModel;

namespace HddBackup.Core.Native
{
    /// <summary>
    /// Page-aligned unmanaged memory, required for unbuffered (FILE_FLAG_NO_BUFFERING) I/O.
    /// </summary>
    internal sealed class AlignedBuffer : IDisposable
    {
        private IntPtr _pointer;

        public AlignedBuffer(int size)
        {
            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size));
            }

            _pointer = NativeMethods.VirtualAlloc(
                IntPtr.Zero, (UIntPtr)(uint)size, NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);
            if (_pointer == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            Size = size;
        }

        public int Size { get; }

        public IntPtr Pointer
        {
            get
            {
                if (_pointer == IntPtr.Zero)
                {
                    throw new ObjectDisposedException(nameof(AlignedBuffer));
                }

                return _pointer;
            }
        }

        public void Dispose()
        {
            if (_pointer != IntPtr.Zero)
            {
                NativeMethods.VirtualFree(_pointer, UIntPtr.Zero, NativeMethods.MemRelease);
                _pointer = IntPtr.Zero;
            }
        }
    }
}
