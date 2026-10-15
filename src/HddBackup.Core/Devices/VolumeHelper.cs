using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HddBackup.Core.Native;

namespace HddBackup.Core.Devices
{
    /// <summary>Volume enumeration and volume-to-disk mapping.</summary>
    internal static class VolumeHelper
    {
        private const int MaxPath = 1024;

        /// <summary>Volume GUID paths with trailing backslash.</summary>
        public static List<string> EnumerateVolumes()
        {
            var result = new List<string>();
            var name = new StringBuilder(MaxPath);
            using (var find = NativeMethods.FindFirstVolume(name, MaxPath))
            {
                if (find.IsInvalid)
                {
                    return result;
                }

                do
                {
                    result.Add(name.ToString());
                    name.Clear();
                }
                while (NativeMethods.FindNextVolume(find, name, MaxPath));
            }

            return result;
        }

        public static int[] GetDiskNumbers(string volumeName)
        {
            using (var handle = DiskIoctl.Open(volumeName.TrimEnd('\\'), 0))
            {
                return handle.IsInvalid ? Array.Empty<int>() : DiskIoctl.GetVolumeDiskNumbers(handle);
            }
        }

        public static List<string> GetMountPoints(string volumeName)
        {
            var buffer = new char[MaxPath];
            if (!NativeMethods.GetVolumePathNamesForVolumeName(volumeName, buffer, (uint)buffer.Length, out uint length))
            {
                return new List<string>();
            }

            return new string(buffer, 0, (int)Math.Min(length, (uint)buffer.Length))
                .Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        /// <summary>Volume GUID path of the volume containing <paramref name="path"/>, or null.</summary>
        public static string GetVolumeNameForPath(string path)
        {
            var mount = new StringBuilder(MaxPath);
            if (!NativeMethods.GetVolumePathName(Path.GetFullPath(path), mount, MaxPath))
            {
                return null;
            }

            var volume = new StringBuilder(MaxPath);
            return NativeMethods.GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, MaxPath)
                ? volume.ToString()
                : null;
        }

        public static string GetMountRootForPath(string path)
        {
            var mount = new StringBuilder(MaxPath);
            return NativeMethods.GetVolumePathName(Path.GetFullPath(path), mount, MaxPath) ? mount.ToString() : null;
        }

        public static int[] GetDiskNumbersForPath(string path)
        {
            var volume = GetVolumeNameForPath(path);
            return volume == null ? Array.Empty<int>() : GetDiskNumbers(volume);
        }

        /// <summary>NT device name such as <c>\Device\HarddiskVolume3</c>, or null.</summary>
        public static string GetNtDeviceName(string volumeName)
        {
            // QueryDosDevice expects "Volume{GUID}" without the "\\?\" prefix and trailing slash.
            var trimmed = volumeName.TrimEnd('\\');
            if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(4);
            }

            var target = new StringBuilder(MaxPath);
            return NativeMethods.QueryDosDevice(trimmed, target, MaxPath) == 0 ? null : target.ToString();
        }

        /// <summary>Maps each disk number to the volumes residing on it.</summary>
        public static Dictionary<int, List<VolumeInfo>> MapVolumesToDisks()
        {
            var map = new Dictionary<int, List<VolumeInfo>>();
            foreach (var volume in EnumerateVolumes())
            {
                int[] disks;
                try
                {
                    disks = GetDiskNumbers(volume);
                }
                catch (Exception)
                {
                    continue;
                }

                if (disks.Length == 0)
                {
                    continue;
                }

                var info = new VolumeInfo { VolumeName = volume, MountPoints = GetMountPoints(volume) };
                foreach (int disk in disks.Distinct())
                {
                    if (!map.TryGetValue(disk, out var list))
                    {
                        list = new List<VolumeInfo>();
                        map[disk] = list;
                    }

                    list.Add(info);
                }
            }

            return map;
        }
    }
}
