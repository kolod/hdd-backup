using System;
using System.IO;
using HddBackup.Core.Devices;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.DeviceTests
{
    public sealed class CachedReadDeviceTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void ReadsMatchTheDevice_AcrossChunksAndTheShortLastChunk_AndNeverWrite()
        {
            // 70 MiB + 3 sectors: more chunks than the cache keeps, and a last chunk that is not full
            long size = (70L * 1024 * 1024) + (3 * 512);
            string path = Path.Combine(_temp.New(), "cache.img");
            var data = new byte[size];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(path, data);
            using (var inner = FileBlockDevice.OpenRead(path))
            using (var cached = new CachedReadDevice(inner))
            {
                Assert.False(cached.CanWrite);
                Assert.Equal(inner.Size, cached.Size);
                var rnd = new Random(11);
                for (int i = 0; i < 400; i++)
                {
                    int count = 512 * rnd.Next(1, 1200);
                    long offset = 512L * rnd.Next(0, (int)((size - count) / 512) + 1);
                    var got = new byte[count];
                    Assert.Equal(count, cached.Read(offset, got, 0, count));
                    for (int k = 0; k < count; k += 997)
                    {
                        Assert.Equal(data[offset + k], got[k]);
                    }

                    Assert.Equal(data[offset + count - 1], got[count - 1]);
                }

                var tail = new byte[3 * 512];
                cached.Read(size - tail.Length, tail, 0, tail.Length);
                Assert.Equal(data[size - 1], tail[tail.Length - 1]);
                Assert.Throws<InvalidOperationException>(() => cached.Write(0, new byte[512], 0, 512));
            }
        }
    }
}
