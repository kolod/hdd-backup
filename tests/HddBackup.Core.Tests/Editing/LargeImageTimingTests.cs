using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Editing;
using Xunit;
using Xunit.Abstractions;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>Manual: set HDDBACKUP_LARGE_IMAGE to a big image; only reads it (builds and checks a change, writes nothing).</summary>
    public class LargeImageTimingTests
    {
        private readonly ITestOutputHelper _out;

        public LargeImageTimingTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void BuildAndCheckAChangeOnALargeImage()
        {
            string path = Environment.GetEnvironmentVariable("HDDBACKUP_LARGE_IMAGE");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var sw = Stopwatch.StartNew();
            long offset, length;
            using (var source = BrowseSource.OpenImage(path))
            {
                var p = source.Browsable.Last();
                offset = p.Offset;
                length = p.Length;
                _out.WriteLine("partition " + p.Number + " " + length / (1 << 20) + " MiB, open " + sw.ElapsedMilliseconds + " ms");
            }

            using (var file = FileBlockDevice.OpenRead(path))
            using (var part = new PartitionView(file, offset, length))
            {
                sw.Restart();
                var editor = new Ext4Editor(part);
                editor.CreateFile("/", "hddbackup-timing.txt", Encoding.UTF8.GetBytes("x"));
                _out.WriteLine("build " + sw.ElapsedMilliseconds + " ms, writes " + editor.Writes.Writes.Count);
                sw.Restart();
                var findings = EditPreflight.Check(editor.Writes, part);
                _out.WriteLine("preflight " + sw.ElapsedMilliseconds + " ms, findings " + findings.Count + " " + string.Join("; ", findings.Take(3)));
            }
        }
        [Fact]
        public void SaveOnACopyOfALargeImage()
        {
            string path = Environment.GetEnvironmentVariable("HDDBACKUP_LARGE_COPY");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var total = Stopwatch.StartNew();
            using (var session = ImageEditSession.Open(path))
            {
                session.Add(EditChange.NewFile("/hddbackup-timing.txt", Encoding.UTF8.GetBytes("timing")));
                var phaseWatch = Stopwatch.StartNew();
                var last = SavePhase.Prepare;
                var result = session.Save((phase, done, all) =>
                {
                    if (phase != last)
                    {
                        _out.WriteLine(last + " took " + phaseWatch.ElapsedMilliseconds + " ms");
                        phaseWatch.Restart();
                        last = phase;
                    }
                }, System.Threading.CancellationToken.None);
                _out.WriteLine(last + " took " + phaseWatch.ElapsedMilliseconds + " ms");
                _out.WriteLine("total " + total.ElapsedMilliseconds + " ms, " + result.Status + " " + result.Detail);
                Assert.Equal(SaveStatus.Saved, result.Status);
            }
        }
    }
}
