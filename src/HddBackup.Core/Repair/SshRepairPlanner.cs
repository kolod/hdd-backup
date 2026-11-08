using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;

namespace HddBackup.Core.Repair
{
    /// <summary>
    /// Turns the reasons of the SSH check (feature 009) into repairs (feature 011, research R-5). Pure: it reads the
    /// file system through a <see cref="SystemReader"/> (which may sit on a pending tree, so repairs build on each
    /// other) and returns plans; nothing is written. Texts of files are handled as Latin-1 so that every byte the
    /// repair does not change is written back exactly as it was.
    /// </summary>
    public static class SshRepairPlanner
    {
        private const ushort Mode0755 = 0x1ED;
        private const ushort Mode0700 = 0x1C0;
        private const ushort Mode0600 = 0x180;
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        /// <summary>The repairs the wizard can offer for the reasons in <paramref name="report"/>, in the order of the reasons.</summary>
        public static IReadOnlyList<RepairOffer> Offers(SystemReader reader, SshReport report, DateTime today)
        {
            var result = new List<RepairOffer>();
            if (report == null || report.Settings == null || report.Server == null)
            {
                return result;
            }

            var server = report.Server;
            if (server.Installed && server.Enabled == ServerEnabledState.No)
            {
                result.Add(new RepairOffer(LoginReason.None, FindingCode.ServerNotEnabled, string.Empty, PlanEnableServer(reader, report), false));
            }
            else if (server.Installed && server.Enabled == ServerEnabledState.Masked)
            {
                result.Add(new RepairOffer(LoginReason.None, FindingCode.ServerMasked, string.Empty, PlanUnmaskServer(reader, report), false));
            }

            var settings = report.Settings;
            // root is left alone when somebody else can log in: a root that cannot is the usual, safe state
            bool someoneElseCanLogIn = (report.Accounts ?? new List<Account>()).Any(x => x.Name != "root" && x.CanLogIn == LoginAnswer.Yes);
            foreach (var account in report.Accounts ?? new List<Account>())
            {
                if (account.CanLogIn != LoginAnswer.No || (account.IsSystemAccount && account.Name != "root") || (account.Name == "root" && someoneElseCanLogIn))
                {
                    continue;
                }

                RepairPlan plan = null;
                bool key = false;
                switch (account.Reason)
                {
                    case LoginReason.NotInAllowUsers:
                        plan = settings.AllowUsers.Value.Count > 0 && !settings.AllowUsers.Value.Any(p => WildcardMatcher.IsMatch(p, account.Name))
                            ? PlanAllowUser(reader, report, account, today)
                            : PlanAllowGroup(reader, report, account, today);
                        break;
                    case LoginReason.Denied:
                        plan = PlanUnblockUser(reader, report, account, today);
                        break;
                    case LoginReason.KeyAuthOffNoPassword:
                        plan = PlanEnableKeyAuth(reader, report, today);
                        break;
                    case LoginReason.RootNotAllowed:
                        if (settings.PermitRootLogin.Value == RootLoginMode.No || settings.PermitRootLogin.Value == RootLoginMode.ForcedCommandsOnly)
                        {
                            plan = PlanRootLogin(reader, report, today);
                        }
                        else
                        {
                            key = true;
                        }

                        break;
                    case LoginReason.PasswordAuthOffNoKey:
                        key = true;
                        if (account.Password == PasswordState.Set)
                        {
                            plan = PlanEnablePasswordAuth(reader, report, today);
                        }

                        break;
                    case LoginReason.NoShell:
                        plan = PlanFixShell(reader, account);
                        break;
                    case LoginReason.KeyFileUnsafe:
                        plan = PlanFixKeyFileModes(reader, account);
                        break;
                    case LoginReason.NoPasswordNoKey:
                    case LoginReason.PasswordLocked:
                    case LoginReason.EmptyPasswordRefused:
                        key = settings.PubkeyAuthentication.Value;
                        break;
                    default:
                        break;
                }

                if (plan != null || key)
                {
                    result.Add(new RepairOffer(account.Reason, FindingCode.AccountCannot, account.Name, plan, key));
                }
            }

            // keys the server ignores although the account can log in another way: still worth repairing
            foreach (var account in report.Accounts ?? new List<Account>())
            {
                if (account.KeyCount > 0 && account.KeyFileSafety != KeyFileSafety.Ok && settings.StrictModes.Value
                    && !account.IsSystemAccount && !result.Any(o => o.Account == account.Name && o.Reason == LoginReason.KeyFileUnsafe))
                {
                    result.Add(new RepairOffer(LoginReason.KeyFileUnsafe, FindingCode.AccountKeyFileUnsafe, account.Name, PlanFixKeyFileModes(reader, account), false));
                }
            }

            return result;
        }

