using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>Entries that come with the distribution are marked Standard; what a person added is Custom.</summary>
    public class EntryOriginTests
    {
        private static StartupEntry Find(StartupResult r, string name, string user = null)
        {
            return r.Entries.First(e => e.Name == name && (user == null || e.User == user));
        }

        [Fact]
        public void VendorUnitsAndPackageEntriesAreStandardAndPersonalOnesAreCustom()
        {
            var r = StartupInventory.Run(StartupInventoryTests.ReferenceSystem().Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.Equal(EntryOrigin.Standard, Find(r, "cron.service").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "lightdm.service").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "apt-daily.timer").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "avahi-daemon.socket").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "Network Manager Applet").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "lxpanel").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "e2scrub_all_cron").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "logrotate", null).Origin);

            Assert.Equal(EntryOrigin.Custom, Find(r, "sync.service").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "led-on.sh").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "Dashboard").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "backup.sh").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "start-tunnel.sh").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "wallpaper.sh").Origin);
        }

        [Fact]
        public void AUnitInEtcOrRunningSomethingUnderUsrLocalIsCustom()
        {
            var b = StartupInventoryTests.ReferenceSystem();
            b.File("/etc/systemd/system/mine.service", "[Service]\nExecStart=/usr/bin/mine\n[Install]\nWantedBy=multi-user.target\n");
            b.Link("/etc/systemd/system/multi-user.target.wants/mine.service", "/etc/systemd/system/mine.service");
            b.Unit("tool.service", "[Service]\nExecStart=/usr/local/bin/tool\n");
            b.Enable("tool.service");

            var r = StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.Equal(EntryOrigin.Custom, Find(r, "mine.service").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "tool.service").Origin);
        }

        [Fact]
        public void WithADpkgDatabaseAFileNoPackageOwnsIsCustom()
        {
            var b = new HddBackup.Core.Tests.Fakes.LinuxRootBuilder().BasicLinux();
            b.Executable("/etc/init.d/cron").Executable("/etc/init.d/bevend.sh");
            b.Link("/etc/rc2.d/S01cron", "../init.d/cron").Link("/etc/rc2.d/S01bevend.sh", "../init.d/bevend.sh");
            b.File("/etc/xdg/autostart/a.desktop", "[Desktop Entry]\nName=Owned\nExec=owned-app\n");
            b.File("/etc/xdg/autostart/b.desktop", "[Desktop Entry]\nName=Handmade\nExec=handmade-app\n");
            b.Executable("/usr/sbin/cron").File("/etc/cron.d/pkg", "0 1 * * * root /usr/bin/pkgjob\n").File("/etc/cron.d/mine", "0 2 * * * root /usr/bin/minejob\n");
            b.File("/var/lib/dpkg/info/cron.list", "/etc/init.d/cron\n/etc/cron.d/pkg\n/usr/sbin/cron\n");
            b.File("/var/lib/dpkg/info/app.list", "/etc/xdg/autostart/a.desktop\n");

            var r = StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.Equal(EntryOrigin.Standard, Find(r, "cron").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "bevend.sh").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "Owned").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "Handmade").Origin);
            Assert.Equal(EntryOrigin.Standard, Find(r, "pkgjob").Origin);
            Assert.Equal(EntryOrigin.Custom, Find(r, "minejob").Origin);
        }

        [Fact]
        public void WithoutADpkgDatabaseTheLocationDecides()
        {
            var b = new HddBackup.Core.Tests.Fakes.LinuxRootBuilder().BasicLinux().Executable("/etc/init.d/x").Link("/etc/rc2.d/S01x", "../init.d/x");

            Assert.Equal(EntryOrigin.Standard, Find(StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None), "x").Origin);
        }
    }
}
