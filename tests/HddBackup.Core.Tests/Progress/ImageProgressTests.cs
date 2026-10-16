using System.IO;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Progress
{
    public class ImageProgressTests
    {
        private const long GiB = 1L << 30;

        [Fact]
        public void UnknownSize_UsesFileShare()
        {
            var p = new ImageProgress(1000, null);
            Assert.Equal(1000, p.Total);
            Assert.Equal(250, p.Units(250, 9999));
        }

        [Fact]
        public void RawImage_UnitsAreBytes()
        {
            var p = new ImageProgress(8 * GiB, 8 * GiB);
            Assert.Equal(GiB, p.Units(GiB, GiB));
        }

        [Fact]
        public void EmptyStartOfGzipImage_FollowsUncompressedBytes()
        {
            // The reported case: 11.8 GiB of a 29.2 GiB image unpacked from 0.5% of the file.
            long size = (long)(29.2 * GiB);
            var p = new ImageProgress(2 * GiB, size);
            long units = p.Units(2 * GiB / 200, (long)(11.8 * GiB));
            Assert.InRange((double)units / p.Total, 0.40, 0.41);
        }

        [Fact]
        public void DataAtStartOfGzipImage_FollowsFileShare()
        {
            var p = new ImageProgress(2 * GiB, 30 * GiB);
            Assert.Equal(15 * GiB, p.Units(GiB, 3 * GiB));
        }

        [Fact]
        public void EstimateTooSmall_IsCapped()
        {
            var p = new ImageProgress(GiB, 4 * GiB);
            Assert.Equal(4 * GiB, p.Units(GiB / 2, 6 * GiB));
        }

        [Theory]
        [InlineData(0u, 1L, 1L << 32)]
        [InlineData(100u, 50L, 100L)]
        [InlineData(100u, 100L, 100L)]
        [InlineData(100u, 101L, (1L << 32) + 100)]
        // A 29.2 GiB card whose last partition ends 1 MiB before the end of the device.
        [InlineData(1_287_651_328u, 31_351_373_824L, 31_352_422_400L)]
        public void FromTrailer_SmallestCongruentAtLeastBound(uint trailer, long bound, long expected)
        {
            Assert.Equal(expected, ImageHead.FromTrailer(trailer, bound));
        }

        [Theory]
        [InlineData("rpi-like-4k")]
        [InlineData("rpi-like-gpt")]
        public void GzipFixture_EstimateEqualsUncompressedSize(string name)
        {
            long raw = new FileInfo(FixtureImages.RawPath(name)).Length;
            var image = ImageDescriptor.Load(FixtureImages.CompressedPath(name));
            Assert.Null(image.DataSize);
            Assert.Equal(raw, image.ProgressSize);
        }

        [Fact]
        public void RawFixture_ProgressSizeIsFileSize()
        {
            string path = FixtureImages.RawPath("rpi-like-4k");
            Assert.Equal(new FileInfo(path).Length, ImageDescriptor.Load(path).ProgressSize);
        }

        [Fact]
        public void TrailerIsNullForRawImage()
        {
            Assert.Null(ImageHead.ReadGzipTrailerSize(FixtureImages.RawPath("rpi-like-4k")));
        }
    }
}
