using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;
using HddBackup.Core.Tests.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>
    /// Feature 011, SC-002 / SC-004: for each broken image the repair is planned, queued, saved, and the SSH check run on the
    /// saved image says "should work"; the image passes the product's checker (and, in CI, e2fsck) and only the expected entries differ.
    /// </summary>
    public class SshRepairEndToEndTests
    {
        private static string Copy(string fixture)
        {
            var image = EditImage.Copy(fixture);
            image.Device.Dispose();
            return image.Path;
        }

        private static Dictionary<string, TreeEntry> Snapshot(string path)
        {
            using (var device = FileBlockDevice.OpenRead(path))
            {
                return EditAssert.Snapshot(device);
            }
        }

        private static PublicKey Key()
        {
            Assert.True(PublicKey.TryParse(PublicKeyTests.Ed25519, out var key, out _));
            return key;
        }

        private static void ShouldWork(string image, string account)
        {
            var report = RepairHarness.Check(image);
            Assert.Equal(VerdictCode.Works, report.Verdict.Code);
            Assert.Contains(report.Verdict.Users, u => u.Account == account);
        }

        [Fact]
        public void ADisabledServerIsEnabledByTheLinksAndTheVerdictTurnsToWorks()
        {
            string image = Copy("edit-ssh-disabled");
            var before = Snapshot(image);
            Assert.Equal(VerdictCode.NotEnabled, RepairHarness.Check(image).Verdict.Code);

            var after = RepairHarness.ApplyAndSave(image, "ssh-enable", (r, rep) => SshRepairPlanner.PlanEnableServer(r, rep));

            Assert.Equal('L', after["/etc/systemd/system/multi-user.target.wants/ssh.service"].Type);
            Assert.Equal("/lib/systemd/system/ssh.service", after["/etc/systemd/system/multi-user.target.wants/ssh.service"].Content);
            Assert.Equal((ushort)0xA1FF, after["/etc/systemd/system/sshd.service"].Mode);
            EditAssert.TreesEqualExcept(before, after, "/etc/systemd/system", "/etc/systemd/system/multi-user.target.wants", "/etc/systemd/system/multi-user.target.wants/ssh.service", "/etc/systemd/system/sshd.service");
            ShouldWork(image, "pi");
        }

        [Fact]
        public void AMaskedServerIsUnmaskedAndEnabled()
        {
            string image = Copy("edit-ssh-masked");
            var before = Snapshot(image);

            var after = RepairHarness.ApplyAndSave(image, "ssh-unmask", (r, rep) => SshRepairPlanner.PlanUnmaskServer(r, rep));

            Assert.False(after.ContainsKey("/etc/systemd/system/ssh.service") && after["/etc/systemd/system/ssh.service"].Content == "/dev/null");
            EditAssert.TreesEqualExcept(before, after, "/etc/systemd/system", "/etc/systemd/system/ssh.service", "/etc/systemd/system/sshd.service", "/etc/systemd/system/multi-user.target.wants", "/etc/systemd/system/multi-user.target.wants/ssh.service");
            ShouldWork(image, "pi");
        }

        [Fact]
        public void AnAllowedUsersListGetsTheAccountAndNothingElseChanges()
        {
            string image = Copy("edit-ssh-allowusers");
            var before = Snapshot(image);
            Assert.NotEqual(VerdictCode.Works, RepairHarness.Check(image).Verdict.Code == VerdictCode.Works ? VerdictCode.NotEnabled : VerdictCode.Works);

            var after = RepairHarness.ApplyAndSave(image, "ssh-allowuser", (r, rep) => SshRepairPlanner.PlanAllowUser(r, rep, rep.Accounts.Single(a => a.Name == "pi"), RepairHarness.Today));

            EditAssert.TreesEqualExcept(before, after, "/etc/ssh/sshd_config", "/etc/ssh");
            Assert.Contains("AllowUsers alice\nAllowUsers pi\n", RepairHarness.Text(image, "/etc/ssh/sshd_config"));
            var report = RepairHarness.Check(image);
            Assert.Contains(report.Verdict.Users, u => u.Account == "pi");
        }

        [Fact]
        public void AnAllowedGroupsListGetsTheAccountsGroup()
        {
            string image = Copy("edit-ssh-allowgroups");
            var after = RepairHarness.ApplyAndSave(image, "ssh-allowgroup", (r, rep) => SshRepairPlanner.PlanAllowGroup(r, rep, rep.Accounts.Single(a => a.Name == "pi"), RepairHarness.Today));

            Assert.True(after.ContainsKey("/etc/ssh/sshd_config"));
            Assert.Contains(RepairHarness.Check(image).Verdict.Users, u => u.Account == "pi");
        }

        [Fact]
        public void ANoLoginShellIsReplacedAndTheAccountCanLogIn()
        {
            string image = Copy("edit-ssh-badshell");
            var before = Snapshot(image);
            var after = RepairHarness.ApplyAndSave(image, "ssh-shell", (r, rep) => SshRepairPlanner.PlanFixShell(r, rep.Accounts.Single(a => a.Name == "pi")));

            EditAssert.TreesEqualExcept(before, after, "/etc/passwd", "/etc");
            Assert.Contains("pi:x:1000:1000:pi:/home/pi:/bin/bash", RepairHarness.Text(image, "/etc/passwd"));
            ShouldWork(image, "pi");
        }

        [Fact]
        public void DisabledKeyLoginIsSwitchedOnAndTheKeyWorks()
        {
            string image = Copy("edit-ssh-dropin");
            var before = Snapshot(image);
            var after = RepairHarness.ApplyAndSave(image, "ssh-keyauth", (r, rep) => SshRepairPlanner.PlanEnableKeyAuth(r, rep, RepairHarness.Today));

            EditAssert.TreesEqualExcept(before, after, "/etc/ssh/sshd_config", "/etc/ssh");
            string text = RepairHarness.Text(image, "/etc/ssh/sshd_config");
            Assert.Contains("# hdd-backup 2026-11-08: PubkeyAuthentication no\nPubkeyAuthentication yes\n", text);
            Assert.Contains("Match Address 10.0.0.0/8\n    PasswordAuthentication no", text);
            ShouldWork(image, "pi");
        }

        [Fact]
        public void UnsafeModesAreFixedTheContentIsUntouchedAndTheKeyCountsAgain()
        {
            string image = Copy("edit-ssh-badmodes");
            var before = Snapshot(image);
            string keys = RepairHarness.Text(image, "/home/pi/.ssh/authorized_keys");

            var after = RepairHarness.ApplyAndSave(image, "ssh-modes", (r, rep) => SshRepairPlanner.PlanFixKeyFileModes(r, rep.Accounts.Single(a => a.Name == "pi")));

            Assert.Equal((ushort)0x8180, after["/home/pi/.ssh/authorized_keys"].Mode);
            Assert.Equal((ushort)0x41ED, after["/home/pi"].Mode);
            Assert.Equal(before["/home/pi/.ssh/authorized_keys"].Content, after["/home/pi/.ssh/authorized_keys"].Content);
            Assert.Equal(keys, RepairHarness.Text(image, "/home/pi/.ssh/authorized_keys"));
            EditAssert.TreesEqualExcept(before, after, "/home/pi", "/home/pi/.ssh/authorized_keys");
            var report = RepairHarness.Check(image);
            var pi = report.Accounts.Single(a => a.Name == "pi");
            Assert.Equal(KeyFileSafety.Ok, pi.KeyFileSafety);
            Assert.Equal(LoginAnswer.Yes, pi.CanLogInWithKey);
            Assert.DoesNotContain(report.Findings, f => f.Code == FindingCode.AccountKeyFileUnsafe);
        }

        [Fact]
        public void AKeyForAnAccountWithoutAFolderRestoresSshAccess()
        {
            string image = Copy("edit-ssh-nokey");
            var before = Snapshot(image);
            Assert.Equal(LoginAnswer.No, RepairHarness.Check(image).Accounts.Single(a => a.Name == "pi").CanLogIn);
            var key = Key();

            var after = RepairHarness.ApplyAndSave(image, "ssh-addkey", (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key));

            EditAssert.TreesEqualExcept(before, after, "/home/pi", "/home/pi/.ssh", "/home/pi/.ssh/authorized_keys");
            Assert.Equal((ushort)0x41C0, after["/home/pi/.ssh"].Mode);
            Assert.Equal(1000u, after["/home/pi/.ssh"].Uid);
            Assert.Equal(1000u, after["/home/pi/.ssh"].Gid);
            Assert.Equal((ushort)0x8180, after["/home/pi/.ssh/authorized_keys"].Mode);
            Assert.Equal(1000u, after["/home/pi/.ssh/authorized_keys"].Uid);
            Assert.Equal(key.Line + "\n", RepairHarness.Text(image, "/home/pi/.ssh/authorized_keys"));
            Assert.Contains(RepairHarness.Check(image).Verdict.Users, u => u.Account == "pi" && u.Method == LoginMethod.Key);
        }

        [Fact]
        public void AKeyAppendedToAnExistingFileLeavesTheOldKeysAndTheModeAlone()
        {
            string image = Copy("edit-ssh-nokey");
            var before = Snapshot(image);
            string old = RepairHarness.Text(image, "/home/bob/.ssh/authorized_keys");
            var key = Key();

            var after = RepairHarness.ApplyAndSave(image, "ssh-appendkey", (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "bob", key));

            EditAssert.TreesEqualExcept(before, after, "/home/bob/.ssh/authorized_keys");
            Assert.Equal(old + "\n" + key.Line + "\n", RepairHarness.Text(image, "/home/bob/.ssh/authorized_keys"));
            Assert.Equal(before["/home/bob/.ssh/authorized_keys"].Mode, after["/home/bob/.ssh/authorized_keys"].Mode);
            Assert.Equal(before["/home/bob/.ssh/authorized_keys"].Uid, after["/home/bob/.ssh/authorized_keys"].Uid);
        }

        [Fact]
        public void TwoRepairsInARowBuildOnEachOther()
        {
            string image = Copy("edit-ssh-nokey");
            var key = Key();

            RepairHarness.ApplyAndSave(
                image,
                "ssh-two-keys",
                (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key),
                (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "bob", key));

            Assert.Equal(key.Line + "\n", RepairHarness.Text(image, "/home/pi/.ssh/authorized_keys"));
            Assert.EndsWith("\n" + key.Line + "\n", RepairHarness.Text(image, "/home/bob/.ssh/authorized_keys"));
        }

        [Fact]
        public void TheImageIsNotTouchedUntilSaveAndUndoLeavesExactlyTheOtherRepair()
        {
            string image = Copy("edit-ssh-disabled");
            string hash = Sha(image);
            var key = Key();

            using (var session = ImageEditSession.Open(image))
            {
                var enable = RepairHarness.Read(image, session.Changes, (r, rep) => SshRepairPlanner.PlanEnableServer(r, rep));
                int g1 = session.AddRepair(enable.Changes, enable.Title);
                var add = RepairHarness.Read(image, session.Changes, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key));
                Assert.True(add.CanApply, add.Unavailable.ToString());
                int g2 = session.AddRepair(add.Changes, add.Title);
                Assert.Equal(hash, Sha(image));

                // the wizard reads the file system as it will be: with both repairs waiting the server is enabled
                var seen = RepairHarness.Check(image, session.Changes);
                Assert.Equal(ServerEnabledState.Yes, seen.Server.Enabled);
                Assert.Equal(2, seen.Accounts.Single(a => a.Name == "pi").KeyCount);

                Assert.Equal(UndoStatus.Done, session.Undo(g2).Status);
                Assert.Equal(hash, Sha(image));
                Assert.Equal(g1, session.Groups.Single().Group);
                Assert.Equal(SaveStatus.Saved, session.Save(null, CancellationToken.None).Status);
            }

            Assert.NotEqual(hash, Sha(image));
            Assert.Equal(ServerEnabledState.Yes, RepairHarness.Check(image).Server.Enabled);
            Assert.DoesNotContain(PublicKeyTests.Ed25519.Split(' ')[1], RepairHarness.Text(image, "/home/pi/.ssh/authorized_keys"));
        }

        [Fact]
        public void ARefusedRepairLeavesTheImageAndTheWaitingListAsTheyWere()
        {
            string image = Copy("edit-ssh-ok");
            string hash = Sha(image);
            using (var session = ImageEditSession.Open(image))
            {
                var bad = new[] { EditChange.NewSymlink("/etc/new-link", new string('x', 70)) };
                Assert.Throws<EditException>(() => session.AddRepair(bad, new RepairTitle(RepairKind.EnableServer, "x")));
                Assert.Empty(session.Changes);
            }

            Assert.Equal(hash, Sha(image));
        }

        private static string Sha(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return BitConverter.ToString(sha.ComputeHash(f));
            }
        }
    }
}
