using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Tests.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>Feature 011 (research R-6): keys in a file that StrictModes would refuse do not count, and the check says why.</summary>
    public class KeyFileSafetyTests
    {
        /// <summary>Runs the SSH check on a fixture with <paramref name="changes"/> laid over it (the tree as it will be).</summary>
        internal static SshReport Check(string fixture, params EditChange[] changes)
        {
            using (var image = EditImage.Copy(fixture))
            {
                image.Device.Dispose();
                using (var source = BrowseSource.OpenImage(image.Path))
                using (var view = source.OpenFileSystem(source.Browsable.First()))
                {
                    IFileTree tree = changes.Length == 0 ? (IFileTree)view : new PendingTree(view, changes);
                    return SshCheck.Run(new SystemReader(tree), null, CancellationToken.None);
                }
            }
        }

        private static Account Pi(SshReport r) => r.Accounts.Single(a => a.Name == "pi");

        [Fact]
        public void ASafeKeyFileIsOkAndHasNoFinding()
        {
            var report = Check("edit-ssh-ok");

            var pi = Pi(report);
            Assert.Equal(KeyFileSafety.Ok, pi.KeyFileSafety);
            Assert.Equal("/home/pi/.ssh/authorized_keys", pi.KeyFilePath);
            Assert.Equal(1, pi.KeyCount);
            Assert.Equal(LoginAnswer.Yes, pi.CanLogInWithKey);
            Assert.DoesNotContain(report.Findings, f => f.Code == FindingCode.AccountKeyFileUnsafe);
            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
        }

        [Fact]
        public void AKeyFileOpenToOthersAndOwnedByRootIsIgnoredByTheServer()
        {
            var report = Check("edit-ssh-badmodes");

            var pi = Pi(report);
            Assert.Equal(KeyFileSafety.FileUnsafe, pi.KeyFileSafety);
            Assert.Equal("/home/pi/.ssh/authorized_keys", pi.KeyFileProblemPath);
            Assert.Equal(LoginAnswer.No, pi.CanLogInWithKey);
            Assert.Equal(LoginReason.KeyFileUnsafe, pi.KeyReason);
            var finding = report.Findings.Single(f => f.Code == FindingCode.AccountKeyFileUnsafe);
            Assert.Equal(FindingStatus.Problem, finding.Status);
            Assert.Equal("pi", finding.Args[0]);
            // the password still works on this fixture, so the verdict is "password only"
            Assert.Equal(LoginMethod.Password, report.Verdict.Users.Single(u => u.Account == "pi").Method);
        }

        [Fact]
        public void WithoutAPasswordTheUnsafeKeyFileIsTheReasonNobodyCanLogIn()
        {
            var report = Check(
                "edit-ssh-badmodes",
                EditChange.Edit("/etc/ssh/sshd_config", Encoding.UTF8.GetBytes("PasswordAuthentication no\nKbdInteractiveAuthentication no\n"), null));

            var pi = Pi(report);
            Assert.Equal(LoginAnswer.No, pi.CanLogIn);
            Assert.Equal(LoginReason.KeyFileUnsafe, pi.Reason);
            Assert.NotEqual(VerdictCode.Works, report.Verdict.Code);
        }

        [Fact]
        public void StrictModesOffMakesTheKeyCountAgainAndTheFindingOnlyInformation()
        {
            var report = Check(
                "edit-ssh-badmodes",
                EditChange.Edit("/etc/ssh/sshd_config", Encoding.UTF8.GetBytes("StrictModes no\nPasswordAuthentication no\nKbdInteractiveAuthentication no\n"), null));

            var pi = Pi(report);
            Assert.Equal(LoginAnswer.Yes, pi.CanLogInWithKey);
            Assert.Equal(KeyFileSafety.FileUnsafe, pi.KeyFileSafety);
            Assert.Equal(FindingStatus.Info, report.Findings.Single(f => f.Code == FindingCode.AccountKeyFileUnsafe).Status);
            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
        }

        [Theory]
        [InlineData(0x1FF, null, null, KeyFileSafety.HomeWritable)]
        [InlineData(0x1FD, null, null, KeyFileSafety.HomeWritable)]
        [InlineData(0x1ED, 4321u, null, KeyFileSafety.WrongOwner)]
        public void TheHomeFolderIsChecked(int mode, uint? uid, uint? gid, KeyFileSafety expected)
        {
            var report = Check("edit-ssh-ok", EditChange.Attributes("/home/pi", (ushort)mode, uid, gid));
            Assert.Equal(expected, Pi(report).KeyFileSafety);
            Assert.Equal("/home/pi", Pi(report).KeyFileProblemPath);
        }

        [Fact]
        public void TheSshFolderIsCheckedAndNamedBeforeTheHome()
        {
            var report = Check("edit-ssh-ok", EditChange.Attributes("/home/pi/.ssh", 0x1FD, null, null));
            Assert.Equal(KeyFileSafety.SshDirUnsafe, Pi(report).KeyFileSafety);
            Assert.Equal("/home/pi/.ssh", Pi(report).KeyFileProblemPath);
        }

        [Fact]
        public void RootOwnershipIsAcceptedLikeTheServerDoes()
        {
            var report = Check("edit-ssh-ok", EditChange.Attributes("/home/pi/.ssh/authorized_keys", null, 0, 0));
            Assert.Equal(KeyFileSafety.Ok, Pi(report).KeyFileSafety);
        }

        [Fact]
        public void ACustomKeyFilePatternGivesTheFileTheServerReads()
        {
            var report = Check(
                "edit-ssh-ok",
                EditChange.Edit("/etc/ssh/sshd_config", Encoding.UTF8.GetBytes("AuthorizedKeysFile %h/keys/%u.pub\n"), null));

            var pi = Pi(report);
            Assert.Equal("/home/pi/keys/pi.pub", pi.KeyFilePath);
            Assert.Equal(0, pi.KeyCount);
        }

        [Fact]
        public void StrictModesIsReadFromTheConfiguration()
        {
            Assert.True(Check("edit-ssh-ok").Settings.StrictModes.Value);
            var off = Check("edit-ssh-ok", EditChange.Edit("/etc/ssh/sshd_config", Encoding.UTF8.GetBytes("StrictModes no\n"), null));
            Assert.False(off.Settings.StrictModes.Value);
            Assert.Equal("/etc/ssh/sshd_config", off.Settings.StrictModes.Source);
            Assert.Equal(1, off.Settings.StrictModes.Line);
        }
    }
}
