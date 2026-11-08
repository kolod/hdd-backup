using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>SC-001 / SC-002: creating, deleting and renaming leaves a consistent file system and changes only what was asked.</summary>
    public class StructureEditTests
    {
        private const uint Now = 1760086400;

        private static byte[] Text(int length)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (sb.Length < length)
            {
                sb.Append("new line ").Append(i++).Append('\n');
            }

            return Encoding.UTF8.GetBytes(sb.ToString().Substring(0, length));
        }

        private static Dictionary<string, TreeEntry> Run(string fixture, string scenario, Action<Ext4Editor> edit, params string[] changed)
        {
            using (var img = EditImage.Copy(fixture))
            {
                EditAssert.CheckerClean(img.Device, fixture + " before");
                var before = EditAssert.Snapshot(img.Device);
                string hash = img.Sha256();
                var editor = new Ext4Editor(img.Device, Now);
                edit(editor);
                Assert.Equal(hash, img.Sha256());
                editor.Writes.ApplyTo(img.Device, null, CancellationToken.None);

                EditAssert.CheckerClean(img.Device, fixture + " " + scenario);
                var after = EditAssert.Snapshot(img.Device);
                EditAssert.TreesEqualExcept(before, after, changed);
                EditAssert.ExportForCi(fixture + "-" + scenario, img, after);
                return after;
            }
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        [InlineData("edit-nocsum")]
        [InlineData("edit-uninit")]
        public void CreateFilesOfEverySize(string fixture)
        {
            var after = Run(fixture, "create", e =>
            {
                e.CreateFile("/etc", "new-empty", new byte[0]);
                e.CreateFile("/etc", "new-small", Text(100));
                e.CreateFile("/etc", "new-block", Text(4096));
                e.CreateFile("/etc", "new-large", Text(300000));
            }, "/etc", "/etc/new-empty", "/etc/new-small", "/etc/new-block", "/etc/new-large");

            Assert.Equal(300000, after["/etc/new-large"].Size);
            Assert.Equal((ushort)0x81A4, after["/etc/new-small"].Mode);
            Assert.Equal((ushort)1, after["/etc/new-small"].Links);
            Assert.Equal(after["/etc"].Uid, after["/etc/new-small"].Uid);
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        [InlineData("edit-uninit")]
        public void CreateDirectoriesAndNestedFiles(string fixture)
        {
            var after = Run(fixture, "mkdir", e =>
            {
                e.CreateDirectory("/", "newdir");
                e.CreateDirectory("/newdir", "inner");
                e.CreateFile("/newdir/inner", "file.txt", Text(5000));
            }, "/", "/newdir", "/newdir/inner", "/newdir/inner/file.txt");

            Assert.Equal('D', after["/newdir"].Type);
            Assert.Equal((ushort)0x41ED, after["/newdir"].Mode);
            Assert.Equal((ushort)3, after["/newdir"].Links);
            Assert.Equal((ushort)2, after["/newdir/inner"].Links);
        }

        [Fact]
        public void CreatingInTheIndexedDirectoryRewritesItLinearly()
        {
            var after = Run("edit-basic", "htree-add", e =>
            {
                for (int i = 0; i < 40; i++)
                {
                    e.CreateFile("/many", "added-entry-with-a-reasonably-long-name-" + i + ".conf", Text(10));
                }
            }, new[] { "/many" }.Concat(Enumerable.Range(0, 40).Select(i => "/many/added-entry-with-a-reasonably-long-name-" + i + ".conf")).ToArray());

            Assert.Equal(640, after.Keys.Count(k => k.StartsWith("/many/", StringComparison.Ordinal)));
        }

        [Fact]
        public void CreatingManyEntriesGrowsAPlainDirectory()
        {
            var names = Enumerable.Range(0, 60).Select(i => "a-rather-long-file-name-so-that-blocks-fill-up-" + i).ToList();
            var changed = new[] { "/etc" }.Concat(names.Select(n => "/etc/" + n)).ToArray();
            Run("edit-1k", "dir-grow", e =>
            {
                foreach (string name in names)
                {
                    e.CreateFile("/etc", name, new byte[0]);
                }
            }, changed);
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        [InlineData("edit-nocsum")]
        public void DeleteFilesAndLinks(string fixture)
        {
            var after = Run(fixture, "delete", e =>
            {
                e.Delete("/etc/crlf.txt");
                e.Delete("/etc/fast-link");
                e.Delete("/etc/slow-link");
                e.Delete("/etc/with-xattr");
                e.Delete("/home/pi/a");
            }, "/etc", "/etc/crlf.txt", "/etc/fast-link", "/etc/slow-link", "/etc/with-xattr", "/home/pi", "/home/pi/a", "/home/pi/b");

            Assert.False(after.ContainsKey("/home/pi/a"));
            Assert.True(after.ContainsKey("/home/pi/b"));
            Assert.Equal((ushort)1, after["/home/pi/b"].Links);
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        public void DeleteADirectoryWithEverythingInIt(string fixture)
        {
            using (var img = EditImage.Copy(fixture))
            {
                var before = EditAssert.Snapshot(img.Device);
                var editor = new Ext4Editor(img.Device, Now);
                long freeBefore = editor.Superblock.FreeBlocksCount;
                editor.Delete("/many");
                editor.Delete("/nested");
                editor.Writes.ApplyTo(img.Device, null, CancellationToken.None);

                EditAssert.CheckerClean(img.Device, fixture + " delete directories");
                var after = EditAssert.Snapshot(img.Device);
                var gone = before.Keys.Where(k => k == "/many" || k == "/nested" || k.StartsWith("/many/", StringComparison.Ordinal) || k.StartsWith("/nested/", StringComparison.Ordinal)).ToArray();
                EditAssert.TreesEqualExcept(before, after, gone.Concat(new[] { "/" }).ToArray());
                Assert.DoesNotContain(after.Keys, k => k.StartsWith("/many", StringComparison.Ordinal));
                Assert.True(editor.Superblock.FreeBlocksCount > freeBefore);
                Assert.Equal(before["/"].Links - 2, after["/"].Links);
            }
        }

        [Fact]
        public void DeleteThenCreateReusesTheSpace()
        {
            Run("edit-basic", "delete-create", e =>
            {
                e.Delete("/home/pi/big.txt");
                e.CreateFile("/home/pi", "big2.txt", Text(300000));
            }, "/home/pi", "/home/pi/big.txt", "/home/pi/big2.txt");
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-1k")]
        [InlineData("edit-nocsum")]
        public void RenameFilesAndDirectories(string fixture)
        {
            var after = Run(fixture, "rename", e =>
            {
                e.Rename("/etc/hostname", "hostname.old");
                e.Rename("/etc/fstab", "f");
                e.Rename("/nested/a", "renamed-directory-with-a-long-name");
            }, "/etc", "/etc/hostname", "/etc/hostname.old", "/etc/fstab", "/etc/f", "/nested", "/nested/a",
               "/nested/renamed-directory-with-a-long-name", "/nested/renamed-directory-with-a-long-name/b", "/nested/renamed-directory-with-a-long-name/b/c",
               "/nested/renamed-directory-with-a-long-name/b/c/deep.txt", "/nested/renamed-directory-with-a-long-name/mid.txt",
               "/nested/a/b", "/nested/a/b/c", "/nested/a/b/c/deep.txt", "/nested/a/mid.txt");

            Assert.True(after.ContainsKey("/etc/hostname.old"));
            Assert.True(after.ContainsKey("/nested/renamed-directory-with-a-long-name/b/c/deep.txt"));
        }

        [Fact]
        public void RenameInsideTheIndexedDirectory()
        {
            var after = Run("edit-basic", "htree-rename", e =>
            {
                e.Rename("/many/entry-with-a-longer-name-1.conf", "renamed.conf");
            }, "/many", "/many/entry-with-a-longer-name-1.conf", "/many/renamed.conf");

            Assert.True(after.ContainsKey("/many/renamed.conf"));
        }

        [Fact]
        public void MixedChangesInOneSessionInOrder()
        {
            Run("edit-basic", "mixed", e =>
            {
                e.ReplaceContent("/etc/hostname", Text(100));
                e.Rename("/etc/hostname", "hostname2");
                e.CreateFile("/etc", "n1", Text(10));
                e.Delete("/etc/n1");
                e.CreateDirectory("/", "d");
                e.CreateFile("/d", "f", Text(7000));
                e.Rename("/d/f", "g");
            }, "/", "/etc", "/etc/hostname", "/etc/hostname2", "/d", "/d/g");
        }

        [Fact]
        public void BadNamesAndExistingNamesAreRefusedWithoutWriting()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var editor = new Ext4Editor(img.Device, Now);

                Assert.Equal(EditError.BadName, Assert.Throws<EditException>(() => editor.CreateFile("/etc", "a/b", Text(1))).Code);
                Assert.Equal(EditError.BadName, Assert.Throws<EditException>(() => editor.CreateFile("/etc", "", Text(1))).Code);
                Assert.Equal(EditError.BadName, Assert.Throws<EditException>(() => editor.CreateFile("/etc", "..", Text(1))).Code);
                Assert.Equal(EditError.BadName, Assert.Throws<EditException>(() => editor.CreateFile("/etc", new string('x', 256), Text(1))).Code);
                Assert.Equal(EditError.NameExists, Assert.Throws<EditException>(() => editor.CreateFile("/etc", "hostname", Text(1))).Code);
                Assert.Equal(EditError.NameExists, Assert.Throws<EditException>(() => editor.Rename("/etc/fstab", "hostname")).Code);
                Assert.Equal(EditError.IsRoot, Assert.Throws<EditException>(() => editor.Delete("/")).Code);
                Assert.Equal(EditError.Protected, Assert.Throws<EditException>(() => editor.Delete("/lost+found")).Code);
                Assert.Equal(EditError.NotDirectory, Assert.Throws<EditException>(() => editor.CreateFile("/etc/hostname", "x", Text(1))).Code);
                Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => editor.Delete("/etc/none")).Code);
                Assert.Empty(editor.Writes.Writes);
            }
        }

        [Fact]
        public void ANameOf255BytesIsAccepted()
        {
            string name = new string('n', 255);
            var after = Run("edit-basic", "long-name", e => e.CreateFile("/etc", name, Text(10)), "/etc", "/etc/" + name);

            Assert.True(after.ContainsKey("/etc/" + name));
        }

        [Fact]
        public void NonAsciiNamesAreStoredAsUtf8()
        {
            var after = Run("edit-basic", "utf8", e => e.CreateFile("/etc", "файл-日本語.txt", Text(10)), "/etc", "/etc/файл-日本語.txt");

            Assert.True(after.ContainsKey("/etc/файл-日本語.txt"));
        }

        [Fact]
        public void TheLinkComesLastForCreationsAndTheUnlinkFirstForDeletions()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var create = new Ext4Editor(img.Device, Now);
                create.CreateFile("/etc", "ordered", Text(9000));
                var phases = create.Writes.Writes.Select(w => w.Phase).ToList();
                Assert.True(phases.LastIndexOf(WritePhase.Data) < phases.IndexOf(WritePhase.Allocation));
                Assert.True(phases.IndexOf(WritePhase.Allocation) < phases.IndexOf(WritePhase.Inode));
                Assert.True(phases.IndexOf(WritePhase.Inode) < phases.IndexOf(WritePhase.Link));

                var delete = new Ext4Editor(img.Device, Now);
                delete.Delete("/etc/fstab");
                var d = delete.Writes.Writes.Select(w => w.Phase).ToList();
                Assert.Equal(WritePhase.Link, d[0]);
                Assert.True(d.IndexOf(WritePhase.Link) < d.IndexOf(WritePhase.Free));
            }
        }

        [Fact]
        public void TheNearlyFullFileSystemRefusesNewFilesBeforeWriting()
        {
            using (var img = EditImage.Copy("edit-full"))
            {
                var editor = new Ext4Editor(img.Device, Now);

                var ex = Assert.Throws<EditException>(() => editor.CreateFile("/etc", "big", Text(100000)));

                Assert.Equal(EditError.NoSpace, ex.Code);
                Assert.Empty(editor.Writes.Writes);
            }
        }
    }
}
