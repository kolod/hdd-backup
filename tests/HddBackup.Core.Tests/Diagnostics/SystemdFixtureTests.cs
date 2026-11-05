using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>systemd scan on real images: real links, merged /usr, template instances, masking and target reachability.</summary>
    public class SystemdFixtureTests
    {
        private static T Scan<T>(string fixture, System.Func<SystemdScan, T> f)
        {
            return DiagnosticsRunner.Run(null, SshFixtureTests.SourceOf(fixture), (r, s) =>
            {
                var scan = new SystemdScan(r);
                DiagnosticExport.Units(fixture, scan);
                return f(scan);
            }, CancellationToken.None);
        }

        [Theory]
        [InlineData("diag-ssh-ok")]
        [InlineData("diag-ssh-keys-only")]
        [InlineData("diag-ssh-disabled")]
        [InlineData("diag-pi-like")]
        [InlineData("diag-startup")]
        public void ExportsTheEnabledUnitsForTheIndependentCheck(string fixture)
        {
            var units = Scan(fixture, s => s.EnabledUnits().ToList());

            Assert.NotNull(units);
        }

        [Fact]
        public void StartupFixtureHasTheExpectedUnits()
        {
            var r = Scan("diag-startup", s => new
            {
                Enabled = s.EnabledUnits().ToList(),
                Default = s.DefaultTarget,
                Reached = s.ReachedTargets.ToList(),
                Plymouth = s.EnabledState("plymouth-start.service").Enabled,
                Ssh = s.EnabledState("ssh.service").Enabled,
                SshdAlias = s.EnabledState("sshd.service").Enabled,
                Getty = s.FindUnit("getty@tty1.service").ExecStart
            });

            Assert.Equal("graphical.target", r.Default);
            Assert.Contains("graphical.target", r.Reached);
            Assert.DoesNotContain("rescue.target", r.Reached);
            Assert.Equal(UnitEnabled.Masked, r.Plymouth);
            Assert.Equal(UnitEnabled.Yes, r.Ssh);
            Assert.Equal(UnitEnabled.Yes, r.SshdAlias);
            Assert.StartsWith("/sbin/agetty", r.Getty);
            var rescue = r.Enabled.Single(u => u.Name == "rescue-helper.service");
            Assert.False(rescue.Reached);
            Assert.True(r.Enabled.Single(u => u.Name == "cron.service").Reached);
            Assert.True(r.Enabled.Single(u => u.Name == "lightdm.service").Reached);
            var getty = r.Enabled.Single(u => u.Name == "getty@tty1.service");
            Assert.Equal("getty@.service", getty.UnitFile);
            Assert.Equal(8, r.Enabled.Count(u => u.Kind == "timer"));
            Assert.Equal(2, r.Enabled.Count(u => u.Kind == "socket" && u.Name != "ssh.socket"));
        }

        [Fact]
        public void PerUserUnitIsFoundInTheHome()
        {
            var units = Scan("diag-startup", s => s.UserUnits("pi", "/home/pi").ToList());

            var u = Assert.Single(units);
            Assert.Equal("sync.service", u.Name);
            Assert.Equal("pi", u.User);
        }
    }
}
