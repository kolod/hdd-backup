using System;
using System.Collections.Generic;

namespace HddBackup.Core.Devices
{
    public interface IDeviceEnumerator
    {
        /// <summary>Only USB/SD/MMC disks not hosting system, boot or page-file volumes.</summary>
        IReadOnlyList<DeviceInfo> GetDevices();
    }

    /// <summary>A device opened for writing with all its volumes locked and dismounted.</summary>
    public interface IExclusiveDevice : IDisposable
    {
        IBlockDevice Device { get; }
    }

    public enum EjectStatus
    {
        Ok,
        Vetoed,
        NotSupported
    }

    public sealed class EjectResult
    {
        public EjectResult(EjectStatus status, string vetoedBy = null)
        {
            Status = status;
            VetoedBy = vetoedBy;
        }

        public EjectStatus Status { get; }

        /// <summary>Name of the device or process that refused removal, when known.</summary>
        public string VetoedBy { get; }
    }

    /// <summary>Facts about the location of an image file.</summary>
    public sealed class PathInfo
    {
        /// <summary>Disks hosting the volume of the path (empty when unknown, e.g. network shares).</summary>
        public IReadOnlyList<int> DiskNumbers { get; set; } = Array.Empty<int>();

        public long? FreeBytes { get; set; }

        /// <summary>File system name such as NTFS or FAT32; empty when unknown.</summary>
        public string FileSystem { get; set; } = string.Empty;
    }

    public interface IDeviceAccess
    {
        IBlockDevice OpenForRead(DeviceInfo device);

        /// <summary>
        /// Locks and dismounts every volume of the device, then opens it for write-through
        /// writes. Throws <see cref="Errors.ImagingException"/> (LockFailed) without writing
        /// anything if exclusive access cannot be obtained. Disposing releases the locks and
        /// makes Windows re-read the partition table.
        /// </summary>
        IExclusiveDevice OpenExclusiveForWrite(DeviceInfo device);

        /// <summary>
        /// Opens the disk writable for changing one partition in place (feature 010): nothing is locked or
        /// dismounted, and the returned device only reaches the given byte range, so the partition table, the
        /// other partitions and their mounted volumes cannot be written to. Throws
        /// <see cref="Errors.ImagingException"/> when the device changed or cannot be opened for writing.
        /// </summary>
        IBlockDevice OpenForPartitionWrite(DeviceInfo device, long partitionOffset, long partitionLength);

        EjectResult Eject(DeviceInfo device);

        PathInfo GetPathInfo(string path);
    }
}
