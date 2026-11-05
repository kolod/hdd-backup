using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class AccountDatabaseTests
    {
        private const string Blob = "AAAAC3NzaC1lZDI1NTE5AAAAIPLANTEDKEYBLOBxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx";

        private static SshSettings Defaults() => SshdConfig.Parse(new LinuxRootBuilder().Reader());

        private static AccountDatabase Read(LinuxRootBuilder b, SshSettings s = null) => AccountDatabase.Read(b.Reader(), s ?? Defaults());

        [Theory]
        [InlineData("$6$PLANTEDSECRET$abcdef", PasswordState.Set)]
        [InlineData("!", PasswordState.Locked)]
        [InlineData("!!", PasswordState.Locked)]
        [InlineData("!$6$PLANTEDSECRET$abcdef", PasswordState.Locked)]
        [InlineData("*", PasswordState.NoPassword)]
        [InlineData("", PasswordState.Empty)]
        [InlineData("$y$j9T$PLANTED$hash", PasswordState.Set)]
        public void EveryShadowFieldMapsToAState(string field, PasswordState expected)
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .Shadow("pi:" + field + ":19000:0:99999:7:::");

            var a = Read(b).Accounts.Single();

            Assert.Equal(expected, a.Password);
        }

        [Fact]
        public void NoShadowEntryOrNoShadowFileIsUnknown()
        {
            var noEntry = new LinuxRootBuilder().Passwd("pi:x:1000:1000::/home/pi:/bin/bash").Shadow("other:!:1:::::");
            var noFile = new LinuxRootBuilder().Passwd("pi:x:1000:1000::/home/pi:/bin/bash");

            Assert.Equal(PasswordState.Unknown, Read(noEntry).Accounts.Single().Password);
            var db = Read(noFile);
            Assert.Equal(PasswordState.Unknown, db.Accounts.Single().Password);
            Assert.False(db.ShadowReadable);
        }

        [Fact]
        public void AnOldStylePasswdFieldIsUsedWithoutShadow()
        {
            var b = new LinuxRootBuilder().Passwd("pi:$1$PLANTED$x:1000:1000::/home/pi:/bin/bash", "bob:*:1001:1001::/home/bob:/bin/bash");

            var accounts = Read(b).Accounts;

            Assert.Equal(PasswordState.Set, accounts[0].Password);
            Assert.Equal(PasswordState.NoPassword, accounts[1].Password);
        }

        [Fact]
        public void ShellsAndSystemAccounts()
        {
            var b = new LinuxRootBuilder().Passwd(
                "root:x:0:0:root:/root:/bin/bash",
                "daemon:x:1:1::/usr/sbin:/usr/sbin/nologin",
                "sync:x:4:65534::/bin:/bin/sync",
                "pi:x:1000:1000::/home/pi:/bin/bash",
                "guest:x:1001:1001::/home/guest:/bin/false",
                "nobody:x:65534:65534::/nonexistent:/usr/sbin/nologin");

            var a = Read(b).Accounts.ToDictionary(x => x.Name);

            Assert.True(a["root"].HasUsableShell);
            Assert.False(a["root"].IsSystemAccount);
            Assert.False(a["daemon"].HasUsableShell);
            Assert.True(a["daemon"].IsSystemAccount);
            Assert.True(a["sync"].HasUsableShell);
            Assert.True(a["sync"].IsSystemAccount);
            Assert.False(a["pi"].IsSystemAccount);
            Assert.False(a["guest"].HasUsableShell);
            Assert.True(a["nobody"].IsSystemAccount);
        }

        [Fact]
        public void KeysAreCountedFromTheHomeFile()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .File("/home/pi/.ssh/authorized_keys",
                    "# comment\n\n"
                    + "ssh-ed25519 " + Blob + " pi@laptop\n"
                    + "command=\"/bin/echo hi there\",no-pty ssh-rsa " + Blob + " old\n"
                    + "not a key at all\n"
                    + "ssh-ed25519 short\n");

            var a = Read(b).Accounts.Single();

            Assert.Equal(2, a.KeyCount);
            Assert.False(a.KeysUnknown);
        }

        [Fact]
        public void CustomAuthorizedKeysFileWithTokens()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash", "bob:x:1001:1001::/home/bob:/bin/bash")
                .File("/etc/ssh/keys/pi", "ssh-ed25519 " + Blob + "\n")
                .File("/home/bob/.keys", "ecdsa-sha2-nistp256 " + Blob + "\nsk-ssh-ed25519@openssh.com " + Blob + "\n")
                .File("/etc/ssh/sshd_config", "AuthorizedKeysFile /etc/ssh/keys/%u %h/.keys\n");
            var settings = SshdConfig.Parse(b.Reader());

            var a = AccountDatabase.Read(b.Reader(), settings).Accounts.ToDictionary(x => x.Name);

            Assert.Equal(1, a["pi"].KeyCount);
            Assert.Equal(2, a["bob"].KeyCount);
        }

        [Fact]
        public void KeysOfAccountsWithoutAShellAreNotRead()
        {
            var b = new LinuxRootBuilder()
                .Passwd("svc:x:900:900::/srv/svc:/usr/sbin/nologin")
                .File("/srv/svc/.ssh/authorized_keys", "ssh-ed25519 " + Blob + "\n");

            Assert.Equal(0, Read(b).Accounts.Single().KeyCount);
        }

        [Fact]
        public void AKeyCommandMakesKeysUnknown()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .File("/etc/ssh/sshd_config", "AuthorizedKeysCommand /usr/bin/keys\n");

            Assert.True(AccountDatabase.Read(b.Reader(), SshdConfig.Parse(b.Reader())).Accounts.Single().KeysUnknown);
        }

        [Fact]
        public void AnUnreadableKeyFileMakesKeysUnknown()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .File("/home/pi/.ssh/authorized_keys", "ssh-ed25519 " + Blob + "\n");
            b.Tree.OnRead = f =>
            {
                if (f.Path.EndsWith("authorized_keys"))
                {
                    throw new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.CorruptImage, "bad");
                }
            };

            var a = Read(b).Accounts.Single();

            Assert.True(a.KeysUnknown);
            Assert.Equal(0, a.KeyCount);
        }

        [Fact]
        public void GroupsIncludeThePrimaryGroupAndMemberships()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .Group("root:x:0:", "pi:x:1000:", "sudo:x:27:pi,bob", "ssh:x:110:bob");

            Assert.Equal(new[] { "pi", "sudo" }, Read(b).Accounts.Single().Groups);
        }

        [Fact]
        public void ExpiryIsNoted()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash", "bob:x:1001:1001::/home/bob:/bin/bash")
                .Shadow("pi:$6$x$y:19000:0:99999:7:::", "bob:$6$x$y:19000:0:99999:7::20000:");

            var a = Read(b).Accounts.ToDictionary(x => x.Name);

            Assert.False(a["pi"].HasExpiry);
            Assert.True(a["bob"].HasExpiry);
        }

        [Fact]
        public void GarbledLinesAreSkipped()
        {
            var b = new LinuxRootBuilder().Passwd("garbage", "", "# comment", "x:y:z", "pi:x:1000:1000::/home/pi:/bin/bash");

            Assert.Equal("pi", Read(b).Accounts.Single().Name);
        }

        [Fact]
        public void NoAccountObjectHoldsAPlantedSecret()
        {
            var b = new LinuxRootBuilder()
                .Passwd("pi:x:1000:1000::/home/pi:/bin/bash")
                .Shadow("pi:$6$PLANTEDSECRET$hash:19000:0:99999:7:::")
                .File("/home/pi/.ssh/authorized_keys", "ssh-ed25519 " + Blob + " pi\n");

            var db = Read(b);

            foreach (var a in db.Accounts)
            {
                string dump = ObjectDump.Of(a);
                Assert.DoesNotContain("PLANTED", dump);
                Assert.DoesNotContain("$6$", dump);
            }

            Assert.DoesNotContain("PLANTED", ObjectDump.Of(db));
        }

        [Theory]
        [InlineData("%h/.ssh/k", "/home/pi/.ssh/k")]
        [InlineData(".ssh/authorized_keys", "/home/pi/.ssh/authorized_keys")]
        [InlineData("/etc/keys/%u", "/etc/keys/pi")]
        [InlineData("/etc/%%/k", "/etc/%/k")]
        [InlineData("/etc/%U/k", null)]
        public void KeyPathTokens(string pattern, string expected)
        {
            var a = new Account { Name = "pi", Home = "/home/pi" };

            Assert.Equal(expected, AccountDatabase.ExpandKeyPath(pattern, a));
        }
    }
}
