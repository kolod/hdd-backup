using System;
using System.IO;
using System.Linq;
using System.Text;
using HddBackup.Core.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>Feature 011 (research R-2): repairs are groups of changes in the diff file; added all or none, undone as a unit.</summary>
    public class RepairGroupTests
    {
        private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

        private static EditChange[] KeyRepair(string dir, string text) => new[]
        {
            EditChange.NewDirectory(dir, 0x1C0, 1000, 1000),
            EditChange.NewFile(dir + "/authorized_keys", Text(text), 0x180, 1000, 1000)
        };

        [Fact]
        public void ARepairIsAddedAsOneGroupAndSurvivesReopening()
        {
            using (var image = EditImage.Copy("edit-ssh-nokey"))
            {
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    int group = session.AddRepair(KeyRepair("/home/pi/.ssh", "ssh-ed25519 AAAA pi\n"), new RepairTitle(RepairKind.AddKey, "pi", "SHA256:x"));
                    Assert.Equal(1, group);
                    Assert.Equal(2, session.Changes.Count);
                    Assert.All(session.Changes, c => Assert.Equal(1, c.Group));
                    var g = session.Groups.Single();
                    Assert.Equal(RepairKind.AddKey, g.Title.Kind);
                    Assert.Equal(2, g.ChangeCount);
                }

                using (var again = ImageEditSession.Open(image.Path))
                {
                    var g = again.Groups.Single();
                    Assert.Equal("pi", g.Title.Arg1);
                    Assert.Equal("SHA256:x", g.Title.Arg2);
                    Assert.Equal(2, again.Changes.Count);
                    again.Discard();
                }
            }
        }

        [Fact]
        public void ARepairThatCannotBeBuiltIsAddedNeitherInPartNorWhole()
        {
            using (var image = EditImage.Copy("edit-ssh-nokey"))
            {
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    var bad = new[]
                    {
                        EditChange.NewDirectory("/home/pi/.ssh", 0x1C0, 1000, 1000),
                        EditChange.NewFile("/home/nobody/.ssh/authorized_keys", Text("x"), 0x180, 1000, 1000)
                    };
                    var ex = Assert.Throws<EditException>(() => session.AddRepair(bad, new RepairTitle(RepairKind.AddKey, "pi")));
                    Assert.Equal(EditError.NotFound, ex.Code);
                    Assert.Empty(session.Changes);
                    Assert.False(File.Exists(session.DiffPath));
                }
            }
        }

        [Fact]
        public void UndoRemovesExactlyOneRepairAndSaveAppliesTheOther()
        {
            using (var image = EditImage.Copy("edit-ssh-nokey"))
            {
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    int first = session.AddRepair(KeyRepair("/home/pi/.ssh", "ssh-ed25519 AAAA pi\n"), new RepairTitle(RepairKind.AddKey, "pi"));
                    int second = session.AddRepair(new[] { EditChange.NewSymlink("/etc/linked", "/etc/hostname") }, new RepairTitle(RepairKind.EnableServer, "ssh.service"));
                    Assert.NotEqual(first, second);

                    Assert.Equal(UndoStatus.Done, session.Undo(first).Status);
                    Assert.Single(session.Changes);
                    Assert.Equal(second, session.Groups.Single().Group);
                    Assert.Equal(UndoStatus.NotFound, session.Undo(first).Status);

                    Assert.Equal(SaveStatus.Saved, session.Save(null, System.Threading.CancellationToken.None).Status);
                }

                using (var device = HddBackup.Core.Devices.FileBlockDevice.OpenRead(image.Path))
                {
                    var tree = EditAssert.Snapshot(device);
                    Assert.True(tree.ContainsKey("/etc/linked"));
                    Assert.False(tree.ContainsKey("/home/pi/.ssh"));
                    EditAssert.CheckerClean(device, "after saving one repair");
                }
            }
        }

        [Fact]
        public void UndoIsRefusedWhenALaterChangeDependsOnTheRepair()
        {
            using (var image = EditImage.Copy("edit-ssh-nokey"))
            {
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    int group = session.AddRepair(KeyRepair("/home/pi/.ssh", "ssh-ed25519 AAAA pi\n"), new RepairTitle(RepairKind.AddKey, "pi"));
                    session.Add(EditChange.Edit("/home/pi/.ssh/authorized_keys", Text("edited by hand\n"), null));

                    var undo = session.Undo(group);
                    Assert.Equal(UndoStatus.Blocked, undo.Status);
                    Assert.Equal("/home/pi/.ssh/authorized_keys", undo.BlockedPath);
                    Assert.Equal(3, session.Changes.Count);

                    // a later repair on the same file blocks as well
                    int second = session.AddRepair(new[] { EditChange.Edit("/home/pi/.ssh/authorized_keys", Text("more\n"), null) }, new RepairTitle(RepairKind.AddKey, "pi", "again"));
                    Assert.Equal(UndoStatus.Blocked, session.Undo(group).Status);
                    Assert.Equal(UndoStatus.Done, session.Undo(second).Status);
                }
            }
        }

        [Fact]
        public void AnIndependentLaterChangeDoesNotBlockTheUndo()
        {
            using (var image = EditImage.Copy("edit-ssh-nokey"))
            {
                image.Device.Dispose();
                using (var session = ImageEditSession.Open(image.Path))
                {
                    int group = session.AddRepair(KeyRepair("/home/pi/.ssh", "ssh-ed25519 AAAA pi\n"), new RepairTitle(RepairKind.AddKey, "pi"));
                    session.Add(EditChange.NewFile("/etc/unrelated.txt", Text("x")));
                    Assert.Equal(UndoStatus.Done, session.Undo(group).Status);
                    Assert.Equal("/etc/unrelated.txt", session.Changes.Single().Path);
                }
            }
        }
    }
}
