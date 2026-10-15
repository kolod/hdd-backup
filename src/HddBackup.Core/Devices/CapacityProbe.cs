using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using HddBackup.Core.Native;
using HddBackup.Core.Partitioning;
using Microsoft.Win32.SafeHandles;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Diagnostic, strictly read-only: checks whether sectors beyond the capacity a driver reports
    /// can be read through SCSI pass-through (READ CAPACITY(10) and READ(10) only).
    /// </summary>
    internal static class CapacityProbe
    {
        private const int ProbeBlocks = 8;

        public static void Run(DeviceInfo device, List<string> facts)
        {
            facts.Add("Pass-through probe (read-only, READ CAPACITY/READ commands only):");
            int ss = device.LogicalSectorSize;
            long reportedSectors = device.Size / ss;

            // Pass-through normally requires read/write access to the handle; no write command is sent.
            var handle = DiskIoctl.Open(PhysicalDiskDevice.PathFor(device.DiskNumber), NativeMethods.GenericRead | NativeMethods.GenericWrite);
            string access = "read/write handle";
            if (handle.IsInvalid)
            {
                handle.Dispose();
                handle = DiskIoctl.Open(PhysicalDiskDevice.PathFor(device.DiskNumber), NativeMethods.GenericRead);
                access = "read-only handle";
            }

            using (handle)
            {
                if (handle.IsInvalid)
                {
                    facts.Add("  cannot open the disk (Win32 error " + Marshal.GetLastWin32Error() + ")");
                    return;
                }

                facts.Add("  using " + access);
                if (ScsiPassThrough.ReadCapacity10(handle, out long lastLba, out int blockLength, out string error))
                {
                    facts.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "  READ CAPACITY(10): last LBA {0}, block {1} bytes => {2} bytes",
                        lastLba,
                        blockLength,
                        (lastLba + 1) * blockLength));
                }
                else
                {
                    facts.Add("  READ CAPACITY(10) failed: " + error);
                }

                var viaPassThrough = new byte[ProbeBlocks * ss];
                var viaDisk = new byte[ProbeBlocks * ss];
                if (!ReadPt(handle, 0, ss, viaPassThrough, facts, "LBA 0"))
                {
                    facts.Add("VERDICT: the driver does not support SCSI pass-through, so the reported capacity cannot be bypassed. Use another card reader (for example a USB one) or a driver with SDHC support.");
                    return;
                }

                bool sane = ReadDisk(device, 0, viaDisk) && viaPassThrough.SequenceEqual(viaDisk);
                facts.Add("  LBA 0 via pass-through " + (sane ? "matches the normal read" : "does NOT match the normal read"));
                if (!sane)
                {
                    facts.Add("VERDICT: pass-through reads return different data; they are not usable on this device.");
                    return;
                }

                bool beyondOk = ReadPt(handle, reportedSectors, ss, viaPassThrough, facts, "first LBA past reported end (" + reportedSectors + ")");
                bool wrapped = beyondOk && ReadDisk(device, 0, viaDisk) && viaPassThrough.SequenceEqual(viaDisk);
                if (wrapped)
                {
                    facts.Add("  data past the end equals LBA 0: the reader wraps addresses around");
                }

                bool partitionOk = true;
                foreach (var p in device.PartitionTable.Partitions.Where(p => p.End > device.Size))
                {
                    long start = p.Offset / ss;
                    long last = (p.End / ss) - 1;
                    if (ReadPt(handle, start, ss, viaPassThrough, facts, "partition #" + p.Number + " start (LBA " + start + ")"))
                    {
                        string fs = FileSystemProbe.Detect(viaPassThrough);
                        long wrappedLba = start % reportedSectors;
                        bool wraps = start >= reportedSectors
                            && wrappedLba + ProbeBlocks <= reportedSectors
                            && ReadDisk(device, wrappedLba, viaDisk)
                            && viaPassThrough.SequenceEqual(viaDisk);
                        facts.Add("    file system signature: " + (fs ?? "none") + (wraps ? " (WRAPPED data)" : string.Empty));
                        partitionOk &= fs != null && !wraps;
                    }
                    else
                    {
                        partitionOk = false;
                    }

                    var one = new byte[ss];
                    partitionOk &= ReadPtSingle(handle, last, ss, one, facts, "partition #" + p.Number + " last sector (LBA " + last + ")");
                }

                facts.Add(beyondOk && !wrapped && partitionOk
                    ? "VERDICT: the whole medium is readable through pass-through; a full backup is possible with a pass-through reader."
                    : "VERDICT: sectors past the reported capacity are NOT readable; use another card reader.");
            }
        }

        private static bool ReadPt(SafeFileHandle handle, long lba, int ss, byte[] destination, List<string> facts, string label)
        {
            bool ok = ScsiPassThrough.Read10(handle, lba, ProbeBlocks, ss, destination, out string error);
            facts.Add("  read " + label + ": " + (ok ? "OK" : "FAILED, " + error));
            return ok;
        }

        private static bool ReadPtSingle(SafeFileHandle handle, long lba, int ss, byte[] destination, List<string> facts, string label)
        {
            bool ok = ScsiPassThrough.Read10(handle, lba, 1, ss, destination, out string error);
            facts.Add("  read " + label + ": " + (ok ? "OK" : "FAILED, " + error));
            return ok;
        }

        private static bool ReadDisk(DeviceInfo device, long lba, byte[] destination)
        {
            try
            {
                using (var disk = PhysicalDiskDevice.Open(device.DiskNumber, writable: false))
                {
                    disk.Read(lba * device.LogicalSectorSize, destination, 0, destination.Length);
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
