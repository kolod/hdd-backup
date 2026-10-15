using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Native
{
    /// <summary>
    /// Read-only SCSI commands sent through IOCTL_SCSI_PASS_THROUGH_DIRECT. This bypasses the
    /// capacity check of the Windows disk class driver, which matters for card reader drivers
    /// that report a wrong (too small) capacity. Only READ CAPACITY(10) and READ(10) are issued.
    /// </summary>
    internal static class ScsiPassThrough
    {
        public const uint IoctlScsiPassThroughDirect = 0x4D014;
        private const byte DataIn = 1;
        private const uint TimeoutSeconds = 15;
        private const int SenseLength = 32;

        /// <summary>Returns false with a description on failure.</summary>
        public static bool ReadCapacity10(SafeFileHandle handle, out long lastLba, out int blockLength, out string error)
        {
            lastLba = 0;
            blockLength = 0;
            var cdb = new byte[10];
            cdb[0] = 0x25;
            using (var buffer = new AlignedBuffer(4096))
            {
                if (!Execute(handle, cdb, buffer, 8, out error))
                {
                    return false;
                }

                var data = new byte[8];
                Marshal.Copy(buffer.Pointer, data, 0, 8);
                lastLba = ((long)data[0] << 24) | ((long)data[1] << 16) | ((long)data[2] << 8) | data[3];
                blockLength = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                return true;
            }
        }

        /// <summary>Reads <paramref name="blocks"/> blocks at <paramref name="lba"/> with READ(10).</summary>
        public static bool Read10(SafeFileHandle handle, long lba, int blocks, int blockSize, byte[] destination, out string error)
        {
            if (lba < 0 || lba > uint.MaxValue || blocks <= 0 || blocks > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(lba));
            }

            int length = blocks * blockSize;
            var cdb = new byte[10];
            cdb[0] = 0x28;
            cdb[2] = (byte)(lba >> 24);
            cdb[3] = (byte)(lba >> 16);
            cdb[4] = (byte)(lba >> 8);
            cdb[5] = (byte)lba;
            cdb[7] = (byte)(blocks >> 8);
            cdb[8] = (byte)blocks;
            using (var buffer = new AlignedBuffer(Math.Max(length, 4096)))
            {
                if (!Execute(handle, cdb, buffer, length, out error))
                {
                    return false;
                }

                Marshal.Copy(buffer.Pointer, destination, 0, length);
                return true;
            }
        }

        private static bool Execute(SafeFileHandle handle, byte[] cdb, AlignedBuffer data, int dataLength, out string error)
        {
            var request = new SptdWithSense
            {
                Spt = new Sptd
                {
                    Length = (ushort)Marshal.SizeOf(typeof(Sptd)),
                    CdbLength = (byte)cdb.Length,
                    SenseInfoLength = SenseLength,
                    DataIn = DataIn,
                    DataTransferLength = (uint)dataLength,
                    TimeOutValue = TimeoutSeconds,
                    DataBuffer = data.Pointer,
                    SenseInfoOffset = (uint)Marshal.OffsetOf(typeof(SptdWithSense), nameof(SptdWithSense.Sense)),
                    Cdb = new byte[16]
                },
                Sense = new byte[SenseLength]
            };
            Buffer.BlockCopy(cdb, 0, request.Spt.Cdb, 0, cdb.Length);

            int size = Marshal.SizeOf(typeof(SptdWithSense));
            IntPtr native = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(request, native, false);
                bool ok = NativeMethods.DeviceIoControl(
                    handle, IoctlScsiPassThroughDirect, native, (uint)size, native, (uint)size, out _, IntPtr.Zero);
                if (!ok)
                {
                    int win32 = Marshal.GetLastWin32Error();
                    error = "DeviceIoControl failed, Win32 error " + win32.ToString(CultureInfo.InvariantCulture)
                        + (win32 == 50 ? " (pass-through not supported by the driver)" : string.Empty);
                    return false;
                }

                var reply = (SptdWithSense)Marshal.PtrToStructure(native, typeof(SptdWithSense));
                if (reply.Spt.ScsiStatus != 0)
                {
                    var s = reply.Sense;
                    error = string.Format(
                        CultureInfo.InvariantCulture,
                        "SCSI status 0x{0:X2}, sense key 0x{1:X}, ASC 0x{2:X2}, ASCQ 0x{3:X2}",
                        reply.Spt.ScsiStatus,
                        s[2] & 0x0F,
                        s[12],
                        s[13]);
                    return false;
                }

                if (reply.Spt.DataTransferLength < dataLength)
                {
                    error = "short transfer: " + reply.Spt.DataTransferLength + " of " + dataLength + " bytes";
                    return false;
                }

                error = null;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Sptd
        {
            public ushort Length;
            public byte ScsiStatus;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte CdbLength;
            public byte SenseInfoLength;
            public byte DataIn;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public IntPtr DataBuffer;
            public uint SenseInfoOffset;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Cdb;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SptdWithSense
        {
            public Sptd Spt;
            public uint Filler;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = SenseLength)]
            public byte[] Sense;
        }
    }
}
