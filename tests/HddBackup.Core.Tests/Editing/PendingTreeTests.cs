using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
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
    }
}
