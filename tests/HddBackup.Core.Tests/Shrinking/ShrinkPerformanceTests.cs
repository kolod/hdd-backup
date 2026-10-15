using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Shrinking
{
    /// <summary>
    /// SC-004: shrink a 16 GiB image with about 3 GiB of data (tests/fixtures/ext4/make-large.sh) to
    /// fit an 8 GiB card. Runs only when HDDBACKUP_PERF_IMAGE names that image; the output and the
    /// working copy go next to it, and a timing line is appended to perf-results.txt there.
    /// </summary>
    public class ShrinkPerformanceTests
    {
        private const long EightGiB = 8L << 30;

        [Fact]
        [Trait("Category", "Performance")]
        public async Task LargeImage_ShrinksToEightGiB()
        {
            var source = Environment.GetEnvironmentVariable("HDDBACKUP_PERF_IMAGE");
            if (string.IsNullOrEmpty(source))
            {
                return; // not configured on this machine
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(source));
            string output = Path.Combine(dir, "perf-shrunk.img");
            File.Delete(output);
            File.Delete(output + ".sha256");

            var engine = new ImagingEngine(new FakeDeviceAccess());
            var phases = new System.Text.StringBuilder();
            var watch = Stopwatch.StartNew();
            var task = engine.RunAsync(new OperationRequest { Kind = OperationKind.ShrinkImage, ImagePath = source, ShrinkOutputPath = output, ShrinkTargetBytes = EightGiB }, CancellationToken.None);
            OperationPhase? last = null;
            while (!task.IsCompleted)
            {
                var phase = engine.CurrentProgress.Phase;
                if (phase != last)
                {
                    phases.AppendFormat(CultureInfo.InvariantCulture, " {0}@{1:0}s", phase, watch.Elapsed.TotalSeconds);
                    last = phase;
                }

                await Task.WhenAny(task, Task.Delay(500));
            }

            watch.Stop();
            var result = await task;
            File.AppendAllText(
                Path.Combine(dir, "perf-results.txt"),
                string.Format(CultureInfo.InvariantCulture, "{0:u} {1} {2} in {3:0.0} min, output {4} bytes;{5}{6}", DateTime.UtcNow, Path.GetFileName(source), result.State, watch.Elapsed.TotalMinutes, File.Exists(output) ? new FileInfo(output).Length : 0, phases, Environment.NewLine));

            Assert.True(result.State == OperationState.Completed, result.ErrorCode + " " + result.ErrorDetail);
            Assert.True(new FileInfo(output).Length <= EightGiB);
            Assert.True(watch.Elapsed < TimeSpan.FromMinutes(15), "SC-004: " + watch.Elapsed);
        }
    }
}
