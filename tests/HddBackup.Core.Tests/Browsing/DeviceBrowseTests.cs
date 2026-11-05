using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    /// <summary>A physical device is browsed and extracted from exactly like an image, and only read (feature 008, User Story 4).</summary>
    public sealed class DeviceBrowseTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();
        private readonly System.Collections.Generic.List<IDisposable> _devices = new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            foreach (var d in _devices)
            {
                d.Dispose();
            }

            _temp.Dispose();
        }

        private (FakeDeviceAccess Access, DeviceInfo Device, ReadOnlyBlockDevice Raw) Attach(string fixture)
        {
            var raw = new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(FixtureImages.RawPath(fixture)));
            _devices.Add(raw);
            var access = new FakeDeviceAccess();
            var info = access.Add(5, raw);
            return (access, info, raw);
        }

        private static OperationResult Run(ImagingEngine engine, OperationRequest request)
        {
            return engine.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }

        private static OperationRequest Request(DeviceInfo device, int partition, string entry, string destination)
        {
            return new OperationRequest
            {
                Kind = OperationKind.ExtractFiles,
                BrowseDevice = device,
                BrowsePartitionNumber = partition,
                ExtractEntryPath = entry,
                ExtractDestination = destination,
                ExtractConflictPolicy = ConflictPolicy.Replace
            };
        }

        [Theory]
        [InlineData("browse-names", 1)]
        [InlineData("rpi-like-4k", 2)]
        public void TheTreeAndCounts_AreTheSameAsForTheImage(string fixture, int partition)
        {
            var (access, device, raw) = Attach(fixture);
            var (imageSource, imageView) = FileSystemViewTests.Open(fixture);
            using (imageSource)
            using (imageView)
            using (var source = BrowseSource.OpenDevice(access, device))
            {
                var browsable = Assert.Single(source.Browsable);
                Assert.Equal(partition, browsable.Number);
                using (var view = source.OpenFileSystem(browsable))
                {
                    var fromDevice = FileSystemViewTests.Walk(view);
                    var fromImage = FileSystemViewTests.Walk(imageView);
                    Assert.Equal(fromImage.Keys.OrderBy(k => k, StringComparer.Ordinal), fromDevice.Keys.OrderBy(k => k, StringComparer.Ordinal));
                    Assert.All(fromImage, kv =>
                    {
                        Assert.Equal(kv.Value.Size, fromDevice[kv.Key].Size);
                        Assert.Equal(kv.Value.Kind, fromDevice[kv.Key].Kind);
                    });
                    Assert.Equal(imageView.Counts.Files, view.Counts.Files);
                    Assert.Equal(imageView.Counts.Directories, view.Counts.Directories);
                }
            }

            Assert.Equal(0, raw.WriteCalls);
            Assert.Equal(1, access.ReadOpens);
            Assert.Equal(1, access.ReadReleases);
        }

        [Fact]
        public void BrowsingADevice_NeverLocksWritesOrEjects()
        {
            var (access, device, raw) = Attach("rpi-like-4k");
            using (var source = BrowseSource.OpenDevice(access, device))
            using (var view = source.OpenFileSystem(source.Browsable.Single()))
            {
                var tree = FileSystemViewTests.Walk(view);
                Assert.NotEmpty(tree);
                var file = tree.Values.First(e => e.Kind == FsEntryKind.File && e.Size > 0);
                Assert.True(FilePreview.Create(view, file, PreviewMode.Hex, CancellationToken.None).ShownBytes > 0);
            }

            Assert.Equal(0, raw.WriteCalls);
            Assert.False(access.AnyWriteAttemptOnReadHandle);
            Assert.Equal(0, access.ExclusiveOpens);
            Assert.Equal(0, access.ExclusiveReleases);
            Assert.Equal(0, access.EjectCalls);
        }

        [Fact]
        public void ExtractingFromADevice_GivesTheSameFilesAsFromTheImage_AndTouchesNothing()
        {
            var (access, device, raw) = Attach("rpi-like-4k");
            var dest = _temp.New();
            var engine = new ImagingEngine(access);
            var request = Request(device, 2, "/etc", dest);

            Assert.Empty(engine.Validate(request));
            var result = Run(engine, request);

            Assert.Equal(OperationState.Completed, result.State);
            var manifest = FixtureImages.Manifest("rpi-like-4k").Where(m => m.Type == "file" && m.Path.StartsWith("etc/", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(manifest);
            foreach (var m in manifest)
            {
                string path = Path.Combine(dest, m.Path.Replace('/', '\\'));
                Assert.True(File.Exists(path), m.Path);
                Assert.Equal(m.Size, new FileInfo(path).Length);
            }

            Assert.Equal(0, raw.WriteCalls);
            Assert.Equal(1, access.ReadOpens);
            Assert.Equal(1, access.ReadReleases);
            Assert.Equal(0, access.ExclusiveOpens);
            Assert.Equal(0, access.EjectCalls);
        }

        [Fact]
        public void ADeviceThatDisappears_ReportsTheCause_WhileBrowsing()
        {
            var (access, device, raw) = Attach("rpi-like-4k");
            using (var source = BrowseSource.OpenDevice(access, device))
            using (var view = source.OpenFileSystem(source.Browsable.Single()))
            {
                var usr = view.GetEntry("/usr");
                var passwd = view.GetEntry("/etc/passwd");
                raw.FailWith = new ImagingException(ErrorCode.DeviceRemoved, "device removed", 4096);
                raw.FailAfterReads = raw.ReadCalls; // every further read fails

                var list = Assert.Throws<ImagingException>(() => view.GetChildren(usr, CancellationToken.None));
                Assert.Equal(ErrorCode.DeviceRemoved, list.Code);
                Assert.Equal(4096, list.Offset);

                var buffer = new byte[16];
                var read = Assert.Throws<ImagingException>(() => view.ReadRange(passwd, 0, buffer, 0, buffer.Length));
                Assert.Equal(ErrorCode.DeviceRemoved, read.Code);
            }
        }

        [Fact]
        public void ADeviceThatDisappears_StopsAnExtraction_WithTheCause_AndNoPartFile()
        {
            // First learn how many reads a complete extraction takes, then cut the device at 70%.
            int total;
            {
                var (access, device, raw) = Attach("rpi-like-4k");
                var ok = Run(new ImagingEngine(access), Request(device, 2, "/usr", _temp.New()));
                Assert.Equal(OperationState.Completed, ok.State);
                total = raw.ReadCalls;
            }

            var (failingAccess, failingDevice, failingRaw) = Attach("rpi-like-4k");
            failingRaw.FailWith = new ImagingException(ErrorCode.DeviceRemoved, "device removed", 8192);
            failingRaw.FailAfterReads = total * 7 / 10;
            var dest = _temp.New();
            var result = Run(new ImagingEngine(failingAccess), Request(failingDevice, 2, "/usr", dest));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DeviceRemoved, result.ErrorCode);
            Assert.False(result.SafeToRemove);
            Assert.Empty(Directory.EnumerateFiles(dest, Ext4Extractor.PartPrefix + "*", SearchOption.AllDirectories));
            Assert.Equal(0, failingRaw.WriteCalls);
            Assert.Equal(failingAccess.ReadOpens, failingAccess.ReadReleases);
        }
    }
}