        // ------------------------------------------------------------------------- the server

        public static RepairPlan PlanEnableServer(SystemReader reader, SshReport report)
        {
            string unit = report.Server.Unit ?? "ssh.service";
            return EnableLinks(reader, unit, RepairKind.EnableServer, new List<EditChange>(), new List<ChangeLine>());
        }

        public static RepairPlan PlanUnmaskServer(SystemReader reader, SshReport report)
        {
            string unit = report.Server.Unit ?? "ssh.service";
            string maskPath = "/etc/systemd/system/" + unit;
            string target = reader.ReadLink(maskPath);
            if (target == null || !target.EndsWith("/dev/null", StringComparison.Ordinal))
            {
                return RepairPlan.Refused(RepairKind.UnmaskServer, RepairRefusal.NotRepairable);
            }

            var changes = new List<EditChange> { EditChange.Remove(maskPath, null) };
            var lines = new List<ChangeLine> { new ChangeLine(ChangeWhat.RemoveLink, maskPath, target) };
            return EnableLinks(reader, unit, RepairKind.UnmaskServer, changes, lines);
        }

        /// <summary>The links "systemctl enable" would make for the unit's [Install] section, appended to the given changes.</summary>
        private static RepairPlan EnableLinks(SystemReader reader, string unit, RepairKind kind, List<EditChange> changes, List<ChangeLine> lines)
        {
            string file = new[] { "/lib/systemd/system/", "/usr/lib/systemd/system/", "/etc/systemd/system/" }
                .Select(d => d + unit)
                .FirstOrDefault(p => reader.IsFile(p));
            if (file == null)
            {
                return RepairPlan.Refused(kind, RepairRefusal.NoInstallSection);
            }

            string text = reader.ReadText(file);
            if (text == null)
            {
                return RepairPlan.Refused(kind, RepairRefusal.Unreadable);
            }

            var parsed = new SystemdUnit(unit);
            SystemdScan.Parse(parsed, text);
            var wantedBy = parsed.Words("Install", "WantedBy").ToList();
            if (wantedBy.Count == 0)
            {
                return RepairPlan.Refused(kind, RepairRefusal.NoInstallSection);
            }

            if (Encoding.UTF8.GetByteCount(file) >= Ext4InodeLinkLimit)
            {
                return RepairPlan.Refused(kind, RepairRefusal.LinkTooLong);
            }

            foreach (string target in wantedBy)
            {
                string dir = "/etc/systemd/system/" + target + ".wants";
                if (!reader.IsDirectory(dir))
                {
                    changes.Add(EditChange.NewDirectory(dir, Mode0755, 0, 0));
                    lines.Add(new ChangeLine(ChangeWhat.CreateFolder, dir));
                }

                string link = dir + "/" + unit;
                if (reader.Lookup(link, false) != null)
                {
                    return RepairPlan.Refused(kind, RepairRefusal.AlreadyThere);
                }

                changes.Add(EditChange.NewSymlink(link, file));
                lines.Add(new ChangeLine(ChangeWhat.CreateLink, link, null, file));
            }

            foreach (string alias in parsed.Words("Install", "Alias"))
            {
                string path = "/etc/systemd/system/" + alias;
                if (reader.Lookup(path, false) == null)
                {
                    changes.Add(EditChange.NewSymlink(path, file));
                    lines.Add(new ChangeLine(ChangeWhat.CreateLink, path, null, file));
                }
            }

            return new RepairPlan(new RepairTitle(kind, unit), lines, changes);
        }

