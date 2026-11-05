using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class SshdConfigTests
    {
        private static SshSettings Parse(string main, LinuxRootBuilder b = null)
        {
            b = b ?? new LinuxRootBuilder();
            if (main != null)
            {
                b.File("/etc/ssh/sshd_config", main);
            }

            return SshdConfig.Parse(b.Reader());
        }

        [Fact]
        public void WithoutAConfigFileEverythingIsDefault()
        {
            var s = Parse(null);

            Assert.False(s.ConfigFound);
            Assert.Equal(22, s.Ports.Single().Value);
            Assert.True(s.Ports.Single().IsDefault);
            Assert.Equal(RootLoginMode.ProhibitPassword, s.PermitRootLogin.Value);
            Assert.True(s.PasswordAuthentication.Value);
            Assert.True(s.KbdInteractiveAuthentication.Value);
            Assert.False(s.UsePam.Value);
            Assert.True(s.PubkeyAuthentication.Value);
            Assert.False(s.PermitEmptyPasswords.Value);
            Assert.Equal(new[] { ".ssh/authorized_keys", ".ssh/authorized_keys2" }, s.AuthorizedKeysFiles.Value);
            Assert.True(s.PasswordAuthentication.IsDefault);
        }

        [Fact]
        public void TheFirstValueWins()
        {
            var s = Parse("PasswordAuthentication no\nPasswordAuthentication yes\nPermitRootLogin yes\nPermitRootLogin no\n");

            Assert.False(s.PasswordAuthentication.Value);
            Assert.Equal(1, s.PasswordAuthentication.Line);
            Assert.Equal("/etc/ssh/sshd_config", s.PasswordAuthentication.Source);
            Assert.Equal(RootLoginMode.Yes, s.PermitRootLogin.Value);
        }

        [Fact]
        public void IncludeInTheMiddleSplitsBeforeAndAfter()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/ssh/sshd_config.d/50-x.conf", "PasswordAuthentication no\nPermitRootLogin yes\n");
            var s = Parse("PermitRootLogin no\nInclude /etc/ssh/sshd_config.d/*.conf\nPasswordAuthentication yes\n", b);

            // Before the Include: PermitRootLogin no wins. After it: the drop-in's PasswordAuthentication wins.
            Assert.Equal(RootLoginMode.No, s.PermitRootLogin.Value);
            Assert.False(s.PasswordAuthentication.Value);
            Assert.Equal("/etc/ssh/sshd_config.d/50-x.conf", s.PasswordAuthentication.Source);
            Assert.Equal(1, s.PasswordAuthentication.Line);
        }

        [Fact]
        public void IncludeGlobsAreSortedByName()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/ssh/sshd_config.d/20-b.conf", "PasswordAuthentication no\n")
                .File("/etc/ssh/sshd_config.d/10-a.conf", "PasswordAuthentication yes\n")
                .File("/etc/ssh/sshd_config.d/30-c.txt", "PasswordAuthentication no\n");
            var s = Parse("Include sshd_config.d/*.conf\n", b);

            Assert.True(s.PasswordAuthentication.Value);
            Assert.Equal("/etc/ssh/sshd_config.d/10-a.conf", s.PasswordAuthentication.Source);
        }

        [Fact]
        public void RelativeIncludeResolvesAgainstEtcSsh()
        {
            var b = new LinuxRootBuilder().File("/etc/ssh/extra.conf", "Port 2222\n");

            Assert.Equal(2222, Parse("Include extra.conf\n", b).Ports.Single().Value);
        }

        [Fact]
        public void AMissingIncludeTargetIsIgnored()
        {
            var s = Parse("Include /etc/ssh/none/*.conf\nInclude /etc/ssh/absent.conf\nPort 2200\n");

            Assert.Equal(2200, s.Ports.Single().Value);
        }

        [Fact]
        public void IncludeLoopsEndAtTheDepthLimit()
        {
            var b = new LinuxRootBuilder().File("/etc/ssh/loop.conf", "Include /etc/ssh/loop.conf\nPort 2300\n");
            var s = Parse("Include /etc/ssh/loop.conf\n", b);

            Assert.Equal(2300, s.Ports.First().Value);
        }

        [Fact]
        public void MatchBlocksAreConditionalAndNeverApplied()
        {
            var s = Parse("PasswordAuthentication no\nMatch User bob\n  PasswordAuthentication yes\n  PermitRootLogin yes\n  AllowUsers bob\n");

            Assert.False(s.PasswordAuthentication.Value);
            Assert.True(s.PasswordAuthentication.Conditional);
            Assert.Equal(RootLoginMode.ProhibitPassword, s.PermitRootLogin.Value);
            Assert.True(s.PermitRootLogin.Conditional);
            Assert.Empty(s.AllowUsers.Value);
            Assert.Equal(new[] { "allowusers", "passwordauthentication", "permitrootlogin" }, s.ConditionalKeywords);
            Assert.True(s.IsConditional("PasswordAuthentication"));
            Assert.False(s.IsConditional("Port"));
        }

        [Fact]
        public void AMatchBlockInAnIncludeDoesNotLeakIntoTheMainFile()
        {
            var b = new LinuxRootBuilder().File("/etc/ssh/sshd_config.d/m.conf", "Match User x\n  PasswordAuthentication yes\n");
            var s = Parse("Include /etc/ssh/sshd_config.d/*.conf\nPasswordAuthentication no\n", b);

            Assert.False(s.PasswordAuthentication.Value);
            Assert.Equal("/etc/ssh/sshd_config", s.PasswordAuthentication.Source);
        }

        [Fact]
        public void KeywordsAreCaseInsensitiveAndAcceptEqualsAndQuotes()
        {
            var s = Parse("passwordauthentication = no\nPERMITROOTLOGIN=yes\nPort \"2022\"\nAllowUsers \"a b\" c\n");

            Assert.False(s.PasswordAuthentication.Value);
            Assert.Equal(RootLoginMode.Yes, s.PermitRootLogin.Value);
            Assert.Equal(2022, s.Ports.Single().Value);
            Assert.Equal(new[] { "a b", "c" }, s.AllowUsers.Value);
        }

        [Fact]
        public void CommentsStartAtTheLineAndAfterTheArguments()
        {
            var s = Parse("# PasswordAuthentication no\n   # indented comment\nPort 2022 # trailing\n\n");

            Assert.True(s.PasswordAuthentication.IsDefault);
            Assert.Equal(2022, s.Ports.Single().Value);
        }

        [Fact]
        public void PortsAndUserListsAccumulate()
        {
            var s = Parse("Port 22\nPort 2222\nAllowUsers pi\nAllowUsers bob alice\nDenyGroups bad\n");

            Assert.Equal(new[] { 22, 2222 }, s.Ports.Select(p => p.Value));
            Assert.Equal(new[] { "pi", "bob", "alice" }, s.AllowUsers.Value);
            Assert.Equal(new[] { "bad" }, s.DenyGroups.Value);
            Assert.Equal(3, s.AllowUsers.Line);
        }

        [Fact]
        public void AuthorizedKeysFileAndMethodsTakeTheFirstLine()
        {
            var s = Parse("AuthorizedKeysFile .ssh/keys /etc/ssh/keys/%u\nAuthorizedKeysFile other\nAuthenticationMethods publickey,password publickey\nAuthenticationMethods any\n");

            Assert.Equal(new[] { ".ssh/keys", "/etc/ssh/keys/%u" }, s.AuthorizedKeysFiles.Value);
            Assert.Equal(2, s.AuthenticationMethods.Count);
            Assert.Equal(new[] { "publickey", "password" }, s.AuthenticationMethods[0]);
            Assert.Equal(new[] { "publickey" }, s.AuthenticationMethods[1]);
        }

        [Fact]
        public void AuthorizedKeysCommandIsRecordedUnlessNone()
        {
            Assert.True(Parse("AuthorizedKeysCommand /usr/bin/keys %u\n").HasAuthorizedKeysCommand);
            Assert.False(Parse("AuthorizedKeysCommand none\n").HasAuthorizedKeysCommand);
            Assert.False(Parse("Port 22\n").HasAuthorizedKeysCommand);
        }

        [Fact]
        public void TheOldKeyboardInteractiveNameIsAnAlias()
        {
            var s = Parse("ChallengeResponseAuthentication no\nKbdInteractiveAuthentication yes\n");

            Assert.False(s.KbdInteractiveAuthentication.Value);
        }

        [Theory]
        [InlineData("yes", "yes", "yes", true)]
        [InlineData("no", "yes", "yes", true)]
        [InlineData("no", "yes", "no", false)]
        [InlineData("no", "no", "yes", false)]
        [InlineData("yes", "no", "no", true)]
        public void PasswordLoginNeedsPasswordAuthOrKbdInteractiveWithPam(string password, string kbd, string pam, bool expected)
        {
            var s = Parse("PasswordAuthentication " + password + "\nKbdInteractiveAuthentication " + kbd + "\nUsePAM " + pam + "\n");

            Assert.Equal(expected, s.PasswordLoginPossible);
        }

        [Theory]
        [InlineData("prohibit-password", RootLoginMode.ProhibitPassword)]
        [InlineData("without-password", RootLoginMode.ProhibitPassword)]
        [InlineData("forced-commands-only", RootLoginMode.ForcedCommandsOnly)]
        [InlineData("yes", RootLoginMode.Yes)]
        [InlineData("no", RootLoginMode.No)]
        public void RootLoginValues(string value, RootLoginMode expected)
        {
            Assert.Equal(expected, Parse("PermitRootLogin " + value + "\n").PermitRootLogin.Value);
        }

        [Fact]
        public void AFileLargerThanTheLimitIsTruncatedAndReported()
        {
            var b = new LinuxRootBuilder();
            string text = "Port 2022\n" + new string('#', 1100000) + "\nPort 2023\n";
            b.File("/etc/ssh/sshd_config", text);
            var r = b.Reader();

            var s = SshdConfig.Parse(r);

            Assert.Equal(new[] { 2022 }, s.Ports.Select(p => p.Value));
            Assert.Contains("/etc/ssh/sshd_config", r.Notes.TruncatedFiles);
        }
    }
}
