using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Things that start with the machine or the desktop session apart from systemd units and cron (research R-8):
    /// <c>/etc/rc.local</c>, XDG autostart entries (<c>.desktop</c>), and the autostart files of the Raspberry Pi OS
    /// desktops (LXDE <c>lxsession</c>, <c>labwc</c>). A user's own entry overrides the system entry of the same name.
    /// </summary>
    public static class AutostartScan
    {
        /// <param name="homes">Account name and home directory of every account worth looking at.</param>
        public static List<StartupEntry> Run(SystemReader reader, IEnumerable<KeyValuePair<string, string>> homes)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var entries = new List<StartupEntry>();
            RcLocal(reader, entries);
            var desktops = InstalledDesktops(reader);
            var userHomes = homes == null
                ? new List<KeyValuePair<string, string>>()
                : homes.Where(h => !string.IsNullOrEmpty(h.Value) && h.Value != "/" && reader.IsDirectory(h.Value)).ToList();

            Xdg(reader, entries, userHomes, desktops);
            DesktopScripts(reader, entries, userHomes);
            return entries;
        }

        // --- /etc/rc.local ---------------------------------------------------------------------

        private static void RcLocal(SystemReader reader, List<StartupEntry> entries)
        {
            const string path = "/etc/rc.local";
            var info = reader.Lookup(path);
            if (info == null || info.Kind != HddBackup.Core.Browsing.FsEntryKind.File)
            {
                return;
            }

            bool executable = (info.Mode & 0x49) != 0;
            // /etc/rc.local is one boot script: a single entry that shows what it runs.
            var statements = Statements(reader.ReadLines(path)).ToList();
            if (statements.Count == 0)
            {
                return;
            }

            entries.Add(new StartupEntry
            {
                Kind = StartupKind.BootScript,
                Name = "rc.local",
                Command = string.Join("; ", statements.Select(x => x.TrimEnd(';'))),
                Schedule = CronSchedule.AtBoot(string.Empty),
                User = "root",
                Source = path,
                InactiveReason = executable ? InactiveReason.None : InactiveReason.NotExecutable,
                Evidence = new[] { path }
            });
        }

        /// <summary>
        /// One statement per command line; an <c>if … fi</c> block (or a <c>for</c>/<c>while</c> loop) is one statement,
        /// so that keywords such as <c>then</c> and <c>fi</c> do not show up as commands of their own.
        /// </summary>
        internal static IEnumerable<string> Statements(IEnumerable<string> lines)
        {
            var block = new List<string>();
            int depth = 0;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || (depth == 0 && IsExit(line)))
                {
                    continue;
                }

                string first = line.Split(' ', '	', ';')[0];
                if (first == "if" || first == "for" || first == "while" || first == "case")
                {
                    depth++;
                }

                block.Add(line);
                if (first == "fi" || first == "done" || first == "esac")
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (depth == 0)
                {
                    yield return JoinBlock(block);
                    block.Clear();
                }
            }

            if (block.Count > 0)
            {
                yield return JoinBlock(block);
            }
        }

        /// <summary>Lines of one block joined the way a shell reads them: with ";" unless a keyword or ";" already ends the line.</summary>
        private static string JoinBlock(List<string> lines)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                sb.Append(l);
                if (i < lines.Count - 1 && !l.EndsWith(";", StringComparison.Ordinal) && !l.EndsWith("then", StringComparison.Ordinal)
                    && !l.EndsWith("else", StringComparison.Ordinal) && !l.EndsWith("do", StringComparison.Ordinal))
                {
                    sb.Append(';');
                }

                if (i < lines.Count - 1)
                {
                    sb.Append(' ');
                }
            }

            return sb.ToString();
        }

        private static bool IsExit(string line)
        {
            return line == "exit" || line.StartsWith("exit ", StringComparison.Ordinal) || line.StartsWith("set -", StringComparison.Ordinal);
        }

        // --- XDG autostart ---------------------------------------------------------------------

        private static void Xdg(SystemReader reader, List<StartupEntry> entries, List<KeyValuePair<string, string>> homes, ISet<string> desktops)
        {
            foreach (string file in DesktopFiles(reader, "/etc/xdg/autostart"))
            {
                var e = ReadDesktop(reader, file, string.Empty, desktops);
                if (e != null)
                {
                    entries.Add(e);
                }
            }

            foreach (var h in homes)
            {
                string dir = SystemReader.Combine(h.Value, ".config/autostart");
                foreach (string file in DesktopFiles(reader, dir))
                {
                    var e = ReadDesktop(reader, file, h.Key, desktops);
                    if (e != null)
                    {
                        entries.Add(e);
                    }
                }
            }
        }

        private static IEnumerable<string> DesktopFiles(SystemReader reader, string dir)
        {
            var list = reader.List(dir);
            if (list == null)
            {
                yield break;
            }

            foreach (var f in list)
            {
                if (f.Name.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) && !f.Name.StartsWith(".", StringComparison.Ordinal))
                {
                    string path = SystemReader.Combine(dir, f.Name);
                    if (reader.IsFile(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        private static StartupEntry ReadDesktop(SystemReader reader, string path, string user, ISet<string> desktops)
        {
            string text = reader.ReadText(path);
            if (text == null)
            {
                return new StartupEntry
                {
                    Kind = StartupKind.Desktop,
                    Name = FileBase(path),
                    User = user,
                    Source = path,
                    InactiveReason = InactiveReason.Unreadable,
                    Evidence = new[] { path }
                };
            }

            var keys = ParseDesktop(text);
            keys.TryGetValue("Exec", out string exec);
            keys.TryGetValue("Name", out string name);
            var reason = InactiveReason.None;
            if (IsTrue(keys, "Hidden") || (keys.TryGetValue("X-GNOME-Autostart-enabled", out string en) && en.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                reason = InactiveReason.HiddenEntry;
            }
            else if (desktops.Count > 0 && ExcludedByDesktop(keys, desktops))
            {
                reason = InactiveReason.HiddenEntry;
            }

            return new StartupEntry
            {
                Kind = StartupKind.Desktop,
                Name = string.IsNullOrWhiteSpace(name) ? FileBase(path) : name.Trim(),
                Command = StripFieldCodes(exec ?? string.Empty),
                User = user,
                Source = path,
                InactiveReason = reason,
                Evidence = new[] { path }
            };
        }

        internal static Dictionary<string, string> ParseDesktop(string text)
        {
            var keys = new Dictionary<string, string>(StringComparer.Ordinal);
            bool inEntry = false;
            foreach (string raw in SystemReader.SplitLines(text))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    inEntry = line == "[Desktop Entry]";
                    continue;
                }

                if (!inEntry)
                {
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq > 0)
                {
                    string key = line.Substring(0, eq).Trim();
                    if (!keys.ContainsKey(key))
                    {
                        keys[key] = line.Substring(eq + 1).Trim();
                    }
                }
            }

            return keys;
        }

        /// <summary>Removes the <c>%f %F %u %U ...</c> field codes of an <c>Exec=</c> line; <c>%%</c> is a percent sign.</summary>
        internal static string StripFieldCodes(string exec)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < exec.Length; i++)
            {
                if (exec[i] == '%' && i + 1 < exec.Length)
                {
                    char c = exec[++i];
                    if (c == '%')
                    {
                        sb.Append('%');
                    }

                    continue;
                }

                sb.Append(exec[i]);
            }

            return string.Join(" ", sb.ToString().Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool IsTrue(Dictionary<string, string> keys, string key)
        {
            return keys.TryGetValue(key, out string v) && v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>OnlyShowIn against the desktops installed on the system (unknown when none is found, then the entry counts).</summary>
        private static bool ExcludedByDesktop(Dictionary<string, string> keys, ISet<string> desktops)
        {
            if (keys.TryGetValue("OnlyShowIn", out string only))
            {
                var list = only.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());
                if (!list.Any(x => desktops.Contains(x.ToLowerInvariant())))
                {
                    return true;
                }
            }

            return false;
        }

        private static ISet<string> InstalledDesktops(SystemReader reader)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dir in new[] { "/usr/share/xsessions", "/usr/share/wayland-sessions" })
            {
                foreach (string name in reader.ListNames(dir))
                {
                    if (name.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
                    {
                        string b = name.Substring(0, name.Length - ".desktop".Length).ToLowerInvariant();
                        set.Add(b);
                        // LXDE-pi and similar profiles belong to the LXDE desktop.
                        int dash = b.IndexOf('-');
                        if (dash > 0)
                        {
                            set.Add(b.Substring(0, dash));
                        }
                    }
                }
            }

            return set;
        }

        private static string FileBase(string path)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            return name.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ".desktop".Length) : name;
        }

        // --- LXDE and labwc autostart files -----------------------------------------------------

        private static void DesktopScripts(SystemReader reader, List<StartupEntry> entries, List<KeyValuePair<string, string>> homes)
        {
            foreach (string file in LxsessionFiles(reader, "/etc/xdg/lxsession").Concat(new[] { "/etc/xdg/labwc/autostart" }))
            {
                Lines(reader, file, string.Empty, entries);
            }

            foreach (var h in homes)
            {
                foreach (string file in LxsessionFiles(reader, SystemReader.Combine(h.Value, ".config/lxsession")).Concat(new[] { SystemReader.Combine(h.Value, ".config/labwc/autostart") }))
                {
                    Lines(reader, file, h.Key, entries);
                }
            }
        }

        private static IEnumerable<string> LxsessionFiles(SystemReader reader, string root)
        {
            var profiles = reader.List(root);
            if (profiles == null)
            {
                yield break;
            }

            foreach (var p in profiles.Where(x => x.IsDirectory))
            {
                yield return SystemReader.Combine(SystemReader.Combine(root, p.Name), "autostart");
            }
        }

        private static void Lines(SystemReader reader, string path, string user, List<StartupEntry> entries)
        {
            if (!reader.IsFile(path))
            {
                return;
            }

            foreach (string raw in reader.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("-@", StringComparison.Ordinal))
                {
                    continue;
                }

                string command = line[0] == '@' ? line.Substring(1).Trim() : line;
                if (command.Length == 0)
                {
                    continue;
                }

                entries.Add(new StartupEntry
                {
                    Kind = StartupKind.Desktop,
                    Name = CronScan.NameOf(command),
                    Command = command,
                    User = user,
                    Source = path,
                    Evidence = new[] { path }
                });
            }
        }
    }
}
