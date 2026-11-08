using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Shrinking;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>The copied or saved text: the same facts as the window, a source, a date and the notes, and no secret.</summary>
    public class ReportBuilderTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 5, 12, 30, 0);

        /// <summary>Texts that show their codes, so the report can be compared exactly and is independent of any language.</summary>
        internal sealed class CodeTexts : IReportTexts
        {
            public string SshTitle(string source) => "SSH|" + source;

            public string StartupTitle(string source) => "STARTUP|" + source;

            public string DateLine(DateTime now) => "DATE|" + now.ToString("yyyy-MM-dd HH:mm");

            public string StateWarning(FileSystemState state) => state.Condition == FileSystemCondition.Clean ? null : "WARN|" + state.Condition;

            public string StepTitle(int page) => "STEP" + page;

            public string Glyph(FindingStatus status) => "[" + status + "]";

            public string Finding(Finding f) => "F:" + f.Code + "(" + string.Join(",", f.Args) + ")" + (f.Reason == LoginReason.None ? string.Empty : "/" + f.Reason);

            public string Where(EvidenceRef e) => e.IsDefault ? "default" : e.Path + (e.Line > 0 ? ":" + e.Line : string.Empty);

            public string Verdict(Verdict v) => "V:" + v.Code + "(" + string.Join(",", v.Users.Select(u => u.Account + "+" + u.Method)) + ")";

            public string WhyCaption => "WHY";

            public string NotesCaption => "NOTES";

            public string AccountLine(Account a) => a.Name + "\t" + a.CanLogIn;

            public string NotLinux => "NOTLINUX";

            public string Empty => "EMPTY";

            public IReadOnlyList<string> StartupColumns => new[] { "KIND", "NAME", "RUNS", "USER", "SOURCE", "STATUS" };

            public IReadOnlyList<string> StartupRow(StartupEntry e) => new[] { e.Kind.ToString(), e.Name, e.Command, e.User, e.Source, e.Active ? "active" : e.InactiveReason.ToString() };

            public string StartupNote(StartupNote n) => "NOTE:" + n.Code;

            public string Truncated(string path) => "TRUNC:" + path;

            public string Unreadable(string path) => "UNREAD:" + path;
        }

        private static SshReport Ssh(SshCheckMatrixTests.Case c, DiagnosticsSource source = null)
        {
            return SshCheck.Run(SshCheckMatrixTests.Build(c).Reader(), source ?? new DiagnosticsSource { DisplayName = "pi.img" }, CancellationToken.None);
        }

        [Fact]
        public void SshReportHasTheHeaderTheVerdictThePagesAndTheAccounts()
        {
            var report = Ssh(new SshCheckMatrixTests.Case { PiKey = true });

            string text = ReportBuilder.Ssh(report, new CodeTexts(), Now);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("SSH|pi.img", lines[0]);
            Assert.Equal("DATE|2026-11-05 12:30", lines[1]);
            Assert.Equal(string.Empty, lines[2]);
            Assert.Equal("[Ok] V:Works(pi+Both)", lines[3]);
            Assert.Contains("STEP1", lines);
            Assert.Contains("STEP2", lines);
            Assert.Contains("STEP3", lines);
            Assert.Contains("  [Ok] F:ServerEnabled(ssh.service)", lines);
            Assert.Contains("      /etc/systemd/system/multi-user.target.wants/ssh.service", lines);
            Assert.Contains("  [Info] F:Port(22)", lines);
            Assert.Contains("      default", lines);
            Assert.Contains("  pi\tYes", lines);
            Assert.Contains("  root\tNo", lines);
            Assert.EndsWith("\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        }

        [Fact]
        public void TheReasonsAndTheNotesAreInTheReport()
        {
            var report = Ssh(new SshCheckMatrixTests.Case { Config = "AuthorizedKeysCommand /bin/keys\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n", PiShadow = "!x" });

            string text = ReportBuilder.Ssh(report, new CodeTexts(), Now);

            Assert.Contains("V:Undetermined", text);
            Assert.Contains("WHY\r\n  [Problem] F:AccountCannot(pi)/LockedRefused", text);
            Assert.Contains("NOTES\r\n  [Unknown] F:KeysFromCommand()", text);
        }

        [Fact]
        public void ANotCleanSourceCarriesTheWarning()
        {
            var source = new DiagnosticsSource { DisplayName = "x.img", State = new FileSystemState { Condition = FileSystemCondition.NeedsRepair } };

            string text = ReportBuilder.Ssh(Ssh(new SshCheckMatrixTests.Case(), source), new CodeTexts(), Now);

            Assert.Contains("WARN|NeedsRepair", text.Split(new[] { "\r\n" }, StringSplitOptions.None).Take(4));
            string clean = ReportBuilder.Ssh(Ssh(new SshCheckMatrixTests.Case(), new DiagnosticsSource { State = new FileSystemState() }), new CodeTexts(), Now);
            Assert.DoesNotContain("WARN", clean);
        }

        [Fact]
        public void ANonLinuxRootGivesOnlyTheHeaderAndTheMessage()
        {
            string text = ReportBuilder.Ssh(Ssh(new SshCheckMatrixTests.Case { NotLinux = true }), new CodeTexts(), Now);

            Assert.Equal("SSH|pi.img\r\nDATE|2026-11-05 12:30\r\n\r\nNOTLINUX\r\n", text);
        }

        [Fact]
        public void ReadNotesAppearAsNotes()
        {
            var report = Ssh(new SshCheckMatrixTests.Case { PiKey = true, Config = "Port 2222\n" + new string('#', 1100000) });

            string text = ReportBuilder.Ssh(report, new CodeTexts(), Now);

            Assert.Contains("TRUNC:/etc/ssh/sshd_config", text);
        }

        [Fact]
        public void StartupReportHasColumnsRowsCountsAndNotes()
        {
            var b = StartupInventoryTests.ReferenceSystem();
            var r = StartupInventory.Run(b.Reader(), new DiagnosticsSource { DisplayName = "ref.img" }, CancellationToken.None);
            var shown = r.Entries.Where(e => e.Active && e.Kind == StartupKind.Cron).ToList();

            string text = ReportBuilder.Startup(r, shown, "9 of 42 entries", new CodeTexts(), Now);
            var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);

            Assert.Equal("STARTUP|ref.img", lines[0]);
            Assert.Equal("DATE|2026-11-05 12:30", lines[1]);
            Assert.Equal("KIND\tNAME\tRUNS\tUSER\tSOURCE\tSTATUS", lines[3]);
            Assert.Equal(shown.Count, lines.Skip(4).TakeWhile(l => l.Length > 0).Count());
            Assert.Contains("Cron\tbackup.sh\t/home/pi/backup.sh\tpi\t/etc/cron.d/backup\tactive", lines);
            Assert.Contains("9 of 42 entries", lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("Service\t"));
        }

        [Fact]
        public void StartupReportOfAnEmptyListAndNotes()
        {
            var b = new HddBackup.Core.Tests.Fakes.LinuxRootBuilder().BasicLinux();
            var r = StartupInventory.Run(b.Reader(), new DiagnosticsSource { DisplayName = "x" }, CancellationToken.None);

            string text = ReportBuilder.Startup(r, new StartupEntry[0], "0 entries", new CodeTexts(), Now);

            Assert.Contains("EMPTY", text);
            Assert.Contains("NOTE:NoCron", text);
        }

        [Fact]
        public void TabsAndLineBreaksInsideAValueDoNotBreakTheTable()
        {
            var entry = new StartupEntry { Kind = StartupKind.Cron, Name = "a\tb", Command = "x\ny", Source = "/s" };
            var r = new StartupResult(new DiagnosticsSource { DisplayName = "q" }) { Entries = new[] { entry } };

            string text = ReportBuilder.Startup(r, r.Entries, "1 entry", new CodeTexts(), Now);

            Assert.Contains("Cron\ta b\tx y\t\t/s\tactive", text);
        }

        [Theory]
        [InlineData("diag-ssh-ok")]
        [InlineData("diag-ssh-keys-only")]
        [InlineData("diag-ssh-disabled")]
        [InlineData("diag-startup")]
        [InlineData("diag-pi-like")]
        public void NoReportOfAFixtureContainsASecret(string fixture)
        {
            string ssh = ReportBuilder.Ssh(SshFixtureTests.Check(fixture), new CodeTexts(), Now);
            var startup = StartupFixtureTests.Run(fixture);
            string list = ReportBuilder.Startup(startup, startup.Entries, "all", new CodeTexts(), Now);

            foreach (string secret in new[] { "PLANTEDSECRET", "PLANTEDKEYBLOB", "PLANTEDPRIVATEKEY", "BEGIN OPENSSH PRIVATE KEY", "$6$", "AAAAC3NzaC1lZDI1NTE5" })
            {
                Assert.DoesNotContain(secret, ssh);
                Assert.DoesNotContain(secret, list);
            }
        }

        [Fact]
        public void TheSameFactsComeOutInAnyCultureOfTheTexts()
        {
            var report = Ssh(new SshCheckMatrixTests.Case { PiKey = true });

            string a = ReportBuilder.Ssh(report, new CodeTexts(), Now);
            string b = ReportBuilder.Ssh(report, new CodeTexts(), Now.AddDays(1));

            // Only the date line differs between two runs of the same result.
            Assert.Equal(a.Replace("2026-11-05", "X"), b.Replace("2026-11-06", "X"));
        }
    }
}
