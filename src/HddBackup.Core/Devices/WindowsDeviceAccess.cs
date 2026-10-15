using System;
using System.IO;
using System.Linq;
using System.Text;
using HddBackup.Core.Errors;
using HddBackup.Core.Native;

namespace HddBackup.Core.Devices
{
    /// <summary>Real device access through Win32.</summary>
    public sealed class WindowsDeviceAccess : IDeviceAccess
    {
        public IBlockDevice OpenForRead(DeviceInfo device)
        {
            VerifyIdentity(device);
            return PhysicalDiskDevice.Open(device.DiskNumber, writable: false);
        }

        public IExclusiveDevice OpenExclusiveForWrite(DeviceInfo device)
        {
            VerifyIdentity(device);
            return DeviceLock.Acquire(device);
        }

        /// <summary>
        /// Disk numbers are reused when media are swapped. Before touching a disk, make sure the
        /// number still belongs to the same USB/SD device and has not become a protected disk.
        /// </summary>
        private static void VerifyIdentity(DeviceInfo device)
        {
            if (SystemDiskGuard.GetSystemDisks().Contains(device.DiskNumber))
            {
                throw new ImagingException(ErrorCode.DeviceChanged, "The disk number now belongs to a system disk.");
            }

            using (var handle = DiskIoctl.Open(PhysicalDiskDevice.PathFor(device.DiskNumber), 0))
            {
                if (handle.IsInvalid)
                {
                    throw new ImagingException(ErrorCode.DeviceRemoved, "The device is no longer present.");
                }

                StorageDescriptor descriptor;
                try
                {
                    descriptor = DiskIoctl.QueryStorageDescriptor(handle);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw PhysicalDiskDevice.MapError(ex.NativeErrorCode, null, false, "query device");
                }

                string model = string.Join(" ", new[] { descriptor.Vendor, descriptor.Product }.Where(s => s.Length > 0));
                bool busOk = DeviceEnumerator.IsAcceptedBus(descriptor.BusType, descriptor.RemovableMedia);
                if (!busOk
                    || !string.Equals(model, device.Model, StringComparison.Ordinal)
                    || !string.Equals(descriptor.Serial, device.SerialNumber, StringComparison.Ordinal))
                {
                    throw new ImagingException(ErrorCode.DeviceChanged, "A different device now uses disk number " + device.DiskNumber + ".");
                }
            }
        }

        public EjectResult Eject(DeviceInfo device)
        {
            return DeviceEjector.Eject(device);
        }

        public PathInfo GetPathInfo(string path)
        {
            var info = new PathInfo();
            string directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                throw new ImagingException(ErrorCode.InvalidRequest, "Invalid image path: " + ex.Message);
            }

            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return info;
            }

            try
            {
                info.DiskNumbers = VolumeHelper.GetDiskNumbersForPath(directory);
            }
            catch (Exception)
            {
                // Unknown (network share or virtual volume).
            }

            if (NativeMethods.GetDiskFreeSpaceEx(directory, out ulong free, out _, out _))
            {
                info.FreeBytes = (long)Math.Min(free, long.MaxValue);
            }

            var root = VolumeHelper.GetMountRootForPath(directory);
            if (root != null)
            {
                var fs = new StringBuilder(64);
                if (NativeMethods.GetVolumeInformation(root, null, 0, out _, out _, out _, fs, fs.Capacity))
                {
                    info.FileSystem = fs.ToString();
                }
            }

            return info;
        }
    }
}
