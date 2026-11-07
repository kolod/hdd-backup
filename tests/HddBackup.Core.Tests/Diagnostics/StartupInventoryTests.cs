using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>SC-002: a reference system of more than forty entries gives exactly the expected list.</summary>
    public class StartupInventoryTests
    {
        private static void Svc(LinuxRootBuilder b, string name, string cmd, string target = "multi-user.target", string user = null)
        {
            b.Unit(name + ".service", "[Unit]\nDescription=" + name + "\n[Service]\n" + (user != null ? "User=" + user + "\n" : string.Empty) + "ExecStart=" + cmd + "\n[Install]\nWantedBy=" + target + "\n");
            b.Enable(name + ".service", target);
        }

        private static void Tmr(LinuxRootBuilder b, string name, string calendar)
        {
            b.Unit(name + ".timer", "[Timer]\nOnCalendar=" + calendar + "\n[Install]\nWantedBy=timers.target\n");
            b.Unit(name + ".service", "[Service]\nType=oneshot\nExecStart=/usr/lib/" + name + "/run\n");
            b.Enable(name + ".timer", "timers.target");
        }

        private static string Desktop(string name, string exec, string extra = "") =>
            "[Desktop Entry]\nType=Application\nName=" + name + "\nExec=" + exec + "\n" + extra;

        internal static LinuxRootBuilder ReferenceSystem()
        {
            var b = new LinuxRootBuilder().BasicLinux("debian", "Debian GNU/Linux 12", passwd: false).Systemd("graphical.target");
            b.Passwd("root:x:0:0:root:/root:/bin/bash", "daemon:x:1:1::/usr/sbin:/usr/sbin/nologin", "pi:x:1000:1000::/home/pi:/bin/bash");
            b.Shadow("root:*:19000:0:99999:7:::", "pi:$6$PLANTEDSECRET$hash:19000:0:99999:7:::");
            b.Dir("/home/pi").Dir("/root");
            b.Executable("/usr/sbin/cron");

            foreach (string n in new[] { "cron", "rsyslog", "ssh", "avahi-daemon", "bluetooth", "wpa_supplicant", "NetworkManager", "triggerhappy", "dphys-swapfile", "rpi-eeprom-update", "ModemManager", "polkit", "unattended-upgrades" })
            {
                Svc(b, n, "/usr/sbin/" + n + " -f");
            }

            b.Unit("getty@.service", "[Service]\nExecStart=-/sbin/agetty --noclear %I $TERM\n[Install]\nWantedBy=getty.target\n");
            b.Enable("getty@.service", "getty.target", "getty@tty1.service");
            Svc(b, "lightdm", "/usr/sbin/lightdm", "graphical.target");
            Svc(b, "rescue-helper", "/usr/local/bin/rescue-helper", "rescue.target");
            Svc(b, "plymouth-start", "/usr/bin/plymouth start");
            b.Link("/etc/systemd/system/plymouth-start.service", "/dev/null");
            b.Link("/etc/systemd/system/multi-user.target.wants/gone.service", "/lib/systemd/system/gone.service");

            Tmr(b, "apt-daily", "*-*-* 6,18:00");
            Tmr(b, "apt-daily-upgrade", "*-*-* 6:00");
            Tmr(b, "man-db", "daily");
            Tmr(b, "logrotate", "daily");
            Tmr(b, "fstrim", "weekly");
            Tmr(b, "e2scrub_all", "Sun *-*-* 03:10:00");
            b.Unit("avahi-daemon.socket", "[Socket]\nListenStream=/run/avahi-daemon/socket\n[Install]\nWantedBy=sockets.target\n");
            b.Unit("syslog.socket", "[Socket]\nListenDatagram=/run/systemd/journal/syslog\n[Install]\nWantedBy=sockets.target\n");
            b.Enable("avahi-daemon.socket", "sockets.target").Enable("syslog.socket", "sockets.target");

            b.File("/home/pi/.config/systemd/user/sync.service", "[Service]\nExecStart=/home/pi/bin/sync.sh\n");
            b.Link("/home/pi/.config/systemd/user/default.target.wants/sync.service", "../sync.service");

            b.Executable("/etc/rc.local", "#!/bin/sh -e\n/usr/local/bin/led-on.sh\n/home/pi/announce-ip.sh &\nexit 0\n");
            b.File("/usr/share/xsessions/LXDE-pi.desktop", "[Desktop Entry]\nName=LXDE\n");
            b.File("/etc/xdg/autostart/nm-applet.desktop", Desktop("Network Manager Applet", "nm-applet %U"));
            b.File("/etc/xdg/autostart/lxpolkit.desktop", Desktop("LXPolKit", "lxpolkit", "OnlyShowIn=LXDE;\n"));
            b.File("/etc/xdg/autostart/pulse.desktop", Desktop("Pulse", "pulseaudio --start"));
            b.File("/etc/xdg/autostart/gnome-keyring.desktop", Desktop("Keyring", "gnome-keyring-daemon", "OnlyShowIn=GNOME;\n"));
            b.File("/home/pi/.config/autostart/pulse.desktop", Desktop("Pulse", "pulseaudio --start", "Hidden=true\n"));
            b.File("/home/pi/.config/autostart/dashboard.desktop", Desktop("Dashboard", "/home/pi/bin/dashboard --kiosk"));
            b.File("/etc/xdg/lxsession/LXDE-pi/autostart", "@lxpanel --profile LXDE-pi\n@pcmanfm --desktop --profile LXDE-pi\n@/home/pi/bin/wallpaper.sh\n");

            b.File("/etc/crontab", "SHELL=/bin/sh\n17 *\t* * *\troot    cd / && run-parts --report /etc/cron.hourly\n25 6\t* * *\troot\ttest -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.daily; }\n47 6\t* * 7\troot\ttest -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.weekly; }\n");
            b.File("/etc/cron.d/backup", "*/15 * * * * pi /home/pi/backup.sh\n");
            b.File("/etc/cron.d/e2scrub_all", "30 3 * * 0 root test -e /run/systemd/system || SERVICE_MODE=1 /usr/lib/e2fsprogs/e2scrub_all_cron\n");
            b.File("/etc/cron.d/old.bak", "0 1 * * * root /usr/local/bin/ignored\n");
            b.File("/var/spool/cron/crontabs/pi", "@reboot /home/pi/start-tunnel.sh\n0 3 * * mon /home/pi/weekly-report.sh\n#30 4 * * * /home/pi/off.sh\nbroken line\n");
            b.File("/var/spool/cron/crontabs/ghost", "0 5 * * * /home/ghost/job.sh\n");
            b.Executable("/etc/cron.daily/apt-compat").Executable("/etc/cron.daily/dpkg").Executable("/etc/cron.daily/logrotate");
            b.File("/etc/cron.daily/notexec", "#!/bin/sh\n").Executable("/etc/cron.daily/with.dot");
            b.Executable("/etc/cron.hourly/fake-hwclock").Executable("/etc/cron.weekly/man-db");
            return b;
        }

        private static StartupResult Run(LinuxRootBuilder b)
        {
            return StartupInventory.Run(b.Reader(), new DiagnosticsSource { DisplayName = "ref" }, CancellationToken.None);
        }

        private static string Key(StartupEntry e) => e.Kind + "|" + e.Name + "|" + e.User + "|" + e.Source;

        // Kind|Name|User|Source — the expected active list, 41 entries.
        private static readonly string[] ExpectedActive =
        {
            "Service|cron.service|root|multi-user.target", "Service|rsyslog.service|root|multi-user.target", "Service|ssh.service|root|multi-user.target",
            "Service|avahi-daemon.service|root|multi-user.target", "Service|bluetooth.service|root|multi-user.target", "Service|wpa_supplicant.service|root|multi-user.target",
            "Service|NetworkManager.service|root|multi-user.target", "Service|triggerhappy.service|root|multi-user.target", "Service|dphys-swapfile.service|root|multi-user.target",
            "Service|rpi-eeprom-update.service|root|multi-user.target", "Service|ModemManager.service|root|multi-user.target", "Service|polkit.service|root|multi-user.target", "Service|unattended-upgrades.service|root|multi-user.target",
            "Service|getty@tty1.service|root|getty.target", "Service|lightdm.service|root|graphical.target",
            "Timer|apt-daily.timer|root|timers.target", "Timer|apt-daily-upgrade.timer|root|timers.target", "Timer|man-db.timer|root|timers.target",
            "Timer|logrotate.timer|root|timers.target", "Timer|fstrim.timer|root|timers.target", "Timer|e2scrub_all.timer|root|timers.target",
            "Socket|avahi-daemon.socket||sockets.target", "Socket|syslog.socket||sockets.target",
            "UserService|sync.service|pi|default.target",
            "BootScript|rc.local|root|/etc/rc.local",
            "Desktop|Network Manager Applet||/etc/xdg/autostart/nm-applet.desktop", "Desktop|LXPolKit||/etc/xdg/autostart/lxpolkit.desktop",
            "Desktop|Pulse||/etc/xdg/autostart/pulse.desktop", "Desktop|Dashboard|pi|/home/pi/.config/autostart/dashboard.desktop",
            "Desktop|lxpanel||/etc/xdg/lxsession/LXDE-pi/autostart", "Desktop|pcmanfm||/etc/xdg/lxsession/LXDE-pi/autostart", "Desktop|wallpaper.sh||/etc/xdg/lxsession/LXDE-pi/autostart",
            "Cron|backup.sh|pi|/etc/cron.d/backup", "Cron|e2scrub_all_cron|root|/etc/cron.d/e2scrub_all",
            "Cron|start-tunnel.sh|pi|/var/spool/cron/crontabs/pi", "Cron|weekly-report.sh|pi|/var/spool/cron/crontabs/pi",
            "Cron|apt-compat|root|/etc/cron.daily", "Cron|dpkg|root|/etc/cron.daily", "Cron|logrotate|root|/etc/cron.daily",
            "Cron|fake-hwclock|root|/etc/cron.hourly", "Cron|man-db|root|/etc/cron.weekly"
        };

        [Fact]
        public void TheActiveListIsExactlyTheExpectedOne()
        {
            var r = Run(ReferenceSystem());

            var active = r.Entries.Where(e => e.Active).Select(Key).OrderBy(x => x).ToList();

            Assert.True(ExpectedActive.Length > 40);
            Assert.Equal(ExpectedActive.OrderBy(x => x).ToList(), active);
            Assert.Equal(active.Count, active.Distinct().Count());
        }

        [Fact]
        public void InactiveEntriesCarryTheirReasons()
        {
            var r = Run(ReferenceSystem());

            var inactive = r.Entries.Where(e => !e.Active).ToDictionary(e => Key(e), e => e.InactiveReason);

            Assert.Equal(InactiveReason.Masked, inactive["Service|plymouth-start.service||multi-user.target"]);
            Assert.Equal(InactiveReason.TargetNotReached, inactive["Service|rescue-helper.service|root|rescue.target"]);
            Assert.Equal(InactiveReason.UnitMissing, inactive["Service|gone.service||multi-user.target"]);
            Assert.Equal(InactiveReason.HiddenEntry, inactive["Desktop|Keyring||/etc/xdg/autostart/gnome-keyring.desktop"]);
            Assert.Equal(InactiveReason.HiddenEntry, inactive["Desktop|Pulse|pi|/home/pi/.config/autostart/pulse.desktop"]);
            Assert.Equal(InactiveReason.IgnoredName, inactive["Cron|ignored|root|/etc/cron.d/old.bak"]);
            Assert.Equal(InactiveReason.NoSuchUser, inactive["Cron|job.sh|ghost|/var/spool/cron/crontabs/ghost"]);
            Assert.Equal(InactiveReason.NotExecutable, inactive["Cron|notexec|root|/etc/cron.daily"]);
            Assert.Equal(InactiveReason.IgnoredName, inactive["Cron|with.dot|root|/etc/cron.daily"]);
            Assert.Equal(InactiveReason.Malformed, inactive.Single(p => p.Key.StartsWith("Cron|broken line")).Value);
            Assert.Equal(10, inactive.Count);
        }

        [Fact]
        public void ActiveEntriesComeFirstThenInactiveSortedByKindAndName()
        {
            var r = Run(ReferenceSystem());

            int firstInactive = r.Entries.ToList().FindIndex(e => !e.Active);

            Assert.All(r.Entries.Take(firstInactive), e => Assert.True(e.Active));
            Assert.All(r.Entries.Skip(firstInactive), e => Assert.False(e.Active));
            Assert.Equal(r.Entries.Count(e => e.Active), r.ActiveCount);
            Assert.Equal(r.Entries.Count(e => !e.Active), r.InactiveCount);
            var activeKinds = r.Entries.Take(firstInactive).Select(e => (int)e.Kind).ToList();
            Assert.Equal(activeKinds.OrderBy(k => k).ToList(), activeKinds);
        }

        [Fact]
        public void SchedulesAreClassified()
        {
            var r = Run(ReferenceSystem());
            var byName = r.Entries.Where(e => e.Active).GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First());

            Assert.Equal(ScheduleClass.DailyAt, byName["apt-daily.timer"].Schedule.Class);
            Assert.Equal("06:00,18:00", string.Join(",", byName["apt-daily.timer"].Schedule.Times));
            Assert.Equal(ScheduleClass.WeeklyOn, byName["e2scrub_all.timer"].Schedule.Class);
            Assert.Equal(ScheduleClass.WeeklyOn, byName["fstrim.timer"].Schedule.Class);
            Assert.Equal(ScheduleClass.EveryNMinutes, byName["backup.sh"].Schedule.Class);
            Assert.Equal(ScheduleClass.AtBoot, byName["start-tunnel.sh"].Schedule.Class);
            Assert.Equal(ScheduleClass.WeeklyOn, byName["weekly-report.sh"].Schedule.Class);
            Assert.Equal("06:25", byName["logrotate"].Schedule.Times[0].ToString());
            Assert.Equal(ScheduleClass.AtBoot, byName["rc.local"].Schedule.Class);
            Assert.Null(byName["cron.service"].Schedule);
        }

        [Fact]
        public void TimersShowTheServiceTheyStartAndServicesTheirCommand()
        {
            var r = Run(ReferenceSystem());
            var timer = r.Entries.Single(e => e.Name == "logrotate.timer");
            var service = r.Entries.Single(e => e.Name == "ssh.service");
            var socket = r.Entries.Single(e => e.Name == "avahi-daemon.socket");

            Assert.Equal("/usr/lib/logrotate/run", timer.Command);
            Assert.Equal("daily", timer.ScheduleText);
            Assert.Equal("/usr/sbin/ssh -f", service.Command);
            Assert.Equal("/run/avahi-daemon/socket", socket.Command);
            Assert.Equal("/sbin/agetty --noclear %I $TERM", r.Entries.Single(e => e.Name == "getty@tty1.service").Command);
        }

        [Fact]
        public void CommandsAreExactlyWhatIsInTheImage()
        {
            var r = Run(ReferenceSystem());

            Assert.Equal("/usr/local/bin/led-on.sh; /home/pi/announce-ip.sh &", r.Entries.Single(e => e.Name == "rc.local").Command);
            Assert.Equal("nm-applet", r.Entries.Single(e => e.Name == "Network Manager Applet").Command);
            Assert.Equal("test -e /run/systemd/system || SERVICE_MODE=1 /usr/lib/e2fsprogs/e2scrub_all_cron", r.Entries.Single(e => e.Name == "e2scrub_all_cron").Command);
        }

        [Fact]
        public void NotesSayWhenCronIsMissingOrNotEnabled()
        {
            var noCron = new LinuxRootBuilder().BasicLinux().Systemd();
            var disabled = new LinuxRootBuilder().BasicLinux().Systemd().Executable("/usr/sbin/cron").Unit("cron.service", "[Service]\nExecStart=/usr/sbin/cron -f\n");

            Assert.Contains(Run(noCron).Notes, n => n.Code == StartupNoteCode.NoCron);
            Assert.Contains(Run(disabled).Notes, n => n.Code == StartupNoteCode.CronNotEnabled);
            Assert.Empty(Run(ReferenceSystem()).Notes);
        }

        [Fact]
        public void ANonLinuxRootHasNoEntries()
        {
            var r = Run(new LinuxRootBuilder().File("/data/x", "x"));

            Assert.False(r.System.IsLinux);
            Assert.Empty(r.Entries);
        }

        [Fact]
        public void OneUnreadableSourceMarksItsEntriesAndDoesNotStopTheScan()
        {
            var b = ReferenceSystem();
            b.Tree.OnRead = f =>
            {
                if (f.Path.EndsWith("avahi-daemon.service"))
                {
                    throw new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.CorruptImage, "bad");
                }
            };

            var r = Run(b);

            Assert.Equal(InactiveReason.Unreadable, r.Entries.Single(e => e.Name == "avahi-daemon.service").InactiveReason);
            Assert.True(r.Entries.Single(e => e.Name == "ssh.service").Active);
            Assert.True(r.Entries.Count(e => e.Active) >= 40);
        }

        [Fact]
        public void SysVScriptsAreListedWhenThereIsNoSystemd()
        {
            var b = new LinuxRootBuilder().BasicLinux().Executable("/etc/init.d/ssh").Link("/etc/rc2.d/S01ssh", "../init.d/ssh").Link("/etc/rc2.d/S02gone", "../init.d/gone");

            var r = Run(b);

            var ssh = r.Entries.Single(e => e.Name == "ssh");
            Assert.Equal(StartupKind.BootScript, ssh.Kind);
            Assert.Equal("/etc/init.d/ssh", ssh.Command);
            Assert.True(ssh.Active);
            Assert.Equal(InactiveReason.UnitMissing, r.Entries.Single(e => e.Name == "gone").InactiveReason);
        }

        [Fact]
        public void SysVScriptsWithAUnitAreNotListedTwice()
        {
            var b = new LinuxRootBuilder().BasicLinux().Systemd().Executable("/etc/init.d/ssh").Link("/etc/rc2.d/S01ssh", "../init.d/ssh");
            b.Unit("ssh.service", "[Service]\nExecStart=/usr/sbin/sshd -D\n").Enable("ssh.service");

            var r = Run(b);

            Assert.Single(r.Entries, e => e.Name == "ssh" || e.Name == "ssh.service");
        }

        [Fact]
        public void TheListIsCutAtTheLimit()
        {
            var b = new LinuxRootBuilder().BasicLinux().Executable("/usr/sbin/cron");
            b.File("/etc/crontab", string.Join("\n", Enumerable.Range(0, StartupInventory.MaxEntries + 20).Select(i => "0 1 * * * root /bin/job" + i)) + "\n");

            var r = Run(b);

            Assert.Equal(StartupInventory.MaxEntries, r.Entries.Count);
            Assert.Contains(r.Notes, n => n.Code == StartupNoteCode.ListCut && n.Args[1] == StartupInventory.MaxEntries.ToString());
        }
    }
}
