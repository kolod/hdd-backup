using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Errors;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Diagnostics
{
    public sealed class DiagnosticsRunnerTests : IDisposable
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

        private static int FirstBrowsable(string image)
        {
            using (var s = BrowseSource.OpenImage(image))
            {
                return s.Browsable.First().Number;
            }
        }

        private (FakeDeviceAccess Access, DeviceInfo Device, ReadOnlyBlockDevice Raw) Attach(string fixture)
        {
            var raw = new ReadOnlyBlockDevice(FileBlockDevice.OpenRead(FixtureImages.RawPath(fixture)));
            _devices.Add(raw);
            var access = new FakeDeviceAccess();
            return (access, access.Add(7, raw), raw);
        }

        [Fact]
        public void RunsAToolOnAnImageAndReleasesTheFile()
        {
            string copy = Path.Combine(_temp.New(), "copy.img");
            File.Copy(FixtureImages.RawPath("rpi-like-4k"), copy);
            var source = new DiagnosticsSource { ImagePath = copy, PartitionNumber = FirstBrowsable(copy), DisplayName = "copy.img" };

            string seen = DiagnosticsRunner.Run(null, source, (reader, effective) => effective.DisplayName + ":" + reader.IsFile("/etc/passwd"), CancellationToken.None);

            Assert.Equal("copy.img:True", seen);
            File.Delete(copy);
            Assert.False(File.Exists(copy));
        }

        [Fact]
        public void RunsAToolOnADeviceThroughTheReadHandleOnly()
        {
            var (access, device, raw) = Attach("rpi-like-4k");
            int number;
            using (var s = BrowseSource.OpenDevice(access, device))
            {
                number = s.Browsable.First().Number;
            }

            int opensBefore = access.ReadOpens;
            var source = new DiagnosticsSource { Device = device, PartitionNumber = number, DisplayName = "card" };

            bool linux = DiagnosticsRunner.Run(access, source, (r, s) => LinuxRoot.Detect(r).IsLinux, CancellationToken.None);

            Assert.True(linux);
            Assert.Equal(access.ReadOpens, access.ReadReleases);
            Assert.True(access.ReadOpens > opensBefore);
            Assert.Equal(0, access.ExclusiveOpens);
            Assert.False(access.AnyWriteAttemptOnReadHandle);
            Assert.Equal(0, raw.WriteCalls);
        }

        [Fact]
        public void ReleasesTheSourceWhenTheToolFailsOrIsCancelled()
        {
            var (access, device, _) = Attach("rpi-like-4k");
            int number;
            using (var s = BrowseSource.OpenDevice(access, device))
            {
                number = s.Browsable.First().Number;
            }

            var source = new DiagnosticsSource { Device = device, PartitionNumber = number };

            Assert.Throws<InvalidOperationException>(() =>
                DiagnosticsRunner.Run<int>(access, source, (r, s) => throw new InvalidOperationException("boom"), CancellationToken.None));
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                DiagnosticsRunner.Run(access, source, (r, s) => 1, cts.Token));

            Assert.Equal(access.ReadOpens, access.ReadReleases);
        }

        [Fact]
        public void ADeviceThatFailsMidwayThrowsAnImagingExceptionAndIsReleased()
        {
            var (access, device, raw) = Attach("rpi-like-4k");
            int number;
            using (var s = BrowseSource.OpenDevice(access, device))
            {
                number = s.Browsable.First().Number;
            }

            raw.FailAfterReads = raw.ReadCalls + 3;
            raw.FailWith = new ImagingException(ErrorCode.DeviceRemoved, "unplugged");
            var source = new DiagnosticsSource { Device = device, PartitionNumber = number };

            var ex = Assert.Throws<ImagingException>(() =>
                DiagnosticsRunner.Run(access, source, (r, s) => r.ReadText("/etc/passwd"), CancellationToken.None));

            Assert.Equal(ErrorCode.DeviceRemoved, ex.Code);
            Assert.Equal(access.ReadOpens, access.ReadReleases);
        }

        [Fact]
        public void APartitionThatCannotBeBrowsedIsRefused()
        {
            var source = new DiagnosticsSource { ImagePath = FixtureImages.RawPath("rpi-like-4k"), PartitionNumber = 99 };

            Assert.Equal(ErrorCode.InvalidRequest, Assert.Throws<ImagingException>(() =>
                DiagnosticsRunner.Run(null, source, (r, s) => 1, CancellationToken.None)).Code);
        }

        [Fact]
        public void TheImageIsNotModified()
        {
            string image = FixtureImages.RawPath("rpi-like-4k");
            string before = HddBackup.Core.Tests.Browsing.BrowseSourceTests.Sha256Of(image);
            var source = new DiagnosticsSource { ImagePath = image, PartitionNumber = FirstBrowsable(image) };

            DiagnosticsRunner.Run(null, source, (r, s) => r.List("/etc").Count + r.ReadLines("/etc/passwd").Count, CancellationToken.None);

            Assert.Equal(before, HddBackup.Core.Tests.Browsing.BrowseSourceTests.Sha256Of(image));
        }

        [Fact]
        public void TheEffectiveSourceKnowsTheStateAndTheOtherPartitions()
        {
            string image = FixtureImages.RawPath("rpi-like-4k");
            var source = new DiagnosticsSource { ImagePath = image, PartitionNumber = FirstBrowsable(image) };

            var effective = DiagnosticsRunner.Run(null, source, (r, s) => s, CancellationToken.None);

            Assert.NotNull(effective.State);
        }

        [Fact]
        public void ALinuxRootNeedsPasswdAndASystemMarker()
        {
            Assert.True(LinuxRoot.Detect(new LinuxRootBuilder().BasicLinux().Reader()).IsLinux);
            Assert.True(LinuxRoot.Detect(new LinuxRootBuilder().File("/etc/passwd", "root:x:0:0::/root:/bin/sh\n").Dir("/bin").Reader()).IsLinux);
            Assert.False(LinuxRoot.Detect(new LinuxRootBuilder().File("/etc/passwd", "root:x:0:0::/root:/bin/sh\n").Reader()).IsLinux);
            Assert.False(LinuxRoot.Detect(new LinuxRootBuilder().OsRelease("debian").Dir("/usr/bin").Reader()).IsLinux);
            Assert.False(LinuxRoot.Detect(new LinuxRootBuilder().Reader()).IsLinux);
        }

        [Fact]
        public void RaspberryPiOsIsRecognised()
        {
            var raspbian = LinuxRoot.Detect(new LinuxRootBuilder().BasicLinux("raspbian", "Raspbian GNU/Linux 12 (bookworm)").Reader());
            var bookworm = LinuxRoot.Detect(new LinuxRootBuilder().BasicLinux("debian", "Debian GNU/Linux 12").File("/etc/rpi-issue", "Raspberry Pi reference 2024-01-01\n").Reader());
            var plain = LinuxRoot.Detect(new LinuxRootBuilder().BasicLinux("debian", "Debian GNU/Linux 12").Reader());

            Assert.True(raspbian.IsRaspberryPiOs);
            Assert.Equal("raspbian", raspbian.DistributionId);
            Assert.True(bookworm.IsRaspberryPiOs);
            Assert.False(plain.IsRaspberryPiOs);
            Assert.Equal("Debian GNU/Linux 12", plain.PrettyName);
        }

        [Fact]
        public void DataOnlyFileSystemIsNotALinuxRoot()
        {
            string image = FixtureImages.RawPath("browse-large");
            var source = new DiagnosticsSource { ImagePath = image, PartitionNumber = FirstBrowsable(image) };

            Assert.False(DiagnosticsRunner.Run(null, source, (r, s) => LinuxRoot.Detect(r).IsLinux, CancellationToken.None));
        }

        [Fact]
        public void TheReferenceRootOfFeature008LooksLikeLinux()
        {
            string image = FixtureImages.RawPath("rpi-like-4k");
            var source = new DiagnosticsSource { ImagePath = image, PartitionNumber = FirstBrowsable(image) };

            Assert.True(DiagnosticsRunner.Run(null, source, (r, s) => LinuxRoot.Detect(r).IsLinux, CancellationToken.None));
        }
    }
}
