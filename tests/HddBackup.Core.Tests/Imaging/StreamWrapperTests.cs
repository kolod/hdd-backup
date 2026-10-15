using System;
using System.IO;
using System.Security.Cryptography;
using HddBackup.Core.Formats;
using HddBackup.Core.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class StreamWrapperTests
    {
        private static byte[] Data(int length)
        {
            var data = new byte[length];
            new Random(42).NextBytes(data);
            return data;
        }

        private static string Sha(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return Sha256Accumulator.ToHex(sha.ComputeHash(data));
            }
        }

        [Fact]
        public void HashingStream_Write_HashesAllBytes()
        {
            var data = Data(1_000_000);
            var target = new MemoryStream();
            using (var hashing = new HashingStream(target, leaveOpen: true))
            {
                hashing.Write(data, 0, 300_000);
                hashing.Write(data, 300_000, data.Length - 300_000);
                Assert.Equal(Sha(data), hashing.FinishHash());
                Assert.Equal(data.Length, hashing.BytesProcessed);
            }

            Assert.Equal(data, target.ToArray());
        }

        [Fact]
        public void HashingStream_Read_HashesEachByteOnce_EvenAfterSeekingBack()
        {
            var data = Data(500_000);
            using (var hashing = new HashingStream(new MemoryStream(data), leaveOpen: false))
            {
                var buffer = new byte[100_000];
                hashing.Read(buffer, 0, buffer.Length);
                hashing.Read(buffer, 0, buffer.Length);
                hashing.Position = 50_000; // re-read an already hashed region
                hashing.Read(buffer, 0, buffer.Length);
                while (hashing.Read(buffer, 0, buffer.Length) > 0)
                {
                }

                Assert.Equal(Sha(data), hashing.FinishHash());
                Assert.Equal(data.Length, hashing.HashedUpTo);
            }
        }

        [Fact]
        public void HashingStream_SeekingPastHashedRegion_IsRejected()
        {
            using (var hashing = new HashingStream(new MemoryStream(Data(1000)), leaveOpen: false))
            {
                Assert.Throws<InvalidOperationException>(() => hashing.Position = 10);
            }
        }

        [Fact]
        public void Sha256Accumulator_MatchesFramework()
        {
            var data = Data(3_000_001);
            var acc = new Sha256Accumulator();
            acc.Append(data, 0, 1);
            acc.Append(data, 1, data.Length - 1);
            Assert.Equal(Sha(data), acc.Finish());
            Assert.Equal(Sha(data), acc.Finish()); // idempotent
        }
    }
}
