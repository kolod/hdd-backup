using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace HddBackup.Core.Diagnostics
{
    /// <summary>
    /// Is SSH going to work on the opened system? Reads the server, its configuration and the
    /// accounts, and ends in a verdict with reasons (research R-4 to R-7). Read-only; the result
    /// holds codes, names and paths, never a secret.
    /// </summary>
    public static class SshCheck
    {
        private static readonly string[] RelevantConditional =
        {
            "passwordauthentication", "kbdinteractiveauthentication", "usepam", "pubkeyauthentication", "permitrootlogin",
            "allowusers", "denyusers", "allowgroups", "denygroups", "authenticationmethods", "permitemptypasswords",
            "authorizedkeysfile", "authorizedkeyscommand"
        };

        /// <summary>Reasons in the order a person would fix them: server settings first, then the account.</summary>
        private static readonly LoginReason[] ReasonOrder =
        {
            LoginReason.NotInAllowUsers, LoginReason.Denied, LoginReason.RootNotAllowed, LoginReason.LockedRefused,
            LoginReason.PasswordAuthOffNoKey, LoginReason.KeyAuthOffNoPassword, LoginReason.MethodsNeedBoth,
            LoginReason.EmptyPasswordRefused, LoginReason.PasswordLocked, LoginReason.NoPasswordNoKey, LoginReason.NoShell
        };

        public static SshReport Run(SystemReader reader, DiagnosticsSource source, CancellationToken cancellationToken)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var report = new SshReport(source) { ReadNotes = reader.Notes };
            var root = LinuxRoot.Detect(reader);
            report.System = root;
            if (!root.IsLinux)
            {
                report.Verdict = new Verdict(VerdictCode.NotALinuxRoot, null, null, null);
                return report;
            }

            var scan = new SystemdScan(reader);
            var settings = SshdConfig.Parse(reader);
            cancellationToken.ThrowIfCancellationRequested();
            var db = AccountDatabase.Read(reader, settings);
            cancellationToken.ThrowIfCancellationRequested();
            var server = DetectServer(reader, scan);
            report.Server = server;
            report.Settings = settings;

            var shown = db.Accounts.Where(a => a.Name == "root" || !a.IsSystemAccount || a.HasUsableShell).ToList();
            foreach (var a in shown)
            {
                Evaluate(a, settings);
            }

            report.Accounts = shown;
            var findings = new List<Finding>();
            var notes = new List<Finding>();
            ServerFindings(findings, server);

            bool notEnabled = server.Installed && (server.Enabled == ServerEnabledState.No || server.Enabled == ServerEnabledState.Masked);
            bool bootFlag = server.Installed
                && server.Enabled == ServerEnabledState.No
                && (server.FirstBootSwitchPresent || (root.IsRaspberryPiOs && source != null && source.HasUnreadBootPartition));
            if (bootFlag)
            {
                var f = new Finding(FindingCode.BootFlagNotRead, FindingStatus.Unknown, 1);
                findings.Add(f);
                notes.Add(f);
            }

            SettingsFindings(findings, notes, settings, db);
            AccountFindings(findings, notes, shown, db, root);

            var verdict = Decide(server, notEnabled, bootFlag, shown, settings, db, notes);
            report.Findings = findings;
            report.Verdict = verdict;
            return report;
        }

        private static ServerState DetectServer(SystemReader reader, SystemdScan scan)
        {
            var s = new ServerState();
            string binary = new[] { "/usr/sbin/sshd", "/sbin/sshd", "/usr/bin/sshd" }.FirstOrDefault(reader.Exists);
            var ssh = scan.FindUnit("ssh.service");
            var sshd = scan.FindUnit("sshd.service");
            var evidence = new List<string>();
            if (binary != null)
            {
                evidence.Add(reader.Resolve(binary) ?? binary);
            }

            foreach (var u in new[] { ssh, sshd })
            {
                if ((u.Exists || u.Masked) && u.Path != null && !evidence.Contains(u.Path))
                {
                    evidence.Add(u.Path);
                }
            }

            s.Installed = binary != null || ssh.Exists || sshd.Exists || ssh.Masked || sshd.Masked;
            s.FirstBootSwitchPresent = scan.FindUnit("sshswitch.service").Exists;
            s.InstalledEvidence = evidence.ToList();
            s.Evidence = evidence;
            if (!s.Installed)
            {
                return s;
            }

            UnitState enabled = null;
            UnitState masked = null;
            foreach (string name in new[] { "ssh.service", "sshd.service" })
            {
                var st = scan.EnabledState(name);
                if (st.Enabled == UnitEnabled.Yes && enabled == null)
                {
                    enabled = st;
                }
                else if (st.Enabled == UnitEnabled.Masked && masked == null)
                {
                    masked = st;
                }
            }

            if (enabled != null)
            {
                s.Enabled = ServerEnabledState.Yes;
                s.Unit = enabled.Unit;
                s.Evidence = enabled.Evidence;
                return s;
            }

            if (masked != null)
            {
                s.Enabled = ServerEnabledState.Masked;
                s.Unit = masked.Unit;
                s.Evidence = masked.Evidence;
                return s;
            }

            foreach (string name in new[] { "ssh.socket", "sshd.socket" })
            {
                var st = scan.EnabledState(name);
                if (st.Enabled == UnitEnabled.Yes)
                {
                    s.Enabled = ServerEnabledState.OnDemand;
                    s.Unit = st.Unit;
                    s.Evidence = st.Evidence;
                    return s;
                }
            }

            if (!scan.HasSystemd || (!ssh.Exists && !sshd.Exists))
            {
                var link = scan.SysVStartLinks().FirstOrDefault(kv => kv.Key == "ssh" || kv.Key == "sshd");
                if (link.Value != null)
                {
                    s.Enabled = ServerEnabledState.Legacy;
                    s.Unit = link.Key;
                    s.Evidence = new[] { link.Value };
                    return s;
                }
            }

            s.Enabled = ServerEnabledState.No;
            s.Unit = ssh.Exists ? "ssh.service" : sshd.Exists ? "sshd.service" : string.Empty;
            return s;
        }

        private static void ServerFindings(List<Finding> findings, ServerState server)
        {
            if (!server.Installed)
            {
                findings.Add(new Finding(FindingCode.ServerMissing, FindingStatus.Problem, 1));
                return;
            }

            var evidence = server.Evidence.Select(p => new EvidenceRef(p)).ToList();
            string first = server.InstalledEvidence.Count > 0 ? server.InstalledEvidence[0] : string.Empty;
            findings.Add(new Finding(FindingCode.ServerInstalled, FindingStatus.Ok, 1, new[] { first }, server.InstalledEvidence.Select(p => new EvidenceRef(p)).ToList()));
            switch (server.Enabled)
            {
                case ServerEnabledState.Yes:
                    findings.Add(new Finding(FindingCode.ServerEnabled, FindingStatus.Ok, 1, new[] { server.Unit }, evidence));
                    break;
                case ServerEnabledState.OnDemand:
                    findings.Add(new Finding(FindingCode.ServerOnDemand, FindingStatus.Ok, 1, new[] { server.Unit }, evidence));
                    break;
                case ServerEnabledState.Legacy:
                    findings.Add(new Finding(FindingCode.ServerLegacy, FindingStatus.Info, 1, new[] { server.Unit }, evidence));
                    break;
                case ServerEnabledState.Masked:
                    findings.Add(new Finding(FindingCode.ServerMasked, FindingStatus.Problem, 1, new[] { server.Unit }, evidence));
                    break;
                default:
                    findings.Add(new Finding(FindingCode.ServerNotEnabled, FindingStatus.Problem, 1, new[] { server.Unit }, evidence));
                    break;
            }
        }

        private static void SettingsFindings(List<Finding> findings, List<Finding> notes, SshSettings s, AccountDatabase db)
        {
            if (!s.ConfigFound)
            {
                var missing = new Finding(FindingCode.ConfigMissing, FindingStatus.Unknown, 2, null, new[] { new EvidenceRef(SshdConfig.MainFile) });
                findings.Add(missing);
                notes.Add(missing);
            }

            foreach (var p in s.Ports)
            {
                findings.Add(new Finding(FindingCode.Port, FindingStatus.Info, 2, new[] { p.Value.ToString(CultureInfo.InvariantCulture) }, new[] { EvidenceRef.From(p) }));
            }

            findings.Add(new Finding(FindingCode.RootLogin, FindingStatus.Info, 2, new[] { s.PermitRootLogin.Value.ToString() }, new[] { EvidenceRef.From(s.PermitRootLogin) }));
            bool password = s.PasswordLoginPossible;
            findings.Add(new Finding(
                password ? FindingCode.PasswordAuthOn : FindingCode.PasswordAuthOff,
                password ? FindingStatus.Ok : FindingStatus.Problem,
                2,
                null,
                new[] { EvidenceRef.From(s.PasswordAuthentication) }));
            bool key = s.PubkeyAuthentication.Value;
            findings.Add(new Finding(
                key ? FindingCode.KeyAuthOn : FindingCode.KeyAuthOff,
                key ? FindingStatus.Ok : FindingStatus.Problem,
                2,
                null,
                new[] { EvidenceRef.From(s.PubkeyAuthentication) }));

            if (!s.PermitEmptyPasswords.IsDefault || db.Accounts.Any(a => a.Password == PasswordState.Empty && a.HasUsableShell))
            {
                findings.Add(new Finding(FindingCode.EmptyPasswords, FindingStatus.Info, 2, new[] { s.PermitEmptyPasswords.Value ? "allowed" : "refused" }, new[] { EvidenceRef.From(s.PermitEmptyPasswords) }));
            }

            foreach (var pair in new[]
            {
                new KeyValuePair<string, SettingValue<IReadOnlyList<string>>>("AllowUsers", s.AllowUsers),
                new KeyValuePair<string, SettingValue<IReadOnlyList<string>>>("DenyUsers", s.DenyUsers),
                new KeyValuePair<string, SettingValue<IReadOnlyList<string>>>("AllowGroups", s.AllowGroups),
                new KeyValuePair<string, SettingValue<IReadOnlyList<string>>>("DenyGroups", s.DenyGroups)
            })
            {
                if (pair.Value.Value.Count > 0)
                {
                    findings.Add(new Finding(FindingCode.UserRestriction, FindingStatus.Info, 2, new[] { pair.Key, string.Join(" ", pair.Value.Value) }, new[] { EvidenceRef.From(pair.Value) }));
                }
            }

            foreach (var d in new[]
            {
                new KeyValuePair<string, bool>("PermitRootLogin", s.PermitRootLogin.IsDefault),
                new KeyValuePair<string, bool>("PasswordAuthentication", s.PasswordAuthentication.IsDefault),
                new KeyValuePair<string, bool>("PubkeyAuthentication", s.PubkeyAuthentication.IsDefault)
            })
            {
                if (d.Value && s.ConfigFound)
                {
                    findings.Add(new Finding(FindingCode.SettingDefault, FindingStatus.Info, 2, new[] { d.Key }, new[] { EvidenceRef.Default }));
                }
            }

            var conditional = s.ConditionalKeywords.Where(k => RelevantConditional.Contains(k)).ToList();
            if (conditional.Count > 0)
            {
                var f = new Finding(FindingCode.ConditionalSettings, FindingStatus.Unknown, 2, new[] { string.Join(", ", conditional) });
                findings.Add(f);
                notes.Add(f);
            }

            if (s.HasAuthorizedKeysCommand)
            {
                var f = new Finding(FindingCode.KeysFromCommand, FindingStatus.Unknown, 2);
                findings.Add(f);
                notes.Add(f);
            }
        }

        private static void AccountFindings(List<Finding> findings, List<Finding> notes, List<Account> shown, AccountDatabase db, LinuxRoot root)
        {
            foreach (var a in shown.Where(x => !x.IsSystemAccount))
            {
                if (a.CanLogIn == LoginAnswer.Yes)
                {
                    findings.Add(new Finding(FindingCode.AccountCanLogIn, FindingStatus.Ok, 3, new[] { a.Name, a.Method.ToString() }));
                }
                else if (a.CanLogIn == LoginAnswer.No)
                {
                    findings.Add(new Finding(FindingCode.AccountCannot, FindingStatus.Problem, 3, new[] { a.Name }, null, a.Reason));
                }
            }

            if (root.IsRaspberryPiOs && !shown.Any(a => a.Name != "root" && !a.IsSystemAccount))
            {
                findings.Add(new Finding(FindingCode.NoRegularAccount, FindingStatus.Info, 3));
            }

            if (!db.ShadowReadable)
            {
                var f = new Finding(FindingCode.ShadowUnreadable, FindingStatus.Unknown, 3);
                findings.Add(f);
                notes.Add(f);
            }
        }

        private static Verdict Decide(
            ServerState server,
            bool notEnabled,
            bool bootFlag,
            List<Account> shown,
            SshSettings settings,
            AccountDatabase db,
            List<Finding> notes)
        {
            if (!server.Installed)
            {
                // Nothing about logins matters when there is no server.
                return new Verdict(VerdictCode.NoServer, null, new[] { new Finding(FindingCode.ServerMissing, FindingStatus.Problem, 1) }, null);
            }

            if (notEnabled)
            {
                var reason = new Finding(
                    server.Enabled == ServerEnabledState.Masked ? FindingCode.ServerMasked : FindingCode.ServerNotEnabled,
                    FindingStatus.Problem,
                    1,
                    new[] { server.Unit },
                    server.Evidence.Select(p => new EvidenceRef(p)).ToList());
                // Only the unchecked boot-partition switch matters when the server is not enabled.
                var relevant = notes.Where(n => n.Code == FindingCode.BootFlagNotRead).ToList();
                return new Verdict(bootFlag ? VerdictCode.NotEnabledBootFlagPossible : VerdictCode.NotEnabled, null, new[] { reason }, relevant);
            }

            var users = shown.Where(a => a.CanLogIn == LoginAnswer.Yes).Select(a => new UserLogin(a.Name, a.Method)).ToList();
            if (users.Count > 0)
            {
                return new Verdict(VerdictCode.Works, users, null, notes);
            }

            bool uncertain = shown.Any(a => a.CanLogIn == LoginAnswer.Unknown)
                || settings.ConditionalKeywords.Any(k => RelevantConditional.Contains(k))
                || settings.HasAuthorizedKeysCommand
                || !db.ShadowReadable;
            // The administrator account is only worth a reason when it is the only account there is.
            bool hasRegular = shown.Any(a => !a.IsSystemAccount && a.Name != "root");
            var reasons = shown
                .Where(a => a.CanLogIn == LoginAnswer.No && a.Reason != LoginReason.None && !a.IsSystemAccount && (a.Name != "root" || !hasRegular))
                .Select(a => new Finding(FindingCode.AccountCannot, FindingStatus.Problem, 4, new[] { a.Name }, null, a.Reason))
                .OrderBy(f => Array.IndexOf(ReasonOrder, f.Reason))
                .ToList();
            return new Verdict(uncertain ? VerdictCode.Undetermined : VerdictCode.NobodyCanLogIn, null, reasons, notes);
        }

        // --- the login rules of sshd ---------------------------------------------------------

        private static void Evaluate(Account a, SshSettings s)
        {
            a.CanLogIn = LoginAnswer.No;
            a.Method = LoginMethod.None;
            a.Reason = LoginReason.None;
            a.CanLogInWithPassword = LoginAnswer.No;
            a.CanLogInWithKey = LoginAnswer.No;
            a.PasswordReason = LoginReason.None;
            a.KeyReason = LoginReason.None;

            if (!a.HasUsableShell)
            {
                a.Reason = LoginReason.NoShell;
                a.PasswordReason = a.KeyReason = LoginReason.NoShell;
                return;
            }

            var access = AccessRules(a, s);
            if (access != LoginReason.None)
            {
                a.Reason = a.PasswordReason = a.KeyReason = access;
                return;
            }

            if (a.Password == PasswordState.Locked && !s.UsePam.Value)
            {
                a.Reason = a.PasswordReason = a.KeyReason = LoginReason.LockedRefused;
                return;
            }

            bool root = a.Uid == 0;
            if (root && s.PermitRootLogin.Value == RootLoginMode.No)
            {
                a.Reason = a.PasswordReason = a.KeyReason = LoginReason.RootNotAllowed;
                return;
            }

            if (root && s.PermitRootLogin.Value == RootLoginMode.ForcedCommandsOnly)
            {
                a.Reason = a.PasswordReason = a.KeyReason = LoginReason.RootNotAllowed;
                return;
            }

            var pw = LoginAnswer.No;
            var pwReason = LoginReason.NoPasswordNoKey;
            if (root && s.PermitRootLogin.Value != RootLoginMode.Yes)
            {
                pwReason = LoginReason.RootNotAllowed;
            }
            else if (!s.PasswordLoginPossible)
            {
                pwReason = LoginReason.PasswordAuthOffNoKey;
            }
            else
            {
                switch (a.Password)
                {
                    case PasswordState.Set:
                        pw = LoginAnswer.Yes;
                        break;
                    case PasswordState.Empty:
                        if (s.PermitEmptyPasswords.Value)
                        {
                            pw = LoginAnswer.Yes;
                        }
                        else
                        {
                            pwReason = LoginReason.EmptyPasswordRefused;
                        }

                        break;
                    case PasswordState.Locked:
                        pwReason = LoginReason.PasswordLocked;
                        break;
                    case PasswordState.NoPassword:
                        pwReason = LoginReason.NoPasswordNoKey;
                        break;
                    default:
                        pw = LoginAnswer.Unknown;
                        break;
                }
            }

            var key = LoginAnswer.No;
            var keyReason = LoginReason.NoPasswordNoKey;
            if (!s.PubkeyAuthentication.Value)
            {
                keyReason = LoginReason.KeyAuthOffNoPassword;
            }
            else if (a.KeyCount > 0)
            {
                key = LoginAnswer.Yes;
            }
            else if (a.KeysUnknown)
            {
                key = LoginAnswer.Unknown;
            }

            a.CanLogInWithPassword = pw;
            a.CanLogInWithKey = key;
            a.PasswordReason = pwReason;
            a.KeyReason = keyReason;

            Combine(a, s, pw, pwReason, key, keyReason);
        }

        private static void Combine(Account a, SshSettings s, LoginAnswer pw, LoginReason pwReason, LoginAnswer key, LoginReason keyReason)
        {
            var alternatives = s.AuthenticationMethods;
            if (alternatives.Count == 0)
            {
                if (pw == LoginAnswer.Yes || key == LoginAnswer.Yes)
                {
                    a.CanLogIn = LoginAnswer.Yes;
                    a.Method = pw == LoginAnswer.Yes && key == LoginAnswer.Yes ? LoginMethod.Both : pw == LoginAnswer.Yes ? LoginMethod.Password : LoginMethod.Key;
                    return;
                }

                if (pw == LoginAnswer.Unknown || key == LoginAnswer.Unknown)
                {
                    a.CanLogIn = LoginAnswer.Unknown;
                    return;
                }

                a.CanLogIn = LoginAnswer.No;
                a.Reason = NoReason(s, a, pwReason, keyReason);
                return;
            }

            LoginAnswer best = LoginAnswer.No;
            LoginMethod method = LoginMethod.None;
            bool someMultiNearlyThere = false;
            foreach (var alt in alternatives)
            {
                var answers = alt.Select(m => MethodAnswer(m, pw, key)).ToList();
                bool hasKey = alt.Contains("publickey");
                bool hasPassword = alt.Any(m => m == "password" || m.StartsWith("keyboard-interactive", StringComparison.Ordinal));
                var m2 = hasKey && hasPassword ? LoginMethod.Both : hasKey ? LoginMethod.Key : LoginMethod.Password;
                if (answers.All(x => x == LoginAnswer.Yes))
                {
                    best = LoginAnswer.Yes;
                    method = m2;
                    break;
                }

                if (!answers.Any(x => x == LoginAnswer.No))
                {
                    best = LoginAnswer.Unknown;
                }
                else if (alt.Count > 1 && answers.Any(x => x == LoginAnswer.Yes))
                {
                    someMultiNearlyThere = true;
                }
            }

            a.CanLogIn = best;
            a.Method = method;
            if (best == LoginAnswer.No)
            {
                a.Reason = someMultiNearlyThere ? LoginReason.MethodsNeedBoth : NoReason(s, a, pwReason, keyReason);
            }
        }

        private static LoginAnswer MethodAnswer(string method, LoginAnswer pw, LoginAnswer key)
        {
            if (method == "publickey")
            {
                return key;
            }

            if (method == "password" || method.StartsWith("keyboard-interactive", StringComparison.Ordinal))
            {
                return pw;
            }

            return LoginAnswer.No;
        }

        private static LoginReason NoReason(SshSettings s, Account a, LoginReason pwReason, LoginReason keyReason)
        {
            bool serverPassword = s.PasswordLoginPossible;
            bool serverKey = s.PubkeyAuthentication.Value;
            if (a.Uid == 0 && s.PermitRootLogin.Value == RootLoginMode.ProhibitPassword && serverKey && a.KeyCount == 0)
            {
                return LoginReason.RootNotAllowed;
            }

            if (!serverPassword)
            {
                return LoginReason.PasswordAuthOffNoKey;
            }

            if (!serverKey)
            {
                return LoginReason.KeyAuthOffNoPassword;
            }

            return pwReason == LoginReason.RootNotAllowed ? LoginReason.RootNotAllowed : pwReason;
        }

        /// <summary>DenyUsers, AllowUsers, DenyGroups, AllowGroups in the order sshd checks them.</summary>
        private static LoginReason AccessRules(Account a, SshSettings s)
        {
            if (UserListMatch(s.DenyUsers.Value, a.Name) == Match.Yes)
            {
                return LoginReason.Denied;
            }

            if (s.AllowUsers.Value.Count > 0 && UserListMatch(s.AllowUsers.Value, a.Name) == Match.No)
            {
                return LoginReason.NotInAllowUsers;
            }

            if (s.DenyGroups.Value.Count > 0 && a.Groups.Any(g => s.DenyGroups.Value.Any(p => WildcardMatcher.IsMatch(p, g))))
            {
                return LoginReason.Denied;
            }

            if (s.AllowGroups.Value.Count > 0 && !a.Groups.Any(g => s.AllowGroups.Value.Any(p => WildcardMatcher.IsMatch(p, g))))
            {
                return LoginReason.NotInAllowUsers;
            }

            return LoginReason.None;
        }

        private enum Match
        {
            No,
            Yes,
            Maybe
        }

        private static Match UserListMatch(IReadOnlyList<string> patterns, string user)
        {
            bool positive = false;
            bool maybe = false;
            foreach (string p in patterns)
            {
                if (p.StartsWith("!", StringComparison.Ordinal))
                {
                    if (WildcardMatcher.IsMatch(p.Substring(1), user))
                    {
                        return Match.No;
                    }

                    continue;
                }

                int at = p.IndexOf('@');
                if (at > 0)
                {
                    // user@host patterns depend on where the connection comes from.
                    if (WildcardMatcher.IsMatch(p.Substring(0, at), user))
                    {
                        maybe = true;
                    }

                    continue;
                }

                if (WildcardMatcher.IsMatch(p, user))
                {
                    positive = true;
                }
            }

            // A "Maybe" counts as allowed here; the conditional note and the verdict tell the person.
            return positive ? Match.Yes : maybe ? Match.Maybe : Match.No;
        }
    }
}
