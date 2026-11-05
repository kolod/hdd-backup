using System;
using System.Collections.Generic;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Whether the opened file system is the root of a Linux system, and which distribution
    /// (research R-3). A data partition has no <c>/etc/passwd</c> and is not a Linux root.
    /// </summary>
    public sealed class LinuxRoot
    {
        public bool IsLinux { get; private set; }

        /// <summary><c>ID=</c> of os-release (e.g. "debian", "raspbian"); empty when unknown.</summary>
        public string DistributionId { get; private set; } = string.Empty;

        public string PrettyName { get; private set; } = string.Empty;

        public bool IsRaspberryPiOs { get; private set; }

        public static LinuxRoot Detect(SystemReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var result = new LinuxRoot();
            if (!reader.IsFile("/etc/passwd"))
            {
                return result;
            }

            bool osRelease = reader.IsFile("/etc/os-release") || reader.IsFile("/usr/lib/os-release");
            if (!osRelease && !reader.IsDirectory("/usr/bin") && !reader.IsDirectory("/bin"))
            {
                return result;
            }

            result.IsLinux = true;
            var values = ParseOsRelease(reader.ReadText("/etc/os-release") ?? reader.ReadText("/usr/lib/os-release"));
            values.TryGetValue("ID", out string id);
            values.TryGetValue("PRETTY_NAME", out string pretty);
            values.TryGetValue("ID_LIKE", out string like);
            result.DistributionId = id ?? string.Empty;
            result.PrettyName = pretty ?? string.Empty;
            result.IsRaspberryPiOs =
                string.Equals(id, "raspbian", StringComparison.OrdinalIgnoreCase)
                || (like != null && like.IndexOf("raspbian", StringComparison.OrdinalIgnoreCase) >= 0)
                || (pretty != null && pretty.IndexOf("Raspberry Pi", StringComparison.OrdinalIgnoreCase) >= 0)
                || reader.Exists("/etc/rpi-issue");
            return result;
        }

        internal static Dictionary<string, string> ParseOsRelease(string text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (text == null)
            {
                return result;
            }

            foreach (string raw in SystemReader.SplitLines(text))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[value.Length - 1] == value[0])
                {
                    value = value.Substring(1, value.Length - 2);
                }

                result[key] = value;
            }

            return result;
        }
    }
}
