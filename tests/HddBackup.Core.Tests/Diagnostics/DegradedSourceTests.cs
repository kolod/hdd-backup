using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>User Story 4: sources that are not what the tools expect give honest, documented results.</summary>
    public class DegradedSourceTests
    {
        private static DiagnosticsSource Of(string fixture)
        {
            string image = FixtureImages.RawPath(fixture);
            int number;
            using (var s = BrowseSource.OpenImage(image))
            {
                number = s.Browsable.First().Number;
            }

            return new DiagnosticsSource { ImagePath = image, PartitionNumber = number, DisplayName = fixture };
        }

        private static T Run<T>(string fixture, System.Func<SystemReader, DiagnosticsSource, T> tool)
        {
            return DiagnosticsRunner.Run(null, Of(fixture), tool, CancellationToken.None);
        }

        [Theory]
        [InlineData("browse-large")]
        public void ADataOnlyFileSystemIsNotALinuxRootForBothTools(string fixture)
        {
            var ssh = Run(fixture, (r, s) => SshCheck.Run(r, s, CancellationToken.None));
            var startup = Run(fixture, (r, s) => StartupInventory.Run(r, s, CancellationToken.None));

            Assert.Equal(VerdictCode.NotALinuxRoot, ssh.Verdict.Code);
            Assert.Empty(ssh.Findings);
            Assert.Empty(ssh.Accounts);
            Assert.False(startup.System.IsLinux);
            Assert.Empty(startup.Entries);
            Assert.Empty(startup.Notes);
        }

        [Fact]
        public void AnEmptyFileSystemIsNotALinuxRoot()
        {
            var b = new LinuxRootBuilder();

            Assert.Equal(VerdictCode.NotALinuxRoot, SshCheck.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None).Verdict.Code);
            Assert.Empty(StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None).Entries);
        }

        [Fact]
        public void ANotCleanlyUnmountedImageKeepsItsStateInBothResultsAndStillWorks()
        {
            var ssh = Run("dirty-journal", (r, s) => SshCheck.Run(r, s, CancellationToken.None));
            var startup = Run("dirty-journal", (r, s) => StartupInventory.Run(r, s, CancellationToken.None));

            Assert.NotNull(ssh.Source.State);
            Assert.Equal(FileSystemCondition.NeedsRepair, ssh.Source.State.Condition);
            Assert.Equal(FileSystemCondition.NeedsRepair, startup.Source.State.Condition);
            // The tools read the on-disk content as it is: this image has /etc/passwd and /usr/bin but no SSH server.
            Assert.True(ssh.System.IsLinux);
            Assert.Equal(VerdictCode.NoServer, ssh.Verdict.Code);
        }

        [Fact]
        public void AGarbledShadowFileIsAnUnreadableNoteAndTheVerdictIsUndeterminedWhenItMatters()
        {
            var noKey = new SshCheckMatrixTests.Case { Shadow = false, Tweak = b => b.Shadow("garbage", "###", "x") };
            var withKey = new SshCheckMatrixTests.Case { Shadow = false, PiKey = true, Tweak = b => b.Shadow("garbage", "###", "x") };

            var a = SshCheck.Run(SshCheckMatrixTests.Build(noKey).Reader(), new DiagnosticsSource(), CancellationToken.None);
            var b2 = SshCheck.Run(SshCheckMatrixTests.Build(withKey).Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.Equal(VerdictCode.Undetermined, a.Verdict.Code);
            Assert.Contains(a.Verdict.Notes, n => n.Code == FindingCode.ShadowUnreadable);
            Assert.Equal(VerdictCode.Works, b2.Verdict.Code);
            Assert.Contains(b2.Verdict.Notes, n => n.Code == FindingCode.ShadowUnreadable);
        }

        [Fact]
        public void AMissingSshdConfigGivesDefaultsWithTheirSourceAndANote()
        {
            var b = new LinuxRootBuilder().BasicLinux("debian", null, passwd: false).Systemd();
            b.Unit("ssh.service", "[Service]\nExecStart=/usr/sbin/sshd -D\n").Enable("ssh.service").Executable("/usr/sbin/sshd");
            b.Passwd("root:x:0:0::/root:/bin/bash", "pi:x:1000:1000::/home/pi:/bin/bash").Shadow("root:*:1:::::", "pi:$6$PLANTEDSECRET$h:1:::::");

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.False(report.Settings.ConfigFound);
            Assert.True(report.Settings.PasswordAuthentication.IsDefault);
            Assert.Contains(report.Verdict.Notes, n => n.Code == FindingCode.ConfigMissing);
            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
            Assert.Equal("pi", report.Verdict.Users.Single().Account);
        }

        [Fact]
        public void UnreadableKeyFilesAndUnitFilesAreNotesNotExceptions()
        {
            var b = SshCheckMatrixTests.Build(new SshCheckMatrixTests.Case { PiKey = true });
            b.Tree.OnRead = f =>
            {
                if (f.Path.EndsWith("authorized_keys") || f.Path.EndsWith("ssh.service"))
                {
                    throw new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.CorruptImage, "bad");
                }
            };

            var report = SshCheck.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);
            var startup = StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);

            Assert.NotNull(report.Verdict);
            Assert.Contains("/home/pi/.ssh/authorized_keys", report.ReadNotes.UnreadableFiles);
            Assert.NotEmpty(startup.Entries);
        }

        [Fact]
        public void ADeviceThatFailsWhileAToolRunsAbortsWithTheCauseAndReleasesTheSource()
        {
            var raw = new ReadOnlyBlockDevice(HddBackup.Core.Devices.FileBlockDevice.OpenRead(FixtureImages.RawPath("diag-startup")));
            var access = new FakeDeviceAccess();
            var device = access.Add(3, raw);
            int number;
            using (var s = BrowseSource.OpenDevice(access, device))
            {
                number = s.Browsable.First().Number;
            }

            raw.FailAfterReads = raw.ReadCalls + 25;
            raw.FailWith = new HddBackup.Core.Errors.ImagingException(HddBackup.Core.Errors.ErrorCode.DeviceRemoved, "unplugged");
            var source = new DiagnosticsSource { Device = device, PartitionNumber = number };

            var ex = Assert.Throws<HddBackup.Core.Errors.ImagingException>(() =>
                DiagnosticsRunner.Run(access, source, (r, s) => StartupInventory.Run(r, s, CancellationToken.None), CancellationToken.None));

            Assert.Equal(HddBackup.Core.Errors.ErrorCode.DeviceRemoved, ex.Code);
            Assert.Equal(access.ReadOpens, access.ReadReleases);
            Assert.Equal(0, raw.WriteCalls);
            raw.Dispose();
        }
    }
}
