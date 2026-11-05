using System;
using System.Collections.Generic;
using System.Linq;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// One account of the opened system, reduced to facts. The password is only a
    /// <see cref="PasswordState"/> and the keys are only counted: no field can hold a secret.
    /// </summary>
    public sealed class Account
    {
        public string Name { get; internal set; } = string.Empty;

        public uint Uid { get; internal set; }

        public uint Gid { get; internal set; }

        public string Home { get; internal set; } = string.Empty;

        public string Shell { get; internal set; } = string.Empty;

        /// <summary>The shell exists in <c>/etc/passwd</c> and is not <c>nologin</c> or <c>false</c>.</summary>
        public bool HasUsableShell { get; internal set; }

        public PasswordState Password { get; internal set; } = PasswordState.Unknown;

        /// <summary>The account has an expiry date (not evaluated: the date of the image is unknown).</summary>
        public bool HasExpiry { get; internal set; }

        /// <summary>Valid key lines found in the authorized-keys files.</summary>
        public int KeyCount { get; internal set; }

        /// <summary>Keys may exist that were not counted (a key command, or an unreadable file).</summary>
        public bool KeysUnknown { get; internal set; }

        /// <summary>Names of the groups the account belongs to (primary group included).</summary>
        public IReadOnlyList<string> Groups { get; internal set; } = Array.Empty<string>();

        /// <summary>uid below 1000 (and not root), or the "nobody" account.</summary>
        public bool IsSystemAccount { get; internal set; }

        public LoginAnswer CanLogInWithPassword { get; internal set; } = LoginAnswer.Unknown;

        public LoginAnswer CanLogInWithKey { get; internal set; } = LoginAnswer.Unknown;

        public LoginReason PasswordReason { get; internal set; }

        public LoginReason KeyReason { get; internal set; }

        /// <summary>The overall answer: can this account log in over SSH at all.</summary>
        public LoginAnswer CanLogIn { get; internal set; } = LoginAnswer.Unknown;

        /// <summary>By what the account can log in (when <see cref="CanLogIn"/> is Yes).</summary>
        public LoginMethod Method { get; internal set; }

        /// <summary>The first reason the account cannot log in (when <see cref="CanLogIn"/> is No).</summary>
        public LoginReason Reason { get; internal set; }
    }

    /// <summary>Accounts from <c>/etc/passwd</c>, <c>/etc/group</c> and <c>/etc/shadow</c>, plus the count of authorized keys.</summary>
    public sealed class AccountDatabase
    {
        private static readonly string[] NoLoginShells =
        {
            "/usr/sbin/nologin", "/sbin/nologin", "/bin/false", "/usr/bin/false", "/usr/sbin/nologin.shadow", "nologin", "false"
        };

        private AccountDatabase(IReadOnlyList<Account> accounts, bool passwdReadable, bool shadowReadable)
        {
            Accounts = accounts;
            PasswdReadable = passwdReadable;
            ShadowReadable = shadowReadable;
        }

        public IReadOnlyList<Account> Accounts { get; }

        public bool PasswdReadable { get; }

        /// <summary>The shadow file was read and holds entries; otherwise the password states are Unknown.</summary>
        public bool ShadowReadable { get; }

        /// <summary>Reads the account files and counts the authorized keys of the accounts that have a usable shell.</summary>
        public static AccountDatabase Read(SystemReader reader, SshSettings settings)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            string passwd = reader.ReadText("/etc/passwd");
            string shadowText = reader.ReadText("/etc/shadow");
            string groupText = reader.ReadText("/etc/group");
            var shadow = ParseShadow(shadowText);
            var groups = ParseGroups(groupText);
            var accounts = new List<Account>();
            if (passwd != null)
            {
                foreach (string raw in SystemReader.SplitLines(passwd))
                {
                    var account = ParsePasswdLine(raw, out string passwdHash);
                    if (account == null)
                    {
                        continue;
                    }

                    ApplyPassword(account, passwdHash, shadow, shadowText != null);
                    account.Groups = groups.GroupsOf(account.Name, account.Gid);
                    accounts.Add(account);
                }
            }

            if (settings != null)
            {
                foreach (var a in accounts.Where(x => x.HasUsableShell))
                {
                    CountKeys(reader, settings, a);
                }
            }

            // A shadow file in which no line could be understood is as good as an unreadable one.
            bool shadowUsable = shadowText != null && (shadow.Count > 0 || accounts.Count == 0);
            return new AccountDatabase(accounts, passwd != null, shadowUsable);
        }

        internal static Account ParsePasswdLine(string raw, out string passwdHash)
        {
            passwdHash = null;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                return null;
            }

            var f = raw.Split(':');
            if (f.Length < 7 || f[0].Length == 0 || !uint.TryParse(f[2], out uint uid) || !uint.TryParse(f[3], out uint gid))
            {
                return null;
            }

            passwdHash = f[1];
            string shell = f[6].Trim();
            var a = new Account
            {
                Name = f[0],
                Uid = uid,
                Gid = gid,
                Home = f[5],
                Shell = shell,
                HasUsableShell = shell.Length > 0 && !NoLoginShells.Contains(shell, StringComparer.Ordinal),
                IsSystemAccount = f[0] != "root" && (uid < 1000 || f[0] == "nobody" || uid == 65534)
            };
            return a;
        }

        /// <summary>Maps the password field to a state; the field itself is dropped here.</summary>
        internal static PasswordState StateOf(string field)
        {
            if (field == null)
            {
                return PasswordState.Unknown;
            }

            if (field.Length == 0)
            {
                return PasswordState.Empty;
            }

            if (field[0] == '!')
            {
                return PasswordState.Locked;
            }

            if (field[0] == '*')
            {
                return PasswordState.NoPassword;
            }

            return PasswordState.Set;
        }

        private static void ApplyPassword(Account account, string passwdHash, Dictionary<string, ShadowEntry> shadow, bool shadowRead)
        {
            if (shadow.TryGetValue(account.Name, out var entry))
            {
                account.Password = entry.State;
                account.HasExpiry = entry.HasExpiry;
                return;
            }

            if (!shadowRead && passwdHash != null && passwdHash != "x")
            {
                // An old-style system keeps the password field in /etc/passwd.
                account.Password = StateOf(passwdHash);
                return;
            }

            account.Password = PasswordState.Unknown;
        }

        private static Dictionary<string, ShadowEntry> ParseShadow(string text)
        {
            var result = new Dictionary<string, ShadowEntry>(StringComparer.Ordinal);
            if (text == null)
            {
                return result;
            }

            foreach (string raw in SystemReader.SplitLines(text))
            {
                if (raw.Length == 0 || raw[0] == '#')
                {
                    continue;
                }

                var f = raw.Split(':');
                if (f.Length < 2 || f[0].Length == 0)
                {
                    continue;
                }

                result[f[0]] = new ShadowEntry(StateOf(f[1]), f.Length > 7 && f[7].Trim().Length > 0);
            }

            return result;
        }

        private static GroupTable ParseGroups(string text)
        {
            var table = new GroupTable();
            if (text == null)
            {
                return table;
            }

            foreach (string raw in SystemReader.SplitLines(text))
            {
                var f = raw.Split(':');
                if (f.Length < 3 || f[0].Length == 0)
                {
                    continue;
                }

                if (uint.TryParse(f[2], out uint gid) && !table.ByGid.ContainsKey(gid))
                {
                    table.ByGid[gid] = f[0];
                }

                if (f.Length > 3)
                {
                    foreach (string m in f[3].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string user = m.Trim();
                        if (!table.Members.TryGetValue(user, out var list))
                        {
                            list = new List<string>();
                            table.Members[user] = list;
                        }

                        list.Add(f[0]);
                    }
                }
            }

            return table;
        }

        private static void CountKeys(SystemReader reader, SshSettings settings, Account account)
        {
            if (settings.HasAuthorizedKeysCommand)
            {
                account.KeysUnknown = true;
            }

            foreach (string pattern in settings.AuthorizedKeysFiles.Value)
            {
                if (pattern.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = ExpandKeyPath(pattern, account);
                if (path == null)
                {
                    continue;
                }

                if (!reader.Exists(path))
                {
                    continue;
                }

                string text = reader.ReadText(path);
                if (text == null)
                {
                    account.KeysUnknown = true;
                    continue;
                }

                foreach (string line in SystemReader.SplitLines(text))
                {
                    if (IsKeyLine(line))
                    {
                        account.KeyCount++;
                    }
                }
            }
        }

        internal static string ExpandKeyPath(string pattern, Account account)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '%' && i + 1 < pattern.Length)
                {
                    char t = pattern[++i];
                    switch (t)
                    {
                        case 'h':
                            sb.Append(account.Home);
                            break;
                        case 'u':
                            sb.Append(account.Name);
                            break;
                        case '%':
                            sb.Append('%');
                            break;
                        default:
                            // %U, %d and others are not evaluated: the file cannot be named reliably.
                            return null;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            string path = sb.ToString();
            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(account.Home))
                {
                    return null;
                }

                path = SystemReader.Combine(account.Home, path);
            }

            return path;
        }

        /// <summary>A line with an optional options prefix, a known key type and a base64 blob. The line is not kept.</summary>
        internal static bool IsKeyLine(string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                return false;
            }

            var tokens = new List<string>();
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            foreach (char c in trimmed)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    sb.Append(c);
                }
                else if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (sb.Length > 0)
                    {
                        tokens.Add(sb.ToString());
                        sb.Clear();
                    }
                }
                else
                {
                    sb.Append(c);
                }

                if (tokens.Count > 2)
                {
                    break;
                }
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
            }

            for (int i = 0; i <= 1 && i + 1 < tokens.Count; i++)
            {
                if (IsKeyType(tokens[i]) && IsBase64(tokens[i + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsKeyType(string t)
        {
            return t.StartsWith("ssh-", StringComparison.Ordinal)
                || t.StartsWith("ecdsa-sha2-", StringComparison.Ordinal)
                || t.StartsWith("sk-ssh-", StringComparison.Ordinal)
                || t.StartsWith("sk-ecdsa-", StringComparison.Ordinal);
        }

        private static bool IsBase64(string t)
        {
            if (t.Length < 16)
            {
                return false;
            }

            foreach (char c in t)
            {
                if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '='))
                {
                    return false;
                }
            }

            return true;
        }

        private struct ShadowEntry
        {
            public ShadowEntry(PasswordState state, bool hasExpiry)
            {
                State = state;
                HasExpiry = hasExpiry;
            }

            public PasswordState State { get; }

            public bool HasExpiry { get; }
        }

        private sealed class GroupTable
        {
            public readonly Dictionary<uint, string> ByGid = new Dictionary<uint, string>();
            public readonly Dictionary<string, List<string>> Members = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            public IReadOnlyList<string> GroupsOf(string user, uint gid)
            {
                var result = new List<string>();
                if (ByGid.TryGetValue(gid, out string primary))
                {
                    result.Add(primary);
                }

                if (Members.TryGetValue(user, out var more))
                {
                    foreach (string g in more)
                    {
                        if (!result.Contains(g))
                        {
                            result.Add(g);
                        }
                    }
                }

                return result;
            }
        }
    }
}
