using System;
using System.Collections.Generic;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Devices
{
    public enum BusType
    {
        Usb,
        Sd,
        Mmc,

        /// <summary>Removable media on another bus, e.g. a built-in card reader reporting SCSI.</summary>
        Other
    }

    public sealed class VolumeInfo
    {
        /// <summary>Volume GUID path, e.g. <c>\\?\Volume{...}\</c>.</summary>
        public string VolumeName { get; set; } = string.Empty;

        /// <summary>Drive letters and folder mount points, e.g. <c>E:\</c>.</summary>
        public IReadOnlyList<string> MountPoints { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// A removable USB/SD/MMC disk. Disks hosting system, boot or page-file volumes are never
    /// turned into a <see cref="DeviceInfo"/>.
    /// </summary>
    public sealed class DeviceInfo
    {
        public int DiskNumber { get; set; }

        public string DevicePath { get; set; } = string.Empty;

        /// <summary>Configuration manager device instance, used for safe removal.</summary>
        public uint DevInst { get; set; }

        public string Model { get; set; } = string.Empty;

        public string SerialNumber { get; set; } = string.Empty;

        public BusType BusType { get; set; }

        /// <summary>False for USB hard disks and SSDs ("external disk"): writes need extra confirmation.</summary>
        public bool IsRemovableMedia { get; set; } = true;

        public long Size { get; set; }

        public int LogicalSectorSize { get; set; } = 512;

        public bool IsWriteProtected { get; set; }

        /// <summary>The running executable lives on this disk; it is never a restore target.</summary>
        public bool HostsExecutable { get; set; }

        public PartitionTable PartitionTable { get; set; } = PartitionTable.Empty;

        public IReadOnlyList<VolumeInfo> Volumes { get; set; } = Array.Empty<VolumeInfo>();

        /// <summary>
        /// The reader exposes less capacity than the card's partition table describes (typical for
        /// old built-in readers with SDHC cards). Such a device is listed but cannot be used:
        /// any backup or restore through it would be incomplete.
        /// </summary>
        public bool IsCapacityMisreported => PartitionTable.ExtendsBeyondDevice;
    }
}
