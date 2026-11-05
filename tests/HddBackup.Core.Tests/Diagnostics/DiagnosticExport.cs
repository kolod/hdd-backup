using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HddBackup.Core.Diagnostics;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>
    /// Writes what the tools found as plain listings for the independent check in CI
    /// (tests/fixtures/ext4/check-diagnostics.sh): the effective SSH settings in the form of
    /// <c>sshd -T</c> and the enabled units in the form of <c>systemctl list-unit-files</c>.
    /// Only when HDDBACKUP_EXPORT_DIR is set.
    /// </summary>
    internal static class DiagnosticExport
    {
        public static void Ssh(string fixture, SshSettings s)
        {
            string dir = Directory();
            if (dir == null)
            {
                return;
            }

            var lines = new List<string>();
            foreach (var p in s.Ports)
            {
                lines.Add("port\t" + p.Value);
            }

            lines.Add("permitrootlogin\t" + RootValue(s.PermitRootLogin.Value));
            lines.Add("passwordauthentication\t" + YesNo(s.PasswordAuthentication.Value));
            lines.Add("kbdinteractiveauthentication\t" + YesNo(s.KbdInteractiveAuthentication.Value));
            lines.Add("usepam\t" + YesNo(s.UsePam.Value));
            lines.Add("pubkeyauthentication\t" + YesNo(s.PubkeyAuthentication.Value));
            lines.Add("permitemptypasswords\t" + YesNo(s.PermitEmptyPasswords.Value));
            lines.Add("authorizedkeysfile\t" + string.Join(" ", s.AuthorizedKeysFiles.Value));
            AddList(lines, "allowusers", s.AllowUsers.Value);
            AddList(lines, "denyusers", s.DenyUsers.Value);
            AddList(lines, "allowgroups", s.AllowGroups.Value);
            AddList(lines, "denygroups", s.DenyGroups.Value);
            File.WriteAllText(Path.Combine(dir, fixture + ".ssh.tsv"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        public static void Units(string fixture, SystemdScan scan)
        {
            string dir = Directory();
            if (dir == null)
            {
                return;
            }

            var names = scan.EnabledUnits()
                .Where(u => !u.Masked && !u.UnitMissing)
                .Select(u => u.UnitFile)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal);
            string text = string.Join("\n", names);
            File.WriteAllText(Path.Combine(dir, fixture + ".units.tsv"), text.Length == 0 ? string.Empty : text + "\n", new UTF8Encoding(false));
        }

        private static string Directory()
        {
            string export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (string.IsNullOrEmpty(export))
            {
                return null;
            }

            string dir = Path.Combine(export, "diagnostics");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static void AddList(List<string> lines, string key, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                lines.Add(key + "\t" + string.Join(" ", values));
            }
        }

        private static string YesNo(bool value) => value ? "yes" : "no";

        private static string RootValue(RootLoginMode mode)
        {
            switch (mode)
            {
                case RootLoginMode.Yes: return "yes";
                case RootLoginMode.No: return "no";
                case RootLoginMode.ForcedCommandsOnly: return "forced-commands-only";
                default: return "without-password";
            }
        }
    }
}
