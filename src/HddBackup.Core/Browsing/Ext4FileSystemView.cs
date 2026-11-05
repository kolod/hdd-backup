using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Browsing
{
    /// <summary>
    /// The tree of one ext4 partition, read-only, as it is on disk (no journal replay). The
    /// reader and its device are not thread-safe, so all access is serialized by one lock.
    /// </summary>
    public sealed class Ext4FileSystemView : IFileTree, IDisposable
    {
        private const uint EncryptFlag = 0x800;

        private readonly object _sync = new object();
        private readonly Ext4Reader _reader;
        private readonly PartitionView _window;
        private Ext4Inode _mappedInode;
        private List<Ext4Extent> _map;
        private FileSystemCounts _counts;

        internal Ext4FileSystemView(Ext4Reader reader, PartitionView window, FileSystemState state)
        {
            _reader = reader;
            _window = window;
            State = state;
            Root = new FsEntry { Inode = 2, Name = "/", Path = "/", Kind = FsEntryKind.Directory };
            Describe(Root);
        }

        public FsEntry Root { get; }

        public FileSystemState State { get; }

        /// <summary>Volume label (empty when none).</summary>
        public string Label
        {
            get
            {
                lock (_sync)
                {
                    return _reader.Superblock.VolumeName;
                }
            }
        }

        /// <summary>
        /// Counts from the group descriptors, in constant time: directories except the root, and
        /// every other used inode except the reserved ones. A hard-linked file counts once.
        /// </summary>
        public FileSystemCounts Counts
        {
            get
            {
                lock (_sync)
                {
                    if (_counts == null)
                    {
                        var sb = _reader.Superblock;
                        long dirs = _reader.Groups.Sum(g => (long)g.UsedDirs);
                        long used = (long)sb.InodesCount - sb.FreeInodesCount;
                        long reserved = Math.Max(0, (long)sb.FirstInode - 2);
                        _counts = new FileSystemCounts
                        {
                            Directories = Math.Max(0, dirs - 1),
                            Files = Math.Max(0, used - dirs - reserved),
                            // After an unclean shutdown the counters in the superblock are stale; with recorded errors they are worse.
                            Approximate = State != null && State.Condition != FileSystemCondition.Clean
                        };
                    }

                    return _counts;
                }
            }
        }

        public IReadOnlyList<FsEntry> GetChildren(FsEntry directory, CancellationToken cancellationToken)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            lock (_sync)
            {
                List<Ext4DirEntry> raw;
                try
                {
                    raw = _reader.ReadDirectory(_reader.ReadInode(directory.Inode));
                }
                catch (Ext4FormatException ex)
                {
                    throw new ImagingException(ErrorCode.CorruptImage, ex.Message, null, ex);
                }

                var result = new List<FsEntry>(raw.Count);
                bool hasFileType = _reader.Superblock.HasFileType;
                foreach (var de in raw)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (de.IsDot || de.IsDotDot || de.Inode == 0)
                    {
                        continue;
                    }

                    var entry = new FsEntry
                    {
                        Inode = de.Inode,
                        Name = Encoding.UTF8.GetString(de.Name),
                        Path = directory.Path == "/" ? "/" + Encoding.UTF8.GetString(de.Name) : directory.Path + "/" + Encoding.UTF8.GetString(de.Name)
                    };
                    if (hasFileType && de.FileType != 0)
                    {
                        ApplyFileType(entry, de.FileType);
                    }
                    else
                    {
                        // Without the filetype feature the kind is only in the inode.
                        Describe(entry);
                    }

                    if (entry.Kind == FsEntryKind.Symlink && entry.LinkTarget == null && entry.Unreadable == null)
                    {
                        Describe(entry);
                    }

                    result.Add(entry);
                }

                result.Sort(CompareEntries);
                return result;
            }
        }

        public void Describe(FsEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            lock (_sync)
            {
                try
                {
                    var inode = _reader.ReadInode(entry.Inode);
                    ApplyInode(entry, inode);
                }
                catch (Exception ex) when (ex is Ext4FormatException || ex is ImagingException || ex is ArgumentException)
                {
                    entry.Unreadable = ex.Message;
                }
            }
        }

        /// <summary>
        /// Finds an entry by path (names compared as decoded text). When names are ambiguous
        /// (invalid bytes decoded alike), <paramref name="inodeHint"/> selects the right one.
        /// Returns null when the path does not exist.
        /// </summary>
        public FsEntry GetEntry(string path, uint? inodeHint = null)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
            {
                return Root;
            }

            var current = Root;
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var children = GetChildren(current, CancellationToken.None);
                bool last = i == parts.Length - 1;
                FsEntry next = null;
                foreach (var c in children)
                {
                    if (!string.Equals(c.Name, parts[i], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (last && inodeHint.HasValue && c.Inode != inodeHint.Value)
                    {
                        continue;
                    }

                    next = c;
                    break;
                }

                if (next == null)
                {
                    return null;
                }

                current = next;
            }

            Describe(current);
            return current;
        }

        public int ReadRange(FsEntry file, long offset, byte[] buffer, int index, int count)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            lock (_sync)
            {
                try
                {
                    var inode = _reader.ReadInode(file.Inode);
                    if (!inode.IsRegular)
                    {
                        throw new ImagingException(ErrorCode.InvalidRequest, file.Path + " is not a regular file.");
                    }

                    long n = Math.Min(count, inode.Size - offset);
                    if (n <= 0)
                    {
                        return 0;
                    }

                    if (_mappedInode == null || _mappedInode.Number != inode.Number)
                    {
                        _map = _reader.MapFile(inode);
                        _mappedInode = inode;
                    }

                    _reader.ReadFile(inode, _map, offset, buffer, index, (int)n);
                    return (int)n;
                }
                catch (Ext4FormatException ex)
                {
                    _mappedInode = null;
                    throw new ImagingException(ErrorCode.CorruptImage, file.Path + ": " + ex.Message, null, ex);
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _reader.Dispose();
                _window.Dispose();
            }
        }

        private static int CompareEntries(FsEntry a, FsEntry b)
        {
            if (a.IsDirectory != b.IsDirectory)
            {
                return a.IsDirectory ? -1 : 1;
            }

            return string.CompareOrdinal(a.Name, b.Name);
        }

        private static void ApplyFileType(FsEntry entry, byte fileType)
        {
            switch (fileType)
            {
                case 2:
                    entry.Kind = FsEntryKind.Directory;
                    break;
                case 7:
                    entry.Kind = FsEntryKind.Symlink;
                    break;
                case 3:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.CharDevice;
                    break;
                case 4:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.BlockDevice;
                    break;
                case 5:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.Fifo;
                    break;
                case 6:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.Socket;
                    break;
                default:
                    entry.Kind = FsEntryKind.File;
                    break;
            }
        }

        private void ApplyInode(FsEntry entry, Ext4Inode inode)
        {
            entry.Mode = inode.Mode;
            entry.Uid = inode.Uid;
            entry.Gid = inode.Gid;
            entry.Modified = DateTimeOffset.FromUnixTimeSeconds(inode.Mtime).UtcDateTime;
            entry.Special = SpecialKind.None;
            switch (inode.Type)
            {
                case Ext4Inode.TypeDir:
                    entry.Kind = FsEntryKind.Directory;
                    entry.Size = 0;
                    break;
                case Ext4Inode.TypeFile:
                    entry.Kind = FsEntryKind.File;
                    entry.Size = inode.Size;
                    if ((inode.Flags & Ext4Inode.FlagInlineData) != 0)
                    {
                        entry.NotViewable = "the content is stored inside the inode (inline data)";
                    }
                    else if ((inode.Flags & EncryptFlag) != 0)
                    {
                        entry.NotViewable = "the content is encrypted";
                    }

                    break;
                case Ext4Inode.TypeSymlink:
                    entry.Kind = FsEntryKind.Symlink;
                    entry.Size = 0;
                    entry.LinkTarget = Encoding.UTF8.GetString(_reader.ReadSymlinkBytes(inode));
                    break;
                case Ext4Inode.TypeChar:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.CharDevice;
                    break;
                case Ext4Inode.TypeBlock:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.BlockDevice;
                    break;
                case Ext4Inode.TypeFifo:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.Fifo;
                    break;
                case Ext4Inode.TypeSocket:
                    entry.Kind = FsEntryKind.Special;
                    entry.Special = SpecialKind.Socket;
                    break;
                default:
                    throw new Ext4FormatException("Inode " + inode.Number + " has an invalid type.");
            }
        }
    }
}
