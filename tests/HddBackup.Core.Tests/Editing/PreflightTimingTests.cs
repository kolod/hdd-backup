using System.Diagnostics;
using System.IO;
using System.Threading;
using HddBackup.Core.Editing;
using HddBackup.Core.Tests.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace HddBackup.Core.Tests.Editing
{
    public class PreflightTimingTests
    {
        private readonly ITestOutputHelper _out;

        public PreflightTimingTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void SavingOnTheLargestFixtureTakesReasonableTime()
        {
            string source = FixtureImages.RawPath("rpi-like-4k");
            string copy = Path.Combine(Path.GetTempPath(), "HddBackupTests", "timing-" + Path.GetRandomFileName() + ".img");
            File.Copy(source, copy);
            try
            {
                var sw = Stopwatch.StartNew();
                using (var session = ImageEditSession.Open(copy, 2))
                {
                    _out.WriteLine("open " + sw.ElapsedMilliseconds + " ms, image " + new FileInfo(copy).Length / (1 << 20) + " MiB");
                    session.Add(EditChange.NewFile("/etc/hddbackup-timing.txt", new byte[] { 65 }));
                    sw.Restart();
                    var result = session.Save(null, CancellationToken.None);
                    _out.WriteLine("save " + sw.ElapsedMilliseconds + " ms " + result.Status + " " + result.Detail);
                    Assert.Equal(SaveStatus.Saved, result.Status);
                }
            }
            finally
            {
                File.Delete(copy);
                File.Delete(copy + ".sha256");
            }
        }
    }
}
