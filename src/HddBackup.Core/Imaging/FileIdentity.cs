using System;
using System.IO;
using HddBackup.Core.Native;

namespace HddBackup.Core.Imaging
{
    /// <summary>
    /// Whether two paths name the same file. Comparing path text is not enough: a mapped network
    /// drive and its UNC path, a subst drive, a short 8.3 name, a symbolic link or a hard link all
    /// reach the same file under a different name, and writing to one would destroy the other.
    /// </summary>
    public static class FileIdentity
    {
        public static bool IsSameFile(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            try
            {
                if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }

            // A path that does not exist yet cannot be another name of an existing file.
            if (!File.Exists(a) || !File.Exists(b))
            {
                return false;
            }

            var first = Identify(a);
            var second = Identify(b);
            return first.HasValue && second.HasValue && first.Value == second.Value;
        }

        private static (uint Volume, ulong Index)? Identify(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                    {
                        return null;
                    }

                    return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
