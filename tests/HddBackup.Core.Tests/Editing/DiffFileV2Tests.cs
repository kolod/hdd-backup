using System.IO;
using System.Linq;
using System.Text;
using HddBackup.Core.Editing;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>Feature 011 (research R-2): the diff file remembers attributes, links and repair groups; version 1 files still read.</summary>
    public class DiffFileV2Tests
    {
        private static ImageIdentity Identity() => new ImageIdentity { FileLength = 1000, PartitionNumber = 2, Offset = 512, Length = 4096, Uuid = new byte[16], MetaHash = new byte[32] };

        private static string TempFile(TempDirs dirs) => Path.Combine(dirs.New(), "x.hddbackup-diff");

        [Fact]
        public void EveryKindRoundTripsWithAttributesGroupAndTitle()
        {
            using (var dirs = new TempDirs())
            {
                string path = TempFile(dirs);
                var title = new RepairTitle(RepairKind.AddKey, "pi", "SHA256:abc");
                var changes = new[]
                {
                    EditChange.NewDirectory("/home/pi/.ssh", 0x1C0, 1000, 1000).InGroup(7, title),
                    EditChange.NewFile("/home/pi/.ssh/authorized_keys", Encoding.UTF8.GetBytes("k\n"), 0x180, 1000, 1000).InGroup(7, title),
                    EditChange.NewSymlink("/etc/systemd/system/multi-user.target.wants/ssh.service", "/lib/systemd/system/ssh.service").InGroup(8, new RepairTitle(RepairKind.EnableServer, "ssh.service")),
                    EditChange.Attributes("/home/pi", 0x1ED, null, 1000, new OriginalStamp(12, 4096, 77)).InGroup(9, new RepairTitle(RepairKind.FixKeyFileModes, "pi")),
                    EditChange.Edit("/etc/passwd", new byte[] { 1, 2, 3 }, new OriginalStamp(5, 3, 9)),
                    EditChange.Remove("/etc/old", new OriginalStamp(6, 1, 2)),
                    EditChange.Move("/etc/a", "b", new OriginalStamp(7, 1, 2))
                };
                new DiffFile(Identity(), changes).Write(path);

                var read = DiffFile.Read(path, out var status);
                Assert.Equal(DiffStatus.Matches, status);
                Assert.Equal(changes.Length, read.Changes.Count);
                for (int i = 0; i < changes.Length; i++)
                {
                    var a = changes[i];
                    var b = read.Changes[i];
                    Assert.Equal(a.Kind, b.Kind);
                    Assert.Equal(a.Path, b.Path);
                    Assert.Equal(a.NewName, b.NewName);
                    Assert.Equal(a.LinkTarget, b.LinkTarget);
                    Assert.Equal(a.Mode, b.Mode);
                    Assert.Equal(a.Uid, b.Uid);
                    Assert.Equal(a.Gid, b.Gid);
                    Assert.Equal(a.Group, b.Group);
                    Assert.Equal(a.Title?.ToString(), b.Title?.ToString());
                    Assert.Equal(a.Content, b.Content);
                    Assert.Equal(a.Original?.Inode, b.Original?.Inode);
                }
            }
        }

        [Fact]
        public void AVersionOneFileIsReadAsBefore()
        {
            using (var dirs = new TempDirs())
            {
                string path = TempFile(dirs);
                var ms = new MemoryStream();
                using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    w.Write(Encoding.ASCII.GetBytes("HDDDIFF1"));
                    w.Write(1);
                    Identity().Write(w);
                    w.Write(2);
                    w.Write((byte)EditKind.CreateFile);
                    w.Write("/new.txt");
                    w.Write(string.Empty);
                    w.Write(false);
                    w.Write(3);
                    w.Write(new byte[] { 65, 66, 67 });
                    w.Write((byte)EditKind.Delete);
                    w.Write("/old.txt");
                    w.Write(string.Empty);
                    w.Write(true);
                    w.Write(9u);
                    w.Write(10L);
                    w.Write(11u);
                    w.Write(-1);
                }

                File.WriteAllBytes(path, DiffFile.Seal(ms.ToArray()));
                var read = DiffFile.Read(path, out var status);

                Assert.Equal(DiffStatus.Matches, status);
                Assert.Equal(2, read.Changes.Count);
                Assert.Equal("/new.txt", read.Changes[0].Path);
                Assert.Equal(new byte[] { 65, 66, 67 }, read.Changes[0].Content);
                Assert.Equal(0, read.Changes[0].Group);
                Assert.Null(read.Changes[0].Title);
                Assert.Null(read.Changes[0].Mode);
                Assert.Equal(EditKind.Delete, read.Changes[1].Kind);
                Assert.Equal(9u, read.Changes[1].Original.Inode);
            }
        }

        [Fact]
        public void AFutureVersionIsUnsupportedAndAnUnknownKindIsCorrupt()
        {
            using (var dirs = new TempDirs())
            {
                string path = TempFile(dirs);
                Body(path, 3, 1, null);
                DiffFile.Read(path, out var status);
                Assert.Equal(DiffStatus.Unsupported, status);

                Body(path, 2, 1, (byte)99);
                DiffFile.Read(path, out status);
                Assert.Equal(DiffStatus.Corrupt, status);
            }
        }

        private static void Body(string path, int version, int count, byte? kind)
        {
            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes("HDDDIFF1"));
                w.Write(version);
                Identity().Write(w);
                w.Write(count);
                if (kind.HasValue)
                {
                    w.Write(kind.Value);
                    w.Write("/x");
                    w.Write(string.Empty);
                    w.Write(false);
                    w.Write(-1);
                    w.Write((byte)0);
                    w.Write(string.Empty);
                    w.Write(0);
                }
            }

            File.WriteAllBytes(path, DiffFile.Seal(ms.ToArray()));
        }

        [Fact]
        public void ATamperedFileIsCorruptAndTheReplaceIsAtomic()
        {
            using (var dirs = new TempDirs())
            {
                string path = TempFile(dirs);
                new DiffFile(Identity(), new[] { EditChange.NewDirectory("/d", 0x1C0, 1, 2) }).Write(path);
                var bytes = File.ReadAllBytes(path);
                bytes[20] ^= 0xFF;
                File.WriteAllBytes(path, bytes);
                DiffFile.Read(path, out var status);
                Assert.Equal(DiffStatus.Corrupt, status);

                new DiffFile(Identity(), new[] { EditChange.NewDirectory("/d2") }).Write(path);
                Assert.False(File.Exists(path + ".tmp"));
                Assert.Equal("/d2", DiffFile.Read(path, out status).Changes.Single().Path);
            }
        }
    }
}
