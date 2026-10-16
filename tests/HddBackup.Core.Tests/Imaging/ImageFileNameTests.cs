using HddBackup.Core.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class ImageFileNameTests
    {
        [Theory]
        [InlineData(@"D:\a\disk.img.gz", false, @"D:\a\disk.img")]
        [InlineData(@"D:\a\disk.img", true, @"D:\a\disk.img.gz")]
        [InlineData(@"D:\a\disk", true, @"D:\a\disk.img.gz")]
        [InlineData(@"D:\a\disk", false, @"D:\a\disk.img")]
        [InlineData(@"D:\a\disk.IMG.GZ", false, @"D:\a\disk.img")]
        // The file dialog replaces only the last extension when the filter changes.
        [InlineData(@"D:\a\disk.img.img", false, @"D:\a\disk.img")]
        [InlineData(@"D:\a\disk.img.gz.img", false, @"D:\a\disk.img")]
        [InlineData(@"D:\a\disk.img.gz.gz", true, @"D:\a\disk.img.gz")]
        [InlineData(@"D:\a\disk.gz", true, @"D:\a\disk.img.gz")]
        [InlineData(@"D:\a\my.backup.img", true, @"D:\a\my.backup.img.gz")]
        public void WithExtension_LeavesExactlyOne(string path, bool gzip, string expected)
        {
            Assert.Equal(expected, ImageFileName.WithExtension(path, gzip));
        }

        [Fact]
        public void WithExtension_EmptyStaysEmpty()
        {
            Assert.Equal(string.Empty, ImageFileName.WithExtension(string.Empty, true));
        }

        [Fact]
        public void Stem_KeepsBareExtensionName()
        {
            Assert.Equal(".img", ImageFileName.Stem(".img"));
        }
    }
}
