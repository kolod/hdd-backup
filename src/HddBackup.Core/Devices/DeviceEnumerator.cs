using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using HddBackup.Core.Native;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Lists USB, SD/MMC and other removable-media disks, excluding protected system disks.
    /// The same probe also produces a diagnostic report explaining why each disk was or was not
    /// listed (built-in card readers report many different bus types).
    /// </summary>
    public sealed class DeviceEnumerator : IDeviceEnumerator
    {
        internal const uint BusTypeUsb = 7;
        internal const uint BusTypeSd = 0x0C;
        internal const uint BusTypeMmc = 0x0D;

        private static readonly string[] BusTypeNames =
        {
            "Unknown", "Scsi", "Atapi", "Ata", "1394", "Ssa", "Fibre", "Usb", "RAID", "iScsi", "Sas", "Sata",
            "Sd", "Mmc", "Virtual", "FileBackedVirtual", "Spaces", "Nvme", "SCM", "Ufs"
        };

        public IReadOnlyList<DeviceInfo> GetDevices()
        {
            return Probe().Where(p => p.Device != null).Select(p => p.Device).OrderBy(d => d.DiskNumber).ToList();
        }

        /// <summary>
        /// Accepts a disk when it is on a USB/SD/MMC bus or reports removable media (built-in
        /// readers often use other bus types, e.g. SCSI or Unknown). System disks are excluded
        /// separately.
        /// </summary>
        internal static bool IsAcceptedBus(uint busType, bool removableMedia)
        {
            return busType == BusTypeUsb || busType == BusTypeSd || busType == BusTypeMmc || removableMedia;
        }

        internal static BusType MapBus(uint busType)
        {
            switch (busType)
            {
                case BusTypeUsb:
                    return BusType.Usb;
                case BusTypeSd:
                    return BusType.Sd;
                case BusTypeMmc:
                    return BusType.Mmc;
                default:
                    return BusType.Other;
            }
        }

        /// <summary>Plain-text report of every disk interface and the listing decision.</summary>
        public string BuildDiagnosticReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("HddBackup device diagnostics");
            sb.AppendLine("Time (UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("OS: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
            sb.AppendLine("Process: " + (Environment.Is64BitProcess ? "64-bit" : "32-bit") + ", CLR " + Environment.Version);
            sb.AppendLine("Administrator: " + IsAdministrator());
            sb.AppendLine("Program version: " + typeof(DeviceEnumerator).Assembly.GetName().Version);
            sb.AppendLine("Protected system disks: " + Join(SafeSet(SystemDiskGuard.GetSystemDisks)));
            sb.AppendLine("Disks hosting this program: " + Join(SafeSet(SystemDiskGuard.GetExecutableDisks)));
            sb.AppendLine();

            var probes = Probe();
            if (probes.Count == 0)
            {
                sb.AppendLine("No disk interfaces were reported by Windows.");
            }

            foreach (var probe in probes)
            {
                if (probe.Device != null && probe.Device.PartitionTable.ExtendsBeyondDevice)
                {
                    try
                    {
                        CapacityProbe.Run(probe.Device, probe.Facts);
                    }
                    catch (Exception ex)
                    {
                        probe.Facts.Add("Pass-through probe crashed: " + ex.Message);
                    }
                }

                sb.AppendLine(probe.Device != null ? "[LISTED]" : "[NOT LISTED] " + probe.Reason);
                foreach (var fact in probe.Facts)
                {
                    sb.AppendLine("    " + fact);
                }

                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static List<ProbeResult> Probe()
        {
            var systemDisks = SafeSet(SystemDiskGuard.GetSystemDisks);
            var executableDisks = SafeSet(SystemDiskGuard.GetExecutableDisks);
            Dictionary<int, List<VolumeInfo>> volumes;
            try
            {
                volumes = VolumeHelper.MapVolumesToDisks();
            }
            catch (Exception)
            {
                volumes = new Dictionary<int, List<VolumeInfo>>();
            }

            var results = new List<ProbeResult>();
            foreach (var (path, devInst) in EnumerateDiskInterfaces())
            {
                var probe = ProbeOne(path, devInst, systemDisks);
                if (probe.Device != null)
                {
                    probe.Device.HostsExecutable = executableDisks.Contains(probe.Device.DiskNumber);
                    probe.Device.Volumes = volumes.TryGetValue(probe.Device.DiskNumber, out var list)
                        ? (IReadOnlyList<VolumeInfo>)list
                        : Array.Empty<VolumeInfo>();
                    probe.Facts.Add("Volumes: " + string.Join(" ", probe.Device.Volumes.SelectMany(v => v.MountPoints).DefaultIfEmpty("(none)")));
                    probe.Facts.Add("Runs this program: " + probe.Device.HostsExecutable);
                }

                results.Add(probe);
            }

            return results;
        }

        private static ProbeResult ProbeOne(string path, uint devInst, HashSet<int> systemDisks)
        {
            var probe = new ProbeResult();
            probe.Facts.Add("Interface: " + path);
            using (var handle = DiskIoctl.Open(path, NativeMethods.GenericRead))
            {
                if (handle.IsInvalid)
                {
                    probe.Reason = "cannot open for reading (Win32 error " + Marshal.GetLastWin32Error() + ")";
                    return probe;
                }

                StorageDescriptor descriptor;
                int number;
                try
                {
                    number = DiskIoctl.GetDeviceNumber(handle);
                    probe.Facts.Add("Disk number: " + number + " (\\\\.\\PhysicalDrive" + number + ")");
                    descriptor = DiskIoctl.QueryStorageDescriptor(handle);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    probe.Reason = "storage query failed (Win32 error " + ex.NativeErrorCode + ")";
                    return probe;
                }

                string model = string.Join(" ", new[] { descriptor.Vendor, descriptor.Product }.Where(s => s.Length > 0));
                probe.Facts.Add("Model: " + (model.Length > 0 ? model : "(empty)") + ", serial: " + (descriptor.Serial.Length > 0 ? descriptor.Serial : "(empty)"));
                probe.Facts.Add("Bus type: " + BusName(descriptor.BusType) + " (" + descriptor.BusType + "), removable media flag: " + descriptor.RemovableMedia);

                if (systemDisks.Contains(number))
                {
                    probe.Reason = "system, boot or page-file disk (protected)";
                    return probe;
                }

                if (!IsAcceptedBus(descriptor.BusType, descriptor.RemovableMedia))
                {
                    probe.Reason = "fixed disk on bus " + BusName(descriptor.BusType) + " (not USB/SD/MMC and not removable media)";
                    return probe;
                }

                var geometry = DiskIoctl.QueryGeometry(handle);
                probe.Facts.Add(geometry.Describe());
                if (geometry.Size <= 0)
                {
                    probe.Reason = "no medium or size unknown";
                    return probe;
                }

                bool writable = true;
                try
                {
                    writable = DiskIoctl.IsWritable(handle);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    probe.Facts.Add("Write-protection query failed (Win32 error " + ex.NativeErrorCode + "), assuming writable");
                }

                probe.Facts.Add("Write-protected: " + !writable);

                var device = new DeviceInfo
                {
                    DiskNumber = number,
                    DevicePath = path,
                    DevInst = devInst,
                    Model = model,
                    SerialNumber = descriptor.Serial,
                    BusType = MapBus(descriptor.BusType),
                    IsRemovableMedia = descriptor.RemovableMedia || descriptor.BusType != BusTypeUsb,
                    Size = geometry.Size,
                    LogicalSectorSize = geometry.SectorSize,
                    IsWriteProtected = !writable
                };

                device.PartitionTable = ReadTable(number, probe.Facts);
                probe.Device = device;
                return probe;
            }
        }

        private static PartitionTable ReadTable(int diskNumber, List<string> facts)
        {
            try
            {
                using (var disk = PhysicalDiskDevice.Open(diskNumber, writable: false))
                {
                    var sector0 = new byte[disk.SectorSize];
                    disk.Read(0, sector0, 0, sector0.Length);
                    facts.Add("Sector 0 signature: " + (MbrParser.HasSignature(sector0) ? "55 AA" : "missing")
                        + ", boot sector file system: " + (FileSystemProbe.Detect(sector0) ?? "none"));
                    if (MbrParser.HasSignature(sector0))
                    {
                        facts.AddRange(MbrParser.DescribeEntries(sector0));
                    }

                    var table = PartitionTableReader.Read(disk);
                    if (table.ExtendsBeyondDevice)
                    {
                        facts.Add("WARNING: data extends beyond the reported disk size (" + disk.Size + " bytes); the reader hides part of the medium. Device is NOT usable.");
                    }
                    else if (table.EntriesOvershootDevice)
                    {
                        facts.Add("NOTE: a partition entry overshoots the reported disk size, but its file system fits; the device is usable (sloppy partition table, e.g. formatted by a camera).");
                    }
                    facts.Add("Partition table: " + table.Style + ", " + table.Partitions.Count + " partition(s)");
                    foreach (var p in table.Partitions)
                    {
                        facts.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            "  #{0}: offset {1}, length {2}, type {3}, {4}, file system size {5}",
                            p.Number,
                            p.Offset,
                            p.Length,
                            p.MbrType.HasValue ? "0x" + p.MbrType.Value.ToString("X2", CultureInfo.InvariantCulture) : p.GptType?.ToString(),
                            p.FileSystem,
                            p.FileSystemSize.HasValue ? p.FileSystemSize.Value.ToString(CultureInfo.InvariantCulture) : "unknown"));
                    }

                    return table;
                }
            }
            catch (Exception ex)
            {
                facts.Add("Partition table read failed: " + ex.Message);
                return PartitionTable.Damaged;
            }
        }

        private static string BusName(uint busType)
        {
            return busType < BusTypeNames.Length ? BusTypeNames[busType] : "0x" + busType.ToString("X", CultureInfo.InvariantCulture);
        }

        private static HashSet<int> SafeSet(Func<HashSet<int>> source)
        {
            try
            {
                return source();
            }
            catch (Exception)
            {
                return new HashSet<int>();
            }
        }

        private static string Join(IEnumerable<int> values)
        {
            var list = values.OrderBy(v => v).ToList();
            return list.Count == 0 ? "(none)" : string.Join(", ", list.Select(v => "#" + v));
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static IEnumerable<(string Path, uint DevInst)> EnumerateDiskInterfaces()
        {
            var result = new List<(string, uint)>();
            var guid = NativeMethods.GuidDevInterfaceDisk;
            var set = NativeMethods.SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero, NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
            {
                return result;
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    var data = new NativeMethods.SpDeviceInterfaceData { CbSize = Marshal.SizeOf(typeof(NativeMethods.SpDeviceInterfaceData)) };
                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    {
                        break;
                    }

                    var devInfo = new NativeMethods.SpDevinfoData { CbSize = Marshal.SizeOf(typeof(NativeMethods.SpDevinfoData)) };
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint required, ref devInfo);
                    if (required == 0)
                    {
                        continue;
                    }

                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize: 8 on x64, 6 on x86 (DWORD + one WCHAR, packed).
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out _, ref devInfo))
                        {
                            var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                            result.Add((path, devInfo.DevInst));
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(set);
            }

            return result;
        }

        private sealed class ProbeResult
        {
            public DeviceInfo Device;
            public string Reason = string.Empty;
            public readonly List<string> Facts = new List<string>();
        }
    }
}
