using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// Names Windows cannot store are changed in a predictable way that never makes two items
    /// collide (research R-6): invalid characters and a trailing dot or space become "_", reserved
    /// device names get a leading "_", and names equal ignoring case get " (2)", " (3)" ...
    /// </summary>
    internal static class WindowsNames
    {
        private const string ExtendedPrefix = @"\\?\";
        private const string ExtendedUncPrefix = @"\\?\UNC\";

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// The Windows name for <paramref name="name"/> in a directory whose names so far are in
        /// <paramref name="usedLower"/> (lower-case invariant); the result is added to it.
        /// </summary>
        public static string Map(string name, ISet<string> usedLower, out bool renamed)
        {
            string candidate = Sanitize(name);
            if (usedLower.Contains(candidate.ToLowerInvariant()))
            {
                string stem = candidate;
                string extension = string.Empty;
                int dot = candidate.LastIndexOf('.');
                if (dot > 0)
                {
                    stem = candidate.Substring(0, dot);
                    extension = candidate.Substring(dot);
                }

                for (int n = 2; ; n++)
                {
                    string next = stem + " (" + n + ")" + extension;
                    if (!usedLower.Contains(next.ToLowerInvariant()))
                    {
                        candidate = next;
                        break;
                    }
                }
            }

            usedLower.Add(candidate.ToLowerInvariant());
            renamed = !string.Equals(candidate, name, StringComparison.Ordinal);
            return candidate;
        }

        public static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool bad = c < 0x20 || c == '<' || c == '>' || c == ':' || c == '"' || c == '/' || c == '\\'
                    || c == '|' || c == '?' || c == '*' || c == '\uFFFD';
                sb.Append(bad ? '_' : c);
            }

            // A trailing dot or space is silently dropped by Windows, which would merge names.
            int end = sb.Length;
            while (end > 0 && (sb[end - 1] == '.' || sb[end - 1] == ' '))
            {
                sb[end - 1] = '_';
                end--;
            }

            string result = sb.ToString();
            int firstDot = result.IndexOf('.');
            string baseName = firstDot < 0 ? result : result.Substring(0, firstDot);
            if (Reserved.Contains(baseName.TrimEnd(' ')))
            {
                result = "_" + result;
            }

            return result.Length == 0 ? "_" : result;
        }

        /// <summary>Adds the extended-length prefix so that paths beyond 260 characters work on Windows 7 too.</summary>
        public static string ToExtendedPath(string path)
        {
            if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
            {
                return path;
            }

            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return ExtendedUncPrefix + full.Substring(2);
            }

            return ExtendedPrefix + full;
        }
    }
}
