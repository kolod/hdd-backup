using System;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    public class FilePreviewTests
    {
        private static FilePreview Preview(FakeFileTree tree, FsEntry file, PreviewMode mode = PreviewMode.Auto)
        {
            return FilePreview.Create(tree, file, mode, CancellationToken.None);
        }

        private static FsEntry Add(FakeFileTree tree, byte[] data) => tree.AddFile(tree.Root, "f", data);

        [Fact]
        public void ATextFile_IsShownAsText_WithWindowsLineBreaks()
        {
            var tree = new FakeFileTree();
            var p = Preview(tree, Add(tree, Encoding.ASCII.GetBytes("one\ntwo\r\nthree\rfour")));
            Assert.Equal(PreviewMode.Text, p.Mode);
            Assert.Equal("one\r\ntwo\r\nthree\r\nfour", p.Text);
            Assert.False(p.Truncated);
            Assert.Equal(p.TotalBytes, p.ShownBytes);
        }

        [Fact]
        public void Utf8Text_ShowsNonAsciiCharacters_AndSkipsABom()
        {
            var tree = new FakeFileTree();
            var data = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("日本語 Привіт")).ToArray();
            var p = Preview(tree, Add(tree, data));
            Assert.Equal(PreviewMode.Text, p.Mode);
            Assert.Equal("日本語 Привіт", p.Text);
        }

        [Fact]
        public void ABinaryFile_IsShownAsHex_InAuto()
        {
            var tree = new FakeFileTree();
            var data = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x00, 0x01, 0x02, 0x03 };
            var p = Preview(tree, Add(tree, data));
            Assert.Equal(PreviewMode.Hex, p.Mode);
            Assert.Equal("00000000  7F 45 4C 46 00 01 02 03                           |.ELF....|", p.Text);
        }

        [Theory]
        [InlineData(new byte[] { 0x41, 0x01, 0x42 })] // control character
        [InlineData(new byte[] { 0x41, 0xFF, 0x42 })] // not UTF-8
        [InlineData(new byte[] { 0xC3, 0x28 })] // broken sequence
        public void NotQuiteTextFiles_AreHexInAuto(byte[] data)
        {
            var tree = new FakeFileTree();
            Assert.Equal(PreviewMode.Hex, Preview(tree, Add(tree, data)).Mode);
        }

        [Fact]
        public void ForcedText_ReplacesNulAndControlCharacters()
        {
            var tree = new FakeFileTree();
            var p = Preview(tree, Add(tree, new byte[] { 0x41, 0x00, 0x42, 0x01, 0x43 }), PreviewMode.Text);
            Assert.Equal(PreviewMode.Text, p.Mode);
            Assert.Equal("A�B�C", p.Text);
            Assert.DoesNotContain('\0', p.Text);
        }

        [Fact]
        public void ForcedHex_OfATextFile()
        {
            var tree = new FakeFileTree();
            var p = Preview(tree, Add(tree, Encoding.ASCII.GetBytes("Hello, world!\n")), PreviewMode.Hex);
            Assert.Equal(PreviewMode.Hex, p.Mode);
            Assert.Equal("00000000  48 65 6C 6C 6F 2C 20 77  6F 72 6C 64 21 0A        |Hello, world!.|", p.Text);
        }

        [Fact]
        public void HexLines_HaveSixteenBytesAndAnOffset()
        {
            var data = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
            var lines = FilePreview.RenderHex(data).Split(new[] { "\r\n" }, StringSplitOptions.None);
            Assert.Equal(3, lines.Length);
            Assert.Equal("00000000  00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  |................|", lines[0]);
            Assert.StartsWith("00000010  10 11 12 13", lines[1]);
            Assert.StartsWith("00000020  20 21 22 23 24 25 26 27  ", lines[2]);
            Assert.EndsWith("| !\"#$%&'|", lines[2]);
            Assert.Equal(lines[0].Length, lines[1].Length);
        }

        [Fact]
        public void AnEmptyFile_HasNoContent()
        {
            var tree = new FakeFileTree();
            var p = Preview(tree, Add(tree, new byte[0]));
            Assert.Equal(string.Empty, p.Text);
            Assert.Equal(0, p.TotalBytes);
            Assert.False(p.Truncated);
        }

        [Fact]
        public void ALargeTextFile_IsCutAtTheLimit_AndOnlyThatMuchIsRead()
        {
            var tree = new FakeFileTree();
            var line = Encoding.ASCII.GetBytes("a line of text in a large file\n");
            var data = new byte[3 << 20];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = line[i % line.Length];
            }

            var p = Preview(tree, Add(tree, data));
            Assert.Equal(PreviewMode.Text, p.Mode);
            Assert.True(p.Truncated);
            Assert.Equal(FilePreview.TextLimit, p.ShownBytes);
            Assert.Equal(3 << 20, p.TotalBytes);
            Assert.True(tree.BytesRead <= FilePreview.TextLimit + FilePreview.AutoProbeBytes, "read " + tree.BytesRead);
        }

        [Fact]
        public void ACutOffUtf8Character_IsNotShownBroken()
        {
            var tree = new FakeFileTree();
            // 3-byte characters: the limit (a power of two) falls inside one.
            var data = new byte[(FilePreview.TextLimit / 3 + 100) * 3];
            for (int i = 0; i < data.Length; i += 3)
            {
                data[i] = 0xE6;
                data[i + 1] = 0x97;
                data[i + 2] = 0xA5; // 日
            }

            var p = Preview(tree, Add(tree, data));
            Assert.Equal(PreviewMode.Text, p.Mode);
            Assert.True(p.Truncated);
            Assert.DoesNotContain('�', p.Text);
            Assert.Equal(0, p.ShownBytes % 3);
            Assert.True(p.ShownBytes <= FilePreview.TextLimit);
            Assert.True(p.ShownBytes > FilePreview.TextLimit - 3);
        }

        [Fact]
        public void ALargeBinaryFile_ShowsTheHexLimit()
        {
            var tree = new FakeFileTree();
            var p = Preview(tree, Add(tree, new byte[1 << 20]), PreviewMode.Hex);
            Assert.Equal(FilePreview.HexLimit, p.ShownBytes);
            Assert.True(p.Truncated);
            Assert.Equal(FilePreview.HexLimit / 16, p.Text.Split(new[] { "\r\n" }, StringSplitOptions.None).Length);
            Assert.True(tree.BytesRead <= FilePreview.HexLimit);
        }

        [Fact]
        public void EntriesWithoutContent_GiveAnEmptyPreview()
        {
            var tree = new FakeFileTree();
            var dir = tree.AddDirectory(tree.Root, "d");
            var link = tree.AddSymlink(tree.Root, "l", "/x");
            var fifo = tree.AddSpecial(tree.Root, "p", SpecialKind.Fifo);
            var secret = tree.AddFile(tree.Root, "s", new byte[10]);
            secret.NotViewable = "the content is encrypted";
            var bad = tree.AddFile(tree.Root, "b", new byte[10]);
            bad.Unreadable = "damaged";
            foreach (var e in new[] { dir, link, fifo, secret, bad })
            {
                var p = Preview(tree, e);
                Assert.Equal(string.Empty, p.Text);
                Assert.Equal(0, p.ShownBytes);
            }

            Assert.Equal(0, tree.BytesRead);
        }

        [Fact]
        public void Cancellation_IsHonoured()
        {
            var tree = new FakeFileTree();
            var file = Add(tree, new byte[200 << 10]);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => FilePreview.Create(tree, file, PreviewMode.Hex, cts.Token));
        }

        [Fact]
        public void FixtureFiles_ArePreviewedLikeTheirKind()
        {
            var (source, view) = FileSystemViewTests.Open("browse-names");
            using (source)
            using (view)
            {
                var text = FilePreview.Create(view, view.GetEntry("/etc/hostname"), PreviewMode.Auto, CancellationToken.None);
                Assert.Equal(PreviewMode.Text, text.Mode);
                Assert.Equal("raspberrypi\r\n", text.Text);

                var binary = FilePreview.Create(view, view.GetEntry("/bin/ls"), PreviewMode.Auto, CancellationToken.None);
                Assert.Equal(PreviewMode.Hex, binary.Mode);
                Assert.StartsWith("00000000  ", binary.Text);

                var link = FilePreview.Create(view, view.GetEntry("/tricky/fast-link"), PreviewMode.Auto, CancellationToken.None);
                Assert.Equal(string.Empty, link.Text);
            }
        }
    }
}
