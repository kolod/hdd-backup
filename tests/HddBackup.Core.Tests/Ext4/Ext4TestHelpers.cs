using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using HddBackup.Core.Ext4;
using HddBackup.Core.Formats;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Tests.Ext4
{
    /// <summary>Walks an ext4 tree through the reader into path-keyed records for comparisons.</summary>
    internal static class Ext4TestHelpers
    {
        public sealed class Node
        {
            public string Path;
            public Ext4Inode Inode;
        }

        public static List<Node> Walk(Ext4Reader reader)
        {
            var result = new List<Node>();
            var stack = new Stack<(string Path, uint Ino)>();
            stack.Push((string.Empty, 2));
            while (stack.Count > 0)
            {
                var (path, ino) = stack.Pop();
                var dir = reader.ReadInode(ino);
                foreach (var entry in reader.ReadDirectory(dir))
                {
                    if (entry.IsDot || entry.IsDotDot)
                    {
                        continue;
                    }

                    string child = path.Length == 0 ? entry.NameText : path + "/" + entry.NameText;
                    var inode = reader.ReadInode(entry.Inode);
                    result.Add(new Node { Path = child, Inode = inode });
                    if (inode.IsDirectory)
                    {
                        stack.Push((child, entry.Inode));
                    }
                }
            }

            return result;
        }

        public static string Sha256(Ext4Reader reader, Ext4Inode inode)
        {
            var map = reader.MapFile(inode);
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[1 << 20];
                for (long offset = 0; offset < inode.Size; offset += buffer.Length)
                {
                    int count = (int)Math.Min(buffer.Length, inode.Size - offset);
                    reader.ReadFile(inode, map, offset, buffer, 0, count);
                    sha.TransformBlock(buffer, 0, count, null, 0);
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Sha256Accumulator.ToHex(sha.Hash);
            }
        }

        /// <summary>
        /// ext4 stores POSIX ACLs in a compact on-disk format; Linux tools show the VFS format
        /// (version 2, 8-byte entries). Converts on-disk → VFS so values compare with getfattr.
        /// </summary>
        public static byte[] AclToVfs(byte[] disk)
        {
            var output = new List<byte>();
            output.AddRange(BitConverter.GetBytes(2u));
            int o = 4;
            while (o < disk.Length)
            {
                ushort tag = BinaryHelpers.U16(disk, o);
                ushort perm = BinaryHelpers.U16(disk, o + 2);
                uint id = 0xFFFFFFFF;
                if (tag == 2 || tag == 8)
                {
                    id = BinaryHelpers.U32(disk, o + 4);
                    o += 8;
                }
                else
                {
                    o += 4;
                }

                output.AddRange(BitConverter.GetBytes(tag));
                output.AddRange(BitConverter.GetBytes(perm));
                output.AddRange(BitConverter.GetBytes(id));
            }

            return output.ToArray();
        }

        public static Dictionary<string, byte[]> XattrsAsTools(Ext4Reader reader, Ext4Inode inode)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var x in reader.ReadXattrs(inode))
            {
                map[x.FullName] = x.NameIndex == 2 || x.NameIndex == 3 ? AclToVfs(x.Value) : x.Value;
            }

            return map;
        }

        public static string TypeName(Ext4Inode inode)
        {
            switch (inode.Type)
            {
                case Ext4Inode.TypeFile: return "file";
                case Ext4Inode.TypeDir: return "dir";
                case Ext4Inode.TypeSymlink: return "symlink";
                case Ext4Inode.TypeChar: return "chr";
                case Ext4Inode.TypeBlock: return "blk";
                case Ext4Inode.TypeFifo: return "fifo";
                case Ext4Inode.TypeSocket: return "sock";
                default: return "?";
            }
        }

        /// <summary>Device numbers as "major:minor" in hex, like stat %t:%T.</summary>
        public static string Rdev(Ext4Inode inode)
        {
            var b = inode.GetBlockArray();
            uint old = BinaryHelpers.U32(b, 0);
            uint major, minor;
            if (old != 0)
            {
                major = (old >> 8) & 0xFF;
                minor = old & 0xFF;
            }
            else
            {
                uint nw = BinaryHelpers.U32(b, 4);
                major = (nw & 0xFFF00) >> 8;
                minor = (nw & 0xFF) | ((nw >> 12) & 0xFFF00);
            }

            return major.ToString("x") + ":" + minor.ToString("x");
        }

        public static bool SameBytes(byte[] a, byte[] b) => a.Length == b.Length && a.SequenceEqual(b);
    }
}
