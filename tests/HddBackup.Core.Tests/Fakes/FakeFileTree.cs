using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;

namespace HddBackup.Core.Tests.Fakes
{
    /// <summary>An in-memory file tree for the planner, extractor and preview tests.</summary>
    public sealed class FakeFileTree : IFileTree
    {
        private readonly Dictionary<uint, List<FsEntry>> _children = new Dictionary<uint, List<FsEntry>>();
        private readonly Dictionary<uint, byte[]> _content = new Dictionary<uint, byte[]>();
        private uint _next = 3;

        public FakeFileTree()
        {
            Root = new FsEntry { Inode = 2, Name = "/", Path = "/", Kind = FsEntryKind.Directory };
            _children[2] = new List<FsEntry>();
        }

        public FsEntry Root { get; }

        /// <summary>Called for every ReadRange; may throw to simulate read errors.</summary>
        public Action<FsEntry> OnRead { get; set; }

        /// <summary>Counts bytes handed out by ReadRange.</summary>
        public long BytesRead { get; private set; }

        public FsEntry AddDirectory(FsEntry parent, string name, DateTime? modified = null)
        {
            var entry = Make(parent, name, FsEntryKind.Directory, modified);
            _children[entry.Inode] = new List<FsEntry>();
            return entry;
        }

        public FsEntry AddFile(FsEntry parent, string name, byte[] data, DateTime? modified = null)
        {
            var entry = Make(parent, name, FsEntryKind.File, modified);
            entry.Size = data.Length;
            _content[entry.Inode] = data;
            return entry;
        }

        /// <summary>A file whose size is declared but not stored (reads give zeros); for size and FAT-limit tests.</summary>
        public FsEntry AddSparseFile(FsEntry parent, string name, long size)
        {
            var entry = Make(parent, name, FsEntryKind.File, null);
            entry.Size = size;
            return entry;
        }

        public FsEntry AddSymlink(FsEntry parent, string name, string target)
        {
            var entry = Make(parent, name, FsEntryKind.Symlink, null);
            entry.LinkTarget = target;
            return entry;
        }

        public FsEntry AddSpecial(FsEntry parent, string name, SpecialKind kind)
        {
            var entry = Make(parent, name, FsEntryKind.Special, null);
            entry.Special = kind;
            return entry;
        }

        public IReadOnlyList<FsEntry> GetChildren(FsEntry directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _children[directory.Inode]
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .ToList();
        }

        public void Describe(FsEntry entry)
        {
        }

        public int ReadRange(FsEntry file, long offset, byte[] buffer, int index, int count)
        {
            OnRead?.Invoke(file);
            long n = Math.Min(count, file.Size - offset);
            if (n <= 0)
            {
                return 0;
            }

            Array.Clear(buffer, index, (int)n);
            if (_content.TryGetValue(file.Inode, out var data))
            {
                Array.Copy(data, offset, buffer, index, n);
            }

            BytesRead += n;
            return (int)n;
        }

        private FsEntry Make(FsEntry parent, string name, FsEntryKind kind, DateTime? modified)
        {
            var entry = new FsEntry
            {
                Inode = _next++,
                Name = name,
                Path = parent.Path == "/" ? "/" + name : parent.Path + "/" + name,
                Kind = kind,
                Mode = 0x81A4,
                Modified = modified ?? new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc)
            };
            _children[parent.Inode].Add(entry);
            return entry;
        }
    }
}
