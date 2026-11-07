using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Everything that starts by itself on the opened system, and every scheduled task, in one list: systemd services,
    /// timers and sockets (only those that really start: reachable from the default target), per-user units, boot
    /// scripts, desktop autostart entries and cron tasks. Inactive entries are kept with the reason (research R-8, R-9).
    /// </summary>
    public static class StartupInventory
    {
        public const int MaxEntries = 5000;

        public static StartupResult Run(SystemReader reader, DiagnosticsSource source, CancellationToken cancellationToken)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var result = new StartupResult(source) { ReadNotes = reader.Notes };
            var root = LinuxRoot.Detect(reader);
            result.System = root;
            if (!root.IsLinux)
            {
                return result;
            }

            var db = AccountDatabase.Read(reader, null);
            var users = new HashSet<string>(db.Accounts.Select(a => a.Name), StringComparer.Ordinal);
            var homes = db.Accounts
                .Where(a => a.Name == "root" || !a.IsSystemAccount || a.HasUsableShell)
                .Select(a => new KeyValuePair<string, string>(a.Name, a.Home))
                .ToList();
            var systemd = new SystemdScan(reader);
            var entries = new List<StartupEntry>();

            if (systemd.HasSystemd)
            {
                SystemdEntries(systemd, entries, homes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            SysVEntries(reader, systemd, entries);
            entries.AddRange(AutostartScan.Run(reader, homes));
            cancellationToken.ThrowIfCancellationRequested();
            var cron = CronScan.Run(reader, systemd, users);
            entries.AddRange(cron.Entries);

            var notes = new List<StartupNote>();
            if (!cron.Installed)
            {
                notes.Add(new StartupNote(StartupNoteCode.NoCron));
            }
            else if (cron.ServiceNotEnabled)
            {
                notes.Add(new StartupNote(StartupNoteCode.CronNotEnabled));
            }

            var packages = new PackageOwnership(reader);
            foreach (var e in entries)
            {
                e.Origin = OriginOf(e);
                if (e.Origin == EntryOrigin.Standard && packages.Available)
                {
                    // A file in /etc that no package lists was put there by a person, whatever its name looks like.
                    string file = ConfigFileOf(e);
                    if (file != null && packages.Owns(file) == false)
                    {
                        e.Origin = EntryOrigin.Custom;
                    }
                }
            }

            var sorted = entries
                .OrderBy(e => e.Active ? 0 : 1)
                .ThenBy(e => (int)e.Kind)
                .ThenBy(e => (int)e.Origin) // what a person added comes first within its kind
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.User, StringComparer.Ordinal)
                .ThenBy(e => e.Source, StringComparer.Ordinal)
                .ToList();
            if (sorted.Count > MaxEntries)
            {
                notes.Add(new StartupNote(StartupNoteCode.ListCut, sorted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), MaxEntries.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                sorted = sorted.Take(MaxEntries).ToList();
            }

            result.Entries = sorted;
            result.Notes = notes;
            return result;
        }

        /// <summary>Standard = shipped by the distribution: vendor units, system autostart, package cron files, init scripts.</summary>
        internal static EntryOrigin OriginOf(StartupEntry e)
        {
            string c = e.Command ?? string.Empty;
            if (c.Contains("/usr/local/") || c.Contains("/home/") || c.Contains("/opt/") || c.Contains("/root/"))
            {
                return EntryOrigin.Custom;
            }

            switch (e.Kind)
            {
                case StartupKind.Service:
                case StartupKind.Timer:
                case StartupKind.Socket:
                    string unit = e.Evidence.Count > 1 ? e.Evidence[1] : string.Empty;
                    return unit.StartsWith("/usr/lib/systemd/system/", StringComparison.Ordinal) || unit.StartsWith("/lib/systemd/system/", StringComparison.Ordinal)
                        ? EntryOrigin.Standard
                        : EntryOrigin.Custom;
                case StartupKind.BootScript:
                    return e.Source == "/etc/rc.local" ? EntryOrigin.Custom : EntryOrigin.Standard;
                case StartupKind.Desktop:
                    return e.User.Length == 0 ? EntryOrigin.Standard : EntryOrigin.Custom;
                case StartupKind.Cron:
                    return e.Source.StartsWith("/var/spool/cron", StringComparison.Ordinal) ? EntryOrigin.Custom : EntryOrigin.Standard;
                default:
                    return EntryOrigin.Custom;
            }
        }

        /// <summary>The file in /etc an entry stands on, when package ownership can tell something about it.</summary>
        private static string ConfigFileOf(StartupEntry e)
        {
            switch (e.Kind)
            {
                case StartupKind.BootScript:
                    return e.Command.StartsWith("/etc/init.d/", StringComparison.Ordinal) ? e.Command : null;
                case StartupKind.Desktop:
                    return e.Source.StartsWith("/etc/", StringComparison.Ordinal) ? e.Source : null;
                case StartupKind.Cron:
                    string path = e.Evidence.Count > 0 ? e.Evidence[0] : null;
                    return path != null && path.StartsWith("/etc/", StringComparison.Ordinal) && path != "/etc/crontab" ? path : null;
                default:
                    return null;
            }
        }

        private static void SystemdEntries(SystemdScan systemd, List<StartupEntry> entries, List<KeyValuePair<string, string>> homes)
        {
            foreach (var e in systemd.EnabledUnits())
            {
                var kind = KindOf(e.Kind);
                if (kind == null)
                {
                    continue;
                }

                entries.Add(FromUnit(systemd, e, kind.Value, string.Empty));
            }

            var listed = new HashSet<string>(entries.Select(x => x.Name), StringComparer.Ordinal);
            foreach (string masked in systemd.MaskedUnits().Where(m => !listed.Contains(m)))
            {
                var kind = KindOf(masked.Substring(masked.LastIndexOf('.') + 1));
                if (kind == null)
                {
                    continue;
                }

                entries.Add(new StartupEntry
                {
                    Kind = kind.Value,
                    Name = masked,
                    Source = "/etc/systemd/system",
                    InactiveReason = InactiveReason.Masked,
                    Evidence = new[] { "/etc/systemd/system/" + masked }
                });
            }

            foreach (var h in homes)
            {
                foreach (var e in systemd.UserUnits(h.Key, h.Value))
                {
                    var kind = KindOf(e.Kind);
                    if (kind == null)
                    {
                        continue;
                    }

                    entries.Add(FromUnit(systemd, e, kind == StartupKind.Service ? StartupKind.UserService : kind.Value, h.Key));
                }
            }
        }

        private static StartupKind? KindOf(string suffix)
        {
            switch (suffix)
            {
                case "service":
                    return StartupKind.Service;
                case "timer":
                    return StartupKind.Timer;
                case "socket":
                    return StartupKind.Socket;
                default:
                    return null;
            }
        }

        private static StartupEntry FromUnit(SystemdScan systemd, EnabledUnit e, StartupKind kind, string user)
        {
            var entry = new StartupEntry
            {
                Kind = kind,
                Name = e.Name,
                Source = e.Target,
                User = user,
                Evidence = new[] { e.LinkPath }
            };
            if (e.Masked)
            {
                entry.InactiveReason = InactiveReason.Masked;
                return entry;
            }

            if (e.UnitMissing)
            {
                entry.InactiveReason = InactiveReason.UnitMissing;
                return entry;
            }

            var unit = systemd.FindUnit(e.Name);
            if (unit.Masked)
            {
                // Masked in /etc/systemd/system: the start link stays but the unit never loads.
                entry.InactiveReason = InactiveReason.Masked;
                return entry;
            }

            if (unit.Unreadable)
            {
                entry.InactiveReason = InactiveReason.Unreadable;
                return entry;
            }

            switch (kind)
            {
                case StartupKind.Timer:
                    FillTimer(systemd, entry, unit);
                    break;
                case StartupKind.Socket:
                    entry.Command = unit.First("Socket", "ListenStream") ?? unit.First("Socket", "ListenDatagram") ?? unit.First("Socket", "ListenSequentialPacket") ?? string.Empty;
                    break;
                default:
                    entry.Command = unit.ExecStart;
                    if (entry.User.Length == 0)
                    {
                        entry.User = unit.User.Length > 0 ? unit.User : "root";
                    }

                    break;
            }

            if (!e.Reached)
            {
                entry.InactiveReason = InactiveReason.TargetNotReached;
            }

            if (entry.Evidence.Count > 0 && unit.Path != null)
            {
                entry.Evidence = new[] { e.LinkPath, unit.Path };
            }

            return entry;
        }

        private static void FillTimer(SystemdScan systemd, StartupEntry entry, SystemdUnit timer)
        {
            string calendar = timer.First("Timer", "OnCalendar");
            string raw = calendar;
            if (calendar != null)
            {
                entry.Schedule = CronSchedule.FromTimer(calendar);
            }
            else
            {
                foreach (string key in new[] { "OnBootSec", "OnStartupSec", "OnActiveSec", "OnUnitActiveSec", "OnUnitInactiveSec" })
                {
                    string v = timer.First("Timer", key);
                    if (v != null)
                    {
                        raw = key + "=" + v;
                        entry.Schedule = CronSchedule.Custom(raw);
                        break;
                    }
                }
            }

            entry.ScheduleText = raw ?? string.Empty;
            string serviceName = timer.First("Timer", "Unit");
            if (string.IsNullOrEmpty(serviceName))
            {
                string n = entry.Name;
                serviceName = n.EndsWith(".timer", StringComparison.Ordinal) ? n.Substring(0, n.Length - ".timer".Length) + ".service" : n;
            }

            var service = systemd.FindUnit(serviceName);
            entry.Command = service.Exists ? service.ExecStart : string.Empty;
            entry.User = service.Exists && service.User.Length > 0 ? service.User : "root";
        }

        /// <summary>
        /// Start links of old init scripts, for systems without systemd or scripts that have no unit (on systemd systems
        /// the generator turns such scripts into services, which are listed through their own links).
        /// </summary>
        private static void SysVEntries(SystemReader reader, SystemdScan systemd, List<StartupEntry> entries)
        {
            foreach (var link in systemd.SysVStartLinks())
            {
                string script = link.Key;
                if (systemd.HasSystemd && (systemd.FindUnit(script + ".service").Exists || systemd.FindUnit(script + ".service").Masked))
                {
                    continue;
                }

                string initScript = "/etc/init.d/" + script;
                bool exists = reader.Exists(initScript);
                entries.Add(new StartupEntry
                {
                    Kind = StartupKind.BootScript,
                    Name = script,
                    Command = initScript,
                    Schedule = CronSchedule.AtBoot(string.Empty),
                    User = "root",
                    Source = link.Value,
                    InactiveReason = exists ? InactiveReason.None : InactiveReason.UnitMissing,
                    Evidence = new[] { link.Value }
                });
            }
        }
    }
}
