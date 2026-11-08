using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>Feature 011 (research R-1, R-3): creating fast symbolic links, setting mode and owner, attributes on new entries.</summary>
    public class SymlinkAndAttributeTests
    {
        private const uint Now = 1760086400;
        private const string Target = "/lib/systemd/system/ssh.service";

        private static Dictionary<string, TreeEntry> Run(string fixture, string scenario, Action<Ext4Editor> edit, params string[] changed)
        {
            using (var img = EditImage.Copy(fixture))
            {
                EditAssert.CheckerClean(img.Device, fixture + " before");
                var before = EditAssert.Snapshot(img.Device);
                var editor = new Ext4Editor(img.Device, Now);
                edit(editor);
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
        public void ACreatedSymlinkIsAFastLinkTheCheckerAccepts(string fixture)
        {
            var after = Run(fixture, "symlink", e => e.CreateSymlink("/etc", "new-link", Target), "/etc", "/etc/new-link");

            var link = after["/etc/new-link"];
            Assert.Equal('L', link.Type);
            Assert.Equal(Target, link.Content);
            Assert.Equal((ushort)0xA1FF, link.Mode);
            Assert.Equal((ushort)1, link.Links);
            Assert.Equal(Target.Length, link.Size);
        }

        [Fact]
        public void ALinkTargetThatDoesNotFitTheInodeIsRefusedBeforeAnyWrite()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var editor = new Ext4Editor(img.Device, Now);
                var tooLong = "/" + new string('a', 59);
                Assert.Equal(EditError.TooLong, Assert.Throws<EditException>(() => editor.CreateSymlink("/etc", "x", tooLong)).Code);
                Assert.Equal(EditError.BadValue, Assert.Throws<EditException>(() => editor.CreateSymlink("/etc", "x", string.Empty)).Code);
                Assert.Equal(EditError.NameExists, Assert.Throws<EditException>(() => editor.CreateSymlink("/etc", "hostname", Target)).Code);
                Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => editor.CreateSymlink("/nope", "x", Target)).Code);
                Assert.Empty(editor.Writes.Writes);
            }
        }

        [Fact]
        public void ALinkOfFiftyNineBytesFits()
        {
            string target = "/" + new string('t', 58);
            var after = Run("edit-basic", "symlink59", e => e.CreateSymlink("/etc", "edge", target), "/etc", "/etc/edge");
            Assert.Equal(target, after["/etc/edge"].Content);
        }

        [Fact]
        public void ACreatedSymlinkCanBeDeleted()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var before = EditAssert.Snapshot(img.Device);
                var create = new Ext4Editor(img.Device, Now);
                create.CreateSymlink("/etc", "gone", Target);
                create.Writes.ApplyTo(img.Device, null, CancellationToken.None);
                var delete = new Ext4Editor(img.Device, Now);
                delete.Delete("/etc/gone");
                delete.Writes.ApplyTo(img.Device, null, CancellationToken.None);

                EditAssert.CheckerClean(img.Device, "after create and delete");
                EditAssert.TreesEqualExcept(before, EditAssert.Snapshot(img.Device), "/etc");
            }
        }

        [Fact]
        public void AMaskingLinkToDevNullCanBeReplacedByNothing()
        {
            // the repair "unmask": delete a link that points to /dev/null (the fixture has a fast link of its own)
            var after = Run("edit-basic", "delete-link", e => e.Delete("/etc/fast-link"), "/etc", "/etc/fast-link");
            Assert.False(after.ContainsKey("/etc/fast-link"));
        }

        [Fact]
        public void SetAttributesChangesOnlyTheGivenFieldsAndKeepsTypeAndMtime()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var before = EditAssert.Snapshot(img.Device);
                var old = before["/home/pi/big.txt"];
                var editor = new Ext4Editor(img.Device, Now);
                editor.SetAttributes("/home/pi/big.txt", new EntryAttributes(0x180, 1234, null));
                editor.Writes.ApplyTo(img.Device, null, CancellationToken.None);

                EditAssert.CheckerClean(img.Device, "after attributes");
                var after = EditAssert.Snapshot(img.Device);
                var now = after["/home/pi/big.txt"];
                Assert.Equal((ushort)0x8180, now.Mode);
                Assert.Equal(1234u, now.Uid);
                Assert.Equal(old.Gid, now.Gid);
                Assert.Equal(old.Mtime, now.Mtime);
                Assert.Equal(old.Size, now.Size);
                Assert.Equal(old.Content, now.Content);
                EditAssert.TreesEqualExcept(before, after, "/home/pi/big.txt");
            }
        }

        [Fact]
        public void SetAttributesOnADirectoryAndAnOwnerAbove65535()
        {
            var after = Run("edit-basic", "attributes-dir", e => e.SetAttributes("/nested", new EntryAttributes(0x1C0, 70000, 80000)), "/nested");
            Assert.Equal((ushort)0x41C0, after["/nested"].Mode);
            Assert.Equal(70000u, after["/nested"].Uid);
            Assert.Equal(80000u, after["/nested"].Gid);
        }

        [Fact]
        public void SetAttributesRefusesWhatCannotBeDone()
        {
            using (var img = EditImage.Copy("edit-basic"))
            {
                var editor = new Ext4Editor(img.Device, Now);
                Assert.Equal(EditError.NotFound, Assert.Throws<EditException>(() => editor.SetAttributes("/missing", new EntryAttributes(0x180, null, null))).Code);
                Assert.Equal(EditError.BadValue, Assert.Throws<EditException>(() => editor.SetAttributes("/etc/hostname", new EntryAttributes(0x1FFF, null, null))).Code);
                var stamp = new OriginalStamp(12, 1, 1);
                Assert.Equal(EditError.EntryChanged, Assert.Throws<EditException>(() => editor.SetAttributes("/etc/hostname", new EntryAttributes(0x180, null, null), stamp)).Code);
                Assert.Empty(editor.Writes.Writes);
            }
        }

        [Theory]
        [InlineData("edit-basic")]
        [InlineData("edit-nocsum")]
        public void NewEntriesCanCarryModeAndOwner(string fixture)
        {
            var after = Run(fixture, "owned", e =>
            {
                e.CreateDirectory("/home/pi", ".ssh", new EntryAttributes(0x1C0, 1000, 1000));
                e.CreateFile("/home/pi/.ssh", "authorized_keys", new byte[] { 0x61, 0x0A }, new EntryAttributes(0x180, 1000, 1000));
            }, "/home/pi", "/home/pi/.ssh", "/home/pi/.ssh/authorized_keys");

            Assert.Equal((ushort)0x41C0, after["/home/pi/.ssh"].Mode);
            Assert.Equal((ushort)0x8180, after["/home/pi/.ssh/authorized_keys"].Mode);
            Assert.Equal(1000u, after["/home/pi/.ssh/authorized_keys"].Uid);
            Assert.Equal(1000u, after["/home/pi/.ssh/authorized_keys"].Gid);
        }

        [Fact]
        public void ANewEntryWithoutAttributesStillTakesTheParentsOwner()
        {
            var after = Run("edit-basic", "default-owner", e => e.CreateFile("/home/pi", "plain", new byte[3]), "/home/pi", "/home/pi/plain");
            Assert.Equal((ushort)0x81A4, after["/home/pi/plain"].Mode);
            Assert.Equal(after["/home/pi"].Uid, after["/home/pi/plain"].Uid);
        }

        [Fact]
        public void AnInterruptionAtAnyWriteOfASymlinkLeavesAReadableTreeWithTheLinkWholeOrAbsent()
        {
            int total;
            using (var probe = EditImage.Copy("edit-basic"))
            {
                var e = new Ext4Editor(probe.Device, Now);
                e.CreateSymlink("/etc", "cut", Target);
                total = e.Writes.Writes.Count;
            }

            Assert.True(total >= 3);
            for (int k = 0; k <= total; k++)
            {
                using (var img = EditImage.Copy("edit-basic"))
                {
                    var editor = new Ext4Editor(img.Device, Now);
                    editor.CreateSymlink("/etc", "cut", Target);
                    foreach (var w in editor.Writes.Writes.Take(k))
                    {
                        img.Device.Write(w.Offset, w.Data, 0, w.Data.Length);
                    }

                    var tree = EditAssert.Snapshot(img.Device);
                    if (tree.TryGetValue("/etc/cut", out var link))
                    {
                        Assert.Equal(Target, link.Content);
                    }

                    if (k == total)
                    {
                        EditAssert.CheckerClean(img.Device, "complete");
                        Assert.True(tree.ContainsKey("/etc/cut"));
                    }
                }
            }
        }

        [Fact]
        public void AnInterruptionAtAnyWriteOfSetAttributesLeavesTheOldOrTheNewAttributes()
        {
            int total;
            using (var probe = EditImage.Copy("edit-basic"))
            {
                var e = new Ext4Editor(probe.Device, Now);
                e.SetAttributes("/etc/hostname", new EntryAttributes(0x180, 5, 6));
                total = e.Writes.Writes.Count;
            }

            for (int k = 0; k <= total; k++)
            {
                using (var img = EditImage.Copy("edit-basic"))
                {
                    var old = EditAssert.Snapshot(img.Device)["/etc/hostname"];
                    var editor = new Ext4Editor(img.Device, Now);
                    editor.SetAttributes("/etc/hostname", new EntryAttributes(0x180, 5, 6));
                    foreach (var w in editor.Writes.Writes.Take(k))
                    {
                        img.Device.Write(w.Offset, w.Data, 0, w.Data.Length);
                    }

                    var now = EditAssert.Snapshot(img.Device)["/etc/hostname"];
                    bool isOld = now.Mode == old.Mode && now.Uid == old.Uid && now.Gid == old.Gid;
                    bool isNew = now.Mode == 0x8180 && now.Uid == 5 && now.Gid == 6;
                    Assert.True(isOld || isNew, "half-changed attributes after write " + k);
                    EditAssert.CheckerClean(img.Device, "attributes after write " + k);
                }
            }
        }
    }
}
