using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Native
{
    /// <summary>Raw storage descriptor fields read via IOCTL_STORAGE_QUERY_PROPERTY.</summary>
    internal sealed class StorageDescriptor
    {
        public uint BusType;
        public bool RemovableMedia;
        public string Vendor = string.Empty;
        public string Product = string.Empty;
        public string Serial = string.Empty;
    }

    /// <summary>Disk size information from IOCTL_DISK_GET_DRIVE_GEOMETRY_EX and IOCTL_DISK_GET_LENGTH_INFO.</summary>
    internal sealed class GeometryInfo
    {
        public long Cylinders;
        public int MediaType;
        public int TracksPerCylinder;
        public int SectorsPerTrack;
        public int SectorSize;
        public long GeometrySize;
        public long LengthInfo;
        public int GeometryError;
        public int LengthError;

        /// <summary>Usable size: length info when available, otherwise the geometry size.</summary>
        public long Size;

        public string Describe()
        {
            return string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Geometry: {0} (cyl {1}, {2} tracks, {3} sectors/track, {4} bytes/sector, media type {5}); length info: {6}; size used: {7} bytes",
                GeometryError == 0 ? GeometrySize + " bytes" : "failed, Win32 error " + GeometryError,
                Cylinders,
                TracksPerCylinder,
                SectorsPerTrack,
                SectorSize,
                MediaType == 11 ? "RemovableMedia" : MediaType == 12 ? "FixedMedia" : MediaType.ToString(System.Globalization.CultureInfo.InvariantCulture),
                LengthError == 0 ? LengthInfo + " bytes" : "failed, Win32 error " + LengthError,
                Size);
        }
    }

    /// <summary>Typed wrappers around disk and volume IOCTLs.</summary>
    internal static class DiskIoctl
    {
        public static SafeFileHandle Open(string path, uint access, uint flags = 0)
        {
            return NativeMethods.CreateFile(
                path, access, NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                IntPtr.Zero, NativeMethods.OpenExisting, flags, IntPtr.Zero);
        }

        /// <summary>Returns false and the Win32 error code when the IOCTL fails.</summary>
        public static bool Call(SafeFileHandle handle, uint code, byte[] input, byte[] output, out int bytesReturned, out int error)
        {
            var inHandle = input == null ? default(GCHandle) : GCHandle.Alloc(input, GCHandleType.Pinned);
            var outHandle = output == null ? default(GCHandle) : GCHandle.Alloc(output, GCHandleType.Pinned);
            try
            {
                bool ok = NativeMethods.DeviceIoControl(
                    handle,
                    code,
                    input == null ? IntPtr.Zero : inHandle.AddrOfPinnedObject(),
                    input == null ? 0u : (uint)input.Length,
                    output == null ? IntPtr.Zero : outHandle.AddrOfPinnedObject(),
                    output == null ? 0u : (uint)output.Length,
                    out uint returned,
                    IntPtr.Zero);
                error = ok ? 0 : Marshal.GetLastWin32Error();
                bytesReturned = (int)returned;
                return ok;
            }
            finally
            {
                if (inHandle.IsAllocated)
                {
                    inHandle.Free();
                }

                if (outHandle.IsAllocated)
                {
                    outHandle.Free();
                }
            }
        }

        public static int GetDeviceNumber(SafeFileHandle handle)
        {
            var output = new byte[12];
            if (!Call(handle, NativeMethods.IoctlStorageGetDeviceNumber, null, output, out _, out int error))
            {
                throw new Win32Exception(error);
            }

            return BitConverter.ToInt32(output, 4);
        }

        public static StorageDescriptor QueryStorageDescriptor(SafeFileHandle handle)
        {
            var query = new byte[12]; // PropertyId = StorageDeviceProperty (0), QueryType = Standard (0)
            var output = new byte[1024];
            if (!Call(handle, NativeMethods.IoctlStorageQueryProperty, query, output, out int returned, out int error))
            {
                throw new Win32Exception(error);
            }

            return new StorageDescriptor
            {
                RemovableMedia = output[10] != 0,
                Vendor = AnsiAt(output, BitConverter.ToInt32(output, 12), returned),
                Product = AnsiAt(output, BitConverter.ToInt32(output, 16), returned),
                Serial = AnsiAt(output, BitConverter.ToInt32(output, 24), returned),
                BusType = BitConverter.ToUInt32(output, 28)
            };
        }

        /// <summary>Returns (size in bytes, logical sector size); throws when no size is available.</summary>
        public static void GetGeometry(SafeFileHandle handle, out long size, out int sectorSize)
        {
            var info = QueryGeometry(handle);
            if (info.Size <= 0)
            {
                throw new Win32Exception(info.GeometryError != 0 ? info.GeometryError : info.LengthError);
            }

            size = info.Size;
            sectorSize = info.SectorSize;
        }

        /// <summary>Queries both size sources Windows offers; never throws.</summary>
        public static GeometryInfo QueryGeometry(SafeFileHandle handle)
        {
            var info = new GeometryInfo();
            var geometry = new byte[256];
            if (Call(handle, NativeMethods.IoctlDiskGetDriveGeometryEx, null, geometry, out _, out int geometryError))
            {
                info.Cylinders = BitConverter.ToInt64(geometry, 0);
                info.MediaType = BitConverter.ToInt32(geometry, 8);
                info.TracksPerCylinder = BitConverter.ToInt32(geometry, 12);
                info.SectorsPerTrack = BitConverter.ToInt32(geometry, 16);
                info.SectorSize = BitConverter.ToInt32(geometry, 20);
                info.GeometrySize = BitConverter.ToInt64(geometry, 24);
            }
            else
            {
                info.GeometryError = geometryError;
            }

            var length = new byte[8];
            if (Call(handle, NativeMethods.IoctlDiskGetLengthInfo, null, length, out _, out int lengthError))
            {
                info.LengthInfo = BitConverter.ToInt64(length, 0);
            }
            else
            {
                info.LengthError = lengthError;
            }

            if (info.SectorSize <= 0)
            {
                info.SectorSize = 512;
            }

            long size = info.LengthInfo > 0 ? info.LengthInfo : info.GeometrySize;
            info.Size = size > 0 ? size - (size % info.SectorSize) : 0;
            return info;
        }

        /// <summary>True when writable, false when write-protected; throws on other errors.</summary>
        public static bool IsWritable(SafeFileHandle handle)
        {
            if (Call(handle, NativeMethods.IoctlDiskIsWritable, null, null, out _, out int error))
            {
                return true;
            }

            if (error == NativeMethods.ErrorWriteProtect)
            {
                return false;
            }

            throw new Win32Exception(error);
        }

        public static int[] GetVolumeDiskNumbers(SafeFileHandle volume)
        {
            for (int capacity = 4; capacity <= 256; capacity *= 4)
            {
                var output = new byte[8 + (24 * capacity)];
                if (Call(volume, NativeMethods.IoctlVolumeGetVolumeDiskExtents, null, output, out _, out int error))
                {
                    int count = BitConverter.ToInt32(output, 0);
                    var disks = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        disks[i] = BitConverter.ToInt32(output, 8 + (24 * i));
                    }

                    return disks;
                }

                if (error != NativeMethods.ErrorMoreData && error != NativeMethods.ErrorInsufficientBuffer)
                {
                    return Array.Empty<int>();
                }
            }

            return Array.Empty<int>();
        }

        private static string AnsiAt(byte[] buffer, int offset, int limit)
        {
            if (offset <= 0 || offset >= limit)
            {
                return string.Empty;
            }

            int end = offset;
            while (end < limit && buffer[end] != 0)
            {
                end++;
            }

            return Encoding.Default.GetString(buffer, offset, end - offset).Trim();
        }
    }
}
