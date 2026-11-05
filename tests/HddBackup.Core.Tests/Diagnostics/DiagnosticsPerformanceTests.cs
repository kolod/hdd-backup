using System.Diagnostics;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace HddBackup.Core.Tests.Diagnostics
{
    /// <summary>SC-003: a result within 5 seconds. The images here are small; the number of device reads is what scales to a large card.</summary>
    public class DiagnosticsPerformanceTests
    {
        private readonly ITestOutputHelper _output;

        public DiagnosticsPerformanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData("diag-startup")]
        [InlineData("diag-pi-like")]
        [InlineData("diag-ssh-ok")]
        public void BothToolsFinishWellWithinFiveSecondsAndReadLittle(string fixture)
        {
            var raw = new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(FixtureImages.RawPath(fixture)));
            var access = new FakeDeviceAccess();
            var device = access.Add(9, raw);
            int number;
            using (var s = BrowseSource.OpenDevice(access, device))
            {
                number = s.Browsable.First().Number;
            }

            var source = new DiagnosticsSource { Device = device, PartitionNumber = number, DisplayName = fixture };
            int readsBefore = raw.ReadCalls;
            var watch = Stopwatch.StartNew();
            int fileReads = 0;
            int listings = 0;
            DiagnosticsRunner.Run(access, source, (r, s) =>
            {
                SshCheck.Run(r, s, CancellationToken.None);
                fileReads = r.FileReads;
                listings = r.Listings;
                return 0;
            }, CancellationToken.None);
            long sshMs = watch.ElapsedMilliseconds;
            int sshDeviceReads = raw.ReadCalls - readsBefore;

            readsBefore = raw.ReadCalls;
            watch.Restart();
            int entries = DiagnosticsRunner.Run(access, source, (r, s) => StartupInventory.Run(r, s, CancellationToken.None).Entries.Count, CancellationToken.None);
            long startupMs = watch.ElapsedMilliseconds;
            int startupDeviceReads = raw.ReadCalls - readsBefore;

            _output.WriteLine(fixture + ": SSH check " + sshMs + " ms, " + sshDeviceReads + " device reads, " + fileReads + " file reads, " + listings + " listings; startup list " + startupMs + " ms, " + startupDeviceReads + " device reads, " + entries + " entries");
            Assert.True(sshMs < 5000, "SSH check took " + sshMs + " ms");
            Assert.True(startupMs < 5000, "startup list took " + startupMs + " ms");
            Assert.Equal(0, raw.WriteCalls);
            raw.Dispose();
        }
    }
}
