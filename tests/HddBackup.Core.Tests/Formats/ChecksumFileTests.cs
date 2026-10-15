using System.IO;
using System.Text;
using HddBackup.Core.Formats;
using Xunit;

namespace HddBackup.Core.Tests.Formats
{
    public class ChecksumFileTests
    {
        private const string Hash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        [Fact]
        public void Format_IsSha256sumBinaryMode()
        {
            Assert.Equal(Hash + " *disk.img.gz\n", ChecksumFile.FormatEntry(Hash, "disk.img.gz"));
        }

        [Theory]
        [InlineData(Hash + " *disk.img\n")]
        [InlineData(Hash + "  disk.img\n")]
        [InlineData(Hash + " *disk.img\r\n")]
        [InlineData("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855 *disk.img")]
        [InlineData("﻿" + Hash + " *disk.img\n\n")]
        public void Parse_AcceptsCommonVariants(string text)
        {
            var file = ChecksumFile.Parse(text, "x.sha256", ChecksumKind.File);

            Assert.Equal(Hash, file.Sha256);
            Assert.Equal("disk.img", file.Label);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a checksum")]
        [InlineData("e3b0c44298fc *disk.img")]
        [InlineData(Hash + " *a.img\n" + Hash + " *b.img\n")]
        [InlineData("zzb0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *disk.img")]
        public void Parse_RejectsMalformed(string text)
        {
            Assert.Throws<InvalidDataException>(() => ChecksumFile.Parse(text, "x.sha256", ChecksumKind.File));
        }

        [Fact]
        public void WriteAndRead_RoundTrip_UsesLfAndNoBom()
        {
            var dir = TestPaths.NewTempDirectory();
            var path = Path.Combine(dir, "disk.img.sha256");

            ChecksumFile.Write(path, Hash, "disk.img");
            var bytes = File.ReadAllBytes(path);
            var read = ChecksumFile.Read(path, ChecksumKind.Content);

            Assert.Equal(Encoding.ASCII.GetBytes(Hash + " *disk.img\n"), bytes);
            Assert.Equal(Hash, read.Sha256);
            Assert.Equal(ChecksumKind.Content, read.Kind);
            Assert.Equal(path, read.Path);
        }
    }
}