        // ------------------------------------------------------------------------- server settings

        public static RepairPlan PlanAllowUser(SystemReader reader, SshReport report, Account account, DateTime today)
        {
            var s = report.Settings.AllowUsers;
            if (s.IsDefault)
            {
                return RepairPlan.Refused(RepairKind.AllowUser, RepairRefusal.NotRepairable, account.Name);
            }

            return InsertListLine(reader, s, "AllowUsers " + account.Name, RepairKind.AllowUser, account.Name);
        }

        public static RepairPlan PlanAllowGroup(SystemReader reader, SshReport report, Account account, DateTime today)
        {
            var s = report.Settings.AllowGroups;
            string group = GroupName(reader, account.Gid);
            if (s.IsDefault)
            {
                return RepairPlan.Refused(RepairKind.AllowGroup, RepairRefusal.NotRepairable, account.Name);
            }

            if (group == null || group.IndexOfAny(new[] { ' ', '\t', '*', '?', '!' }) >= 0)
            {
                return RepairPlan.Refused(RepairKind.AllowGroup, RepairRefusal.NoGroupName, account.Name);
            }

            return InsertListLine(reader, s, "AllowGroups " + group, RepairKind.AllowGroup, account.Name, group);
        }

        private static RepairPlan InsertListLine(SystemReader reader, SettingValue<IReadOnlyList<string>> s, string line, RepairKind kind, string account, string arg2 = null)
        {
            string path = reader.Resolve(s.Source) ?? s.Source;
            if (!TryReadRaw(reader, path, out string text, out string bom))
            {
                return RepairPlan.Refused(kind, RepairRefusal.Unreadable, account);
            }

            string edited = SshdEdit.InsertAfter(text, s.Line, Raw(line));
            if (edited == null)
            {
                return RepairPlan.Refused(kind, RepairRefusal.NotRepairable, account);
            }

            return new RepairPlan(
                new RepairTitle(kind, account, arg2),
                new[] { new ChangeLine(ChangeWhat.EditText, path, null, line) },
                new[] { EditChange.Edit(path, ToBytes(bom, edited), null) });
        }

        public static RepairPlan PlanUnblockUser(SystemReader reader, SshReport report, Account account, DateTime today)
        {
            var tokens = report.Settings.ListTokens("denyusers").Where(t => t.Token == account.Name).ToList();
            if (tokens.Count == 0)
            {
                return RepairPlan.Refused(RepairKind.UnblockUser, RepairRefusal.NotRepairable, account.Name);
            }

            var changes = new List<EditChange>();
            var lines = new List<ChangeLine>();
            foreach (var file in tokens.GroupBy(t => t.File))
            {
                string path = reader.Resolve(file.Key) ?? file.Key;
                if (!TryReadRaw(reader, path, out string text, out string bom))
                {
                    return RepairPlan.Refused(RepairKind.UnblockUser, RepairRefusal.Unreadable, account.Name);
                }

                foreach (int line in file.Select(t => t.Line).Distinct().OrderByDescending(x => x))
                {
                    text = SshdEdit.RemoveToken(text, line, account.Name, SshdEdit.Note(today));
                    if (text == null)
                    {
                        return RepairPlan.Refused(RepairKind.UnblockUser, RepairRefusal.NotRepairable, account.Name);
                    }
                }

                changes.Add(EditChange.Edit(path, ToBytes(bom, text), null));
                lines.Add(new ChangeLine(ChangeWhat.EditText, path, "DenyUsers … " + account.Name, "DenyUsers …"));
            }

            return new RepairPlan(new RepairTitle(RepairKind.UnblockUser, account.Name), lines, changes);
        }

