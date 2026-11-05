using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>The startup list on a real image made by mke2fs (quickstart scenario 7).</summary>
    public class StartupFixtureTests
    {
        internal static StartupResult Run(string fixture)
        {
            return DiagnosticsRunner.Run(null, SshFixtureTests.SourceOf(fixture), (r, s) => StartupInventory.Run(r, s, CancellationToken.None), CancellationToken.None);
        }

        [Fact]
        public void TheFixtureListsMoreThanFortyActiveEntriesOfEveryKind()
        {
            var r = Run("diag-startup");

            var active = r.Entries.Where(e => e.Active).ToList();

            Assert.True(active.Count > 40, "active: " + active.Count);
            foreach (StartupKind kind in new[] { StartupKind.Service, StartupKind.Timer, StartupKind.Socket, StartupKind.UserService, StartupKind.BootScript, StartupKind.Desktop, StartupKind.Cron })
            {
                Assert.Contains(active, e => e.Kind == kind);
            }

            Assert.Empty(r.Notes);
        }

        [Fact]
        public void ServicesTimersAndTargets()
        {
            var r = Run("diag-startup");
            var e = r.Entries.ToLookup(x => x.Name);

            Assert.True(e["cron.service"].Single().Active);
            Assert.Equal("/usr/sbin/cron -f", e["cron.service"].Single().Command);
            Assert.True(e["lightdm.service"].Single().Active);
            Assert.Equal(InactiveReason.TargetNotReached, e["rescue-helper.service"].Single().InactiveReason);
            Assert.Equal(InactiveReason.Masked, e["plymouth-start.service"].Single().InactiveReason);
            var tty = e["getty@tty1.service"].Single();
            Assert.True(tty.Active);
            Assert.StartsWith("/sbin/agetty", tty.Command);
            var timer = e["apt-daily.timer"].Single();
            Assert.Equal(ScheduleClass.DailyAt, timer.Schedule.Class);
            Assert.Equal("06:00,18:00", string.Join(",", timer.Schedule.Times));
            Assert.Equal("/usr/lib/apt-daily/run", timer.Command);
            Assert.Equal(ScheduleClass.WeeklyOn, e["e2scrub_all.timer"].Single().Schedule.Class);
            Assert.Equal("pi", e["sync.service"].Single().User);
            Assert.Equal(StartupKind.UserService, e["sync.service"].Single().Kind);
        }

        [Fact]
        public void BootScriptsDesktopEntriesAndCron()
        {
            var r = Run("diag-startup");
            var e = r.Entries.ToLookup(x => x.Name);

            Assert.Equal(2, r.Entries.Count(x => x.Kind == StartupKind.BootScript && x.Active));
            Assert.Equal(InactiveReason.HiddenEntry, e["Keyring"].Single().InactiveReason);
            var pulse = r.Entries.Where(x => x.Source.EndsWith("/pulseaudio-start.desktop")).ToList();
            Assert.Equal(2, pulse.Count);
            Assert.Contains(pulse, x => x.User == "pi" && !x.Active);
            Assert.Contains(pulse, x => x.User == string.Empty && x.Active);
            Assert.True(e["My dashboard"].Single().Active);
            Assert.Equal("/home/pi/bin/dashboard --kiosk", e["My dashboard"].Single().Command);
            Assert.True(e["backup.sh"].Single().Active);
            Assert.Equal("pi", e["backup.sh"].Single().User);
            Assert.Equal(ScheduleClass.EveryNMinutes, e["backup.sh"].Single().Schedule.Class);
            Assert.Equal(ScheduleClass.AtBoot, e["start-tunnel.sh"].Single().Schedule.Class);
            Assert.Equal(InactiveReason.NoSuchUser, e["job.sh"].Single().InactiveReason);
            Assert.Equal(InactiveReason.IgnoredName, e["ignored-because-of-the-dot"].Single().InactiveReason);
            Assert.Equal(InactiveReason.NotExecutable, e["notexec"].Single().InactiveReason);
            Assert.Equal(InactiveReason.IgnoredName, e["with.dot"].Single().InactiveReason);
            Assert.Equal("06:25", e["logrotate"].Single(x => x.Kind == StartupKind.Cron).Schedule.Times[0].ToString());
        }

        [Fact]
        public void NoSecretOfTheImageAppearsInTheList()
        {
            SecretLeakTests.AssertClean(Run("diag-startup"), "StartupResult of diag-startup");
        }
    }
}
