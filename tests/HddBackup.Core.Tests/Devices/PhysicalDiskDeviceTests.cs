using HddBackup.Core.Devices;
using HddBackup.Core.Native;
using Xunit;

namespace HddBackup.Core.Tests.DeviceTests
{
    public class PhysicalDiskDeviceTests
    {
        [Theory]
        [InlineData(NativeMethods.ErrorInvalidFunction)]
        [InlineData(NativeMethods.ErrorNotSupported)]
        public void FlushRejectedByDevice_IsNotAFailure(int error)
        {
            Assert.True(PhysicalDiskDevice.IsFlushUnsupported(error));
        }

        [Theory]
        [InlineData(NativeMethods.ErrorNotReady)]
        [InlineData(NativeMethods.ErrorWriteProtect)]
        [InlineData(NativeMethods.ErrorDeviceNotConnected)]
        [InlineData(1117)] // ERROR_IO_DEVICE
        public void RealFlushErrors_StillFail(int error)
        {
            Assert.False(PhysicalDiskDevice.IsFlushUnsupported(error));
        }
    }
}
