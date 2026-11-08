using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Diagnostics;
using HddBackup.Core.Editing;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    public class PendingTreeTests
    {
        private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

        private static FsEntry Walk(PendingTree tree, string path)
        {
            var entry = tree.Root;
            foreach (string part in path.Split(new[] { '/' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                entry = tree.GetChildren(entry, CancellationToken.None).First(e => e.Name == part);
            }

            return entry;
        }

        [Fact]
        public void ShowsTheChangesAsTheyWillBeSaved()
        {
            using (var image = EditImage.Copy("edit-basic"))
            {
                var snapshot = EditAssert.Snapshot(image.Device);
                string file = snapshot.Values.First(e => e.Type == 'F').Path;
                string other = snapshot.Values.First(e => e.Type == 'F' && e.Path != file).Path;
                image.Device.Dispose();
                using (var source = BrowseSource.OpenImage(image.Path))
                using (var view = source.OpenFileSystem(source.Browsable.First()))
                {
                    var changes = new[]
                    {
                        EditChange.Edit(file, Text("changed"), null),
                        EditChange.NewDirectory("/newdir"),
                        EditChange.NewFile("/newdir/a.txt", Text("abc")),
                        EditChange.Remove(other, null)
                    };
                    var tree = new PendingTree(view, changes);

                    var top = tree.GetChildren(tree.Root, CancellationToken.None);
                    var edited = Walk(tree, file);
                    Assert.Equal(PendingState.Edited, edited.Pending);
                    Assert.Equal(7, edited.Size);
                    var created = top.First(e => e.Path == "/newdir");
                    Assert.Equal(PendingState.New, created.Pending);
                    Assert.True(created.IsDirectory);

                    var inner = tree.GetChildren(created, CancellationToken.None).Single();
                    var buffer = new byte[10];
                    Assert.Equal(3, tree.ReadRange(inner, 0, buffer, 0, 10));
                    Assert.Equal("abc", Encoding.UTF8.GetString(buffer, 0, 3));
                    Assert.False(tree.Exists(other));
                    Assert.Equal("changed", Encoding.UTF8.GetString(tree.ReadAll(edited, 100)));
                }
            }
        }

        [Fact]
        public void ShowsPendingSymlinksAndAttributesToTheDiagnosticReader()
        {
            using (var image = EditImage.Copy("edit-ssh-disabled"))
            {
                image.Device.Dispose();
                using (var source = BrowseSource.OpenImage(image.Path))
                using (var view = source.OpenFileSystem(source.Browsable.First()))
                {
                    var changes = new[]
                    {
                        EditChange.NewSymlink("/etc/systemd/system/multi-user.target.wants/ssh.service", "/lib/systemd/system/ssh.service"),
                        EditChange.NewDirectory("/home/pi/.ssh", 0x1C0, 1000, 1000),
                        EditChange.NewFile("/home/pi/.ssh/authorized_keys", Text("ssh-ed25519 AAAA x\n"), 0x180, 1000, 1000),
                        EditChange.Attributes("/home/pi", 0x1ED, 4321, null)
                    };
                    var tree = new PendingTree(view, changes);
                    var reader = new SystemReader(tree);

                    Assert.Equal("/lib/systemd/system/ssh.service", reader.ReadLink("/etc/systemd/system/multi-user.target.wants/ssh.service"));
                    Assert.True(reader.Exists("/etc/systemd/system/multi-user.target.wants/ssh.service"));
                    var dir = reader.Lookup("/home/pi/.ssh");
                    Assert.Equal((ushort)0x41C0, dir.Mode);
                    Assert.Equal(1000u, dir.Uid);
                    var file = reader.Lookup("/home/pi/.ssh/authorized_keys");
                    Assert.Equal((ushort)0x8180, file.Mode);
                    Assert.Equal(PendingState.New, file.Pending);
                    Assert.Equal(19, file.Size);
                    var home = reader.Lookup("/home/pi");
                    Assert.Equal((ushort)0x41ED, home.Mode);
                    Assert.Equal(4321u, home.Uid);
                    Assert.Equal(1000u, home.Gid);
                    Assert.Equal(PendingState.Edited, home.Pending);
                }
            }
        }

        [Fact]
        public void ANewEntryWithoutAnOwnerTakesTheOwnerOfItsParent()
        {
            using (var image = EditImage.Copy("edit-ssh-disabled"))
            {
                image.Device.Dispose();
                using (var source = BrowseSource.OpenImage(image.Path))
                using (var view = source.OpenFileSystem(source.Browsable.First()))
                {
                    var tree = new PendingTree(view, new[] { EditChange.NewFile("/home/pi/note", Text("x")) });
                    var entry = new SystemReader(tree).Lookup("/home/pi/note");
                    Assert.Equal(1000u, entry.Uid);
                    Assert.Equal(1000u, entry.Gid);
                }
            }
        }
    }
}
