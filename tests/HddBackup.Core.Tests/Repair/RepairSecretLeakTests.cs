using System;
using System.Collections.Generic;
using System.Linq;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using HddBackup.Core.Repair;
using HddBackup.Core.Tests.Diagnostics;
using HddBackup.Core.Tests.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Feature 011, FR-014 / SC-006: no password hash, private key or key data in titles, summaries, logs and reports.</summary>
    public class RepairSecretLeakTests
    {
        private sealed class WordTexts : IRepairReportTexts
        {
            public string RepairsCaption => "Repairs";

            public string Repair(RepairLogEntry e) => e.State + " " + e.Title.Kind + " " + e.Title.Arg1 + " " + e.Title.Arg2;
        }

        private static string Copy(string fixture)
        {
            var image = EditImage.Copy(fixture);
            image.Device.Dispose();
            return image.Path;
        }

        public static IEnumerable<object[]> Fixtures()
        {
            foreach (var f in new[] { "edit-ssh-ok", "edit-ssh-nokey", "edit-ssh-disabled", "edit-ssh-masked", "edit-ssh-allowusers", "edit-ssh-allowgroups", "edit-ssh-badshell", "edit-ssh-badmodes", "edit-ssh-dropin", "edit-ssh-keyscommand" })
            {
                yield return new object[] { f };
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void NothingTheWizardShowsOrKeepsAboutARepairHoldsASecret(string fixture)
        {
            string image = Copy(fixture);
            var report = RepairHarness.Check(image);
            SecretLeakTests.AssertClean(report, "SshReport of " + fixture);

            foreach (var offer in RepairHarness.Offers(image))
            {
                var plans = new[] { offer.Plan }.Where(p => p != null).SelectMany(p => new[] { p }.Concat(p.Alternatives)).ToList();
                foreach (var plan in plans)
                {
                    SecretLeakTests.AssertClean(plan.Title, "title of " + plan.Kind);
                    SecretLeakTests.AssertClean(plan.Summary, "summary of " + plan.Kind);
                }

                SecretLeakTests.AssertClean(offer.Account + offer.Reason + offer.Finding, "offer");
            }
        }

        [Fact]
        public void AddingAKeyLeavesNoKeyTextInTheTitleTheSummaryTheLogOrTheReport()
        {
            string image = Copy("edit-ssh-nokey");
            Assert.True(PublicKey.TryParse(PublicKeyTests.Ed25519, out var key, out _));
            string body = PublicKeyTests.Ed25519.Split(' ')[1];
            var plan = RepairHarness.Plan(image, (r, rep) => SshRepairPlanner.PlanAddKey(r, rep, "pi", key));
            var log = new RepairLog();
            log.Add(plan, RepairState.Waiting, new DateTime(2026, 11, 8), 3);

            var texts = new List<string> { plan.Title.ToString() };
            texts.AddRange(plan.Summary.Select(l => l.Path + "|" + l.Before + "|" + l.After));
            texts.AddRange(log.Entries.Select(e => new WordTexts().Repair(e)));
            var report = RepairHarness.Check(image);
            texts.Add(ReportBuilder.Ssh(report, new ReportBuilderTests.CodeTexts(), new DateTime(2026, 11, 8), log.Entries, new WordTexts()));

            foreach (string text in texts)
            {
                Assert.DoesNotContain(body, text);
                Assert.DoesNotContain("PRIVATE KEY", text);
            }

            Assert.Contains(texts, t => t.Contains(key.Fingerprint));
        }

        [Fact]
        public void APrivateKeyIsRefusedBeforeAnyPlanOrObjectExists()
        {
            const string secret = "PLANTEDPRIVATEKEYBODY";
            string privateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\n" + secret + "\n-----END OPENSSH PRIVATE KEY-----\n";

            Assert.False(PublicKey.TryParse(privateKey, out var key, out var refusal));

            Assert.Null(key);
            Assert.Equal(KeyRefusal.PrivateKey, refusal);
            // the refusal is a plain code: it cannot hold the text it refused
            Assert.DoesNotContain(secret, refusal.ToString());
        }

        [Fact]
        public void TheLogTracksStatesWithoutKeepingAnythingElse()
        {
            var log = new RepairLog();
            var plan = new RepairPlan(new RepairTitle(RepairKind.EnableServer, "ssh.service"), null, new[] { EditChange.NewSymlink("/etc/x", "/y") });
            log.Add(plan, RepairState.Waiting, DateTime.UtcNow, 1);
            log.Add(plan, RepairState.Waiting, DateTime.UtcNow, 2);
            log.Remove(1);
            Assert.Single(log.Entries);
            log.Saved();
            Assert.Equal(RepairState.Saved, log.Entries.Single().State);
            log.Add(plan, RepairState.Written, DateTime.UtcNow);
            log.Discarded();
            Assert.Equal(2, log.Entries.Count);
        }

        [Fact]
        public void ARepairPlanIsImmutableAndRiskPlansOnlyAppearAsAlternativesOrOnTheirOwn()
        {
            var safe = new RepairPlan(new RepairTitle(RepairKind.RootKeysOnly), null, new[] { EditChange.NewDirectory("/x") });
            var risky = new RepairPlan(new RepairTitle(RepairKind.RootPassword), null, new[] { EditChange.NewDirectory("/y") }, RepairRisk.LowersSecurity);
            var primary = new RepairPlan(safe.Title, safe.Summary, safe.Changes, RepairRisk.None, new[] { risky });

            Assert.Equal(RepairRisk.None, primary.Risk);
            Assert.Equal(RepairRisk.LowersSecurity, primary.Alternatives.Single().Risk);
            Assert.True(primary.CanApply);
            Assert.False(RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.NoHome, "pi").CanApply);
            Assert.Equal(RepairRefusal.NoHome, RepairPlan.Refused(RepairKind.AddKey, RepairRefusal.NoHome, "pi").Unavailable);
        }

        [Fact]
        public void ARepairTitleClipsLongArgumentsAndNeverNull()
        {
            var title = new RepairTitle(RepairKind.AddKey, new string('a', 500), null);
            Assert.Equal(RepairTitle.MaxArgument, title.Arg1.Length);
            Assert.Equal(string.Empty, title.Arg2);
        }
    }
}
