using System;
using System.Globalization;
using System.Runtime.InteropServices;
using HddBackup.Core.Errors;
using HddBackup.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// A physical disk opened with unbuffered I/O (and write-through when writable), so that
    /// completed writes are on the medium and progress reflects real work.
    /// </summary>
    public sealed class PhysicalDiskDevice : IBlockDevice
    {
        private readonly object _sync = new object();
        private SafeFileHandle _handle;
        private AlignedBuffer _buffer;

        private PhysicalDiskDevice(SafeFileHandle handle, int diskNumber, long size, int sectorSize, bool writable)
        {
            _handle = handle;
            DiskNumber = diskNumber;
            Size = size;
            SectorSize = sectorSize;
            CanWrite = writable;
        }

        public int DiskNumber { get; }

        public long Size { get; }

        public int SectorSize { get; }

        public bool CanWrite { get; }

        internal SafeFileHandle Handle => _handle;

        public static string PathFor(int diskNumber)
        {
            return @"\\.\PhysicalDrive" + diskNumber.ToString(CultureInfo.InvariantCulture);
        }

        public static PhysicalDiskDevice Open(int diskNumber, bool writable)
        {
            uint access = NativeMethods.GenericRead | (writable ? NativeMethods.GenericWrite : 0);
            uint flags = NativeMethods.FileFlagNoBuffering | (writable ? NativeMethods.FileFlagWriteThrough : 0);
            var handle = DiskIoctl.Open(PathFor(diskNumber), access, flags);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw MapError(error, null, writable, "open disk " + diskNumber.ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                DiskIoctl.GetGeometry(handle, out long size, out int sectorSize);
                return new PhysicalDiskDevice(handle, diskNumber, size, sectorSize, writable);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                handle.Dispose();
                throw MapError(ex.NativeErrorCode, null, writable, "query geometry");
            }
        }

        public int Read(long offset, byte[] buffer, int index, int count)
        {
            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            lock (_sync)
            {
                var aligned = EnsureBuffer(count);
                int done = 0;
                while (done < count)
                {
                    Seek(offset + done, false);
                    if (!NativeMethods.ReadFile(_handle, aligned.Pointer, (uint)(count - done), out uint read, IntPtr.Zero))
                    {
                        throw MapError(Marshal.GetLastWin32Error(), offset + done, false, "read");
                    }

                    if (read == 0)
                    {
                        throw new ImagingException(ErrorCode.ReadError, "Unexpected end of device.", offset + done);
                    }

                    Marshal.Copy(aligned.Pointer, buffer, index + done, (int)read);
                    done += (int)read;
                }

                return done;
            }
        }

        public void Write(long offset, byte[] buffer, int index, int count)
        {
            if (!CanWrite)
            {
                throw new InvalidOperationException("Device was opened read-only.");
            }

            BlockDeviceGuard.CheckRange(this, offset, buffer, index, count);
            lock (_sync)
            {
                var aligned = EnsureBuffer(count);
                Marshal.Copy(buffer, index, aligned.Pointer, count);
                int done = 0;
                while (done < count)
                {
                    Seek(offset + done, true);
                    var source = IntPtr.Add(aligned.Pointer, done);
                    if (!NativeMethods.WriteFile(_handle, source, (uint)(count - done), out uint written, IntPtr.Zero))
                    {
                        throw MapError(Marshal.GetLastWin32Error(), offset + done, true, "write");
                    }

                    if (written == 0)
                    {
                        throw new ImagingException(ErrorCode.WriteError, "Device accepted no data.", offset + done);
                    }

                    done += (int)written;
                }
            }
        }

        public void Flush()
        {
            if (!CanWrite)
            {
                return;
            }

            lock (_sync)
            {
                if (!NativeMethods.FlushFileBuffers(_handle))
                {
                    throw MapError(Marshal.GetLastWin32Error(), null, true, "flush");
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _handle?.Dispose();
                _handle = null;
                _buffer?.Dispose();
                _buffer = null;
            }
        }

        internal static ImagingException MapError(int error, long? offset, bool writing, string what)
        {
            string detail = what + " failed, Win32 error " + error.ToString(CultureInfo.InvariantCulture);
            switch (error)
            {
                case NativeMethods.ErrorNoSuchDevice:
                case NativeMethods.ErrorDeviceNotConnected:
                case NativeMethods.ErrorNotReady:
                case NativeMethods.ErrorFileNotFound:
                case NativeMethods.ErrorMediaChanged:
                case NativeMethods.ErrorNoMediaInDrive:
                    return new ImagingException(ErrorCode.DeviceRemoved, detail, offset);
                case NativeMethods.ErrorWriteProtect:
                    return new ImagingException(ErrorCode.WriteProtected, detail, offset);
                default:
                    return new ImagingException(writing ? ErrorCode.WriteError : ErrorCode.ReadError, detail, offset);
            }
        }

        private void Seek(long offset, bool writing)
        {
            if (_handle == null)
            {
                throw new ObjectDisposedException(nameof(PhysicalDiskDevice));
            }

            if (!NativeMethods.SetFilePointerEx(_handle, offset, out _, NativeMethods.FileBegin))
            {
                throw MapError(Marshal.GetLastWin32Error(), offset, writing, "seek");
            }
        }

        private AlignedBuffer EnsureBuffer(int count)
        {
            if (_buffer == null || _buffer.Size < count)
            {
                _buffer?.Dispose();
                _buffer = new AlignedBuffer(Math.Max(count, SectorSize));
            }

            return _buffer;
        }
    }
}
