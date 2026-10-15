using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace HddBackup.App.Settings
{
    /// <summary>
    /// Settings in <c>HddBackup.ini</c> next to the executable (no registry). The file is only
    /// written when a setting changed; if the folder is read-only, settings live in memory.
    /// </summary>
    internal sealed class PortableSettings
    {
        public const string FileName = "HddBackup.ini";

        private readonly string _path;
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _dirty;

        private PortableSettings(string path)
        {
            _path = path;
        }

        /// <summary>Language code, or empty to follow the OS language.</summary>
        public string Language
        {
            get => Get("language", string.Empty);
            set => Set("language", value ?? string.Empty);
        }

        public bool EjectAfter
        {
            get => GetBool("ejectAfter", true);
            set => SetBool("ejectAfter", value);
        }

        public bool VerifyAfterRestore
        {
            get => GetBool("verifyAfterRestore", true);
            set => SetBool("verifyAfterRestore", value);
        }

        public bool VerifyAfterBackup
        {
            get => GetBool("verifyAfterBackup", false);
            set => SetBool("verifyAfterBackup", value);
        }

        public bool FastCompression
        {
            get => GetBool("fastCompression", false);
            set => SetBool("fastCompression", value);
        }

        public string LastImageFolder
        {
            get => Get("lastImageFolder", string.Empty);
            set => Set("lastImageFolder", value ?? string.Empty);
        }

        public static PortableSettings Load()
        {
            var settings = new PortableSettings(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName));
            try
            {
                if (File.Exists(settings._path))
                {
                    foreach (var line in File.ReadAllLines(settings._path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0 && !line.TrimStart().StartsWith(";", StringComparison.Ordinal))
                        {
                            settings._values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Unreadable settings: use defaults.
            }

            return settings;
        }

        public void SaveIfChanged()
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                var sb = new StringBuilder();
                sb.Append("; HddBackup settings\n");
                foreach (var pair in _values)
                {
                    sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
                }

                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
                _dirty = false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Read-only location (e.g. write-protected stick): keep settings for this session only.
            }
        }

        private string Get(string key, string fallback)
        {
            return _values.TryGetValue(key, out var value) ? value : fallback;
        }

        private void Set(string key, string value)
        {
            if (!_values.TryGetValue(key, out var old) || old != value)
            {
                _values[key] = value;
                _dirty = true;
            }
        }

        private bool GetBool(string key, bool fallback)
        {
            return _values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private void SetBool(string key, bool value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        }
    }
}
