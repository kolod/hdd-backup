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
    /// <summary>Constitution Principle I for feature 008: the source is never written and the destination is guarded.</summary>
    public sealed class ReadOnlyTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static OperationRequest Request(string image, int partition, string entry, string destination, ConflictPolicy policy = ConflictPolicy.Replace)
        {
            return new OperationRequest
            {
                Kind = OperationKind.ExtractFiles,
                BrowseImagePath = image,
                BrowsePartitionNumber = partition,
                ExtractEntryPath = entry,
                ExtractDestination = destination,
                ExtractConflictPolicy = policy
            };
        }

        private static OperationResult Run(ImagingEngine engine, OperationRequest request, CancellationToken ct = default(CancellationToken))
        {
            return engine.RunAsync(request, ct).GetAwaiter().GetResult();
        }

        [Fact]
        public void BrowsingPlanningAndExtracting_NeverWriteToTheSource()
        {
            var path = FixtureImages.RawPath("rpi-like-4k");
            var device = new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(path));
            using (var source = BrowseSource.Open(device))
            using (var view = source.OpenFileSystem(source.Browsable.Single()))
            {
                var summary = ExtractorTests.Extract(view, view.GetEntry("/etc"), _temp.New());
                Assert.True(summary.FilesExtracted > 0);
                var preview = FileSystemViewTests.Walk(view);
                Assert.NotEmpty(preview);
            }

            Assert.Equal(0, device.WriteCalls);
            Assert.True(device.ReadCalls > 0);
        }

        [Fact]
        public void EngineExtraction_LeavesTheImageUnchanged_AndTouchesNoDevice()
        {
            var image = FixtureImages.RawPath("rpi-like-4k");
            string before = BrowseSourceTests.Sha256Of(image);
            var access = new FakeDeviceAccess();
            var engine = new ImagingEngine(access);
            var dest = _temp.New();
            var request = Request(image, 2, "/etc", dest);

            Assert.Empty(engine.Validate(request));
            var result = Run(engine, request);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.True(result.ContentComplete);
            Assert.True(result.SafeToRemove);
            Assert.True(result.Extraction.FilesExtracted > 0);
            Assert.True(File.Exists(Path.Combine(dest, "etc", "passwd")));
            Assert.Equal(100, engine.CurrentProgress.Percent, 3);
            Assert.Equal(before, BrowseSourceTests.Sha256Of(image));
            Assert.Equal(0, access.ExclusiveOpens);
            Assert.Equal(0, access.EjectCalls);
            Assert.Equal(0, access.ReadOpens);
        }

        [Fact]
        public void ADestinationThatIsTheImageItself_IsRefused_AndTheImageIsUntouched()
        {
            var dest = _temp.New();
            string image = Path.Combine(dest, "hostname"); // same name as the file that would be extracted
            File.Copy(FixtureImages.RawPath("browse-names"), image);
            string before = BrowseSourceTests.Sha256Of(image);

            var result = Run(new ImagingEngine(new FakeDeviceAccess()), Request(image, 1, "/etc/hostname", dest));

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DestinationIsSource, result.ErrorCode);
            Assert.Equal(before, BrowseSourceTests.Sha256Of(image));
            Assert.Equal(new[] { image }, Directory.GetFileSystemEntries(dest));
        }

        [Fact]
        public void ADestinationOnTheBrowsedDevice_IsRefusedBeforeAnythingHappens()
        {
            var access = new FakeDeviceAccess
            {
                PathInfo = new PathInfo { DiskNumbers = new[] { 5 }, FreeBytes = long.MaxValue, FileSystem = "FAT32" }
            };
            var device = access.Add(5, new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(FixtureImages.RawPath("rpi-like-4k"))));
            var request = new OperationRequest
            {
                Kind = OperationKind.ExtractFiles,
                BrowseDevice = device,
                BrowsePartitionNumber = 2,
                ExtractEntryPath = "/etc",
                ExtractDestination = _temp.New()
            };

            var ex = Assert.Throws<ImagingException>(() => new ImagingEngine(access).Validate(request));
            Assert.Equal(ErrorCode.DestinationIsSource, ex.Code);
            Assert.Equal(0, access.ReadOpens);
        }

        [Fact]
        public void InvalidRequests_AreRefusedByValidate()
        {
            var engine = new ImagingEngine(new FakeDeviceAccess());
            var image = FixtureImages.RawPath("browse-names");
            var dest = _temp.New();

            var noDestination = Assert.Throws<ImagingException>(() => engine.Validate(Request(image, 1, "/etc", Path.Combine(dest, "missing"))));
            Assert.Equal(ErrorCode.InvalidRequest, noDestination.Code);
            var noEntry = Assert.Throws<ImagingException>(() => engine.Validate(Request(image, 1, string.Empty, dest)));
            Assert.Equal(ErrorCode.InvalidRequest, noEntry.Code);
            var noSource = Assert.Throws<ImagingException>(() => engine.Validate(Request(null, 1, "/etc", dest)));
            Assert.Equal(ErrorCode.InvalidRequest, noSource.Code);
        }

        [Fact]
        public void ACompressedImageOrAMissingEntry_FailsTheOperationCleanly()
        {
            var dest = _temp.New();
            var gz = Path.Combine(_temp.New(), "x.img.gz");
            File.Copy(FixtureImages.CompressedPath("browse-names"), gz);
            var engine = new ImagingEngine(new FakeDeviceAccess());

            var compressed = Run(engine, Request(gz, 1, "/etc", dest));
            Assert.Equal(OperationState.Failed, compressed.State);
            Assert.Equal(ErrorCode.InvalidRequest, compressed.ErrorCode);

            var missing = Run(new ImagingEngine(new FakeDeviceAccess()), Request(FixtureImages.RawPath("browse-names"), 1, "/no/such/thing", dest));
            Assert.Equal(OperationState.Failed, missing.State);
            Assert.Equal(ErrorCode.InvalidRequest, missing.ErrorCode);
            Assert.Empty(Directory.GetFileSystemEntries(dest));
        }

        [Fact]
        public void NotEnoughSpace_StopsBeforeWriting()
        {
            var access = new FakeDeviceAccess { PathInfo = new PathInfo { FreeBytes = 100, FileSystem = "NTFS" } };
            var dest = _temp.New();
            var result = Run(new ImagingEngine(access), Request(FixtureImages.RawPath("rpi-like-4k"), 2, "/usr", dest));
            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.DestinationFull, result.ErrorCode);
            Assert.Empty(Directory.GetFileSystemEntries(dest));
        }

        [Fact]
        public void CancellingTheOperation_IsReportedAsCancelled_WithNothingWritten()
        {
            var dest = _temp.New();
            var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = Run(new ImagingEngine(new FakeDeviceAccess()), Request(FixtureImages.RawPath("rpi-like-4k"), 2, "/usr", dest), cts.Token);
            Assert.Equal(OperationState.Cancelled, result.State);
            Assert.False(result.ContentComplete);
            Assert.Empty(Directory.GetFileSystemEntries(dest));
        }

        [Fact]
        public void AnExtractionThatSkipsExistingFiles_DoesNotTouchThem()
        {
            var image = FixtureImages.RawPath("browse-names");
            var dest = _temp.New();
            Directory.CreateDirectory(Path.Combine(dest, "etc"));
            File.WriteAllText(Path.Combine(dest, "etc", "hostname"), "mine");

            var result = Run(new ImagingEngine(new FakeDeviceAccess()), Request(image, 1, "/etc", dest, ConflictPolicy.Skip));

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "etc", "hostname")));
            Assert.Contains(result.Extraction.Issues, i => i.Reason == ExtractionIssueReason.AlreadyExists);
        }
    }
}
