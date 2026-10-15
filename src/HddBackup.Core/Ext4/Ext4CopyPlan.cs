using System;
using System.Collections.Generic;
using System.Threading;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Everything the writer needs to know about the source before building the new file system:
    /// reachable inodes with their new numbers, directory contents, and block requirements.
    /// The same plan gives the shrink planner an exact data size.
    /// </summary>
    internal sealed class Ext4CopyPlan
    {
        public const uint RootInode = 2;
        public const uint ResizeInode = 7;
        public const uint JournalInode = 8;
        public const uint LostAndFoundInode = 11;

        private Ext4CopyPlan()
        {
        }

        public sealed class Node
        {
            public uint SourceInode;
            public uint NewInode;
            public Ext4Inode Inode;
            public uint NewParent;
            public List<Child> Children;      // directories only, excluding "." and ".."
            public int SubdirCount;
            public int LinkCount;              // number of directory entries pointing here
            public long DataBlocks;            // data blocks to copy (directories: new linear blocks)
            public int SourceExtentCount;
            public bool IsFastOrSpecial;
        }

        public struct Child
        {
            public byte[] Name;
            public byte FileType;
            public uint SourceInode;
        }

        /// <summary>Nodes in new inode number order (root first).</summary>
        public List<Node> Nodes { get; } = new List<Node>();

        public Dictionary<uint, Node> BySource { get; } = new Dictionary<uint, Node>();

        /// <summary>Source xattr blocks and how many copied inodes reference each.</summary>
        public Dictionary<long, int> XattrBlockRefs { get; } = new Dictionary<long, int>();

        public long DataBlocks { get; private set; }

        public long DirectoryBlocks { get; private set; }

        /// <summary>Upper bound of extent tree blocks the copies will need.</summary>
        public long ExtentTreeBlocks { get; private set; }

        public uint HighestInode { get; private set; }

        public long SourceJournalBlocks { get; private set; }

        public long TotalBytesToCopy { get; private set; }

        public static Ext4CopyPlan Build(Ext4Reader source, CancellationToken cancel)
        {
            var plan = new Ext4CopyPlan();
            var sb = source.Superblock;
            int blockSize = source.BlockSize;
            bool csum = sb.HasMetadataCsum;
            uint next = sb.FirstInode;

            // Breadth-first walk from the root, numbering inodes as they are discovered.
            var queue = new Queue<Node>();
            var root = plan.AddNode(source, RootInode, RootInode);
            root.NewParent = RootInode;
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = queue.Dequeue();
                dir.Children = new List<Child>();
                foreach (var entry in source.ReadDirectory(dir.Inode))
                {
                    if (entry.IsDot || entry.IsDotDot)
                    {
                        continue;
                    }

                    if (!plan.BySource.TryGetValue(entry.Inode, out var child))
                    {
                        uint newNumber;
                        if (dir.SourceInode == RootInode && entry.Inode == LostAndFoundInode && entry.NameText == "lost+found")
                        {
                            newNumber = LostAndFoundInode;
                        }
                        else
                        {
                            if (next == LostAndFoundInode)
                            {
                                next++;
                            }

                            newNumber = next++;
                        }

                        child = plan.AddNode(source, entry.Inode, newNumber);
                        child.NewParent = dir.NewInode;
                        if (child.Inode.IsDirectory)
                        {
                            queue.Enqueue(child);
                        }
                    }
                    else if (child.Inode.IsDirectory)
                    {
                        throw new Ext4FormatException("Directory inode " + entry.Inode + " is linked twice.");
                    }

                    child.LinkCount++;
                    if (child.Inode.IsDirectory)
                    {
                        dir.SubdirCount++;
                    }

                    byte fileType = sb.HasFileType ? Ext4DirEntry.FileTypeFor(child.Inode.Type) : (byte)0;
                    dir.Children.Add(new Child { Name = entry.Name, FileType = fileType, SourceInode = entry.Inode });
                }

                dir.DataBlocks = DirectoryBlockCount(dir, blockSize, csum);
                plan.DirectoryBlocks += dir.DataBlocks;
            }

            plan.Nodes.Sort((a, b) => a.NewInode.CompareTo(b.NewInode));
            foreach (var node in plan.Nodes)
            {
                plan.HighestInode = Math.Max(plan.HighestInode, node.NewInode);
                if (!node.Inode.IsDirectory)
                {
                    plan.DataBlocks += node.DataBlocks;
                }

                if (node.SourceExtentCount > 4)
                {
                    int perBlock = (blockSize - 12) / 12;
                    plan.ExtentTreeBlocks += ((node.SourceExtentCount + perBlock - 1) / perBlock) + 1;
                }
            }

            if (sb.HasJournal && sb.JournalInode == JournalInode)
            {
                plan.SourceJournalBlocks = source.ReadInode(JournalInode).Size / blockSize;
            }

            return plan;
        }

        /// <summary>Blocks a linear directory with these entries needs (lost+found keeps at least 16 KiB).</summary>
        public static long DirectoryBlockCount(Node dir, int blockSize, bool csum)
        {
            int limit = blockSize - (csum ? 12 : 0);
            long blocks = 1;
            int used = 12 + 12; // "." and ".." (minimum sizes)
            foreach (var child in dir.Children)
            {
                int size = (8 + child.Name.Length + 3) & ~3;
                if (used + size > limit)
                {
                    blocks++;
                    used = 0;
                }

                used += size;
            }

            if (dir.NewInode == LostAndFoundInode)
            {
                blocks = Math.Max(blocks, 16384 / blockSize);
            }

            return blocks;
        }

        private Node AddNode(Ext4Reader source, uint sourceInode, uint newInode)
        {
            var inode = source.ReadInode(sourceInode);
            if ((inode.Flags & Ext4Inode.FlagInlineData) != 0)
            {
                throw new Ext4FormatException("Inode " + sourceInode + " uses inline data.");
            }

            if ((inode.Flags & Ext4Inode.FlagEaInode) != 0)
            {
                throw new Ext4FormatException("Inode " + sourceInode + " is an xattr value inode.");
            }

            var node = new Node { SourceInode = sourceInode, NewInode = newInode, Inode = inode };
            node.IsFastOrSpecial = source.IsFastSymlinkOrSpecial(inode);
            if (!inode.IsDirectory && !node.IsFastOrSpecial)
            {
                var map = source.MapFile(inode);
                node.SourceExtentCount = map.Count;
                foreach (var e in map)
                {
                    node.DataBlocks += e.Count;
                    if (!e.Uninitialized)
                    {
                        TotalBytesToCopy += (long)e.Count * source.BlockSize;
                    }
                }
            }

            if (inode.FileAcl != 0)
            {
                XattrBlockRefs.TryGetValue(inode.FileAcl, out int refs);
                XattrBlockRefs[inode.FileAcl] = refs + 1;
            }

            Nodes.Add(node);
            BySource[sourceInode] = node;
            return node;
        }
    }
}
