using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Diagnostics;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>SC-004: no result of a tool contains a password hash, key text or private key text, however it is stored.</summary>
    public class SecretLeakTests
    {
        private static readonly string[] Planted =
        {
            "PLANTEDSECRET", "PLANTEDKEYBLOB", "PLANTEDPRIVATEKEY", "BEGIN OPENSSH PRIVATE KEY", "$6$", "AAAAC3NzaC1lZDI1NTE5"
        };

        internal static void AssertClean(object result, string what)
        {
            string dump = ObjectDump.Of(result);
            foreach (string secret in Planted)
            {
                Assert.False(dump.Contains(secret), what + " contains " + secret);
            }
        }

        public static IEnumerable<object[]> Fixtures()
        {
            foreach (var f in new[] { "diag-ssh-ok", "diag-ssh-keys-only", "diag-ssh-disabled", "diag-startup", "diag-pi-like" })
            {
                yield return new object[] { f };
            }
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void SshReportOfARealImageHoldsNoSecret(string fixture)
        {
            var report = SshFixtureTests.Check(fixture);

            AssertClean(report, "SshReport of " + fixture);
        }

        [Fact]
        public void SshReportOfEveryMatrixCaseHoldsNoSecret()
        {
            int checkedCases = 0;
            foreach (var c in SshCheckMatrixTests.All())
            {
                var b = SshCheckMatrixTests.Build(c);
                var report = SshCheck.Run(b.Reader(), new DiagnosticsSource { HasUnreadBootPartition = c.HasFat }, CancellationToken.None);

                AssertClean(report, "SshReport of case " + c.Name);
                Assert.DoesNotContain(b.ReadPaths, p => p.Contains("id_"));
                checkedCases++;
            }

            Assert.True(checkedCases >= 12);
        }

        [Theory]
        [MemberData(nameof(Fixtures))]
        public void StartupResultOfARealImageHoldsNoSecret(string fixture)
        {
            AssertClean(StartupFixtureTests.Run(fixture), "StartupResult of " + fixture);
        }

        [Fact]
        public void StartupResultOfTheReferenceSystemAndTheMatrixHoldsNoSecret()
        {
            var reference = StartupInventoryTests.ReferenceSystem();
            AssertClean(StartupInventory.Run(reference.Reader(), new DiagnosticsSource(), CancellationToken.None), "reference system");
            Assert.DoesNotContain(reference.ReadPaths, p => p.Contains("id_"));

            foreach (var c in SshCheckMatrixTests.All())
            {
                var b = SshCheckMatrixTests.Build(c);
                AssertClean(StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None), "startup list of case " + c.Name);
            }
        }

        [Fact]
        public void ACommandThatContainsASecretIsShownAsWrittenAndNothingElseIsAdded()
        {
            // A cron line with a password argument is the person's own configuration and is shown verbatim (research R-14);
            // nothing beyond the planted command text may appear.
            var b = new HddBackup.Core.Tests.Fakes.LinuxRootBuilder().BasicLinux().Executable("/usr/sbin/cron")
                .File("/etc/cron.d/job", "0 1 * * * root /usr/bin/upload --password=hunter2\n");

            var r = StartupInventory.Run(b.Reader(), new DiagnosticsSource(), CancellationToken.None);

            var e = r.Entries.Single();
            Assert.Equal("/usr/bin/upload --password=hunter2", e.Command);
            Assert.Equal("upload", e.Name);
        }

        [Fact]
        public void TheDumpHelperDoesFindASecretThatIsPresent()
        {
            var holder = new Holder { Inner = new List<string> { "x", "my $6$PLANTEDSECRET$ hash" } };

            Assert.Throws<Xunit.Sdk.FalseException>(() => AssertClean(holder, "holder"));
        }

        private sealed class Holder
        {
            private string _hidden = "ok";

            public List<string> Inner { get; set; }

            public override string ToString() => _hidden;
        }
    }
}
