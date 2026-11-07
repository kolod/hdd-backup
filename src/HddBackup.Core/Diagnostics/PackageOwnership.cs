using System;
using System.Collections.Generic;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Whether a file belongs to an installed Debian package, from the lists of files in
    /// <c>/var/lib/dpkg/info/*.list</c>. A file in <c>/etc</c> that no package owns was put there by a person.
    /// The lists are read once, on the first question; systems without dpkg answer "not known".
    /// </summary>
    public sealed class PackageOwnership
    {
        private const string InfoDir = "/var/lib/dpkg/info";
        private readonly SystemReader _reader;
        private HashSet<string> _paths;

        public PackageOwnership(SystemReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>The system has a dpkg database, so ownership can be answered.</summary>
        public bool Available => _reader.IsDirectory(InfoDir);

        /// <summary>True when a package lists the path; null when there is no dpkg database.</summary>
        public bool? Owns(string path)
        {
            if (!Available)
            {
                return null;
            }

            if (_paths == null)
            {
                _paths = Load();
            }

            return _paths.Contains(path);
        }

        private HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            var list = _reader.List(InfoDir, int.MaxValue);
            if (list == null)
            {
                return set;
            }

            foreach (var f in list)
            {
                if (!f.Name.EndsWith(".list", StringComparison.Ordinal))
                {
                    continue;
                }

                string text = _reader.ReadText(SystemReader.Combine(InfoDir, f.Name));
                if (text == null)
                {
                    continue;
                }

                foreach (string line in SystemReader.SplitLines(text))
                {
                    if (line.StartsWith("/etc/", StringComparison.Ordinal))
                    {
                        set.Add(line.TrimEnd());
                    }
                }
            }

            return set;
        }
    }
}
