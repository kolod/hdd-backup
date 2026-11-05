using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class SystemdScanTests
    {
        private const string Ssh = "[Unit]\nDescription=OpenBSD Secure Shell server\n[Service]\nExecStart=/usr/sbin/sshd -D $SSHD_OPTS\n[Install]\nWantedBy=multi-user.target\nAlias=sshd.service\n";

        private static SystemdScan Scan(LinuxRootBuilder b) => new SystemdScan(b.Reader());

        [Fact]
        public void EnabledThroughTheWantsDirectory()
        {
            var s = Scan(new LinuxRootBuilder().Systemd().Unit("ssh.service", Ssh).Enable("ssh.service"));

            var state = s.EnabledState("ssh.service");

            Assert.Equal(UnitEnabled.Yes, state.Enabled);
            Assert.True(state.Exists);
            Assert.Contains("/etc/systemd/system/multi-user.target.wants/ssh.service", state.Evidence);
        }

        [Fact]
        public void NotEnabledWithoutALink()
        {
            var s = Scan(new LinuxRootBuilder().Systemd().Unit("ssh.service", Ssh));

            var state = s.EnabledState("ssh.service");

            Assert.Equal(UnitEnabled.No, state.Enabled);
            Assert.True(state.Exists);
            Assert.False(state.InUnreachedTarget);
        }

        [Fact]
        public void MaskedByALinkToDevNull()
        {
            var s = Scan(new LinuxRootBuilder().Systemd().Unit("ssh.service", Ssh).Link("/etc/systemd/system/ssh.service", "/dev/null"));

            Assert.Equal(UnitEnabled.Masked, s.EnabledState("ssh.service").Enabled);
            Assert.True(s.FindUnit("ssh.service").Masked);
        }

        [Fact]
        public void AnAliasLeadsToTheEnabledUnit()
        {
            var s = Scan(new LinuxRootBuilder().Systemd()
                .Unit("ssh.service", Ssh).Enable("ssh.service")
                .Link("/etc/systemd/system/sshd.service", "/lib/systemd/system/ssh.service"));

            var state = s.EnabledState("sshd.service");

            Assert.Equal(UnitEnabled.Yes, state.Enabled);
            Assert.Equal("ssh.service", state.Unit);
        }

        [Fact]
        public void EnabledOnlyInAGraphicalTargetIsNotReachedWhenTheDefaultIsMultiUser()
        {
            var b = new LinuxRootBuilder().Systemd("multi-user.target").Unit("app.service", "[Service]\nExecStart=/bin/app\n").Enable("app.service", "graphical.target");
            var s = Scan(b);

            var state = s.EnabledState("app.service");

            Assert.Equal(UnitEnabled.No, state.Enabled);
            Assert.True(state.InUnreachedTarget);
            Assert.DoesNotContain("graphical.target", s.ReachedTargets);
            Assert.False(s.EnabledUnits().Single(u => u.Name == "app.service").Reached);
        }

        [Fact]
        public void EnabledInAGraphicalTargetIsReachedWhenTheDefaultIsGraphical()
        {
            var b = new LinuxRootBuilder().Systemd("graphical.target").Unit("app.service", "[Service]\nExecStart=/bin/app\n").Enable("app.service", "graphical.target");
            var s = Scan(b);

            Assert.Equal(UnitEnabled.Yes, s.EnabledState("app.service").Enabled);
            Assert.Contains("multi-user.target", s.ReachedTargets);
            Assert.Contains("timers.target", s.ReachedTargets);
        }

        [Fact]
        public void DefaultTargetFromTheEtcLinkWins()
        {
            var b = new LinuxRootBuilder().Systemd("graphical.target").Link("/etc/systemd/system/default.target", "/lib/systemd/system/multi-user.target");

            Assert.Equal("multi-user.target", Scan(b).DefaultTarget);
        }

        [Fact]
        public void SocketActivationIsAnEnabledSocket()
        {
            var b = new LinuxRootBuilder().Systemd()
                .Unit("ssh.service", Ssh)
                .Unit("ssh.socket", "[Socket]\nListenStream=22\n")
                .Enable("ssh.socket", "sockets.target");
            var s = Scan(b);

            Assert.Equal(UnitEnabled.Yes, s.EnabledState("ssh.socket").Enabled);
            Assert.Equal(UnitEnabled.No, s.EnabledState("ssh.service").Enabled);
        }

        [Fact]
        public void ALinkWhoseUnitFileIsMissingDoesNotCountAsEnabled()
        {
            var b = new LinuxRootBuilder().Systemd().Enable("gone.service");
            var s = Scan(b);

            var state = s.EnabledState("gone.service");

            Assert.Equal(UnitEnabled.No, state.Enabled);
            Assert.False(state.Exists);
            Assert.True(s.EnabledUnits().Single().UnitMissing);
        }

        [Fact]
        public void TemplateInstancesFindTheirTemplate()
        {
            var b = new LinuxRootBuilder().Systemd()
                .Unit("getty@.service", "[Service]\nExecStart=-/sbin/agetty -o '-p -- \\\\u' --noclear %I $TERM\n")
                .Enable("getty@.service", "getty.target", "getty@tty1.service");
            var s = Scan(b);

            var unit = s.FindUnit("getty@tty1.service");

            Assert.True(unit.Exists);
            Assert.StartsWith("/sbin/agetty", unit.ExecStart);
            var e = s.EnabledUnits().Single();
            Assert.Equal("getty@.service", e.UnitFile);
            Assert.Equal("getty@tty1.service", e.Name);
            Assert.True(e.Reached);
        }

        [Fact]
        public void TimersKeepTheirCalendarAndUnit()
        {
            var b = new LinuxRootBuilder().Systemd()
                .Unit("apt-daily.timer", "[Timer]\nOnCalendar=*-*-* 6,18:00\nRandomizedDelaySec=12h\nUnit=apt-daily.service\n[Install]\nWantedBy=timers.target\n")
                .Unit("apt-daily.service", "[Service]\nType=oneshot\nExecStart=/usr/lib/apt/apt.systemd.daily update\n")
                .Enable("apt-daily.timer", "timers.target");
            var s = Scan(b);

            var timer = s.FindUnit("apt-daily.timer");

            Assert.Equal("*-*-* 6,18:00", timer.First("Timer", "OnCalendar"));
            Assert.Equal("apt-daily.service", timer.First("Timer", "Unit"));
            Assert.Equal("timer", s.EnabledUnits().Single().Kind);
            Assert.True(s.EnabledUnits().Single().Reached);
        }

        [Fact]
        public void PerUserUnitsAreListedFromTheHome()
        {
            var b = new LinuxRootBuilder().Systemd()
                .File("/home/pi/.config/systemd/user/sync.service", "[Service]\nExecStart=/home/pi/sync.sh\n")
                .Link("/home/pi/.config/systemd/user/default.target.wants/sync.service", "../sync.service");
            var s = Scan(b);

            var units = s.UserUnits("pi", "/home/pi");

            var u = Assert.Single(units);
            Assert.Equal("pi", u.User);
            Assert.Equal("sync.service", u.UnitFile);
            Assert.True(u.Reached);
            Assert.Empty(s.UserUnits("nobody", "/nonexistent"));
        }

        [Fact]
        public void SysVLinksAreFoundWhenThereIsNoSystemd()
        {
            var b = new LinuxRootBuilder().BasicLinux()
                .Executable("/etc/init.d/ssh")
                .Link("/etc/rc2.d/S01ssh", "../init.d/ssh")
                .Link("/etc/rc3.d/S01ssh", "../init.d/ssh")
                .Link("/etc/rc2.d/K01cron", "../init.d/cron");
            var s = Scan(b);

            Assert.False(s.HasSystemd);
            var link = Assert.Single(s.SysVStartLinks());
            Assert.Equal("ssh", link.Key);
            Assert.Equal("/etc/rc2.d/S01ssh", link.Value);
        }

        [Theory]
        [InlineData("-/usr/bin/foo --bar", "/usr/bin/foo --bar")]
        [InlineData("@/usr/bin/foo name", "/usr/bin/foo name")]
        [InlineData("+!/usr/bin/foo", "/usr/bin/foo")]
        [InlineData("!!/usr/bin/foo", "/usr/bin/foo")]
        [InlineData(":/usr/bin/foo", "/usr/bin/foo")]
        [InlineData("/usr/bin/foo", "/usr/bin/foo")]
        public void ExecStartPrefixesAreStripped(string raw, string expected)
        {
            var b = new LinuxRootBuilder().Systemd().Unit("x.service", "[Service]\nExecStart=" + raw + "\n");

            Assert.Equal(expected, Scan(b).FindUnit("x.service").ExecStart);
        }

        [Fact]
        public void AnUnreadableUnitFileIsMarkedAndTheScanContinues()
        {
            var b = new LinuxRootBuilder().Systemd()
                .Unit("bad.service", "[Service]\nExecStart=/bin/bad\n")
                .Unit("ok.service", "[Service]\nExecStart=/bin/ok\n");
            b.Tree.OnRead = f =>
            {
                if (f.Path.EndsWith("bad.service"))
                {
                    throw new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.CorruptImage, "bad");
                }
            };
            var s = Scan(b);

            Assert.True(s.FindUnit("bad.service").Unreadable);
            Assert.Equal("/bin/ok", s.FindUnit("ok.service").ExecStart);
        }

        [Fact]
        public void UnitFilesHandleContinuationsCommentsAndResets()
        {
            var b = new LinuxRootBuilder().Systemd().Unit("x.service",
                "# comment\n[Unit]\nDescription=Test\\\n  unit\nWants=a.service b.service\nWants=c.service\n[Service]\nUser=pi\nExecStart=/bin/one\nExecStart=\nExecStart=/bin/two\n");
            var u = Scan(b).FindUnit("x.service");

            Assert.Equal("Test unit", u.Description);
            Assert.Equal(new[] { "a.service", "b.service", "c.service" }, u.Words("Unit", "Wants"));
            Assert.Equal("pi", u.User);
            Assert.Equal("/bin/two", u.ExecStart);
        }

        [Fact]
        public void TheEtcDirectoryOverridesTheVendorUnit()
        {
            var b = new LinuxRootBuilder().Systemd()
                .Unit("x.service", "[Service]\nExecStart=/bin/vendor\n")
                .File("/etc/systemd/system/x.service", "[Service]\nExecStart=/bin/local\n");

            Assert.Equal("/bin/local", Scan(b).FindUnit("x.service").ExecStart);
        }
    }
}
