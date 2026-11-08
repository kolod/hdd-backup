using System.Linq;
using System.Text;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;
using HddBackup.Core.Tests.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Feature 011 (research R-5): each reason of the SSH check has the repair the plan promises, or an honest refusal.</summary>
    public class SshRepairPlannerTests
    {
        private static PublicKey Key(string line = PublicKeyTests.Ed25519)
        {
            Assert.True(PublicKey.TryParse(line, out var key, out var why), why.ToString());
            return key;
        }

        /// <summary>A private copy of the fixture that no handle holds open (the copy lives in the test run's temp folder).</summary>
        private static string Path(string fixture)
        {
            var image = EditImage.Copy(fixture);
            image.Device.Dispose();
            return image.Path;
        }

        private static byte[] Latin1(string s) => Encoding.GetEncoding("ISO-8859-1").GetBytes(s);

        private static string Latin1(byte[] b) => Encoding.GetEncoding("ISO-8859-1").GetString(b);

        // ------------------------------------------------------------------------- adding a key

        [Fact]
        public void AKeyForAnAccountWithoutASshFolderCreatesTheFolderAndTheFileInSafeOrder()
        {
            string image = Path("edit-ssh-nokey");
            var key = Key();
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key));

            Assert.True(plan.CanApply);
            Assert.Equal(RepairKind.AddKey, plan.Kind);
            Assert.Equal("pi", plan.Title.Arg1);
            Assert.Equal(key.Fingerprint, plan.Title.Arg2);
            Assert.Equal(2, plan.Changes.Count);
            var dir = plan.Changes[0];
            Assert.Equal(EditKind.CreateDirectory, dir.Kind);
            Assert.Equal("/home/pi/.ssh", dir.Path);
            Assert.Equal((ushort)0x1C0, dir.Mode);
            Assert.Equal(1000u, dir.Uid);
            Assert.Equal(1000u, dir.Gid);
            var file = plan.Changes[1];
            Assert.Equal(EditKind.CreateFile, file.Kind);
            Assert.Equal("/home/pi/.ssh/authorized_keys", file.Path);
            Assert.Equal((ushort)0x180, file.Mode);
            Assert.Equal(key.Line + "\n", Encoding.ASCII.GetString(file.Content));
            Assert.Equal(RepairRisk.None, plan.Risk);
            Assert.Contains(plan.Summary, l => l.What == ChangeWhat.AppendKey && l.After == key.Fingerprint);
        }

        [Fact]
        public void TheSummaryNeverHoldsTheKeyBody()
        {
            string image = Path("edit-ssh-nokey");
            var key = Key();
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key));
            string body = key.Line.Split(' ')[1];
            foreach (var line in plan.Summary)
            {
                Assert.DoesNotContain(body, line.Path + line.Before + line.After);
            }

            Assert.DoesNotContain(body, plan.Title.Arg1 + plan.Title.Arg2);
        }

        [Fact]
        public void AKeyIsAppendedAfterAMissingFinalLineBreakAndTheOldBytesStay()
        {
            string image = Path("edit-ssh-nokey");
            var key = Key();
            byte[] old = RepairHarness.Bytes(image, "/home/bob/.ssh/authorized_keys");
            Assert.NotEqual((byte)'\n', old.Last());

            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "bob", key));

            var change = plan.Changes.Single();
            Assert.Equal(EditKind.EditContent, change.Kind);
            Assert.Equal(old, change.Content.Take(old.Length).ToArray());
            Assert.Equal("\n" + key.Line + "\n", Latin1(change.Content.Skip(old.Length).ToArray()));
        }

        [Fact]
        public void AKeyIsAppendedWithTheLineEndingOfAFileThatUsesCrlf()
        {
            string image = Path("edit-ssh-nokey");
            var key = Key();
            byte[] old = RepairHarness.Bytes(image, "/home/eve/.ssh/authorized_keys");
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "eve", key));

            var change = plan.Changes.Single();
            Assert.Equal(old, change.Content.Take(old.Length).ToArray());
            Assert.Equal(key.Line + "\r\n", Latin1(change.Content.Skip(old.Length).ToArray()));
        }

        [Fact]
        public void TheSameKeyTwiceIsRefusedWhateverItsCommentOrOptions()
        {
            string image = Path("edit-ssh-nokey");
            var key = Key();
            var first = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "bob", key));
            string again = "no-pty " + PublicKeyTests.Ed25519.Replace("ed@host", "another name");
            var existing = first.Changes.Single();

            // the key is already in the file once the first repair is waiting
            var second = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "bob", Key(again)), existing);
            Assert.Equal(RepairRefusal.Duplicate, second.Unavailable);
            Assert.False(second.CanApply);
        }

        [Fact]
        public void AKeyIsRefusedWhenTheKeysComeOnlyFromACommand()
        {
            string image = Path("edit-ssh-ok");
            var only = EditChange.Edit("/etc/ssh/sshd_config", Encoding.ASCII.GetBytes("AuthorizedKeysFile none\nAuthorizedKeysCommand /usr/local/bin/keys %u\n"), null);
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", Key()), only);
            Assert.Equal(RepairRefusal.KeysFromCommand, plan.Unavailable);
        }

        [Fact]
        public void ACustomKeyFilePatternDecidesWhereTheKeyGoes()
        {
            string image = Path("edit-ssh-ok");
            var custom = EditChange.Edit("/etc/ssh/sshd_config", Encoding.ASCII.GetBytes("AuthorizedKeysFile %h/keys/%u.pub\n"), null);
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", Key()), custom);

            Assert.True(plan.CanApply);
            Assert.Equal("/home/pi/keys", plan.Changes[0].Path);
            Assert.Equal("/home/pi/keys/pi.pub", plan.Changes[1].Path);
        }

        [Fact]
        public void AnAccountWithoutAHomeFolderGetsNoKey()
        {
            string image = Path("edit-ssh-ok");
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", Key()), EditChange.Remove("/home/pi", null));
            Assert.Equal(RepairRefusal.NoHome, plan.Unavailable);
        }

        [Fact]
        public void AKeyFileThatIsALinkLeavingTheFileSystemIsRefused()
        {
            string image = Path("edit-ssh-ok");
            var plan = RepairHarness.Plan(
                image,
                (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", Key()),
                EditChange.Remove("/home/pi/.ssh/authorized_keys", null),
                EditChange.NewSymlink("/home/pi/.ssh/authorized_keys", "/mnt/usb/keys"));
            Assert.Equal(RepairRefusal.OutsideFileSystem, plan.Unavailable);
        }

        [Fact]
        public void AKeyFileThatIsALinkInsideTheFileSystemIsFollowed()
        {
            string image = Path("edit-ssh-ok");
            var plan = RepairHarness.Plan(
                image,
                (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", Key()),
                EditChange.Remove("/home/pi/.ssh/authorized_keys", null),
                EditChange.NewFile("/home/pi/real_keys", Encoding.ASCII.GetBytes("# real\n"), 0x180, 1000, 1000),
                EditChange.NewSymlink("/home/pi/.ssh/authorized_keys", "/home/pi/real_keys"));
            Assert.True(plan.CanApply);
            Assert.Equal("/home/pi/real_keys", plan.Changes.Single().Path);
        }

        // ------------------------------------------------------------------------- fixes

        [Fact]
        public void ADisabledServerGetsTheLinksSystemctlEnableWouldMake()
        {
            var offer = RepairHarness.Offers(Path("edit-ssh-disabled")).Single();

            Assert.Equal(FindingCode.ServerNotEnabled, offer.Finding);
            var plan = offer.Plan;
            Assert.Equal(RepairKind.EnableServer, plan.Kind);
            var links = plan.Changes.Where(c => c.Kind == EditKind.CreateSymlink).ToList();
            Assert.Contains(links, c => c.Path == "/etc/systemd/system/multi-user.target.wants/ssh.service" && c.LinkTarget == "/lib/systemd/system/ssh.service");
            Assert.Contains(links, c => c.Path == "/etc/systemd/system/sshd.service" && c.LinkTarget == "/lib/systemd/system/ssh.service");
        }

        [Fact]
        public void AMaskedServerLosesItsMaskAndGetsEnabled()
        {
            var offer = RepairHarness.Offers(Path("edit-ssh-masked")).Single();

            Assert.Equal(FindingCode.ServerMasked, offer.Finding);
            var plan = offer.Plan;
            Assert.Equal(RepairKind.UnmaskServer, plan.Kind);
            Assert.Equal(EditKind.Delete, plan.Changes[0].Kind);
            Assert.Equal("/etc/systemd/system/ssh.service", plan.Changes[0].Path);
            Assert.Contains(plan.Changes, c => c.Kind == EditKind.CreateSymlink && c.Path == "/etc/systemd/system/multi-user.target.wants/ssh.service");
        }

        [Fact]
        public void AMaskThatIsNotALinkToDevNullIsNotRemoved()
        {
            string image = Path("edit-ssh-masked");
            var plan = RepairHarness.Plan(
                image,
                (r, rep) => SshRepairPlanner.PlanUnmaskServer(r, rep),
                EditChange.Remove("/etc/systemd/system/ssh.service", null),
                EditChange.NewSymlink("/etc/systemd/system/ssh.service", "/etc/other.service"));
            Assert.Equal(RepairRefusal.NotRepairable, plan.Unavailable);
        }

        [Fact]
        public void AUnitWithoutAnInstallSectionCannotBeEnabled()
        {
            string image = Path("edit-ssh-disabled");
            var plan = RepairHarness.Plan(
                image,
                (r, rep) => SshRepairPlanner.PlanEnableServer(r, rep),
                EditChange.Edit("/usr/lib/systemd/system/ssh.service", Encoding.ASCII.GetBytes("[Unit]\nDescription=x\n[Service]\nExecStart=/usr/sbin/sshd -D\n"), null));
            Assert.Equal(RepairRefusal.NoInstallSection, plan.Unavailable);
        }

        [Fact]
        public void AnAllowUsersListWithoutTheAccountGetsAnExtraLineRightAfterTheFirst()
        {
            string image = Path("edit-ssh-allowusers");
            string original = RepairHarness.Text(image, "/etc/ssh/sshd_config");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi");

            Assert.Equal(LoginReason.NotInAllowUsers, offer.Reason);
            var change = offer.Plan.Changes.Single();
            Assert.Equal("/etc/ssh/sshd_config", change.Path);
            Assert.Equal(original.Replace("AllowUsers alice\n", "AllowUsers alice\nAllowUsers pi\n"), Latin1(change.Content));
            Assert.Equal(RepairKind.AllowUser, offer.Plan.Kind);
        }

        [Fact]
        public void AnAllowGroupsListWithoutTheAccountsGroupGetsTheGroupAdded()
        {
            string image = Path("edit-ssh-allowgroups");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi");

            Assert.Equal(RepairKind.AllowGroup, offer.Plan.Kind);
            Assert.Equal("pi", offer.Plan.Title.Arg2);
            Assert.Contains("AllowGroups sshusers\nAllowGroups pi\n", Latin1(offer.Plan.Changes.Single().Content));
        }

        [Fact]
        public void AnAccountInDenyUsersIsTakenOutOfTheListLeavingTheOthers()
        {
            string image = Path("edit-ssh-ok");
            var deny = EditChange.Edit("/etc/ssh/sshd_config.d/60-deny.conf", Encoding.ASCII.GetBytes("DenyUsers mallory pi\n"), null);
            var create = EditChange.NewFile("/etc/ssh/sshd_config.d/60-deny.conf", Encoding.ASCII.GetBytes("DenyUsers mallory pi\n"));
            var offer = RepairHarness.Offers(image, create).Single(o => o.Account == "pi");

            Assert.Equal(LoginReason.Denied, offer.Reason);
            string text = Latin1(offer.Plan.Changes.Single().Content);
            Assert.Equal("# hdd-backup 2026-11-08: DenyUsers mallory pi\nDenyUsers mallory\n", text);
            Assert.NotNull(deny);
        }

        [Fact]
        public void AWildcardDenyListCannotBeRepairedAccountByAccount()
        {
            string image = Path("edit-ssh-ok");
            var offer = RepairHarness.Offers(image, EditChange.NewFile("/etc/ssh/sshd_config.d/60-deny.conf", Encoding.ASCII.GetBytes("DenyUsers p*\n"))).Single(o => o.Account == "pi");
            Assert.Equal(RepairRefusal.NotRepairable, offer.Plan.Unavailable);
        }

        [Fact]
        public void DisabledKeyLoginWithALockedPasswordIsRepairedByTurningKeyLoginOn()
        {
            string image = Path("edit-ssh-dropin");
            string original = RepairHarness.Text(image, "/etc/ssh/sshd_config");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi");

            Assert.Equal(LoginReason.KeyAuthOffNoPassword, offer.Reason);
            Assert.Equal(RepairKind.EnableKeyAuth, offer.Plan.Kind);
            Assert.Equal(RepairRisk.None, offer.Plan.Risk);
            Assert.Equal(original.Replace("PubkeyAuthentication no\n", "# hdd-backup 2026-11-08: PubkeyAuthentication no\nPubkeyAuthentication yes\n"), Latin1(offer.Plan.Changes.Single().Content));
        }

        [Fact]
        public void PasswordLoginOffWithoutAKeyOffersAKeyFirstAndPasswordLoginAsARiskyPlan()
        {
            string image = Path("edit-ssh-nokey");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi");

            Assert.Equal(LoginReason.PasswordAuthOffNoKey, offer.Reason);
            Assert.True(offer.OffersKey);
            Assert.Equal(RepairKind.EnablePasswordAuth, offer.Plan.Kind);
            Assert.Equal(RepairRisk.LowersSecurity, offer.Plan.Risk);
            Assert.Contains("PasswordAuthentication yes", Latin1(offer.Plan.Changes.Single().Content));
        }

        [Fact]
        public void RootLoginIsRepairedByKeysOnlyAndPasswordLoginIsOnlyTheRiskyAlternative()
        {
            string image = Path("edit-ssh-ok");
            var config = EditChange.Edit("/etc/ssh/sshd_config", Encoding.ASCII.GetBytes("PermitRootLogin no\nAllowUsers root\nPasswordAuthentication yes\n"), null);
            var offer = RepairHarness.Offers(image, config).Single(o => o.Account == "root");

            Assert.Equal(LoginReason.RootNotAllowed, offer.Reason);
            Assert.Equal(RepairKind.RootKeysOnly, offer.Plan.Kind);
            Assert.Equal(RepairRisk.None, offer.Plan.Risk);
            Assert.Contains("PermitRootLogin prohibit-password", Latin1(offer.Plan.Changes.Single().Content));
            var alternative = offer.Plan.Alternatives.Single();
            Assert.Equal(RepairKind.RootPassword, alternative.Kind);
            Assert.Equal(RepairRisk.LowersSecurity, alternative.Risk);
        }

        [Fact]
        public void ANoLoginShellIsReplacedByAnInstalledOneAndNothingElseInPasswdChanges()
        {
            string image = Path("edit-ssh-badshell");
            string original = RepairHarness.Text(image, "/etc/passwd");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi");

            Assert.Equal(LoginReason.NoShell, offer.Reason);
            Assert.Equal(RepairKind.FixShell, offer.Plan.Kind);
            Assert.Equal("/bin/bash", offer.Plan.Title.Arg2);
            Assert.Equal(original.Replace(":/home/pi:/usr/sbin/nologin", ":/home/pi:/bin/bash"), Latin1(offer.Plan.Changes.Single().Content));
        }

        [Fact]
        public void WithoutAnInstalledLoginShellTheShellIsNotChanged()
        {
            string image = Path("edit-ssh-badshell");
            var plan = RepairHarness.Plan(
                image,
                (r, rep) => SshRepairPlanner.PlanFixShell(r, rep.Accounts.Single(a => a.Name == "pi")),
                EditChange.Remove("/usr/bin/bash", null),
                EditChange.Remove("/usr/bin/sh", null));
            Assert.Equal(RepairRefusal.NoSuitableShell, plan.Unavailable);
        }

        [Fact]
        public void UnsafeKeyFileModesAreTightenedAndTheContentIsLeftAlone()
        {
            string image = Path("edit-ssh-badmodes");
            var offer = RepairHarness.Offers(image).Single(o => o.Account == "pi" && o.Reason == LoginReason.KeyFileUnsafe);

            var plan = offer.Plan;
            Assert.Equal(RepairKind.FixKeyFileModes, plan.Kind);
            Assert.All(plan.Changes, c => Assert.Equal(EditKind.SetAttributes, c.Kind));
            var file = plan.Changes.Single(c => c.Path == "/home/pi/.ssh/authorized_keys");
            Assert.Equal((ushort)0x180, file.Mode);
            Assert.Null(file.Uid); // owned by root, which the server accepts
            // the .ssh folder (755) is not writable by others, so the server accepts it and it is left alone
            Assert.DoesNotContain(plan.Changes, c => c.Path == "/home/pi/.ssh");
            var home = plan.Changes.Single(c => c.Path == "/home/pi");
            Assert.Equal((ushort)0x1ED, home.Mode);
            Assert.Null(home.Uid);
        }

        [Fact]
        public void ARepairedSystemOffersNothing()
        {
            Assert.Empty(RepairHarness.Offers(Path("edit-ssh-ok")));
        }

        [Fact]
        public void ThePasswordHashNeverAppearsInAnyPlan()
        {
            foreach (string fixture in new[] { "edit-ssh-nokey", "edit-ssh-badshell", "edit-ssh-allowusers", "edit-ssh-badmodes" })
            {
                foreach (var offer in RepairHarness.Offers(Path(fixture)))
                {
                    var plans = new[] { offer.Plan }.Concat(offer.Plan?.Alternatives ?? new RepairPlan[0]).Where(p => p != null);
                    foreach (var plan in plans)
                    {
                        foreach (var change in plan.Changes.Where(c => c.Path != "/etc/passwd" && c.Content != null))
                        {
                            Assert.DoesNotContain("PLANTEDSECRET", Latin1(change.Content));
                        }

                        foreach (var line in plan.Summary)
                        {
                            Assert.DoesNotContain("PLANTED", line.Path + line.Before + line.After);
                        }
                    }
                }
            }
        }
    }
}
