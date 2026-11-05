using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>The whole chain (ext4 reader, links, parsers) on real images made by mke2fs (quickstart scenarios 2-5).</summary>
    public class SshFixtureTests
    {
        internal static DiagnosticsSource SourceOf(string fixture)
        {
            string image = FixtureImages.RawPath(fixture);
            int number;
            using (var s = BrowseSource.OpenImage(image))
            {
                number = s.Browsable.First().Number;
            }

            return new DiagnosticsSource { ImagePath = image, PartitionNumber = number, DisplayName = fixture + ".img" };
        }

        internal static SshReport Check(string fixture)
        {
            return DiagnosticsRunner.Run(null, SourceOf(fixture), (r, s) =>
            {
                var report = SshCheck.Run(r, s, CancellationToken.None);
                if (report.Settings != null)
                {
                    DiagnosticExport.Ssh(fixture, report.Settings);
                }

                return report;
            }, CancellationToken.None);
        }

        [Fact]
        public void ServerEnabledWithPasswordAndKeyLogin()
        {
            var report = Check("diag-ssh-ok");

            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
            var pi = report.Verdict.Users.Single();
            Assert.Equal("pi", pi.Account);
            Assert.Equal(LoginMethod.Both, pi.Method);
            Assert.True(report.Server.Installed);
            Assert.Equal(ServerEnabledState.Yes, report.Server.Enabled);
            Assert.Equal("ssh.service", report.Server.Unit);
            Assert.Equal("/etc/ssh/sshd_config.d/50-local.conf", report.Settings.PasswordAuthentication.Source);
            Assert.Equal(2, report.Settings.PasswordAuthentication.Line);
            Assert.Equal(2, report.Accounts.Single(a => a.Name == "pi").KeyCount);
            Assert.Equal(PasswordState.Set, report.Accounts.Single(a => a.Name == "pi").Password);
            Assert.Equal(PasswordState.NoPassword, report.Accounts.Single(a => a.Name == "root").Password);
        }

        [Fact]
        public void KeysOnlyListsTheLockedUserWithAReason()
        {
            var report = Check("diag-ssh-keys-only");

            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
            var pi = report.Verdict.Users.Single();
            Assert.Equal(LoginMethod.Key, pi.Method);
            Assert.False(report.Settings.PasswordLoginPossible);
            Assert.Equal(RootLoginMode.No, report.Settings.PermitRootLogin.Value);
            var guest = report.Accounts.Single(a => a.Name == "guest");
            Assert.Equal(LoginAnswer.No, guest.CanLogIn);
            Assert.NotEqual(LoginReason.None, guest.Reason);
            Assert.Equal(PasswordState.Locked, guest.Password);
            Assert.Contains(report.Findings, f => f.Code == FindingCode.AccountCannot && f.Args[0] == "guest");
        }

        [Fact]
        public void InstalledButNotEnabled()
        {
            var report = Check("diag-ssh-disabled");

            Assert.Equal(VerdictCode.NotEnabled, report.Verdict.Code);
            Assert.True(report.Server.Installed);
            Assert.Equal(ServerEnabledState.No, report.Server.Enabled);
            Assert.Equal(FindingCode.ServerNotEnabled, report.Verdict.Reasons.Single().Code);
        }

        [Fact]
        public void RaspberryPiLikeImageNeverSaysSshIsOff()
        {
            var report = Check("diag-pi-like");

            Assert.Equal(VerdictCode.NotEnabledBootFlagPossible, report.Verdict.Code);
            Assert.True(report.System.IsRaspberryPiOs);
            Assert.True(report.Server.FirstBootSwitchPresent);
            Assert.Contains(report.Verdict.Notes, n => n.Code == FindingCode.BootFlagNotRead);
        }

        [Fact]
        public void TheRunnerKnowsAboutTheFatBootPartition()
        {
            var source = SourceOf("diag-pi-like");
            Assert.Equal(2, source.PartitionNumber);

            bool fat = DiagnosticsRunner.Run(null, source, (r, s) => s.HasUnreadBootPartition, CancellationToken.None);

            Assert.True(fat);
        }

        [Theory]
        [InlineData("diag-ssh-ok")]
        [InlineData("diag-ssh-keys-only")]
        [InlineData("diag-ssh-disabled")]
        [InlineData("diag-pi-like")]
        public void TheImageIsNotModified(string fixture)
        {
            string image = FixtureImages.RawPath(fixture);
            string before = HddBackup.Core.Tests.Browsing.BrowseSourceTests.Sha256Of(image);

            Check(fixture);

            Assert.Equal(before, HddBackup.Core.Tests.Browsing.BrowseSourceTests.Sha256Of(image));
        }

        [Fact]
        public void NeverOpensThePrivateKey()
        {
            var tree = new RecordingTree(FixtureImages.RawPath("diag-ssh-ok"));

            using (tree)
            {
                var reader = new SystemReader(tree.View);
                SshCheck.Run(reader, new DiagnosticsSource(), CancellationToken.None);
            }

            Assert.DoesNotContain(tree.Reads, p => p.Contains("id_ed25519"));
            Assert.Contains("/home/pi/.ssh/authorized_keys", tree.Reads);
        }
    }
}
