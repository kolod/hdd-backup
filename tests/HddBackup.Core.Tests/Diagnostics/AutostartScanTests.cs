using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class AutostartScanTests
    {
        private static readonly KeyValuePair<string, string>[] Pi = { new KeyValuePair<string, string>("pi", "/home/pi") };

        private static List<StartupEntry> Scan(LinuxRootBuilder b, params KeyValuePair<string, string>[] homes)
        {
            return AutostartScan.Run(b.Reader(), homes.Length == 0 ? Pi : homes);
        }

        private static string Desktop(string name, string exec, string extra = "") =>
            "[Desktop Entry]\nType=Application\nName=" + name + "\nExec=" + exec + "\n" + extra;

        [Fact]
        public void RcLocalListsOneEntryPerCommandAndSkipsExit()
        {
            var b = new LinuxRootBuilder().Executable("/etc/rc.local", "#!/bin/sh -e\n# comment\n/usr/local/bin/led-on.sh\n\n/home/pi/announce-ip.sh &\nexit 0\n");

            var e = Scan(b);

            Assert.Single(e);
            Assert.All(e, x => Assert.Equal(StartupKind.BootScript, x.Kind));
            Assert.Equal("rc.local", e[0].Name);
            Assert.Equal("/usr/local/bin/led-on.sh; /home/pi/announce-ip.sh &", e[0].Command);
            Assert.Equal(ScheduleClass.AtBoot, e[0].Schedule.Class);
            Assert.Equal("/etc/rc.local", e[0].Source);
            Assert.All(e, x => Assert.True(x.Active));
        }

        [Fact]
        public void RcLocalThatIsNotExecutableIsInactive()
        {
            var b = new LinuxRootBuilder().File("/etc/rc.local", "#!/bin/sh\n/usr/local/bin/x\n");

            Assert.Equal(InactiveReason.NotExecutable, Scan(b).Single().InactiveReason);
        }

        [Fact]
        public void RcLocalWithOnlyExitHasNothing()
        {
            Assert.Empty(Scan(new LinuxRootBuilder().Executable("/etc/rc.local", "#!/bin/sh\nexit 0\n")));
        }

        [Fact]
        public void DesktopEntriesAreReadWithTheirNameAndFieldCodesStripped()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/xdg/autostart/nm-applet.desktop", Desktop("Network", "nm-applet %U --indicator %%x"))
                .File("/etc/xdg/autostart/notdesktop.txt", "x");

            var e = Assert.Single(Scan(b));

            Assert.Equal(StartupKind.Desktop, e.Kind);
            Assert.Equal("Network", e.Name);
            Assert.Equal("nm-applet --indicator %x", e.Command);
            Assert.Equal("", e.User);
            Assert.True(e.Active);
        }

        [Fact]
        public void HiddenAndDisabledEntriesAreInactive()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/xdg/autostart/a.desktop", Desktop("A", "a", "Hidden=true\n"))
                .File("/etc/xdg/autostart/b.desktop", Desktop("B", "b", "X-GNOME-Autostart-enabled=false\n"))
                .File("/etc/xdg/autostart/c.desktop", Desktop("C", "c", "Hidden=false\n"));

            var e = Scan(b).ToDictionary(x => x.Name);

            Assert.Equal(InactiveReason.HiddenEntry, e["A"].InactiveReason);
            Assert.Equal(InactiveReason.HiddenEntry, e["B"].InactiveReason);
            Assert.True(e["C"].Active);
        }

        [Fact]
        public void AUserFileOverridesTheSystemEntryOfTheSameNameForThatUser()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/xdg/autostart/pulse.desktop", Desktop("Pulse", "pulseaudio --start"))
                .File("/home/pi/.config/autostart/pulse.desktop", Desktop("Pulse", "pulseaudio --start", "Hidden=true\n"))
                .File("/home/pi/.config/autostart/mine.desktop", Desktop("Mine", "/home/pi/mine"));

            var e = Scan(b);

            Assert.Equal(3, e.Count);
            var system = e.Single(x => x.Name == "Pulse" && x.User == "");
            var user = e.Single(x => x.Name == "Pulse" && x.User == "pi");
            Assert.True(system.Active);
            Assert.Equal(InactiveReason.HiddenEntry, user.InactiveReason);
            Assert.True(e.Single(x => x.Name == "Mine").Active);
            Assert.Equal("/home/pi/.config/autostart/mine.desktop", e.Single(x => x.Name == "Mine").Source);
        }

        [Fact]
        public void OnlyShowInIsEvaluatedAgainstTheInstalledDesktops()
        {
            var b = new LinuxRootBuilder()
                .File("/usr/share/xsessions/LXDE-pi-x.desktop", "[Desktop Entry]\nName=LXDE\n")
                .File("/etc/xdg/autostart/gnome.desktop", Desktop("Gnome", "g", "OnlyShowIn=GNOME;\n"))
                .File("/etc/xdg/autostart/lxde.desktop", Desktop("Lxde", "l", "OnlyShowIn=LXDE;\n"))
                .File("/etc/xdg/autostart/any.desktop", Desktop("Any", "a"));

            var e = Scan(b).ToDictionary(x => x.Name);

            Assert.Equal(InactiveReason.HiddenEntry, e["Gnome"].InactiveReason);
            Assert.True(e["Lxde"].Active);
            Assert.True(e["Any"].Active);
        }

        [Fact]
        public void WithoutAnyInstalledDesktopOnlyShowInIsIgnored()
        {
            var b = new LinuxRootBuilder().File("/etc/xdg/autostart/gnome.desktop", Desktop("Gnome", "g", "OnlyShowIn=GNOME;\n"));

            Assert.True(Scan(b).Single().Active);
        }

        [Fact]
        public void LxdeAutostartLinesAndComments()
        {
            var b = new LinuxRootBuilder().File("/etc/xdg/lxsession/LXDE-pi/autostart", "@lxpanel --profile LXDE-pi\n@pcmanfm --desktop --profile LXDE-pi\n# @xscreensaver -no-splash\n-@pcmanfm\n@/home/pi/bin/wallpaper.sh\n");

            var e = Scan(b);

            Assert.Equal(3, e.Count);
            Assert.Equal(new[] { "lxpanel", "pcmanfm", "wallpaper.sh" }, e.Select(x => x.Name));
            Assert.Equal("lxpanel --profile LXDE-pi", e[0].Command);
            Assert.All(e, x => Assert.Equal(StartupKind.Desktop, x.Kind));
            Assert.Equal("/etc/xdg/lxsession/LXDE-pi/autostart", e[0].Source);
        }

        [Fact]
        public void LabwcAndPerUserAutostartFiles()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/xdg/labwc/autostart", "/usr/bin/lwrespawn /usr/bin/wf-panel-pi &\n")
                .File("/home/pi/.config/labwc/autostart", "/home/pi/bin/mine.sh &\n")
                .File("/home/pi/.config/lxsession/LXDE-pi/autostart", "@xterm\n");

            var e = Scan(b);

            Assert.Equal(3, e.Count);
            Assert.Equal("", e.Single(x => x.Name == "lwrespawn").User);
            Assert.Equal("pi", e.Single(x => x.Name == "mine.sh").User);
            Assert.Equal("pi", e.Single(x => x.Name == "xterm").User);
        }

        [Fact]
        public void AccountsWithoutAHomeAreSkipped()
        {
            var b = new LinuxRootBuilder().File("/etc/xdg/autostart/a.desktop", Desktop("A", "a"));

            var e = Scan(b, new KeyValuePair<string, string>("nobody", "/nonexistent"), new KeyValuePair<string, string>("root", "/"), new KeyValuePair<string, string>("x", ""));

            Assert.Single(e);
        }

        [Fact]
        public void AnUnreadableDesktopFileIsMarkedAndTheScanContinues()
        {
            var b = new LinuxRootBuilder()
                .File("/etc/xdg/autostart/bad.desktop", Desktop("Bad", "bad"))
                .File("/etc/xdg/autostart/good.desktop", Desktop("Good", "good"));
            b.Tree.OnRead = f =>
            {
                if (f.Path.EndsWith("bad.desktop"))
                {
                    throw new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.CorruptImage, "bad");
                }
            };

            var e = Scan(b).ToDictionary(x => x.Name);

            Assert.Equal(InactiveReason.Unreadable, e["bad"].InactiveReason);
            Assert.True(e["Good"].Active);
        }

        [Fact]
        public void RcLocalIsOneEntryEvenWithShellBlocks()
        {
            var b = new LinuxRootBuilder().Executable("/etc/rc.local", "#!/bin/sh -e\n_IP=$(hostname -I) || true\nif [ \"$_IP\" ]; then\n  printf \"My IP is %s\\n\" \"$_IP\"\nfi\n/usr/local/bin/led-on.sh\nexit 0\n");

            var e = Scan(b);

            var rc = Assert.Single(e);
            Assert.Equal("rc.local", rc.Name);
            Assert.Contains("if [ \"$_IP\" ]; then printf", rc.Command);
            Assert.Contains("; fi; /usr/local/bin/led-on.sh", rc.Command);
            Assert.DoesNotContain("exit", rc.Command);
        }

        [Theory]
        [InlineData("app %f", "app")]
        [InlineData("app --x=%u %U", "app --x=")]
        [InlineData("app 100%%", "app 100%")]
        [InlineData("  spaced   out  ", "spaced out")]
        public void FieldCodes(string exec, string expected)
        {
            Assert.Equal(expected, AutostartScan.StripFieldCodes(exec));
        }
    }
}
