using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>SC-001: at least a dozen prepared SSH configurations with the expected verdict, users and reasons.</summary>
    public class SshCheckMatrixTests
    {
        private const string Blob = "AAAAC3NzaC1lZDI1NTE5AAAAIPLANTEDKEYBLOBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";
        private const string SshUnit = "[Unit]\nDescription=OpenBSD Secure Shell server\n[Service]\nExecStart=/usr/sbin/sshd -D\n[Install]\nWantedBy=multi-user.target\nAlias=sshd.service\n";

        public sealed class Case
        {
            public string Name = string.Empty;
            public string Config = string.Empty;
            public string PiShadow = "$6$PLANTEDSECRET$y";
            public bool PiKey;
            public bool RootKey;
            public string PiShell = "/bin/bash";
            public string Groups = "sudo:x:27:pi";
            public bool Shadow = true;
            public bool Server = true;
            public string State = "enabled"; // enabled, disabled, masked, socket, legacy, alias
            public string Os = "debian";
            public bool HasFat;
            public bool NotLinux;
            public string[] ExtraPasswd = new string[0];
            public Action<LinuxRootBuilder> Tweak;
            public VerdictCode Verdict;
            public string Users = string.Empty;       // "pi:Both,root:Key"
            public LoginReason[] Reasons = new LoginReason[0];
            public FindingCode[] Notes = new FindingCode[0];

            public override string ToString() => Name;
        }

        public static IEnumerable<object[]> Cases()
        {
            foreach (var c in All())
            {
                yield return new object[] { c };
            }
        }

        internal static IEnumerable<Case> All()
        {
            yield return new Case { Name = "01 enabled, password and key", PiKey = true, Verdict = VerdictCode.Works, Users = "pi:Both" };
            yield return new Case { Name = "02 keys only, with a key", Config = "PasswordAuthentication no\nKbdInteractiveAuthentication no\n", PiKey = true, Verdict = VerdictCode.Works, Users = "pi:Key" };
            yield return new Case { Name = "03 keys only, no key anywhere", Config = "PasswordAuthentication no\nKbdInteractiveAuthentication no\n", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.PasswordAuthOffNoKey } };
            yield return new Case { Name = "04 server not enabled", State = "disabled", Verdict = VerdictCode.NotEnabled };
            yield return new Case { Name = "05 server masked", State = "masked", Verdict = VerdictCode.NotEnabled };
            yield return new Case { Name = "06 no server", Server = false, Verdict = VerdictCode.NoServer };
            yield return new Case { Name = "07 root login off, root has the only key", Config = "PermitRootLogin no\n", PiShadow = null, RootKey = true, Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.RootNotAllowed } };
            yield return new Case { Name = "08 root with a key, prohibit-password", PiShadow = null, RootKey = true, Verdict = VerdictCode.Works, Users = "root:Key" };
            yield return new Case { Name = "09a locked user, UsePAM no, key present", PiShadow = "!$6$PLANTEDSECRET$y", PiKey = true, Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.LockedRefused } };
            yield return new Case { Name = "09b locked user, UsePAM yes, key present", Config = "UsePAM yes\n", PiShadow = "!$6$PLANTEDSECRET$y", PiKey = true, Verdict = VerdictCode.Works, Users = "pi:Key" };
            yield return new Case { Name = "10a empty password refused", PiShadow = "", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.EmptyPasswordRefused } };
            yield return new Case { Name = "10b empty password allowed", Config = "PermitEmptyPasswords yes\n", PiShadow = "", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case { Name = "11 AllowUsers excludes the only user", Config = "AllowUsers bob\n", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.NotInAllowUsers } };
            yield return new Case { Name = "12 DenyGroups", Config = "DenyGroups sudo\n", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.Denied } };
            yield return new Case
            {
                Name = "13 drop-in wins over a later setting",
                Config = "Include /etc/ssh/sshd_config.d/*.conf\nPasswordAuthentication yes\nKbdInteractiveAuthentication no\n",
                Tweak = b => b.File("/etc/ssh/sshd_config.d/50-x.conf", "PasswordAuthentication no\n"),
                Verdict = VerdictCode.NobodyCanLogIn,
                Reasons = new[] { LoginReason.PasswordAuthOffNoKey }
            };
            yield return new Case
            {
                Name = "14 Match block is only a note",
                Config = "PasswordAuthentication no\nKbdInteractiveAuthentication no\nMatch User bob\n  PasswordAuthentication yes\n",
                PiKey = true,
                Verdict = VerdictCode.Works,
                Users = "pi:Key",
                Notes = new[] { FindingCode.ConditionalSettings }
            };
            yield return new Case { Name = "15 key command, nothing else", Config = "AuthorizedKeysCommand /usr/local/bin/keys\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n", PiShadow = "!x", Verdict = VerdictCode.Undetermined, Reasons = new[] { LoginReason.LockedRefused }, Notes = new[] { FindingCode.KeysFromCommand } };
            yield return new Case { Name = "16 socket activation", State = "socket", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case { Name = "17 Raspberry Pi OS, not enabled, FAT partition", State = "disabled", Os = "raspbian", HasFat = true, Verdict = VerdictCode.NotEnabledBootFlagPossible, Notes = new[] { FindingCode.BootFlagNotRead } };
            yield return new Case { Name = "18a no shadow, no key", Shadow = false, Verdict = VerdictCode.Undetermined, Notes = new[] { FindingCode.ShadowUnreadable } };
            yield return new Case { Name = "18b no shadow, a key", Shadow = false, PiKey = true, Verdict = VerdictCode.Works, Users = "pi:Key", Notes = new[] { FindingCode.ShadowUnreadable } };
            yield return new Case { Name = "19 Raspberry Pi OS with no regular account", Os = "raspbian", PiShadow = null, Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.RootNotAllowed } };
            yield return new Case { Name = "20 not a Linux root", NotLinux = true, Verdict = VerdictCode.NotALinuxRoot };
            yield return new Case { Name = "21a locked password with both methods required", Config = "AuthenticationMethods publickey,password\n", PiKey = false, PiShadow = "!$6$PLANTEDSECRET$y", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.LockedRefused } };
            yield return new Case { Name = "21b methods need both, both present", Config = "AuthenticationMethods publickey,password\n", PiKey = true, Verdict = VerdictCode.Works, Users = "pi:Both" };
            yield return new Case { Name = "21c methods need both, only a key", Config = "AuthenticationMethods publickey,password\nUsePAM yes\n", PiKey = true, PiShadow = "!$6$PLANTEDSECRET$y", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.MethodsNeedBoth } };
            yield return new Case { Name = "22 legacy init scripts", State = "legacy", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case { Name = "23 user without a login shell", PiShell = "/usr/sbin/nologin", Verdict = VerdictCode.NobodyCanLogIn, Reasons = new[] { LoginReason.NoShell } };
            yield return new Case { Name = "24 AllowUsers with a host part", Config = "AllowUsers pi@192.168.1.*\n", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case { Name = "25 enabled through the alias name", State = "alias", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case { Name = "26 AllowUsers pattern and negation", Config = "AllowUsers p? !px\n", Verdict = VerdictCode.Works, Users = "pi:Password" };
            yield return new Case
            {
                Name = "27 reasons come in fix-first order",
                Config = "PasswordAuthentication no\nKbdInteractiveAuthentication no\n",
                ExtraPasswd = new[] { "guest:x:1001:1001::/home/guest:/usr/sbin/nologin" },
                Verdict = VerdictCode.NobodyCanLogIn,
                Reasons = new[] { LoginReason.PasswordAuthOffNoKey, LoginReason.NoShell }
            };
        }

        internal static LinuxRootBuilder Build(Case c)
        {
            var b = new LinuxRootBuilder();
            if (c.NotLinux)
            {
                return b.File("/data/readme.txt", "no system here");
            }

            b.BasicLinux(c.Os, c.Os == "raspbian" ? "Raspberry Pi OS" : null, passwd: false);
            if (c.State == "legacy")
            {
                if (c.Server)
                {
                    b.Executable("/etc/init.d/ssh").Link("/etc/rc2.d/S01ssh", "../init.d/ssh");
                }
            }
            else
            {
                b.Systemd();
                if (c.Server)
                {
                    b.Unit("ssh.service", SshUnit).Unit("ssh.socket", "[Socket]\nListenStream=22\n");
                }
            }

            if (c.Server)
            {
                b.Executable("/usr/sbin/sshd");
            }

            switch (c.Server ? c.State : "none")
            {
                case "enabled":
                    b.Enable("ssh.service");
                    break;
                case "masked":
                    b.Link("/etc/systemd/system/ssh.service", "/dev/null");
                    break;
                case "socket":
                    b.Enable("ssh.socket", "sockets.target");
                    break;
                case "alias":
                    b.Enable("ssh.service").Link("/etc/systemd/system/sshd.service", "/lib/systemd/system/ssh.service");
                    break;
            }

            b.File("/etc/ssh/sshd_config", c.Config);
            var passwd = new List<string> { "root:x:0:0:root:/root:/bin/bash" };
            var shadow = new List<string> { "root:*:19000:0:99999:7:::" };
            if (c.PiShadow != null)
            {
                passwd.Add("pi:x:1000:1000:Pi:/home/pi:" + c.PiShell);
                shadow.Add("pi:" + c.PiShadow + ":19000:0:99999:7:::");
            }

            passwd.AddRange(c.ExtraPasswd);
            b.Passwd(passwd.ToArray());
            if (c.Shadow)
            {
                b.Shadow(shadow.ToArray());
            }

            b.Group("root:x:0:", "pi:x:1000:", c.Groups);
            if (c.PiKey)
            {
                b.File("/home/pi/.ssh/authorized_keys", "ssh-ed25519 " + Blob + " pi@laptop\n");
            }

            if (c.RootKey)
            {
                b.File("/root/.ssh/authorized_keys", "ssh-ed25519 " + Blob + " admin\n");
            }

            c.Tweak?.Invoke(b);
            return b;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void VerdictUsersReasonsAndNotes(Case c)
        {
            var b = Build(c);
            var source = new DiagnosticsSource { DisplayName = "test", HasUnreadBootPartition = c.HasFat };

            var report = SshCheck.Run(b.Reader(), source, System.Threading.CancellationToken.None);

            Assert.Equal(c.Verdict, report.Verdict.Code);
            string users = string.Join(",", report.Verdict.Users.Select(u => u.Account + ":" + u.Method));
            Assert.Equal(c.Users, users);
            if (c.Verdict == VerdictCode.NobodyCanLogIn || c.Verdict == VerdictCode.Undetermined)
            {
                Assert.Equal(c.Reasons, report.Verdict.Reasons.Select(r => r.Reason).ToArray());
            }

            foreach (var note in c.Notes)
            {
                Assert.Contains(report.Verdict.Notes, n => n.Code == note);
            }
        }

        [Fact]
        public void KeysInstalledForUsersAreReportedPerAccount()
        {
            var withKey = SshCheck.Run(Build(new Case { PiKey = true }).Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);
            var noKey = SshCheck.Run(Build(new Case { Config = "PasswordAuthentication no\nKbdInteractiveAuthentication no\n" }).Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);
            var viaCommand = SshCheck.Run(Build(new Case { Config = "AuthorizedKeysCommand /bin/keys\n" }).Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            var ok = withKey.Findings.Single(f => f.Code == FindingCode.AccountKeys);
            Assert.Equal(new[] { "pi", "1" }, ok.Args);
            Assert.Equal(FindingStatus.Ok, ok.Status);
            var none = noKey.Findings.Single(f => f.Code == FindingCode.AccountNoKeys && f.Args[0] == "pi");
            Assert.Equal("pi", none.Args[0]);
            Assert.Equal(FindingStatus.Problem, none.Status);
            Assert.Contains(withKey.Findings, f => f.Code == FindingCode.AccountNoKeys && f.Args[0] == "root");
            Assert.Contains(viaCommand.Findings, f => f.Code == FindingCode.AccountKeysUnknown && f.Args[0] == "pi");
        }

        [Fact]
        public void TheMatrixHasAtLeastAScoreOfConfigurations()
        {
            Assert.True(All().Count() >= 12);
        }

        [Fact]
        public void ServerFindingsNameTheUnitAndTheEvidence()
        {
            var b = Build(new Case { PiKey = true });

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            var enabled = report.Findings.Single(f => f.Code == FindingCode.ServerEnabled);
            Assert.Equal("ssh.service", enabled.Args[0]);
            Assert.Contains(enabled.Evidence, e => e.Path == "/etc/systemd/system/multi-user.target.wants/ssh.service");
            Assert.Contains(report.Findings, f => f.Code == FindingCode.ServerInstalled);
            Assert.Equal(1, report.Findings.Single(f => f.Code == FindingCode.ServerEnabled).Page);
        }

        [Fact]
        public void SettingsFindingsSayWhereValuesComeFrom()
        {
            var b = Build(new Case { Config = "Port 2222\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n", PiKey = true });

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            var port = report.Findings.Single(f => f.Code == FindingCode.Port);
            Assert.Equal("2222", port.Args[0]);
            Assert.Equal(1, port.Evidence[0].Line);
            Assert.Equal("/etc/ssh/sshd_config", port.Evidence[0].Path);
            Assert.Contains(report.Findings, f => f.Code == FindingCode.PasswordAuthOff);
            Assert.Contains(report.Findings, f => f.Code == FindingCode.KeyAuthOn);
            var rootLogin = report.Findings.Single(f => f.Code == FindingCode.RootLogin);
            Assert.True(rootLogin.Evidence[0].IsDefault);
            Assert.Contains(report.Findings, f => f.Code == FindingCode.SettingDefault && f.Args[0] == "PermitRootLogin");
        }

        [Fact]
        public void AccountsListRootRegularAndShellAccountsButHideNoLoginSystemAccounts()
        {
            var b = Build(new Case
            {
                ExtraPasswd = new[] { "daemon:x:1:1::/usr/sbin:/usr/sbin/nologin", "sync:x:4:65534::/bin:/bin/sync" }
            });

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            Assert.Equal(new[] { "root", "pi", "sync" }, report.Accounts.Select(a => a.Name));
            Assert.True(report.Accounts.Single(a => a.Name == "sync").IsSystemAccount);
        }

        [Fact]
        public void ARaspberryPiRootWithOnlyRootGetsTheNoRegularAccountFinding()
        {
            var b = Build(new Case { Os = "raspbian", PiShadow = null });

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            Assert.Contains(report.Findings, f => f.Code == FindingCode.NoRegularAccount);
        }

        [Fact]
        public void ANonLinuxRootHasNoFindings()
        {
            var report = SshCheck.Run(Build(new Case { NotLinux = true }).Reader(), new DiagnosticsSource(), System.Threading.CancellationToken.None);

            Assert.Empty(report.Findings);
            Assert.Empty(report.Accounts);
        }
    }
}