        public static RepairPlan PlanEnableKeyAuth(SystemReader reader, SshReport report, DateTime today) =>
            ReplaceSetting(reader, report.Settings.PubkeyAuthentication, "PubkeyAuthentication", "yes", RepairKind.EnableKeyAuth, RepairRisk.None, today);

        public static RepairPlan PlanEnablePasswordAuth(SystemReader reader, SshReport report, DateTime today) =>
            ReplaceSetting(reader, report.Settings.PasswordAuthentication, "PasswordAuthentication", "yes", RepairKind.EnablePasswordAuth, RepairRisk.LowersSecurity, today);

        /// <summary>Root may log in with a key (the safe repair); logging in with a password is listed as the risky alternative.</summary>
        public static RepairPlan PlanRootLogin(SystemReader reader, SshReport report, DateTime today)
        {
            var keys = ReplaceSetting(reader, report.Settings.PermitRootLogin, "PermitRootLogin", "prohibit-password", RepairKind.RootKeysOnly, RepairRisk.None, today);
            var password = ReplaceSetting(reader, report.Settings.PermitRootLogin, "PermitRootLogin", "yes", RepairKind.RootPassword, RepairRisk.LowersSecurity, today);
            if (!keys.CanApply)
            {
                return keys;
            }

            return new RepairPlan(keys.Title, keys.Summary, keys.Changes, keys.Risk, password.CanApply ? new[] { password } : null);
        }

        private static RepairPlan ReplaceSetting<T>(SystemReader reader, SettingValue<T> setting, string keyword, string value, RepairKind kind, RepairRisk risk, DateTime today)
        {
            string line = keyword + " " + value;
            string note = SshdEdit.Note(today);
            if (setting.IsDefault)
            {
                string main = reader.Resolve("/etc/ssh/sshd_config") ?? "/etc/ssh/sshd_config";
                if (!TryReadRaw(reader, main, out string mainText, out string mainBom))
                {
                    return RepairPlan.Refused(kind, RepairRefusal.Unreadable);
                }

                return new RepairPlan(
                    new RepairTitle(kind),
                    new[] { new ChangeLine(ChangeWhat.EditText, main, null, line) },
                    new[] { EditChange.Edit(main, ToBytes(mainBom, SshdEdit.AddSetting(mainText, line, note)), null) },
                    risk);
            }

            string path = reader.Resolve(setting.Source) ?? setting.Source;
            if (!TryReadRaw(reader, path, out string text, out string bom))
            {
                return RepairPlan.Refused(kind, RepairRefusal.Unreadable);
            }

            string edited = SshdEdit.ReplaceLine(text, setting.Line, line, note);
            if (edited == null)
            {
                return RepairPlan.Refused(kind, RepairRefusal.NotRepairable);
            }

            string old = setting.Line >= 1 ? text.Split('\n').ElementAtOrDefault(setting.Line - 1)?.Trim() : null;
            return new RepairPlan(
                new RepairTitle(kind),
                new[] { new ChangeLine(ChangeWhat.EditText, path, old == null ? null : Unraw(old), line) },
                new[] { EditChange.Edit(path, ToBytes(bom, edited), null) },
                risk);
        }

        // ------------------------------------------------------------------------- the account

