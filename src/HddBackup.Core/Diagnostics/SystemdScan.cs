using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Diagnostics
{
    public enum UnitEnabled
    {
        No,
        Yes,
        Masked
    }

    /// <summary>A systemd unit file, parsed. Keys are case-sensitive as in systemd; values are verbatim.</summary>
    public sealed class SystemdUnit
    {
        private readonly Dictionary<string, List<string>> _keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        internal SystemdUnit(string name)
        {
            Name = name;
        }

        public string Name { get; }

        /// <summary>Canonical path of the file in the image; null when the unit was not found.</summary>
        public string Path { get; internal set; }

        public bool Exists => Path != null;

        /// <summary>The file exists but could not be read.</summary>
        public bool Unreadable { get; internal set; }

        public bool Masked { get; internal set; }

        /// <summary>"service", "timer", "socket", "target", ... (the suffix after the last dot).</summary>
        public string Kind
        {
            get
            {
                int i = Name.LastIndexOf('.');
                return i < 0 ? string.Empty : Name.Substring(i + 1);
            }
        }

        public string Description => First("Unit", "Description") ?? string.Empty;

        /// <summary>The first <c>ExecStart=</c> command with the systemd prefixes (<c>@ - : + !</c>) removed.</summary>
        public string ExecStart => StripPrefixes(First("Service", "ExecStart"));

        public string User => First("Service", "User") ?? string.Empty;

        public string First(string section, string key)
        {
            return _keys.TryGetValue(section + "." + key, out var list) && list.Count > 0 ? list[0] : null;
        }

        public IReadOnlyList<string> All(string section, string key)
        {
            return _keys.TryGetValue(section + "." + key, out var list) ? (IReadOnlyList<string>)list : Array.Empty<string>();
        }

        /// <summary>All whitespace-separated words of every occurrence of a key (<c>Wants=a b</c>).</summary>
        public IEnumerable<string> Words(string section, string key)
        {
            return All(section, key).SelectMany(v => v.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        internal void Add(string section, string key, string value)
        {
            string k = section + "." + key;
            if (!_keys.TryGetValue(k, out var list))
            {
                list = new List<string>();
                _keys[k] = list;
            }

            // An empty assignment resets the list in systemd.
            if (value.Length == 0)
            {
                list.Clear();
            }
            else
            {
                list.Add(value);
            }
        }

        internal static string StripPrefixes(string command)
        {
            if (command == null)
            {
                return string.Empty;
            }

            int i = 0;
            while (i < command.Length && "@-:+!".IndexOf(command[i]) >= 0)
            {
                i++;
            }

            return command.Substring(i).Trim();
        }
    }

    /// <summary>A link in a <c>*.wants</c> or <c>*.requires</c> directory: a unit that is switched on.</summary>
    public sealed class EnabledUnit
    {
        /// <summary>Name of the link (an instance such as <c>getty@tty1.service</c>, or an alias).</summary>
        public string Name { get; internal set; }

        /// <summary>The unit file the link leads to (<c>getty@.service</c> for a template instance).</summary>
        public string UnitFile { get; internal set; }

        /// <summary>The target the link hangs on, e.g. <c>multi-user.target</c>.</summary>
        public string Target { get; internal set; }

        public string LinkPath { get; internal set; }

        /// <summary>The link points to <c>/dev/null</c>.</summary>
        public bool Masked { get; internal set; }

        /// <summary>The link leads nowhere (the unit file is missing).</summary>
        public bool UnitMissing { get; internal set; }

        /// <summary>The target is reached from the default target, so the unit really starts.</summary>
        public bool Reached { get; internal set; }

        /// <summary>For per-user units: the account; null for system units.</summary>
        public string User { get; internal set; }

        public string Kind
        {
            get
            {
                int i = Name.LastIndexOf('.');
                return i < 0 ? string.Empty : Name.Substring(i + 1);
            }
        }
    }

    /// <summary>The on-disk state of one unit, as <c>systemctl is-enabled</c> sees it.</summary>
    public sealed class UnitState
    {
        public string Unit { get; internal set; }

        /// <summary>The unit file (after following aliases) exists.</summary>
        public bool Exists { get; internal set; }

        public UnitEnabled Enabled { get; internal set; }

        /// <summary>A link exists, but only in a target that is not reached from the default target.</summary>
        public bool InUnreachedTarget { get; internal set; }

        /// <summary>Image paths the answer rests on.</summary>
        public List<string> Evidence { get; } = new List<string>();
    }

    /// <summary>
    /// What systemd would start, read from the files of the image (research R-4, R-8): unit lookup,
    /// aliases, masking, <c>*.wants</c> links, and the targets reached from the default target.
    /// </summary>
    public sealed class SystemdScan
    {
        private static readonly string[] UnitDirs =
        {
            "/etc/systemd/system", "/run/systemd/system", "/usr/lib/systemd/system", "/lib/systemd/system"
        };

        private readonly SystemReader _reader;
        private readonly Dictionary<string, SystemdUnit> _units = new Dictionary<string, SystemdUnit>(StringComparer.Ordinal);
        private List<EnabledUnit> _enabled;
        private HashSet<string> _reached;
        private string _defaultTarget;

        public SystemdScan(SystemReader reader)
        {
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>The system runs systemd (its binary or its unit directories are present).</summary>
        public bool HasSystemd =>
            _reader.Exists("/lib/systemd/systemd")
            || _reader.Exists("/usr/lib/systemd/systemd")
            || _reader.IsDirectory("/etc/systemd/system")
            || _reader.IsDirectory("/usr/lib/systemd/system")
            || _reader.IsDirectory("/lib/systemd/system");

        /// <summary>The unit the system boots into, e.g. <c>multi-user.target</c>.</summary>
        public string DefaultTarget
        {
            get
            {
                if (_defaultTarget == null)
                {
                    _defaultTarget = FindDefaultTarget();
                }

                return _defaultTarget;
            }
        }

        /// <summary>Targets reached from the default target through <c>Wants=</c>, <c>Requires=</c> and the <c>.wants</c> directories.</summary>
        public ISet<string> ReachedTargets
        {
            get
            {
                if (_reached == null)
                {
                    _reached = BuildReached();
                }

                return _reached;
            }
        }

        /// <summary>
        /// Looks a unit up in the search path (<c>/etc</c>, <c>/run</c>, <c>/usr/lib</c>, <c>/lib</c>); a template instance
        /// falls back to its template. Never null: check <see cref="SystemdUnit.Exists"/> and <see cref="SystemdUnit.Masked"/>.
        /// </summary>
        public SystemdUnit FindUnit(string name)
        {
            if (_units.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var unit = Load(name);
            if (!unit.Exists && !unit.Masked && !unit.Unreadable)
            {
                string template = TemplateOf(name);
                if (template != null)
                {
                    var t = Load(template);
                    if (t.Exists || t.Masked || t.Unreadable)
                    {
                        unit = t;
                    }
                }
            }

            _units[name] = unit;
            return unit;
        }

        /// <summary>Every unit that is switched on by a link in a system <c>*.wants</c>/<c>*.requires</c> directory of <c>/etc/systemd/system</c>.</summary>
        public IReadOnlyList<EnabledUnit> EnabledUnits()
        {
            if (_enabled == null)
            {
                _enabled = ScanWants("/etc/systemd/system", null, systemScope: true);
            }

            return _enabled;
        }

        /// <summary>Names of the units that are masked (a link to <c>/dev/null</c> directly in <c>/etc/systemd/system</c>).</summary>
        public IReadOnlyList<string> MaskedUnits()
        {
            var result = new List<string>();
            var list = _reader.List("/etc/systemd/system");
            if (list == null)
            {
                return result;
            }

            foreach (var e in list)
            {
                if (e.Kind == FsEntryKind.Symlink && e.Name.IndexOf('.') > 0 && _reader.ReadLink(SystemReader.Combine("/etc/systemd/system", e.Name)) == "/dev/null")
                {
                    result.Add(e.Name);
                }
            }

            return result;
        }

        /// <summary>Units enabled in the systemd user manager of one account (<c>~/.config/systemd/user</c>).</summary>
        public IReadOnlyList<EnabledUnit> UserUnits(string user, string home)
        {
            if (string.IsNullOrEmpty(home) || home == "/")
            {
                return Array.Empty<EnabledUnit>();
            }

            return ScanWants(SystemReader.Combine(home, ".config/systemd/user"), user, systemScope: false);
        }

        /// <summary>The state of one unit by name (an alias is followed to the unit it stands for).</summary>
        public UnitState EnabledState(string name)
        {
            var state = new UnitState { Unit = name };
            string canonical = name;
            string etcPath = "/etc/systemd/system/" + name;
            string linkTarget = _reader.ReadLink(etcPath);
            if (linkTarget == "/dev/null")
            {
                state.Enabled = UnitEnabled.Masked;
                state.Evidence.Add(etcPath);
                return state;
            }

            if (linkTarget != null)
            {
                string resolved = _reader.Resolve(etcPath);
                string baseName = BaseName(resolved ?? linkTarget);
                if (!string.Equals(baseName, name, StringComparison.Ordinal))
                {
                    canonical = baseName;
                    state.Unit = canonical;
                    state.Evidence.Add(etcPath);
                }
            }

            var unit = FindUnit(canonical);
            state.Exists = unit.Exists;
            if (unit.Masked)
            {
                state.Enabled = UnitEnabled.Masked;
                state.Evidence.Add(unit.Path ?? etcPath);
                return state;
            }

            if (unit.Exists)
            {
                state.Evidence.Add(unit.Path);
            }

            foreach (var e in EnabledUnits().Where(x => !x.Masked && (x.UnitFile == canonical || x.Name == canonical || x.Name == name)))
            {
                if (e.UnitMissing && !unit.Exists)
                {
                    continue;
                }

                if (e.Reached)
                {
                    state.Enabled = UnitEnabled.Yes;
                    state.Evidence.Add(e.LinkPath);
                }
                else if (state.Enabled != UnitEnabled.Yes)
                {
                    state.InUnreachedTarget = true;
                    state.Evidence.Add(e.LinkPath);
                }
            }

            return state;
        }

        /// <summary>
        /// SysV start links (<c>S??name</c> in <c>/etc/rc[2-5].d</c> and <c>/etc/rcS.d</c>): script name and link path.
        /// Used for systems without systemd, or for scripts without a unit.
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> SysVStartLinks()
        {
            var result = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dir in new[] { "/etc/rcS.d", "/etc/rc2.d", "/etc/rc3.d", "/etc/rc4.d", "/etc/rc5.d" })
            {
                var list = _reader.List(dir);
                if (list == null)
                {
                    continue;
                }

                foreach (var e in list)
                {
                    if (e.Name.Length > 3 && e.Name[0] == 'S' && char.IsDigit(e.Name[1]) && char.IsDigit(e.Name[2]))
                    {
                        string script = e.Name.Substring(3);
                        if (seen.Add(script))
                        {
                            result.Add(new KeyValuePair<string, string>(script, SystemReader.Combine(dir, e.Name)));
                        }
                    }
                }
            }

            return result;
        }

        private static string TemplateOf(string name)
        {
            int at = name.IndexOf('@');
            int dot = name.LastIndexOf('.');
            if (at <= 0 || dot <= at + 1)
            {
                return null;
            }

            return name.Substring(0, at + 1) + name.Substring(dot);
        }

        internal static string BaseName(string path)
        {
            if (path == null)
            {
                return string.Empty;
            }

            int i = path.LastIndexOf('/');
            return i < 0 ? path : path.Substring(i + 1);
        }

        private SystemdUnit Load(string name)
        {
            var unit = new SystemdUnit(name);
            foreach (string dir in UnitDirs)
            {
                string path = SystemReader.Combine(dir, name);
                var entry = _reader.Lookup(path, false);
                if (entry == null)
                {
                    continue;
                }

                if (entry.Kind == FsEntryKind.Symlink && _reader.ReadLink(path) == "/dev/null")
                {
                    unit.Masked = true;
                    unit.Path = path;
                    return unit;
                }

                string resolved = _reader.Resolve(path);
                if (resolved == null)
                {
                    // A dangling link: keep looking in the vendor directories.
                    continue;
                }

                string text = _reader.ReadText(resolved);
                if (text == null)
                {
                    unit.Unreadable = true;
                    unit.Path = resolved;
                    return unit;
                }

                unit.Path = resolved;
                Parse(unit, text);
                return unit;
            }

            return unit;
        }

        internal static void Parse(SystemdUnit unit, string text)
        {
            string section = string.Empty;
            var lines = SystemReader.SplitLines(text);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                {
                    continue;
                }

                while (line.EndsWith("\\", StringComparison.Ordinal) && i + 1 < lines.Count)
                {
                    line = line.Substring(0, line.Length - 1).TrimEnd() + " " + lines[++i].Trim();
                }

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                unit.Add(section, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            }
        }

        private string FindDefaultTarget()
        {
            foreach (string path in new[] { "/etc/systemd/system/default.target", "/run/systemd/system/default.target", "/usr/lib/systemd/system/default.target", "/lib/systemd/system/default.target" })
            {
                if (_reader.IsLink(path))
                {
                    string resolved = _reader.Resolve(path);
                    string name = BaseName(resolved ?? _reader.ReadLink(path));
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
            }

            return FindUnit("graphical.target").Exists ? "graphical.target" : "multi-user.target";
        }

        private HashSet<string> BuildReached()
        {
            var set = new HashSet<string>(StringComparer.Ordinal) { "default.target" };
            var queue = new Queue<string>();
            Add(DefaultTarget);
            while (queue.Count > 0)
            {
                string t = queue.Dequeue();
                var unit = FindUnit(t);
                foreach (string key in new[] { "Wants", "Requires", "Requisite", "BindsTo", "Upholds" })
                {
                    foreach (string dep in unit.Words("Unit", key))
                    {
                        Add(dep);
                    }
                }

                foreach (string dir in UnitDirs)
                {
                    foreach (string suffix in new[] { ".wants", ".requires" })
                    {
                        var list = _reader.List(SystemReader.Combine(dir, t + suffix));
                        if (list == null)
                        {
                            continue;
                        }

                        foreach (var e in list)
                        {
                            Add(e.Name);
                        }
                    }
                }
            }

            return set;

            void Add(string name)
            {
                if (name.EndsWith(".target", StringComparison.Ordinal) && set.Add(name))
                {
                    queue.Enqueue(name);
                }
            }
        }

        private List<EnabledUnit> ScanWants(string root, string user, bool systemScope)
        {
            var result = new List<EnabledUnit>();
            var dirs = _reader.List(root);
            if (dirs == null)
            {
                return result;
            }

            foreach (var d in dirs)
            {
                string owner;
                if (d.Name.EndsWith(".wants", StringComparison.Ordinal))
                {
                    owner = d.Name.Substring(0, d.Name.Length - ".wants".Length);
                }
                else if (d.Name.EndsWith(".requires", StringComparison.Ordinal))
                {
                    owner = d.Name.Substring(0, d.Name.Length - ".requires".Length);
                }
                else
                {
                    continue;
                }

                if (!owner.EndsWith(".target", StringComparison.Ordinal))
                {
                    continue;
                }

                string dirPath = SystemReader.Combine(root, d.Name);
                var links = _reader.List(dirPath);
                if (links == null)
                {
                    continue;
                }

                foreach (var l in links)
                {
                    string linkPath = SystemReader.Combine(dirPath, l.Name);
                    string target = _reader.ReadLink(linkPath);
                    var e = new EnabledUnit
                    {
                        Name = l.Name,
                        Target = owner,
                        LinkPath = linkPath,
                        User = user,
                        Reached = !systemScope || ReachedTargets.Contains(owner)
                    };
                    if (target == null)
                    {
                        // A regular file in a .wants directory is a unit of its own, not a link.
                        e.UnitFile = l.Name;
                    }
                    else if (target == "/dev/null")
                    {
                        e.Masked = true;
                        e.UnitFile = l.Name;
                    }
                    else
                    {
                        string resolved = _reader.Resolve(linkPath);
                        e.UnitMissing = resolved == null;
                        e.UnitFile = BaseName(resolved ?? target);
                    }

                    result.Add(e);
                }
            }

            return result;
        }
    }
}
