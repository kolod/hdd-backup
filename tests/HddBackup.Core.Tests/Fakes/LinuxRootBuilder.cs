using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;

namespace HddBackup.Core.Tests.Fakes
{
    /// <summary>
    /// Builds an in-memory Linux root on a <see cref="FakeFileTree"/> from absolute paths:
    /// parents are created, links are stored as links (not followed while building), modes are set.
    /// </summary>
    public sealed class LinuxRootBuilder
    {
        private readonly FakeFileTree _tree = new FakeFileTree();
        private readonly Dictionary<string, FsEntry> _entries = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
        private readonly List<string> _read = new List<string>();

        public LinuxRootBuilder()
        {
            _entries["/"] = _tree.Root;
            _tree.OnRead = f => _read.Add(f.Path);
        }

        public FakeFileTree Tree => _tree;

        /// <summary>Paths of the files whose content was read, in order (links resolved by the reader are recorded by their final path).</summary>
        public IReadOnlyList<string> ReadPaths => _read;

        public LinuxRootBuilder Dir(string path)
        {
            EnsureDir(path);
            return this;
        }

        public LinuxRootBuilder File(string path, string text, int mode = 0x1A4)
        {
            var parent = EnsureDir(ParentOf(path));
            var entry = _tree.AddFile(parent, NameOf(path), new UTF8Encoding(false).GetBytes(text ?? string.Empty));
            entry.Mode = (ushort)(0x8000 | mode);
            Register(path, entry);
            return this;
        }

        public LinuxRootBuilder Executable(string path, string text = "#!/bin/sh\n")
        {
            return File(path, text, 0x1ED);
        }

        public LinuxRootBuilder Link(string path, string target)
        {
            var parent = EnsureDir(ParentOf(path));
            var entry = _tree.AddSymlink(parent, NameOf(path), target);
            entry.Mode = 0xA1FF;
            Register(path, entry);
            return this;
        }

        public LinuxRootBuilder Passwd(params string[] lines)
        {
            return File("/etc/passwd", Join(lines));
        }

        public LinuxRootBuilder Shadow(params string[] lines)
        {
            return File("/etc/shadow", Join(lines), 0x180);
        }

        public LinuxRootBuilder Group(params string[] lines)
        {
            return File("/etc/group", Join(lines));
        }

        public LinuxRootBuilder OsRelease(string id, string pretty = null)
        {
            return File("/etc/os-release", "ID=" + id + "\nPRETTY_NAME=\"" + (pretty ?? id) + "\"\n");
        }

        /// <summary>A minimal Linux root: /etc/passwd with root, /etc/os-release and /usr/bin.</summary>
        public LinuxRootBuilder BasicLinux(string id = "debian", string pretty = null, bool passwd = true)
        {
            Dir("/usr/bin");
            OsRelease(id, pretty);
            if (passwd)
            {
                File("/etc/passwd", "root:x:0:0:root:/root:/bin/bash\n");
            }

            return this;
        }

        /// <summary>systemd with the usual targets under /lib/systemd/system and <c>default.target</c> pointing at <paramref name="defaultTarget"/>.</summary>
        public LinuxRootBuilder Systemd(string defaultTarget = "multi-user.target")
        {
            Executable("/lib/systemd/systemd");
            Dir("/etc/systemd/system");
            File("/lib/systemd/system/multi-user.target", "[Unit]\nRequires=basic.target\nWants=getty.target\n");
            File("/lib/systemd/system/basic.target", "[Unit]\nRequires=sysinit.target\nWants=sockets.target timers.target paths.target slices.target\n");
            File("/lib/systemd/system/graphical.target", "[Unit]\nRequires=multi-user.target\n");
            foreach (string t in new[] { "sysinit", "sockets", "timers", "paths", "slices", "getty" })
            {
                File("/lib/systemd/system/" + t + ".target", "[Unit]\n");
            }

            Link("/lib/systemd/system/default.target", defaultTarget);
            return this;
        }

        /// <summary>A vendor unit file under /lib/systemd/system.</summary>
        public LinuxRootBuilder Unit(string name, string body)
        {
            return File("/lib/systemd/system/" + name, body);
        }

        /// <summary>The link that <c>systemctl enable</c> creates in the target's .wants directory.</summary>
        public LinuxRootBuilder Enable(string unit, string target = "multi-user.target", string linkName = null)
        {
            return Link("/etc/systemd/system/" + target + ".wants/" + (linkName ?? unit), "/lib/systemd/system/" + unit);
        }

        public SystemReader Reader(CancellationToken ct = default(CancellationToken)) => new SystemReader(_tree, ct);

        private static string Join(string[] lines) => string.Join("\n", lines) + "\n";

        private static string ParentOf(string path)
        {
            int i = path.LastIndexOf('/');
            return i <= 0 ? "/" : path.Substring(0, i);
        }

        private static string NameOf(string path) => path.Substring(path.LastIndexOf('/') + 1);

        private void Register(string path, FsEntry entry)
        {
            if (_entries.ContainsKey(path))
            {
                throw new InvalidOperationException("Path built twice: " + path);
            }

            _entries[path] = entry;
        }

        private FsEntry EnsureDir(string path)
        {
            if (_entries.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var parent = EnsureDir(ParentOf(path));
            var dir = _tree.AddDirectory(parent, NameOf(path));
            dir.Mode = 0x41ED;
            _entries[path] = dir;
            return dir;
        }
    }
}
