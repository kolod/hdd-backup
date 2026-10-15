using System;
using System.IO;
using HddBackup.Core.Devices;
using Xunit;

namespace HddBackup.Core.Tests.DeviceTests
{
    public class FileBlockDeviceTests
    {
        [Fact]
        public void CreateWriteReadBack_AndUnwrittenAreasAreZero()
        {
            var path = Path.Combine(TestPaths.NewTempDirectory(), "dev.img");
            var data = new byte[1024];
            new Random(1).NextBytes(data);
            using (var dev = FileBlockDevice.Create(path, 1 << 20))
            {
                dev.Write(4096, data, 0, data.Length);
                var back = new byte[2048];
                dev.Read(3584, back, 0, back.Length);
                Assert.All(new ArraySegment<byte>(back, 0, 512), b => Assert.Equal(0, b));
                Assert.Equal(data, new ArraySegment<byte>(back, 512, 1024));
                Assert.Equal(1L << 20, dev.Size);
            }
        }

        [Fact]
        public void ReadOnly_RefusesWrites()
        {
            var path = Path.Combine(TestPaths.NewTempDirectory(), "ro.img");
            File.WriteAllBytes(path, new byte[4096]);
            using (var dev = FileBlockDevice.OpenRead(path))
            {
                Assert.False(dev.CanWrite);
                Assert.Throws<InvalidOperationException>(() => dev.Write(0, new byte[512], 0, 512));
            }
        }

        [Fact]
        public void UnalignedAccess_IsRejected()
        {
            var path = Path.Combine(TestPaths.NewTempDirectory(), "a.img");
            using (var dev = FileBlockDevice.Create(path, 4096))
            {
                Assert.Throws<ArgumentException>(() => dev.Read(100, new byte[512], 0, 512));
            }
        }
    }
}
