using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Editing
{
    /// <summary>
    /// The file system as it will be after the waiting changes are saved: the real tree with the changes laid over it.
    /// Nothing is read from or written to the image except through the base tree.
    /// </summary>
    public sealed class PendingTree : IFileTree
    {
        private sealed class Node
        {
            public string Name;
            public FsEntry Base;
            public bool IsDirectory;
            public byte[] Content;
            public PendingState State;
            public Dictionary<string, Node> Children;
            public DateTime Modified;
        }

        private readonly IFileTree _base;
        private readonly Node _root;
        private readonly DateTime _now;

        public PendingTree(IFileTree baseTree, IEnumerable<EditChange> changes, DateTime? now = null)
        {
            _base = baseTree;
            _now = now ?? DateTime.UtcNow;
            _root = new Node { Name = "/", Base = baseTree.Root, IsDirectory = true };
            Root = Make(_root, "/");
            foreach (var change in changes)
            {
                Apply(change);
            }

            Root = Make(_root, "/");
        }

        public FsEntry Root { get; private set; }

        /// <summary>Stamp of what the user sees, for a change of this entry.</summary>
        public static OriginalStamp StampOf(FsEntry entry)
        {
            var seconds = (uint)new DateTimeOffset(DateTime.SpecifyKind(entry.Modified, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return new OriginalStamp(entry.Inode, entry.Size, seconds);
        }

        /// <summary>What the image holds for this path now (null for entries that only exist in the waiting changes).</summary>
        public OriginalStamp OriginalStampFor(string path)
        {
            var node = Find(path);
            if (node?.Base == null)
            {
                return null;
            }

            _base.Describe(node.Base);
            var seconds = (uint)new DateTimeOffset(DateTime.SpecifyKind(node.Base.Modified, DateTimeKind.Utc)).ToUnixTimeSeconds();
            return new OriginalStamp(node.Base.Inode, node.Base.Size, seconds);
        }

        public bool Exists(string path) => Find(path) != null;

        public IReadOnlyList<FsEntry> GetChildren(FsEntry directory, CancellationToken cancellationToken)
        {
            var node = Find(directory.Path) ?? throw new InvalidOperationException("No such directory: " + directory.Path);
            var children = Children(node, cancellationToken);
            var result = new List<FsEntry>(children.Count);
            foreach (var child in children.Values)
            {
                result.Add(Make(child, Join(directory.Path, child.Name)));
            }

            result.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }

                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        public void Describe(FsEntry entry)
        {
            var node = Find(entry.Path);
            if (node == null)
            {
                entry.Unreadable = "removed";
                return;
            }

            Fill(entry, node);
        }

        public int ReadRange(FsEntry file, long offset, byte[] buffer, int index, int count)
        {
            var node = Find(file.Path) ?? throw new InvalidOperationException("No such file: " + file.Path);
            if (node.Content != null)
            {
                if (offset >= node.Content.Length)
                {
                    return 0;
                }

                int n = (int)Math.Min(count, node.Content.Length - offset);
                Buffer.BlockCopy(node.Content, (int)offset, buffer, index, n);
                return n;
            }

            return _base.ReadRange(node.Base, offset, buffer, index, count);
        }

        /// <summary>The content of a file as it will be saved.</summary>
        public byte[] ReadAll(FsEntry file, long limit)
        {
            Describe(file);
            if (file.Size > limit)
            {
                throw new InvalidOperationException("too large");
            }

            var data = new byte[file.Size];
            int done = 0;
            while (done < data.Length)
            {
                int n = ReadRange(file, done, data, done, data.Length - done);
                if (n <= 0)
                {
                    break;
                }

                done += n;
            }

            return data;
        }

        private void Apply(EditChange change)
        {
            EditChange.Split(change.Path, out string parentPath, out string name);
            switch (change.Kind)
            {
                case EditKind.EditContent:
                {
                    var node = Find(change.Path);
                    if (node != null)
                    {
                        node.Content = change.Content ?? new byte[0];
                        node.Modified = _now;
                        if (node.State == PendingState.None)
                        {
                            node.State = PendingState.Edited;
                        }
                    }

                    break;
                }

                case EditKind.CreateFile:
                case EditKind.CreateDirectory:
                {
                    var parent = Find(parentPath);
                    if (parent != null && parent.IsDirectory)
                    {
                        Children(parent, CancellationToken.None)[name] = new Node
                        {
                            Name = name,
                            IsDirectory = change.Kind == EditKind.CreateDirectory,
                            Content = change.Kind == EditKind.CreateFile ? (change.Content ?? new byte[0]) : null,
                            State = PendingState.New,
                            Modified = _now,
                            Children = change.Kind == EditKind.CreateDirectory ? new Dictionary<string, Node>(StringComparer.Ordinal) : null
                        };
                    }

                    break;
                }

                case EditKind.Delete:
                {
                    var parent = Find(parentPath);
                    if (parent != null)
                    {
                        Children(parent, CancellationToken.None).Remove(name);
                    }

                    break;
                }

                case EditKind.Rename:
                {
                    var parent = Find(parentPath);
                    if (parent != null)
                    {
                        var children = Children(parent, CancellationToken.None);
                        if (children.TryGetValue(name, out var node))
                        {
                            children.Remove(name);
                            node.Name = change.NewName;
                            if (node.State == PendingState.None)
                            {
                                node.State = PendingState.Renamed;
                            }

                            children[change.NewName] = node;
                        }
                    }

                    break;
                }
            }
        }

        private Node Find(string path)
        {
            var node = _root;
            foreach (string part in path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.IsDirectory || !Children(node, CancellationToken.None).TryGetValue(part, out node))
                {
                    return null;
                }
            }

            return node;
        }

        private Dictionary<string, Node> Children(Node node, CancellationToken ct)
        {
            if (node.Children == null)
            {
                node.Children = new Dictionary<string, Node>(StringComparer.Ordinal);
                if (node.Base != null)
                {
                    foreach (var entry in _base.GetChildren(node.Base, ct))
                    {
                        node.Children[entry.Name] = new Node { Name = entry.Name, Base = entry, IsDirectory = entry.IsDirectory };
                    }
                }
            }

            return node.Children;
        }

        private FsEntry Make(Node node, string path)
        {
            var entry = new FsEntry
            {
                Name = node.Name,
                Path = path,
                Inode = node.Base?.Inode ?? 0,
                Kind = node.IsDirectory ? FsEntryKind.Directory : node.Base != null ? node.Base.Kind : FsEntryKind.File,
                Special = node.Base?.Special ?? SpecialKind.None,
                LinkTarget = node.Base?.LinkTarget,
                Pending = node.State
            };
            if (node.Base == null || node.Content != null)
            {
                Fill(entry, node);
            }

            return entry;
        }

        private void Fill(FsEntry entry, Node node)
        {
            entry.Pending = node.State;
            if (node.Base != null)
            {
                _base.Describe(node.Base);
                entry.Inode = node.Base.Inode;
                entry.Size = node.Base.Size;
                entry.Mode = node.Base.Mode;
                entry.Uid = node.Base.Uid;
                entry.Gid = node.Base.Gid;
                entry.Modified = node.Base.Modified;
                entry.LinkTarget = node.Base.LinkTarget;
                entry.Unreadable = node.Base.Unreadable;
                entry.NotViewable = node.Base.NotViewable;
            }
            else
            {
                entry.Mode = (ushort)(node.IsDirectory ? 0x41ED : 0x81A4);
                entry.Modified = node.Modified;
            }

            if (node.Content != null)
            {
                entry.Size = node.Content.Length;
                entry.Modified = node.Modified;
                entry.NotViewable = null;
            }
        }

        private static string Join(string parent, string name) => parent == "/" ? "/" + name : parent + "/" + name;
    }
}