        public static RepairPlan PlanFixShell(SystemReader reader, Account account)
        {
            string shells = reader.ReadText("/etc/shells") ?? string.Empty;
            var listed = new HashSet<string>(SystemReader.SplitLines(shells).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'), StringComparer.Ordinal);
            string shell = new[] { "/bin/bash", "/usr/bin/bash", "/bin/sh", "/usr/bin/sh" }
                .FirstOrDefault(c => reader.Exists(c) && (listed.Count == 0 || listed.Contains(c)));
            if (shell == null)
            {
                return RepairPlan.Refused(RepairKind.FixShell, RepairRefusal.NoSuitableShell, account.Name);
            }

            string path = reader.Resolve("/etc/passwd") ?? "/etc/passwd";
            if (!TryReadRaw(reader, path, out string text, out string bom))
            {
                return RepairPlan.Refused(RepairKind.FixShell, RepairRefusal.Unreadable, account.Name);
            }

            string edited = PasswdEdit.ChangeShell(text, Raw(account.Name), Raw(shell));
            if (edited == null)
            {
                return RepairPlan.Refused(RepairKind.FixShell, RepairRefusal.NotRepairable, account.Name);
            }

            return new RepairPlan(
                new RepairTitle(RepairKind.FixShell, account.Name, shell),
                new[] { new ChangeLine(ChangeWhat.ChangeShell, path, account.Shell, shell) },
                new[] { EditChange.Edit(path, ToBytes(bom, edited), null) });
        }

        public static RepairPlan PlanFixKeyFileModes(SystemReader reader, Account account)
        {
            var problems = KeyFileSafetyCheck.Problems(reader, account, account.KeyFilePath);
            if (problems.Count == 0)
            {
                return RepairPlan.Refused(RepairKind.FixKeyFileModes, RepairRefusal.AlreadyThere, account.Name);
            }

            var changes = new List<EditChange>();
            var lines = new List<ChangeLine>();
            foreach (var p in problems)
            {
                ushort? mode = null;
                if (p.Writable)
                {
                    mode = p.Level == 0 ? Mode0600 : p.Level == 1 && !p.IsHome ? Mode0700 : (ushort)(p.Entry.Mode & 0xFFF & ~0x12);
                }

                uint? uid = p.WrongOwner ? account.Uid : (uint?)null;
                uint? gid = p.WrongOwner ? account.Gid : (uint?)null;
                changes.Add(EditChange.Attributes(p.Path, mode, uid, gid));
                string before = Convert.ToString(p.Entry.Mode & 0xFFF, 8) + " " + p.Entry.Uid + ":" + p.Entry.Gid;
                string after = Convert.ToString(mode ?? (p.Entry.Mode & 0xFFF), 8) + " " + (uid ?? p.Entry.Uid) + ":" + (gid ?? p.Entry.Gid);
                lines.Add(new ChangeLine(ChangeWhat.SetModeOwner, p.Path, before, after));
            }

            return new RepairPlan(new RepairTitle(RepairKind.FixKeyFileModes, account.Name), lines, changes);
        }

        // ------------------------------------------------------------------------- adding a key

        /// <summary>
        /// Adds <paramref name="key"/> to the key file the server reads for the account, creating the folder (mode 700) and
        /// the file (mode 600, owner the account) when they are missing. Existing keys are kept byte for byte.
        /// </summary>
        public static RepairPlan PlanAddKey(SystemReader reader, SshReport report, string accountName, PublicKey key)
        {
            var account = report?.Accounts?.FirstOrDefault(a => a.Name == accountName);
            if (account == null || string.IsNullOrEmpty(account.Home) || !reader.IsDirectory(account.Home))
            {
                return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.NoHome, accountName);
            }

            var settings = report.Settings;
            bool onlyCommand = settings.HasAuthorizedKeysCommand && settings.AuthorizedKeysFiles.Value.All(p => p.Equals("none", StringComparison.OrdinalIgnoreCase));
            if (onlyCommand || account.KeyFilePath == null)
            {
                return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.KeysFromCommand, accountName);
            }

            var title = new RepairTitle(RepairKind.AddKey, accountName, key.Fingerprint);
            string path = account.KeyFilePath;
            var link = reader.Lookup(path, false);
            if (link != null && link.Kind == FsEntryKind.Symlink)
            {
                string final = reader.Resolve(path);
                if (final == null)
                {
                    return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.OutsideFileSystem, accountName);
                }

                path = final;
            }

