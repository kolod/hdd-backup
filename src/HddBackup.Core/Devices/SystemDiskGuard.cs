using System;
using System.Collections.Generic;
using System.Text;
using HddBackup.Core.Native;
using Microsoft.Win32;

namespace HddBackup.Core.Devices
{
    /// <summary>
    /// Finds disks that must never be listed (Windows, boot and page-file volumes) and disks
    /// that host the running executable (never a restore target).
    /// </summary>
    public static class SystemDiskGuard
    {
        public static HashSet<int> GetSystemDisks()
        {
            var disks = new HashSet<int>();

            var windows = new StringBuilder(260);
            if (NativeMethods.GetSystemWindowsDirectory(windows, 260) > 0)
            {
                AddPath(disks, windows.ToString());
            }

            AddPath(disks, Environment.GetEnvironmentVariable("SystemDrive") + "\\");

            foreach (var drive in GetPageFileDrives())
            {
                AddPath(disks, drive);
            }

            AddBootPartitionDisks(disks);
            return disks;
        }

        public static HashSet<int> GetExecutableDisks()
        {
            var disks = new HashSet<int>();
            AddPath(disks, AppDomain.CurrentDomain.BaseDirectory);
            return disks;
        }

        private static void AddPath(HashSet<int> disks, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                foreach (int disk in VolumeHelper.GetDiskNumbersForPath(path))
                {
                    disks.Add(disk);
                }
            }
            catch (Exception)
            {
                // Unknown location (e.g. network share): nothing to protect.
            }
        }

        private static IEnumerable<string> GetPageFileDrives()
        {
            var result = new List<string>();
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
                {
                    if (key?.GetValue("ExistingPageFiles") is string[] files)
                    {
                        foreach (var file in files)
                        {
                            // Entries look like "\??\C:\pagefile.sys".
                            int colon = file.IndexOf(':');
                            if (colon > 0)
                            {
                                result.Add(file.Substring(colon - 1, 2) + "\\");
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Registry not readable: page files then live on the Windows disk, which is protected.
            }

            return result;
        }

        private static void AddBootPartitionDisks(HashSet<int> disks)
        {
            string systemPartition;
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup"))
                {
                    systemPartition = key?.GetValue("SystemPartition") as string;
                }
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrEmpty(systemPartition))
            {
                return;
            }

            foreach (var volume in VolumeHelper.EnumerateVolumes())
            {
                if (string.Equals(VolumeHelper.GetNtDeviceName(volume), systemPartition, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (int disk in VolumeHelper.GetDiskNumbers(volume))
                    {
                        disks.Add(disk);
                    }
                }
            }
        }
    }
}
