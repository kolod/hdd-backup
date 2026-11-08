using System;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>SC-001 / SC-002: editing the content of a file leaves a consistent file system and touches nothing else.</summary>
    public class ContentEditTests
    {
        private const uint Now = 1760086400;

        private static byte[] Text(int length)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (sb.Length < length)
            {
                sb.Append("edited line ").Append(i++).Append('\n');
            }

            return Encoding.UTF8.GetBytes(sb.ToString().Substring(0, length));
        }

        private static void Apply(EditImage img, Ext4Editor editor)
        {
            editor.Writes.ApplyTo(img.Device, null, CancellationToken.None);
        }

        private static void EditAndVerify(string fixture, string path, byte[] content, string scenario)
        {
            using (var img = EditImage.Copy(fixture))
            {
                EditAssert.CheckerClean(img.Device, fixture + " before");
                var before = EditAssert.Snapshot(img.Device);
                string hash = img.Sha256();

                var editor = new Ext4Editor(img.Device, Now);
                editor.ReplaceContent(path, content);

                Assert.Equal(hash, img.Sha256());
                Assert.True(editor.Writes.Writes.Count > 0);
                Apply(img, editor);

                EditAssert.CheckerClean(img.Device, fixture + " after " + scenario);
                Assert.Equal(content, EditAssert.Content(img.Device, path));
                var after = EditAssert.Snapshot(img.Device);
                EditAssert.TreesEqualExcept(before, after, path);
                Assert.Equal(content.Length, after[path].Size);
                Assert.Equal(Now, after[path].Mtime);
                EditAssert.ExportForCi(fixture + "-" + scenario, img, after);
            }
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        [InlineData("edit-nocsum")]
        [InlineData("edit-uninit")]
        public void GrowAndShrinkAFile(string fixture)
        {
            EditAndVerify(fixture, "/etc/hostname", Encoding.UTF8.GetBytes("raspberrypi-lab\n"), "short");
            EditAndVerify(fixture, "/etc/hostname", Text(5000), "grow");
            EditAndVerify(fixture, "/etc/hostname", Text(100000), "big");
            EditAndVerify(fixture, "/etc/hostname", new byte[0], "empty");
            EditAndVerify(fixture, "/etc/fstab", Text(1), "one-byte");
        }

        [Theory]
        [InlineData("edit-basic", 4096)]
        [InlineData("edit-1k", 1024)]
        public void AcrossBlockBoundaries(string fixture, int bs)
        {
            foreach (int length in new[] { bs - 1, bs, bs + 1, (2 * bs) - 1, 2 * bs, (2 * bs) + 1 })
            {
                EditAndVerify(fixture, "/etc/fstab", Text(length), "len" + length);
            }
        }

        [Fact]
        public void ShrinkALargeFileAndEditAnEmptyFile()
        {
            EditAndVerify("edit-basic", "/home/pi/big.txt", Text(10), "shrink-big");
            EditAndVerify("edit-basic", "/etc/empty", Text(3000), "fill-empty");
        }

        [Fact]
        public void ACrLfFileKeepsItsBytes()
        {
            EditAndVerify("edit-basic", "/etc/crlf.txt", Encoding.UTF8.GetBytes("a\r\nb\r\nc"), "crlf");
        }

        [Fact]
        public void FilesWithAnXattrBlockAndHardLinksKeepThem()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var before = EditAssert.Snapshot(img.Device);
                var editor = new Ext4Editor(img.Device, Now);
                editor.ReplaceContent("/etc/with-xattr", Text(2000));
                editor.ReplaceContent("/home/pi/a", Text(7000));
                Apply(img, editor);

                EditAssert.CheckerClean(img.Device, "xattr and hard link");
                var after = EditAssert.Snapshot(img.Device);
                Assert.Equal(EditAssert.Content(img.Device, "/home/pi/a"), EditAssert.Content(img.Device, "/home/pi/b"));
                Assert.Equal(after["/home/pi/a"].Inode, after["/home/pi/b"].Inode);
                Assert.Equal((ushort)2, after["/home/pi/a"].Links);
                EditAssert.TreesEqualExcept(before, after, "/etc/with-xattr", "/home/pi/a", "/home/pi/b");
            }
        }

        [Fact]
        public void SeveralChangesInOneSession()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var before = EditAssert.Snapshot(img.Device);
                var editor = new Ext4Editor(img.Device, Now);
                editor.ReplaceContent("/etc/hostname", Text(300));
                editor.ReplaceContent("/etc/hostname", Text(9000));
                editor.ReplaceContent("/etc/fstab", Text(1000));
                Apply(img, editor);

                EditAssert.CheckerClean(img.Device, "several");
                var after = EditAssert.Snapshot(img.Device);
                Assert.Equal(Text(9000), EditAssert.Content(img.Device, "/etc/hostname"));
                EditAssert.TreesEqualExcept(before, after, "/etc/hostname", "/etc/fstab");
            }
        }

        [Fact]
        public void TheNearlyFullFileSystemRefusesBeforeWritingAnything()
        {
            using (var img = EditImage.Copy("edit-full"))
            {
                string hash = img.Sha256();
                var editor = new Ext4Editor(img.Device, Now);

                var ex = Assert.Throws<EditException>(() => editor.ReplaceContent("/etc/fstab", Text(200000)));

                Assert.Equal(EditError.NoSpace, ex.Code);
                Assert.True(ex.Needed > ex.Available);
                Assert.Empty(editor.Writes.Writes);
                Assert.Equal(hash, img.Sha256());
            }
        }

        [Fact]
        public void ASmallGrowthFitsTheNearlyFullFileSystem()
        {
            using (var img = EditImage.Copy("edit-full"))
            {
                var editor = new Ext4Editor(img.Device, Now);
                editor.ReplaceContent("/etc/fstab", Text(2500));
                Apply(img, editor);

                EditAssert.CheckerClean(img.Device, "full");
                Assert.Equal(Text(2500), EditAssert.Content(img.Device, "/etc/fstab"));
            }
        }

        [Fact]
        public void UnknownAndNonRegularPathsAreRefused()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var editor = new Ext4Editor(img.Device, Now);

                Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => editor.ReplaceContent("/etc/nope", Text(5))).Code);
                Assert.Equal(EditError.NotRegularFile, Assert.Throws<EditException>(() => editor.ReplaceContent("/etc", Text(5))).Code);
                Assert.Equal(EditError.NotRegularFile, Assert.Throws<EditException>(() => editor.ReplaceContent("/etc/fast-link", Text(5))).Code);
                Assert.Empty(editor.Writes.Writes);
            }
        }

        [Fact]
        public void AFileChangedSinceTheUserSawItIsRefused()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var before = EditAssert.Snapshot(img.Device);
                var e = before["/etc/hostname"];
                var editor = new Ext4Editor(img.Device, Now);

                var stale = new OriginalStamp(e.Inode, e.Size + 1, e.Mtime);

                Assert.Equal(EditError.EntryChanged, Assert.Throws<EditException>(() => editor.ReplaceContent("/etc/hostname", Text(5), stale)).Code);
                editor.ReplaceContent("/etc/hostname", Text(5), new OriginalStamp(e.Inode, e.Size, e.Mtime));
            }
        }

        [Fact]
        public void TheWritesComeInTheOrderOfTheSafetyRule()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var editor = new Ext4Editor(img.Device, Now);
                editor.ReplaceContent("/etc/hostname", Text(9000));

                var phases = editor.Writes.Writes.Select(w => w.Phase).ToList();
                int lastData = phases.LastIndexOf(WritePhase.Data);
                int firstAllocation = phases.IndexOf(WritePhase.Allocation);
                int inode = phases.IndexOf(WritePhase.Inode);
                int free = phases.IndexOf(WritePhase.Free);

                Assert.True(lastData < firstAllocation);
                Assert.True(firstAllocation < inode);
                Assert.True(inode < free);
                Assert.Equal(1, phases.Count(p => p == WritePhase.Inode));
                var inodeWrite = editor.Writes.Writes.Single(w => w.Phase == WritePhase.Inode);
                Assert.Equal(512, inodeWrite.Data.Length);
            }
        }
    }
}
