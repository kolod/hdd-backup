using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using HddBackup.Core.Errors;
using HddBackup.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Exclusive write access: every volume on the disk is locked and dismounted before the
    /// disk is opened for writing; locks are held until disposal.
    /// </summary>
    public sealed class DeviceLock : IExclusiveDevice
    {
        private const int LockAttempts = 20;
        private const int LockRetryDelayMs = 100;

        private readonly List<SafeFileHandle> _volumes;
        private PhysicalDiskDevice _disk;

        private DeviceLock(List<SafeFileHandle> volumes, PhysicalDiskDevice disk)
        {
            _volumes = volumes;
            _disk = disk;
        }

        public IBlockDevice Device => _disk;

        public static DeviceLock Acquire(DeviceInfo device)
        {
            var handles = new List<SafeFileHandle>();
            try
            {
                // Re-read the volume list: it may have changed since the device was listed.
                var map = VolumeHelper.MapVolumesToDisks();
                var volumes = map.TryGetValue(device.DiskNumber, out var list) ? list : new List<VolumeInfo>();
                foreach (var volume in volumes)
                {
                    handles.Add(LockAndDismount(volume));
                }

                var disk = PhysicalDiskDevice.Open(device.DiskNumber, writable: true);
                if (!DiskIoctl.IsWritable(disk.Handle))
                {
                    disk.Dispose();
                    throw new ImagingException(ErrorCode.WriteProtected, "Medium is write-protected.");
                }

                return new DeviceLock(handles, disk);
            }
            catch
            {
                ReleaseVolumes(handles);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disk != null)
            {
                // Ask Windows to re-read the (possibly new) partition table.
                DiskIoctl.Call(_disk.Handle, NativeMethods.IoctlDiskUpdateProperties, null, null, out _, out _);
                _disk.Dispose();
                _disk = null;
            }

            ReleaseVolumes(_volumes);
        }

        private static SafeFileHandle LockAndDismount(VolumeInfo volume)
        {
            string label = volume.MountPoints.Count > 0 ? string.Join(", ", volume.MountPoints) : volume.VolumeName;
            var handle = DiskIoctl.Open(volume.VolumeName.TrimEnd('\\'), NativeMethods.GenericRead | NativeMethods.GenericWrite);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new ImagingException(ErrorCode.LockFailed, "Cannot open volume " + label);
            }

            bool locked = false;
            int error = 0;
            for (int attempt = 0; attempt < LockAttempts && !locked; attempt++)
            {
                locked = DiskIoctl.Call(handle, NativeMethods.FsctlLockVolume, null, null, out _, out error);
                if (!locked)
                {
                    Thread.Sleep(LockRetryDelayMs);
                }
            }

            if (!locked)
            {
                handle.Dispose();
                throw new ImagingException(
                    ErrorCode.LockFailed,
                    string.Format(CultureInfo.InvariantCulture, "Volume {0} is in use (Win32 error {1}).", label, error));
            }

            if (!DiskIoctl.Call(handle, NativeMethods.FsctlDismountVolume, null, null, out _, out error))
            {
                DiskIoctl.Call(handle, NativeMethods.FsctlUnlockVolume, null, null, out _, out _);
                handle.Dispose();
                throw new ImagingException(
                    ErrorCode.LockFailed,
                    string.Format(CultureInfo.InvariantCulture, "Cannot dismount volume {0} (Win32 error {1}).", label, error));
            }

            return handle;
        }

        private static void ReleaseVolumes(List<SafeFileHandle> handles)
        {
            foreach (var handle in handles.Where(h => !h.IsInvalid && !h.IsClosed))
            {
                DiskIoctl.Call(handle, NativeMethods.FsctlUnlockVolume, null, null, out _, out _);
                handle.Dispose();
            }

            handles.Clear();
        }
    }
}
