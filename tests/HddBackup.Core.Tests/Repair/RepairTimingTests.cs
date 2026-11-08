using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Repair;
using Xunit;
using Xunit.Abstractions;

namespace HddBackup.Core.Tests.Repair
{
    /// <summary>Manual: set HDDBACKUP_LARGE_IMAGE to a big image; only reads it (checks SSH, plans repairs, writes nothing).</summary>
    public class RepairTimingTests
    {
        private readonly ITestOutputHelper _out;

        public RepairTimingTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void CheckAndPlanOnALargeImage()
        {
            string path = Environment.GetEnvironmentVariable("HDDBACKUP_LARGE_IMAGE");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Assert.True(PublicKey.TryParse(PublicKeyTests.Ed25519, out var key, out _));
            var sw = Stopwatch.StartNew();
            using (var source = BrowseSource.OpenImage(path))
            using (var view = source.OpenFileSystem(source.Browsable.Last()))
            {
                _out.WriteLine("open " + sw.ElapsedMilliseconds + " ms");
                sw.Restart();
                var reader = new SystemReader(view);
                var report = SshCheck.Run(reader, null, CancellationToken.None);
                _out.WriteLine("SSH check " + sw.ElapsedMilliseconds + " ms: " + report.Verdict.Code);
                sw.Restart();
                var offers = SshRepairPlanner.Offers(reader, report, DateTime.Today);
                _out.WriteLine("offers " + sw.ElapsedMilliseconds + " ms: " + string.Join(", ", offers.Select(o => o.Account + "/" + o.Reason + "/" + (o.Plan == null ? "key" : o.Plan.Kind.ToString()))));
                sw.Restart();
                foreach (var account in report.Accounts.Where(a => !a.IsSystemAccount && a.Home.Length > 0))
                {
                    var plan = SshRepairPlanner.PlanAddKey(reader, report, account.Name, key);
                    _out.WriteLine("plan add key for " + account.Name + " " + sw.ElapsedMilliseconds + " ms: " + (plan.CanApply ? plan.Changes.Count + " changes" : plan.Unavailable.ToString()));
                    sw.Restart();
                }
            }
        }
    }
}
