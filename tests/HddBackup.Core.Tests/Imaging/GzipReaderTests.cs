using System;
using System.IO;
using System.IO.Compression;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class GzipReaderTests
    {
        private static byte[] Content(int length)
        {
            var data = new byte[length];
            new Random(7).NextBytes(data);
            for (int i = 0; i < length; i += 3)
            {
                data[i] = 0; // make it compressible
            }

            return data;
        }

        private static byte[] ReadAll(string path, out string fileHash)
        {
            using (var reader = new ImageReader(path))
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[100_000];
                int n;
                while ((n = reader.ReadFull(buffer, 0, buffer.Length)) > 0)
                {
                    ms.Write(buffer, 0, n);
                }

                fileHash = reader.FinishFileHash();
                return ms.ToArray();
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(5)]
        public void MultiMemberGzip_IsReadCompletely(int members)
        {
            var content = Content(3_000_000);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "multi.img.gz");
            EngineHarness.WriteMultiMemberGzip(path, content, members);

            var read = ReadAll(path, out var fileHash);

            Assert.Equal(content, read);
            Assert.Equal(EngineHarness.ShaOfFile(path), fileHash);
        }

        [Fact]
        public void MultiMemberGzip_RestoresThroughEngine()
        {
            var content = Content(4 << 20);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "pigz.img.gz");
            EngineHarness.WriteMultiMemberGzip(path, content, 3);
            var access = new FakeDeviceAccess();
            var target = new MemoryBlockDevice(4 << 20, 512);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: true);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(content, target.Data);
        }

        [Fact]
        public void TrailingZeroPadding_IsIgnored()
        {
            var content = Content(500_000);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "padded.img.gz");
            EngineHarness.WriteMultiMemberGzip(path, content, 1);
            using (var f = new FileStream(path, FileMode.Append))
            {
                f.Write(new byte[4096], 0, 4096);
            }

            var read = ReadAll(path, out var fileHash);

            Assert.Equal(content, read);
            Assert.Equal(EngineHarness.ShaOfFile(path), fileHash);
        }

        [Theory]
        [InlineData(4)]
        [InlineData(20)]
        [InlineData(5000)]
        public void TruncatedGzip_IsReportedAsCorrupt(int cut)
        {
            var content = Content(2_000_000);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "cut.img.gz");
            EngineHarness.WriteMultiMemberGzip(path, content, 1);
            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.AsSpanCompat(bytes.Length - cut));

            var ex = Assert.Throws<ImagingException>(() => ReadAll(path, out _));
            Assert.Equal(ErrorCode.CorruptImage, ex.Code);
            Assert.NotNull(ex.Offset);
        }

        [Fact]
        public void GarbageInsideDeflateData_IsReportedAsCorrupt()
        {
            var content = Content(2_000_000);
            var path = Path.Combine(TestPaths.NewTempDirectory(), "bad.img.gz");
            EngineHarness.WriteMultiMemberGzip(path, content, 1);
            var bytes = File.ReadAllBytes(path);
            for (int i = 100; i < 400; i++)
            {
                bytes[i] = 0xFF;
            }

            File.WriteAllBytes(path, bytes);

            var ex = Assert.Throws<ImagingException>(() => ReadAll(path, out _));
            Assert.Equal(ErrorCode.CorruptImage, ex.Code);
        }

        [Fact]
        [Trait("Category", "Slow")]
        public async System.Threading.Tasks.Task LogicalSizeOver4GB_IsStreamedCorrectly()
        {
            const long size = (4L << 30) + (64L << 20); // 4.0625 GiB of zeros
            var path = Path.Combine(TestPaths.NewTempDirectory(), "big.img.gz");
            var zeros = new byte[4 << 20];
            using (var file = File.Create(path))
            using (var gz = new GZipStream(file, CompressionLevel.Fastest))
            {
                for (long written = 0; written < size; written += zeros.Length)
                {
                    gz.Write(zeros, 0, zeros.Length);
                }
            }

            var access = new FakeDeviceAccess();
            var target = new NullBlockDevice(size);
            var info = access.Add(2, target);

            var result = await EngineHarness.NewEngine(access).RunAsync(
                new OperationRequest { Kind = OperationKind.RestoreDevice, Device = info, ImagePath = path, VerifyAfter = false },
                System.Threading.CancellationToken.None);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.Equal(size, result.DataBytes);
            Assert.Equal(size, target.BytesWritten);
        }
    }

    internal static class ArrayExtensions
    {
        public static byte[] AsSpanCompat(this byte[] source, int length)
        {
            var copy = new byte[length];
            Array.Copy(source, copy, length);
            return copy;
        }
    }
}
