using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>A private writable copy of a fixture image (a bare ext4 file system).</summary>
    internal sealed class EditImage : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        private EditImage(string path)
        {
            Path = path;
            Device = FileBlockDevice.OpenReadWrite(path);
        }

        public string Path { get; }

        public FileBlockDevice Device { get; private set; }

        public static EditImage Copy(string fixture)
        {
            var temp = new TempDirs();
            string target = System.IO.Path.Combine(temp.New(), fixture + ".img");
            File.Copy(FixtureImages.RawPath(fixture), target);
            var image = new EditImage(target);
            image._owner = temp;
            return image;
        }

        private TempDirs _owner;

        public string Sha256()
        {
            Device.Dispose();
            try
            {
                using (var sha = SHA256.Create())
                using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return BitConverter.ToString(sha.ComputeHash(f));
                }
            }
            finally
            {
                Device = FileBlockDevice.OpenReadWrite(Path);
            }
        }

        public void Dispose()
        {
            Device.Dispose();
            _owner?.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>One entry of a tree snapshot: everything a user could notice, and the content hash.</summary>
    internal sealed class TreeEntry
    {
        public string Path { get; set; }

        public char Type { get; set; }

        public uint Inode { get; set; }

        public ushort Mode { get; set; }

        public uint Uid { get; set; }

        public uint Gid { get; set; }

        public long Size { get; set; }

        public uint Mtime { get; set; }

        public ushort Links { get; set; }

        public string Content { get; set; }

        public bool Same(TreeEntry o) =>
            Type == o.Type && Inode == o.Inode && Mode == o.Mode && Uid == o.Uid && Gid == o.Gid && Size == o.Size
            && Mtime == o.Mtime && Links == o.Links && Content == o.Content;
    }

    internal static class EditAssert
    {
        public static Dictionary<string, TreeEntry> Snapshot(IBlockDevice device)
        {
            using (var reader = new Ext4Reader(device))
            {
                var result = new Dictionary<string, TreeEntry>(StringComparer.Ordinal);
                Walk(reader, reader.ReadInode(2), "/", result);
                return result;
            }
        }

        private static void Walk(Ext4Reader reader, Ext4Inode dir, string path, Dictionary<string, TreeEntry> result)
        {
            result[path] = Describe(reader, dir, path);
            foreach (var e in reader.ReadDirectory(dir))
            {
                if (e.IsDot || e.IsDotDot)
                {
                    continue;
                }

                string child = path == "/" ? "/" + e.NameText : path + "/" + e.NameText;
                var inode = reader.ReadInode(e.Inode);
                if (inode.IsDirectory)
                {
                    Walk(reader, inode, child, result);
                }
                else
                {
                    result[child] = Describe(reader, inode, child);
                }
            }
        }

        private static TreeEntry Describe(Ext4Reader reader, Ext4Inode inode, string path)
        {
            var entry = new TreeEntry
            {
                Path = path,
                Inode = inode.Number,
                Mode = inode.Mode,
                Uid = inode.Uid,
                Gid = inode.Gid,
                Size = inode.Size,
                Mtime = inode.Mtime,
                Links = inode.LinksCount,
                Type = inode.IsDirectory ? 'D' : inode.IsRegular ? 'F' : inode.IsSymlink ? 'L' : 'S'
            };
            if (inode.IsRegular)
            {
                var data = new byte[inode.Size];
                reader.ReadFile(inode, reader.MapFile(inode), 0, data, 0, data.Length);
                using (var sha = SHA256.Create())
                {
                    entry.Content = BitConverter.ToString(sha.ComputeHash(data));
                }
            }
            else if (inode.IsSymlink)
            {
                entry.Content = reader.ReadSymlink(inode);
            }

            return entry;
        }

        /// <summary>Every entry outside <paramref name="changed"/> (and the parents of changed entries when asked) equals its old self.</summary>
        public static void TreesEqualExcept(Dictionary<string, TreeEntry> before, Dictionary<string, TreeEntry> after, params string[] changed)
        {
            var allowed = new HashSet<string>(changed, StringComparer.Ordinal);
            foreach (var pair in before)
            {
                if (allowed.Contains(pair.Key))
                {
                    continue;
                }

                Assert.True(after.TryGetValue(pair.Key, out var now), "missing after the edit: " + pair.Key);
                Assert.True(pair.Value.Same(now), "changed although it was not edited: " + pair.Key);
            }

            foreach (var key in after.Keys)
            {
                Assert.True(before.ContainsKey(key) || allowed.Contains(key), "appeared without being created: " + key);
            }
        }

        public static void CheckerClean(IBlockDevice device, string what)
        {
            var findings = Ext4Checker.Check(device);
            Assert.True(findings.Count == 0, what + ": " + string.Join("; ", findings.Take(5)));
        }

        public static byte[] Content(IBlockDevice device, string path)
        {
            using (var reader = new Ext4Reader(device))
            {
                var inode = reader.ReadInode(2);
                foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var hit = reader.ReadDirectory(inode).First(e => e.NameText == part);
                    inode = reader.ReadInode(hit.Inode);
                }

                var data = new byte[inode.Size];
                reader.ReadFile(inode, reader.MapFile(inode), 0, data, 0, data.Length);
                return data;
            }
        }

        /// <summary>For the independent check in CI: the saved image and its expected tree (HDDBACKUP_EXPORT_DIR/editing).</summary>
        public static void ExportForCi(string scenario, EditImage image, Dictionary<string, TreeEntry> expectedTree)
        {
            string export = Environment.GetEnvironmentVariable("HDDBACKUP_EXPORT_DIR");
            if (string.IsNullOrEmpty(export))
            {
                return;
            }

            string dir = System.IO.Path.Combine(export, "editing");
            Directory.CreateDirectory(dir);
            image.Device.Flush();
            image.Device.Dispose();
            File.Copy(image.Path, System.IO.Path.Combine(dir, "saved-" + scenario + ".img"), true);
            var lines = new List<string>();
            foreach (var e in expectedTree.Values.OrderBy(x => x.Path, StringComparer.Ordinal))
            {
                if (e.Path == "/")
                {
                    continue;
                }

                string rel = e.Path.TrimStart('/');
                if (e.Type == 'D')
                {
                    lines.Add("D\t" + rel);
                }
                else if (e.Type == 'F')
                {
                    lines.Add("F\t" + e.Content.Replace("-", string.Empty).ToLowerInvariant() + "\t" + rel);
                }
            }

            File.WriteAllText(System.IO.Path.Combine(dir, "saved-" + scenario + ".tree.tsv"), string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }
    }
}