            var existing = reader.Lookup(path);
            if (existing != null && existing.Kind != FsEntryKind.File)
            {
                return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.NotRepairable, accountName);
            }

            var changes = new List<EditChange>();
            var lines = new List<ChangeLine>();
            string keyLine = Raw(key.Line);
            if (existing != null)
            {
                byte[] bytes = reader.ReadBytes(path);
                if (bytes == null)
                {
                    return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.Unreadable, accountName);
                }

                string text = Latin1.GetString(bytes);
                if (key.IsIn(text))
                {
                    return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.Duplicate, accountName);
                }

                string eol = new LineText(text).Dominant;
                string add = (bytes.Length > 0 && bytes[bytes.Length - 1] != (byte)'\n' ? eol : string.Empty) + keyLine + eol;
                var all = new byte[bytes.Length + add.Length];
                Buffer.BlockCopy(bytes, 0, all, 0, bytes.Length);
                Buffer.BlockCopy(Latin1.GetBytes(add), 0, all, bytes.Length, add.Length);
                changes.Add(EditChange.Edit(path, all, null));
            }
            else
            {
                var missing = new List<string>();
                string dir = ParentOf(path);
                while (dir.Length > 0 && dir != "/" && !reader.IsDirectory(dir))
                {
                    missing.Insert(0, dir);
                    dir = ParentOf(dir);
                }

                string home = (reader.Resolve(account.Home) ?? account.Home).TrimEnd('/');
                if (missing.Any(m => m == home || !m.StartsWith(home + "/", StringComparison.Ordinal)))
                {
                    return RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.OutsideFileSystem, accountName);
                }

                foreach (string m in missing)
                {
                    changes.Add(EditChange.NewDirectory(m, Mode0700, account.Uid, account.Gid));
                    lines.Add(new ChangeLine(ChangeWhat.CreateFolder, m, null, "700 " + account.Uid + ":" + account.Gid));
                }

                changes.Add(EditChange.NewFile(path, Latin1.GetBytes(keyLine + "\n"), Mode0600, account.Uid, account.Gid));
                lines.Add(new ChangeLine(ChangeWhat.CreateFile, path, null, "600 " + account.Uid + ":" + account.Gid));
            }

            lines.Add(new ChangeLine(ChangeWhat.AppendKey, path, null, key.Fingerprint));
            return new RepairPlan(title, lines, changes);
        }

        // ------------------------------------------------------------------------- helpers

        private const int Ext4InodeLinkLimit = 60;

        private static string ParentOf(string path)
        {
            EditChange.Split(path, out string parent, out _);
            return parent;
        }

        private static string GroupName(SystemReader reader, uint gid)
        {
            foreach (string line in reader.ReadLines("/etc/group"))
            {
                string[] f = line.Split(':');
                if (f.Length >= 3 && uint.TryParse(f[2], out uint id) && id == gid && f[0].Length > 0)
                {
                    return f[0];
                }
            }

            return null;
        }

        /// <summary>A file as Latin-1 text (every byte one char) so that editing is lossless; a leading UTF-8 byte-order mark is split off.</summary>
        private static bool TryReadRaw(SystemReader reader, string path, out string text, out string bom)
        {
            text = null;
            bom = string.Empty;
            byte[] bytes = path == null ? null : reader.ReadBytes(path);
            if (bytes == null)
            {
                return false;
            }

            int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            bom = skip == 0 ? string.Empty : Latin1.GetString(bytes, 0, 3);
            text = Latin1.GetString(bytes, skip, bytes.Length - skip);
            return true;
        }

        private static byte[] ToBytes(string bom, string text) => Latin1.GetBytes(bom + text);

        /// <summary>A Unicode string as the Latin-1 text of its UTF-8 bytes, to be mixed into raw file text.</summary>
        private static string Raw(string s) => Latin1.GetString(Encoding.UTF8.GetBytes(s));

        private static string Unraw(string s) => Encoding.UTF8.GetString(Latin1.GetBytes(s));
    }
}
