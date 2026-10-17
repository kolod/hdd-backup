using System;
using System.IO;
using System.Runtime.InteropServices;
using HddBackup.Core.Errors;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class FileIdentityTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(string newLink, string existing, IntPtr reserved);

        private static string NewFile(string dir, string name, string text = "data")
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        /// <summary>A second name for the same file (no admin rights needed on NTFS).</summary>
        private static string HardLink(string existing, string link)
        {
            Assert.True(CreateHardLink(link, existing, IntPtr.Zero), "CreateHardLink failed: " + Marshal.GetLastWin32Error());
            return link;
        }

        [Fact]
        public void SamePath_DifferentCase_IsSame()
        {
            var file = NewFile(TestPaths.NewTempDirectory(), "disk.img");
            Assert.True(FileIdentity.IsSameFile(file, file.ToUpperInvariant()));
        }

        [Fact]
        public void RelativeAndAbsolute_IsSame()
        {
            var file = NewFile(TestPaths.NewTempDirectory(), "disk.img");
            var dir = Path.GetDirectoryName(file);
            Assert.True(FileIdentity.IsSameFile(file, Path.Combine(dir, "..", Path.GetFileName(dir), "disk.img")));
        }

        [Fact]
        public void HardLink_IsSame()
        {
            var dir = TestPaths.NewTempDirectory();
            var file = NewFile(dir, "disk.img");
            var link = HardLink(file, Path.Combine(dir, "other-name.img"));
            Assert.True(FileIdentity.IsSameFile(file, link));
        }

        [Fact]
        public void DifferentFiles_WithSameContent_AreNotSame()
        {
            var dir = TestPaths.NewTempDirectory();
            Assert.False(FileIdentity.IsSameFile(NewFile(dir, "a.img"), NewFile(dir, "b.img")));
        }

        [Fact]
        public void MissingOutput_IsNotSame()
        {
            var dir = TestPaths.NewTempDirectory();
            Assert.False(FileIdentity.IsSameFile(NewFile(dir, "a.img"), Path.Combine(dir, "new.img")));
        }

        [Fact]
        public void Convert_ToHardLinkOfSource_IsRefused_AndSourceKept()
        {
            var access = new FakeDeviceAccess();
            var device = BackupDeviceTests.BuildDevice("MBR", 512);
            var dir = TestPaths.NewTempDirectory();
            var source = Path.Combine(dir, "disk.img");
            EngineHarness.Backup(access, access.Add(1, device), source);
            // A name that looks like a different, compressed image but is the source itself.
            var alias = HardLink(source, Path.Combine(dir, "alias.img.gz"));
            var before = EngineHarness.ShaOfFile(source);

            var result = EngineHarness.Run(access, new OperationRequest
            {
                Kind = OperationKind.ConvertImage,
                ImagePath = source,
                OutputPath = alias,
                EjectAfter = false
            });

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.InvalidRequest, result.ErrorCode);
            Assert.Equal(before, EngineHarness.ShaOfFile(source));
        }

        [Fact]
        public void Shrink_ToHardLinkOfSource_IsRefused()
        {
            var dir = TestPaths.NewTempDirectory();
            var source = Path.Combine(dir, "pi.img");
            File.Copy(FixtureImages.RawPath("rpi-like-4k"), source);
            var alias = HardLink(source, Path.Combine(dir, "pi_shrunk.img"));

            var ex = Assert.Throws<ImagingException>(() => ImageShrinker.Run(
                new ShrinkRequest { SourcePath = source, OutputPath = alias },
                new PhaseModel(false),
                System.Threading.CancellationToken.None));

            Assert.Equal(ErrorCode.InvalidRequest, ex.Code);
        }

        [Fact]
        public void FirstFree_ReturnsPathWhenFree()
        {
            var path = Path.Combine(TestPaths.NewTempDirectory(), "pi_shrunk_8G.img");
            Assert.Equal(path, ImageFileName.FirstFree(path));
        }

        [Fact]
        public void FirstFree_SkipsExistingImagesAndChecksumFiles()
        {
            var dir = TestPaths.NewTempDirectory();
            NewFile(dir, "pi_shrunk_8G.img");
            NewFile(dir, "pi_shrunk_8G (2).img.sha256"); // checksum left from a deleted result
            Assert.Equal(Path.Combine(dir, "pi_shrunk_8G (3).img"), ImageFileName.FirstFree(Path.Combine(dir, "pi_shrunk_8G.img")));
        }

        [Fact]
        public void FirstFree_KeepsGzipExtension()
        {
            var dir = TestPaths.NewTempDirectory();
            NewFile(dir, "pi.img.gz");
            Assert.Equal(Path.Combine(dir, "pi (2).img.gz"), ImageFileName.FirstFree(Path.Combine(dir, "pi.img.gz")));
        }
    }
}
