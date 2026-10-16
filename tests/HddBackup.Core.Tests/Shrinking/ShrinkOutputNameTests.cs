using HddBackup.Core.Shrinking;
using Xunit;

namespace HddBackup.Core.Tests.Shrinking
{
    public class ShrinkOutputNameTests
    {
        [Theory]
        [InlineData(@"D:\img\raspios.img.gz")]
        [InlineData(@"D:\img\raspios.img")]
        [InlineData(@"D:\img\raspios.gz")]
        [InlineData(@"D:\img\raspios.IMG.GZ")]
        public void StripsKnownExtensions(string source)
        {
            Assert.Equal(@"D:\img\raspios_shrunk.img", ShrinkOutputName.Default(source, null, false));
        }

        [Fact]
        public void KeepsUnknownExtension()
        {
            Assert.Equal(@"D:\img\disk.bin_shrunk.img", ShrinkOutputName.Default(@"D:\img\disk.bin", null, false));
        }

        [Fact]
        public void GzipOutput()
        {
            Assert.Equal(@"D:\img\raspios_shrunk.img.gz", ShrinkOutputName.Default(@"D:\img\raspios.img", null, true));
        }

        [Fact]
        public void WholeGibTarget()
        {
            Assert.Equal(@"D:\img\raspios_shrunk_8G.img", ShrinkOutputName.Default(@"D:\img\raspios.img.gz", 8L << 30, false));
        }

        [Fact]
        public void FractionalTargetUsesInvariantCulture()
        {
            // A "16 GB" card: 15 931 539 456 bytes = 14.84 GiB.
            Assert.Equal(@"D:\img\raspios_shrunk_14.8G.img.gz", ShrinkOutputName.Default(@"D:\img\raspios.img", 15931539456L, true));
        }

        [Fact]
        public void FileNameWithoutDirectory()
        {
            Assert.Equal("raspios_shrunk.img", ShrinkOutputName.Default("raspios.img", null, false));
        }
    }
}
