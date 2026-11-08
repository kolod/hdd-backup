using System;
using System.Text;

namespace HddBackup.Core.Browsing
{
    public enum FsEntryKind
    {
        Directory,
        File,
        Symlink,
        Special
    }

    public enum SpecialKind
    {
        None,
        CharDevice,
        BlockDevice,
        Fifo,
        Socket
    }

    /// <summary>Whether an entry has a waiting (not yet saved) change in edit mode.</summary>
    public enum PendingState
    {
        None,
        Edited,
        New,
        Renamed
    }

    /// <summary>One entry of a browsed file system. No ext4 types leak through it.</summary>
    public sealed class FsEntry
    {
        public uint Inode { get; internal set; }

        /// <summary>Edit mode: this entry has a change waiting to be saved.</summary>
        public PendingState Pending { get; internal set; }

        /// <summary>Decoded as UTF-8; invalid bytes show as U+FFFD. "/" for the root.</summary>
        public string Name { get; internal set; } = string.Empty;

        /// <summary>Full path built from the names, e.g. "/etc/hostname".</summary>
        public string Path { get; internal set; } = "/";

        public FsEntryKind Kind { get; internal set; }

        public SpecialKind Special { get; internal set; }

        /// <summary>Bytes; 0 for directories and special files.</summary>
        public long Size { get; internal set; }

        public ushort Mode { get; internal set; }

        public uint Uid { get; internal set; }

        public uint Gid { get; internal set; }

        public DateTime Modified { get; internal set; }

        /// <summary>Symbolic links only.</summary>
        public string LinkTarget { get; internal set; }

        /// <summary>Reason the entry's metadata could not be read; null when readable.</summary>
        public string Unreadable { get; internal set; }

        /// <summary>Reason the content cannot be shown or extracted (encrypted, unsupported storage); null otherwise.</summary>
        public string NotViewable { get; internal set; }

        public bool IsDirectory => Kind == FsEntryKind.Directory;

        /// <summary>A regular file whose content can be read.</summary>
        public bool HasContent => Kind == FsEntryKind.File && Unreadable == null && NotViewable == null;

        /// <summary>Permissions as "-rw-r--r--", with the file type letter first.</summary>
        public static string FormatMode(ushort mode)
        {
            char type;
            switch (mode & 0xF000)
            {
                case 0x4000: type = 'd'; break;
                case 0xA000: type = 'l'; break;
                case 0x2000: type = 'c'; break;
                case 0x6000: type = 'b'; break;
                case 0x1000: type = 'p'; break;
                case 0xC000: type = 's'; break;
                default: type = '-'; break;
            }

            var sb = new StringBuilder(10);
            sb.Append(type);
            sb.Append((mode & 0x100) != 0 ? 'r' : '-');
            sb.Append((mode & 0x080) != 0 ? 'w' : '-');
            sb.Append((mode & 0x800) != 0 ? ((mode & 0x040) != 0 ? 's' : 'S') : ((mode & 0x040) != 0 ? 'x' : '-'));
            sb.Append((mode & 0x020) != 0 ? 'r' : '-');
            sb.Append((mode & 0x010) != 0 ? 'w' : '-');
            sb.Append((mode & 0x400) != 0 ? ((mode & 0x008) != 0 ? 's' : 'S') : ((mode & 0x008) != 0 ? 'x' : '-'));
            sb.Append((mode & 0x004) != 0 ? 'r' : '-');
            sb.Append((mode & 0x002) != 0 ? 'w' : '-');
            sb.Append((mode & 0x200) != 0 ? ((mode & 0x001) != 0 ? 't' : 'T') : ((mode & 0x001) != 0 ? 'x' : '-'));
            return sb.ToString();
        }
    }

    /// <summary>Number of files and directories of a file system, from its own counters.</summary>
    public sealed class FileSystemCounts
    {
        public long Files { get; internal set; }

        public long Directories { get; internal set; }

        /// <summary>True when the file system is damaged, so the counters may be off.</summary>
        public bool Approximate { get; internal set; }
    }
}
