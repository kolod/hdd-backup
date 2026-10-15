using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Compares two ext4 trees object by object (research R-6 step 2): type, mode, owner, size,
    /// all timestamps, generation, project id, content, symlink target, device numbers, xattrs
    /// and hard-link grouping. Inode numbers and on-disk placement may differ.
    /// </summary>
    internal static class Ext4TreeComparer
    {
        private const int MaxFindings = 50;
        private const uint IgnoredFlags = Ext4Inode.FlagExtents | Ext4Inode.FlagIndex | Ext4Inode.FlagHugeFile;

        public static List<string> Compare(Ext4Reader a, Ext4Reader b, CancellationToken cancel)
        {
            var findings = new List<string>();
            var treeA = Walk(a, cancel);
            var treeB = Walk(b, cancel);

            foreach (var path in treeA.Keys.Where(p => !treeB.ContainsKey(p)).Take(10))
            {
                findings.Add("Missing in result: " + path);
            }

            foreach (var path in treeB.Keys.Where(p => !treeA.ContainsKey(p)).Take(10))
            {
                findings.Add("Extra in result: " + path);
            }

            var groupsA = HardLinkGroups(treeA);
            var groupsB = HardLinkGroups(treeB);
            var buffers = (new byte[1 << 20], new byte[1 << 20]);

            foreach (var kv in treeA)
            {
                cancel.ThrowIfCancellationRequested();
                if (findings.Count >= MaxFindings)
                {
                    break;
                }

                if (!treeB.TryGetValue(kv.Key, out uint inoB))
                {
                    continue;
                }

                var x = a.ReadInode(kv.Value);
                var y = b.ReadInode(inoB);
                string diff = CompareInodes(a, x, b, y, buffers);
                if (diff != null)
                {
                    findings.Add(kv.Key + ": " + diff);
                }

                if (!x.IsDirectory && groupsA[kv.Value].Count > 1)
                {
                    var peersA = groupsA[kv.Value];
                    var peersB = groupsB[inoB];
                    if (!peersA.SequenceEqual(peersB))
                    {
                        findings.Add(kv.Key + ": hard link group differs");
                    }
                }
            }

            return findings;
        }

        private static string CompareInodes(Ext4Reader a, Ext4Inode x, Ext4Reader b, Ext4Inode y, (byte[] A, byte[] B) buffers)
        {
            if (x.Mode != y.Mode) return "mode";
            if (x.Uid != y.Uid || x.Gid != y.Gid) return "owner";
            if (!x.IsDirectory && x.Size != y.Size) return "size";
            if (x.Atime != y.Atime || x.Ctime != y.Ctime || x.Mtime != y.Mtime) return "timestamps";
            if (x.Generation != y.Generation) return "generation";
            if ((x.Flags & ~IgnoredFlags) != (y.Flags & ~IgnoredFlags)) return "flags";
            if (!SameExtraFields(x, y)) return "extended timestamps or project id";
            if (x.IsDirectory)
            {
                // Directory contents are compared through their children.
            }
            else if (x.IsSymlink)
            {
                if (!a.ReadSymlinkBytes(x).SequenceEqual(b.ReadSymlinkBytes(y))) return "symlink target";
            }
            else if (!x.IsRegular)
            {
                if (!x.GetBlockArray().SequenceEqual(y.GetBlockArray())) return "device numbers";
            }
            else if (!SameContent(a, x, b, y, buffers))
            {
                return "content";
            }

            if (!SameXattrs(a.ReadXattrs(x), b.ReadXattrs(y))) return "extended attributes";
            return null;
        }

        private static bool SameExtraFields(Ext4Inode x, Ext4Inode y)
        {
            int extra = Math.Min(x.ExtraIsize, y.ExtraIsize);
            for (int o = 0x84; o < 0x80 + extra; o++)
            {
                if (x.Raw[o] != y.Raw[o])
                {
                    return false;
                }
            }

            return x.ExtraIsize == y.ExtraIsize;
        }

        private static bool SameContent(Ext4Reader a, Ext4Inode x, Ext4Reader b, Ext4Inode y, (byte[] A, byte[] B) buffers)
        {
            var mapA = a.MapFile(x);
            var mapB = b.MapFile(y);
            for (long offset = 0; offset < x.Size; offset += buffers.A.Length)
            {
                int count = (int)Math.Min(buffers.A.Length, x.Size - offset);
                a.ReadFile(x, mapA, offset, buffers.A, 0, count);
                b.ReadFile(y, mapB, offset, buffers.B, 0, count);
                for (int i = 0; i < count; i++)
                {
                    if (buffers.A[i] != buffers.B[i])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool SameXattrs(List<Ext4Xattr> x, List<Ext4Xattr> y)
        {
            if (x.Count != y.Count)
            {
                return false;
            }

            string Key(Ext4Xattr e) => e.NameIndex + ":" + Convert.ToBase64String(e.Name) + "=" + Convert.ToBase64String(e.Value);
            return x.Select(Key).OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(y.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
        }

        private static SortedDictionary<string, uint> Walk(Ext4Reader reader, CancellationToken cancel)
        {
            var result = new SortedDictionary<string, uint>(StringComparer.Ordinal);
            var queue = new Queue<(string Path, uint Ino)>();
            queue.Enqueue((string.Empty, Ext4CopyPlan.RootInode));
            result["/"] = Ext4CopyPlan.RootInode;
            while (queue.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var (path, ino) = queue.Dequeue();
                foreach (var e in reader.ReadDirectory(reader.ReadInode(ino)))
                {
                    if (e.IsDot || e.IsDotDot)
                    {
                        continue;
                    }

                    string child = path + "/" + Convert.ToBase64String(e.Name);
                    result[child] = e.Inode;
                    if (e.FileType == 2 || (e.FileType == 0 && reader.ReadInode(e.Inode).IsDirectory))
                    {
                        queue.Enqueue((child, e.Inode));
                    }
                }
            }

            return result;
        }

        /// <summary>For each inode, the sorted list of paths pointing to it.</summary>
        private static Dictionary<uint, List<string>> HardLinkGroups(SortedDictionary<string, uint> tree)
        {
            var groups = new Dictionary<uint, List<string>>();
            foreach (var kv in tree)
            {
                if (!groups.TryGetValue(kv.Value, out var list))
                {
                    list = new List<string>();
                    groups[kv.Value] = list;
                }

                list.Add(kv.Key);
            }

            return groups;
        }
    }
}
