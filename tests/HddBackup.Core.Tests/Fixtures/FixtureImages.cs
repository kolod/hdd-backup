using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HddBackup.Core.Devices;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Tests.Fixtures
{
    /// <summary>One line of a fixture manifest (see tests/fixtures/ext4/make-fixtures.sh).</summary>
    public sealed class ManifestEntry
    {
        public string Path;
        public string Type;
        public int Mode;
        public uint Uid;
        public uint Gid;
        public long Size;
        public long Mtime;
        public string Sha256;
        public string LinkTarget;
        public string Rdev;
        public string HardLinkKey;
        public IReadOnlyDictionary<string, byte[]> Xattrs;
    }

    /// <summary>Access to the committed ext4 fixtures; each is decompressed once per test run.</summary>
    public static class FixtureImages
    {
        private static readonly ConcurrentDictionary<string, string> Extracted = new ConcurrentDictionary<string, string>();

        public static string Directory => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures", "ext4");

        public static string CompressedPath(string name) => Path.Combine(Directory, name + ".img.gz");

        /// <summary>Path of the decompressed image (shared, must not be modified by tests).</summary>
        public static string RawPath(string name)
        {
            return Extracted.GetOrAdd(name, n =>
            {
                var target = Path.Combine(Path.GetTempPath(), "HddBackupTests", "fixture-" + n + ".img");
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                var source = CompressedPath(n);
                if (!File.Exists(target) || File.GetLastWriteTimeUtc(target) < File.GetLastWriteTimeUtc(source))
                {
                    var temp = target + ".tmp";
                    using (var input = new GZipStream(File.OpenRead(source), CompressionMode.Decompress))
                    using (var output = File.Create(temp))
                    {
                        input.CopyTo(output);
                    }

                    File.Copy(temp, target, true);
                    File.Delete(temp);
                }

                return target;
            });
        }

        /// <summary>Opens the decompressed image read-only.</summary>
        public static FileBlockDevice Open(string name) => FileBlockDevice.OpenRead(RawPath(name));

        /// <summary>The last partition of the fixture (the ext4 root) as a read-only device window.</summary>
        public static PartitionView OpenRoot(string name, out FileBlockDevice image)
        {
            image = Open(name);
            var table = PartitionTableReader.Read(image);
            var root = table.Partitions.OrderBy(p => p.Offset).Last();
            return new PartitionView(image, root.Offset, root.Length);
        }

        public static IReadOnlyList<ManifestEntry> Manifest(string name)
        {
            var result = new List<ManifestEntry>();
            foreach (var line in File.ReadAllLines(Path.Combine(Directory, name + ".manifest.tsv"), Encoding.UTF8))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var f = line.Split('\t');
                result.Add(new ManifestEntry
                {
                    Path = f[0],
                    Type = f[1],
                    Mode = Convert.ToInt32(f[2], 8),
                    Uid = uint.Parse(f[3], CultureInfo.InvariantCulture),
                    Gid = uint.Parse(f[4], CultureInfo.InvariantCulture),
                    Size = long.Parse(f[5], CultureInfo.InvariantCulture),
                    Mtime = long.Parse(f[6], CultureInfo.InvariantCulture),
                    Sha256 = f[7] == "-" ? null : f[7],
                    LinkTarget = f[8] == "-" ? null : f[8],
                    Rdev = f[9] == "-" ? null : f[9],
                    HardLinkKey = f[10] == "-" ? null : f[10],
                    Xattrs = ParseXattrs(f[11])
                });
            }

            return result;
        }

        private static IReadOnlyDictionary<string, byte[]> ParseXattrs(string field)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (field == "-")
            {
                return map;
            }

            foreach (var item in field.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = item.IndexOf('=');
                var hex = item.Substring(eq + 1);
                if (hex.StartsWith("0x", StringComparison.Ordinal))
                {
                    hex = hex.Substring(2);
                }

                var value = new byte[hex.Length / 2];
                for (int i = 0; i < value.Length; i++)
                {
                    value[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                map[item.Substring(0, eq)] = value;
            }

            return map;
        }
    }
}
