using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Diagnostics
{
    public sealed class CronScanResult
    {
        public List<StartupEntry> Entries { get; } = new List<StartupEntry>();

        /// <summary>The cron program is present (<c>/usr/sbin/cron</c> or <c>crond</c>).</summary>
        public bool Installed { get; internal set; }

        /// <summary>The cron service is installed as a unit but not enabled (systemd systems only).</summary>
        public bool ServiceNotEnabled { get; internal set; }
    }

    /// <summary>
    /// Scheduled tasks of Debian-family cron and anacron (research R-9): <c>/etc/crontab</c>, <c>/etc/cron.d</c>,
    /// user crontabs, <c>/etc/anacrontab</c> and the periodic directories, with the <c>run-parts</c> rules that
    /// decide whether a script really runs. Entries that will not run are kept, marked with the reason.
    /// </summary>
    public static class CronScan
    {
        private static readonly string[] Periods = { "hourly", "daily", "weekly", "monthly" };
        private static readonly Regex RunPartsName = new Regex("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        private static readonly Regex EnvLine = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.Compiled);
        private static readonly Regex PeriodicDir = new Regex(@"/etc/cron\.(hourly|daily|weekly|monthly)\b", RegexOptions.Compiled);

        public static CronScanResult Run(SystemReader reader, SystemdScan systemd, ISet<string> users)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var result = new CronScanResult();
            result.Installed = new[] { "/usr/sbin/cron", "/usr/sbin/crond", "/sbin/cron", "/usr/bin/cron", "/usr/bin/crond" }.Any(reader.Exists);
            bool anacron = reader.Exists("/usr/sbin/anacron");
            if (result.Installed && systemd != null && systemd.HasSystemd)
            {
                var cron = systemd.EnabledState("cron.service");
                var crond = systemd.EnabledState("crond.service");
                bool unitExists = cron.Exists || crond.Exists;
                result.ServiceNotEnabled = unitExists && cron.Enabled != UnitEnabled.Yes && crond.Enabled != UnitEnabled.Yes;
            }

            var runParts = new Dictionary<string, CronSchedule>(StringComparer.Ordinal);
            var entries = result.Entries;

            ParseTable(reader, "/etc/crontab", true, null, users, false, anacron, entries, runParts);
            var cronD = reader.List("/etc/cron.d");
            if (cronD != null)
            {
                foreach (var f in cronD)
                {
                    string path = "/etc/cron.d/" + f.Name;
                    if (!reader.IsFile(path))
                    {
                        continue;
                    }

                    ParseTable(reader, path, true, null, users, !RunPartsName.IsMatch(f.Name), anacron, entries, runParts);
                }
            }

            foreach (string dir in new[] { "/var/spool/cron/crontabs", "/var/spool/cron" })
            {
                var list = reader.List(dir);
                if (list == null)
                {
                    continue;
                }

                foreach (var f in list)
                {
                    string path = SystemReader.Combine(dir, f.Name);
                    if (f.Name.StartsWith(".", StringComparison.Ordinal) || !reader.IsFile(path))
                    {
                        continue;
                    }

                    ParseTable(reader, path, false, f.Name, users, false, anacron, entries, runParts);
                }
            }

            if (anacron)
            {
                ParseAnacron(reader, entries, runParts);
            }

            ScanPeriodic(reader, entries, runParts);

            if (!result.Installed)
            {
                foreach (var e in entries.Where(x => x.Active))
                {
                    e.InactiveReason = InactiveReason.NoCron;
                }
            }

            return result;
        }

        private static void ParseTable(
            SystemReader reader,
            string path,
            bool systemFormat,
            string fileUser,
            ISet<string> users,
            bool ignoredName,
            bool anacron,
            List<StartupEntry> entries,
            Dictionary<string, CronSchedule> runParts)
        {
            var lines = reader.ReadLines(path);
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || EnvLine.IsMatch(line))
                {
                    continue;
                }

                var tokens = Tokens(line);
                int scheduleTokens = line[0] == '@' ? 1 : 5;
                int needed = scheduleTokens + (systemFormat ? 1 : 0) + 1;
                if (tokens.Count < needed)
                {
                    entries.Add(Malformed(line, path));
                    continue;
                }

                string scheduleText = line.Substring(0, tokens[scheduleTokens - 1].End);
                var schedule = CronSchedule.Parse(scheduleText);
                string user = fileUser ?? string.Empty;
                int commandFrom = scheduleTokens;
                if (systemFormat)
                {
                    user = line.Substring(tokens[scheduleTokens].Start, tokens[scheduleTokens].End - tokens[scheduleTokens].Start);
                    commandFrom = scheduleTokens + 1;
                }

                string command = line.Substring(tokens[commandFrom].Start).Trim();
                if (schedule == null || command.Length == 0)
                {
                    entries.Add(Malformed(line, path));
                    continue;
                }

                var periodic = PeriodicDir.Match(command);
                if (periodic.Success && command.Contains("run-parts"))
                {
                    // The line that runs a periodic directory: it explains the scripts there, it is not a task itself.
                    bool superseded = anacron && command.Contains("/usr/sbin/anacron");
                    if (!superseded && !runParts.ContainsKey(periodic.Value))
                    {
                        runParts[periodic.Value] = schedule;
                    }

                    continue;
                }

                var reason = InactiveReason.None;
                if (ignoredName)
                {
                    reason = InactiveReason.IgnoredName;
                }
                else if (user.Length > 0 && users != null && users.Count > 0 && !users.Contains(user))
                {
                    reason = InactiveReason.NoSuchUser;
                }

                entries.Add(new StartupEntry
                {
                    Kind = StartupKind.Cron,
                    Name = NameOf(command),
                    Command = command,
                    Schedule = schedule,
                    ScheduleText = schedule.Raw,
                    User = user,
                    Source = path,
                    InactiveReason = reason,
                    Evidence = new[] { path }
                });
            }
        }

        private static void ParseAnacron(SystemReader reader, List<StartupEntry> entries, Dictionary<string, CronSchedule> runParts)
        {
            const string path = "/etc/anacrontab";
            foreach (string raw in reader.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || EnvLine.IsMatch(line))
                {
                    continue;
                }

                var tokens = Tokens(line);
                if (tokens.Count < 4)
                {
                    entries.Add(Malformed(line, path));
                    continue;
                }

                string period = line.Substring(tokens[0].Start, tokens[0].End - tokens[0].Start);
                string command = line.Substring(tokens[3].Start).Trim();
                CronSchedule schedule;
                switch (period.ToLowerInvariant())
                {
                    case "@daily":
                        schedule = CronSchedule.EveryDays(1, period);
                        break;
                    case "@weekly":
                        schedule = CronSchedule.EveryDays(7, period);
                        break;
                    case "@monthly":
                    case "@yearly":
                    case "@annually":
                        schedule = CronSchedule.Custom(period);
                        break;
                    default:
                        schedule = int.TryParse(period, out int days) && days > 0 ? CronSchedule.EveryDays(days, period) : null;
                        break;
                }

                if (schedule == null)
                {
                    entries.Add(Malformed(line, path));
                    continue;
                }

                var periodic = PeriodicDir.Match(command);
                if (periodic.Success && command.Contains("run-parts"))
                {
                    if (!runParts.ContainsKey(periodic.Value))
                    {
                        runParts[periodic.Value] = schedule;
                    }

                    continue;
                }

                entries.Add(new StartupEntry
                {
                    Kind = StartupKind.Cron,
                    Name = NameOf(command),
                    Command = command,
                    Schedule = schedule,
                    ScheduleText = schedule.Raw,
                    User = "root",
                    Source = path,
                    Evidence = new[] { path }
                });
            }
        }

        private static void ScanPeriodic(SystemReader reader, List<StartupEntry> entries, Dictionary<string, CronSchedule> runParts)
        {
            foreach (string period in Periods)
            {
                string dir = "/etc/cron." + period;
                var list = reader.List(dir);
                if (list == null)
                {
                    continue;
                }

                runParts.TryGetValue(dir, out var schedule);
                foreach (var f in list)
                {
                    if (f.Name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue; // .placeholder and hidden files are never part of the list
                    }

                    string path = SystemReader.Combine(dir, f.Name);
                    var info = reader.Lookup(path);
                    if (info == null || info.Kind != FsEntryKind.File)
                    {
                        continue;
                    }

                    var reason = InactiveReason.None;
                    if (!RunPartsName.IsMatch(f.Name))
                    {
                        reason = InactiveReason.IgnoredName;
                    }
                    else if ((info.Mode & 0x49) == 0)
                    {
                        reason = InactiveReason.NotExecutable;
                    }
                    else if (schedule == null)
                    {
                        reason = InactiveReason.NothingRunsIt;
                    }

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupKind.Cron,
                        Name = f.Name,
                        Command = path,
                        Schedule = schedule,
                        ScheduleText = schedule?.Raw ?? string.Empty,
                        User = "root",
                        Source = dir,
                        InactiveReason = reason,
                        Evidence = new[] { path }
                    });
                }
            }
        }

        private static StartupEntry Malformed(string line, string path)
        {
            return new StartupEntry
            {
                Kind = StartupKind.Cron,
                Name = line.Length > 40 ? line.Substring(0, 40) : line,
                Command = line,
                Source = path,
                InactiveReason = InactiveReason.Malformed,
                Evidence = new[] { path }
            };
        }

        /// <summary>A short name for a command: the program of its last segment (guards such as <c>test ... ||</c> come first).</summary>
        internal static string NameOf(string command)
        {
            var segments = Regex.Split(command, @"&&|\|\||;");
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                foreach (string token in segments[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token == "{" || token == "}" || token == "(" || token == ")" || (token.IndexOf('=') > 0 && !token.StartsWith("/", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    string word = token.TrimStart('(', '{', '-', '@');
                    int slash = word.LastIndexOf('/');
                    return slash >= 0 && slash < word.Length - 1 ? word.Substring(slash + 1) : word;
                }
            }

            return command.Length > 40 ? command.Substring(0, 40) : command;
        }

        private struct Token
        {
            public int Start;
            public int End;
        }

        private static List<Token> Tokens(string s)
        {
            var list = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                if (i >= s.Length)
                {
                    break;
                }

                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                list.Add(new Token { Start = start, End = i });
            }

            return list;
        }
    }
}
