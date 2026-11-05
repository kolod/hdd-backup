using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public class CronScanTests
    {
        private const string Crontab =
            "# /etc/crontab\nSHELL=/bin/sh\nPATH=/usr/local/sbin:/usr/local/bin:/sbin:/bin:/usr/sbin:/usr/bin\n\n"
            + "17 *\t* * *\troot    cd / && run-parts --report /etc/cron.hourly\n"
            + "25 6\t* * *\troot\ttest -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.daily; }\n"
            + "47 6\t* * 7\troot\ttest -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.weekly; }\n"
            + "52 6\t1 * *\troot\ttest -x /usr/sbin/anacron || { cd / && run-parts --report /etc/cron.monthly; }\n";

        private static LinuxRootBuilder Root(bool cron = true)
        {
            var b = new LinuxRootBuilder().BasicLinux();
            if (cron)
            {
                b.Executable("/usr/sbin/cron");
            }

            return b;
        }

        private static CronScanResult Scan(LinuxRootBuilder b, params string[] users)
        {
            var set = new HashSet<string>(users.Length == 0 ? new[] { "root", "pi" } : users);
            var r = b.Reader();
            return CronScan.Run(r, new SystemdScan(r), set);
        }

        private static StartupEntry Entry(CronScanResult r, string name) => r.Entries.Single(e => e.Name == name);

        [Fact]
        public void SystemCrontabHasAUserFieldAndEnvironmentLinesAreSkipped()
        {
            var r = Scan(Root().File("/etc/crontab", "SHELL=/bin/sh\nMAILTO = root\n30 4 * * 1 pi /home/pi/weekly.sh --now >/dev/null 2>&1\n"));

            var e = Assert.Single(r.Entries);
            Assert.Equal("weekly.sh", e.Name);
            Assert.Equal("pi", e.User);
            Assert.Equal("/home/pi/weekly.sh --now >/dev/null 2>&1", e.Command);
            Assert.Equal(ScheduleClass.WeeklyOn, e.Schedule.Class);
            Assert.Equal("30 4 * * 1", e.ScheduleText);
            Assert.Equal("/etc/crontab", e.Source);
            Assert.True(e.Active);
            Assert.Equal(StartupKind.Cron, e.Kind);
        }

        [Fact]
        public void CronDDropInsAreListedAndAFileNameWithADotIsIgnored()
        {
            var r = Scan(Root()
                .File("/etc/cron.d/backup", "*/15 * * * * pi /home/pi/backup.sh\n")
                .File("/etc/cron.d/old.bak", "0 1 * * * root /usr/local/bin/x\n")
                .File("/etc/cron.d/e2scrub_all", "30 3 * * 0 root test -e /run/systemd/system || SERVICE_MODE=1 /usr/lib/e2fsprogs/e2scrub_all_cron\n"));

            Assert.True(Entry(r, "backup.sh").Active);
            Assert.Equal(ScheduleClass.EveryNMinutes, Entry(r, "backup.sh").Schedule.Class);
            Assert.Equal(InactiveReason.IgnoredName, Entry(r, "x").InactiveReason);
            var e2 = Entry(r, "e2scrub_all_cron");
            Assert.True(e2.Active);
            Assert.Equal("root", e2.User);
        }

        [Fact]
        public void UserCrontabsUseTheFileNameAsTheUserAndAMissingUserIsInactive()
        {
            var r = Scan(Root()
                .File("/var/spool/cron/crontabs/pi", "# comment\n@reboot /home/pi/start.sh\n0 3 * * mon /home/pi/weekly.sh\n#30 4 * * * /home/pi/off.sh\n")
                .File("/var/spool/cron/crontabs/ghost", "0 5 * * * /home/ghost/job.sh\n"));

            var boot = Entry(r, "start.sh");
            Assert.Equal(ScheduleClass.AtBoot, boot.Schedule.Class);
            Assert.Equal("pi", boot.User);
            Assert.True(boot.Active);
            Assert.True(Entry(r, "weekly.sh").Active);
            Assert.DoesNotContain(r.Entries, e => e.Name == "off.sh");
            var ghost = Entry(r, "job.sh");
            Assert.Equal("ghost", ghost.User);
            Assert.Equal(InactiveReason.NoSuchUser, ghost.InactiveReason);
        }

        [Fact]
        public void RedHatStyleSpoolDirectoryIsReadToo()
        {
            var r = Scan(Root().File("/var/spool/cron/pi", "0 0 * * * /home/pi/nightly.sh\n"));

            Assert.Equal("pi", Entry(r, "nightly.sh").User);
        }

        [Fact]
        public void ShortcutsAreParsed()
        {
            var r = Scan(Root().File("/etc/cron.d/short", "@daily root /usr/bin/a\n@reboot root /usr/bin/b\n@hourly root /usr/bin/c\n"));

            Assert.Equal(ScheduleClass.DailyAt, Entry(r, "a").Schedule.Class);
            Assert.Equal(ScheduleClass.AtBoot, Entry(r, "b").Schedule.Class);
            Assert.Equal(ScheduleClass.HourlyAt, Entry(r, "c").Schedule.Class);
        }

        [Fact]
        public void PeriodicScriptsGetTheScheduleOfTheirRunPartsLine()
        {
            var r = Scan(Root()
                .File("/etc/crontab", Crontab)
                .Executable("/etc/cron.daily/logrotate")
                .Executable("/etc/cron.hourly/fake-hwclock")
                .Executable("/etc/cron.weekly/man-db"));

            var daily = Entry(r, "logrotate");
            Assert.True(daily.Active);
            Assert.Equal(ScheduleClass.DailyAt, daily.Schedule.Class);
            Assert.Equal("06:25", daily.Schedule.Times[0].ToString());
            Assert.Equal("/etc/cron.daily", daily.Source);
            Assert.Equal("/etc/cron.daily/logrotate", daily.Command);
            Assert.Equal("root", daily.User);
            Assert.Equal(ScheduleClass.HourlyAt, Entry(r, "fake-hwclock").Schedule.Class);
            Assert.Equal(17, Entry(r, "fake-hwclock").Schedule.Number);
            Assert.Equal(ScheduleClass.WeeklyOn, Entry(r, "man-db").Schedule.Class);
        }

        [Fact]
        public void TheRunPartsLinesThemselvesAreNotListed()
        {
            var r = Scan(Root().File("/etc/crontab", Crontab));

            Assert.Empty(r.Entries);
        }

        [Fact]
        public void PeriodicScriptsThatRunPartsSkips()
        {
            var r = Scan(Root()
                .File("/etc/crontab", Crontab)
                .Executable("/etc/cron.daily/good")
                .File("/etc/cron.daily/notexec", "#!/bin/sh\n")
                .Executable("/etc/cron.daily/with.dot")
                .Executable("/etc/cron.daily/.placeholder")
                .Dir("/etc/cron.daily/subdir"));

            Assert.True(Entry(r, "good").Active);
            Assert.Equal(InactiveReason.NotExecutable, Entry(r, "notexec").InactiveReason);
            Assert.Equal(InactiveReason.IgnoredName, Entry(r, "with.dot").InactiveReason);
            Assert.DoesNotContain(r.Entries, e => e.Name == ".placeholder" || e.Name == "subdir");
        }

        [Fact]
        public void ALinkToAnExecutableScriptCounts()
        {
            var r = Scan(Root()
                .File("/etc/crontab", Crontab)
                .Executable("/usr/local/bin/tidy")
                .Link("/etc/cron.daily/tidy", "/usr/local/bin/tidy"));

            Assert.True(Entry(r, "tidy").Active);
        }

        [Fact]
        public void ADirectoryNobodyRunsIsInactive()
        {
            var r = Scan(Root().File("/etc/crontab", "17 * * * * root cd / && run-parts --report /etc/cron.hourly\n").Executable("/etc/cron.daily/lonely"));

            Assert.Equal(InactiveReason.NothingRunsIt, Entry(r, "lonely").InactiveReason);
        }

        [Fact]
        public void AnacronTakesOverTheDailyWeeklyAndMonthlyDirectories()
        {
            var r = Scan(Root()
                .Executable("/usr/sbin/anacron")
                .File("/etc/crontab", Crontab)
                .File("/etc/anacrontab", "SHELL=/bin/sh\nRANDOM_DELAY=45\n1\t5\tcron.daily\trun-parts --report /etc/cron.daily\n7\t10\tcron.weekly\trun-parts --report /etc/cron.weekly\n@monthly\t15\tcron.monthly\trun-parts --report /etc/cron.monthly\n1 20 backup-job /home/pi/backup.sh\n")
                .Executable("/etc/cron.daily/logrotate")
                .Executable("/etc/cron.weekly/man-db")
                .Executable("/etc/cron.monthly/cleanup"));

            var daily = Entry(r, "logrotate");
            Assert.Equal(ScheduleClass.EveryNDays, daily.Schedule.Class);
            Assert.Equal(1, daily.Schedule.Number);
            Assert.Equal(7, Entry(r, "man-db").Schedule.Number);
            Assert.Equal(ScheduleClass.Custom, Entry(r, "cleanup").Schedule.Class);
            var job = Entry(r, "backup.sh");
            Assert.Equal("/etc/anacrontab", job.Source);
            Assert.Equal(ScheduleClass.EveryNDays, job.Schedule.Class);
        }

        [Fact]
        public void MalformedLinesAreInactiveAndTheRestIsStillListed()
        {
            var r = Scan(Root().File("/etc/cron.d/mixed", "61 * * * * root /bin/bad\nthis is not cron\n0 1 * * *\n0 2 * * * root /bin/good\n"));

            Assert.Equal(3, r.Entries.Count(e => e.InactiveReason == InactiveReason.Malformed));
            Assert.True(Entry(r, "good").Active);
        }

        [Fact]
        public void WithoutCronEverythingIsInactive()
        {
            var r = Scan(Root(cron: false).File("/etc/crontab", Crontab).File("/etc/cron.d/x", "0 1 * * * root /bin/x\n").Executable("/etc/cron.daily/a"));

            Assert.False(r.Installed);
            Assert.All(r.Entries, e => Assert.Equal(InactiveReason.NoCron, e.InactiveReason));
            Assert.Equal(2, r.Entries.Count);
        }

        [Fact]
        public void CronInstalledButItsServiceNotEnabledIsOnlyANote()
        {
            var b = Root().Systemd().Unit("cron.service", "[Service]\nExecStart=/usr/sbin/cron -f\n[Install]\nWantedBy=multi-user.target\n")
                .File("/etc/cron.d/x", "0 1 * * * root /bin/x\n");

            var r = Scan(b);

            Assert.True(r.ServiceNotEnabled);
            Assert.True(Entry(r, "x").Active);
        }

        [Fact]
        public void CronServiceEnabledGivesNoNote()
        {
            var b = Root().Systemd().Unit("cron.service", "[Service]\nExecStart=/usr/sbin/cron -f\n").Enable("cron.service");

            Assert.False(Scan(b).ServiceNotEnabled);
        }

        [Fact]
        public void ALongCrontabAndNonAsciiCommandsAreHandled()
        {
            var lines = Enumerable.Range(0, 2000).Select(i => "0 " + (i % 24) + " * * * root /usr/bin/job" + i + " ключ-日本語").ToArray();
            var r = Scan(Root().File("/etc/crontab", string.Join("\n", lines) + "\n"));

            Assert.Equal(2000, r.Entries.Count);
            Assert.Equal("/usr/bin/job1999 ключ-日本語", r.Entries.Last().Command);
        }

        [Theory]
        [InlineData("/home/pi/backup.sh --full", "backup.sh")]
        [InlineData("cd / && run-parts x", "run-parts")]
        [InlineData("test -x /usr/sbin/anacron || { cd / && foo; }", "foo")]
        [InlineData("FOO=1 /usr/bin/bar", "bar")]
        [InlineData("/usr/bin/php -q /var/www/cron.php", "php")]
        public void NamesComeFromTheProgram(string command, string name)
        {
            Assert.Equal(name, CronScan.NameOf(command));
        }
    }
}
