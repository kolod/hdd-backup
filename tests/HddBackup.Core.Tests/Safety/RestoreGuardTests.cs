using System.IO;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Safety
{
    public class RestoreGuardTests
    {
        private static (FakeDeviceAccess Access, string Image, MemoryBlockDevice Source) PrepareImage(long size = 4 << 20)
        {
            var source = TestImageBuilder.Mbr(size, 512, new TestPartition(1 << 20, size - (2 << 20), 0x0C));
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img");
            Assert.Equal(OperationState.Completed, EngineHarness.Backup(access, access.Add(1, source), path).State);
            return (access, path, source);
        }

        private static void AssertRefusedWithoutWriting(FakeDeviceAccess access, DeviceInfo target, MemoryBlockDevice device, string image, ErrorCode expected, bool confirmExternal = false)
        {
            var before = (byte[])device.Data.Clone();
            var request = new OperationRequest
            {
                Kind = OperationKind.RestoreDevice,
                Device = target,
                ImagePath = image,
                ExternalDiskConfirmed = confirmExternal
            };

            var ex = Assert.Throws<ImagingException>(() => EngineHarness.NewEngine(access).Validate(request));
            Assert.Equal(expected, ex.Code);

            var result = EngineHarness.Run(access, request);
            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(expected, result.ErrorCode);
            Assert.Equal(0, device.WriteCalls);
            Assert.Equal(before, device.Data);
        }

        [Fact]
        public void OversizeImage_IsRefused()
        {
            var (access, image, _) = PrepareImage(4 << 20);
            var small = new MemoryBlockDevice(2 << 20, 512);
            AssertRefusedWithoutWriting(access, access.Add(2, small), small, image, ErrorCode.SizeMismatch);
        }

        [Fact]
        public void WriteProtectedTarget_IsRefused()
        {
            var (access, image, _) = PrepareImage();
            var target = new MemoryBlockDevice(4 << 20, 512);
            AssertRefusedWithoutWriting(access, access.Add(2, target, d => d.IsWriteProtected = true), target, image, ErrorCode.WriteProtected);
        }

        [Fact]
        public void TargetHostingTheExecutable_IsRefused()
        {
            var (access, image, _) = PrepareImage();
            var target = new MemoryBlockDevice(4 << 20, 512);
            AssertRefusedWithoutWriting(access, access.Add(2, target, d => d.HostsExecutable = true), target, image, ErrorCode.TargetHostsExecutable);
        }

        [Fact]
        public void ImageStoredOnTarget_IsRefused()
        {
            var (access, image, _) = PrepareImage();
            var target = new MemoryBlockDevice(4 << 20, 512);
            var info = access.Add(2, target);
            access.PathInfo = new PathInfo { DiskNumbers = new[] { 2 }, FreeBytes = long.MaxValue };
            AssertRefusedWithoutWriting(access, info, target, image, ErrorCode.ImageOnTarget);
        }

        [Fact]
        public void ExternalDisk_RequiresConfirmation()
        {
            var (access, image, source) = PrepareImage();
            var target = new MemoryBlockDevice(4 << 20, 512);
            var info = access.Add(2, target, d => d.IsRemovableMedia = false);
            AssertRefusedWithoutWriting(access, info, target, image, ErrorCode.ExternalDiskNotConfirmed);

            var warnings = EngineHarness.NewEngine(access).Validate(new OperationRequest
            {
                Kind = OperationKind.RestoreDevice,
                Device = info,
                ImagePath = image,
                ExternalDiskConfirmed = true
            });
            Assert.Contains(ValidationWarning.ExternalDisk, warnings);
        }

        [Fact]
        public void LargerTarget_ProducesWarning()
        {
            var (access, image, _) = PrepareImage();
            var info = access.Add(2, new MemoryBlockDevice(8 << 20, 512));
            var warnings = EngineHarness.NewEngine(access).Validate(new OperationRequest
            {
                Kind = OperationKind.RestoreDevice,
                Device = info,
                ImagePath = image
            });
            Assert.Contains(ValidationWarning.TargetLarger, warnings);
        }

        [Fact]
        public void DeviceSizeChangedSinceListing_IsRefusedBeforeWriting()
        {
            var (access, image, _) = PrepareImage();
            var target = new MemoryBlockDevice(8 << 20, 512);
            var info = access.Add(2, target);
            info.Size = 16 << 20; // stale listing

            var result = EngineHarness.Restore(access, info, image);

            Assert.Equal(ErrorCode.DeviceChanged, result.ErrorCode);
            Assert.Equal(0, target.WriteCalls);
            Assert.Equal(1, access.ExclusiveReleases);
        }

        [Fact]
        public void GzipImageLargerThanTarget_FailsWithOffset_AndReleasesDevice()
        {
            var source = TestImageBuilder.Mbr(4 << 20, 512, new TestPartition(1 << 20, 2 << 20, 0x0C));
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            EngineHarness.Backup(access, access.Add(1, source), path);
            var small = new MemoryBlockDevice(2 << 20, 512);

            var result = EngineHarness.Restore(access, access.Add(2, small), path);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.SizeMismatch, result.ErrorCode);
            Assert.Equal(2L << 20, result.ErrorOffset);
            Assert.True(result.SafeToRemove);
            Assert.False(result.ContentComplete);
            Assert.Equal(1, access.ExclusiveReleases);
            Assert.Equal(small.WriteCalls, small.WritesAtLastFlush);
        }
    }
}
