using System;
using System.Text;
using HddBackup.Core.Partitioning;

namespace HddBackup.Core.Ext4
{
    /// <summary>The ext4 data is not in the expected format (bad magic, checksum, or structure).</summary>
    [Serializable]
    public sealed class Ext4FormatException : Exception
    {
        public Ext4FormatException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// ext4 superblock. All fields live in <see cref="Raw"/>, so unknown fields survive a copy.
    /// Offsets follow the published ext4 on-disk layout.
    /// </summary>
    internal sealed class Ext4Superblock
    {
        public const int Size = 1024;
        public const int ByteOffset = 1024;
        public const ushort MagicValue = 0xEF53;
        public const ushort StateValid = 0x0001;
        public const ushort StateErrors = 0x0002;
        public const ushort StateOrphans = 0x0004;

        public Ext4Superblock(byte[] raw)
        {
            if (raw.Length != Size)
            {
                throw new ArgumentException("Superblock must be 1024 bytes.");
            }

            Raw = raw;
        }

        public byte[] Raw { get; }

        public uint InodesCount { get => U32(0x00); set => Put(0x00, value); }

        public long BlocksCount
        {
            get => U32(0x04) | (Is64Bit ? (long)U32(0x150) << 32 : 0);
            set { Put(0x04, (uint)value); if (Is64Bit) { Put(0x150, (uint)(value >> 32)); } }
        }

        public long ReservedBlocksCount
        {
            get => U32(0x08) | (Is64Bit ? (long)U32(0x154) << 32 : 0);
            set { Put(0x08, (uint)value); if (Is64Bit) { Put(0x154, (uint)(value >> 32)); } }
        }

        public long FreeBlocksCount
        {
            get => U32(0x0C) | (Is64Bit ? (long)U32(0x158) << 32 : 0);
            set { Put(0x0C, (uint)value); if (Is64Bit) { Put(0x158, (uint)(value >> 32)); } }
        }

        public uint FreeInodesCount { get => U32(0x10); set => Put(0x10, value); }

        public uint FirstDataBlock { get => U32(0x14); set => Put(0x14, value); }

        public int LogBlockSize => (int)U32(0x18);

        public int LogClusterSize => (int)U32(0x1C);

        public int BlockSize => 1024 << LogBlockSize;

        public uint BlocksPerGroup { get => U32(0x20); set => Put(0x20, value); }

        public uint ClustersPerGroup { get => U32(0x24); set => Put(0x24, value); }

        public uint InodesPerGroup { get => U32(0x28); set => Put(0x28, value); }

        public uint WriteTime { get => U32(0x30); set => Put(0x30, value); }

        public ushort MountCount { get => U16(0x34); set => BinaryHelpers.PutU16(Raw, 0x34, value); }

        public ushort Magic => U16(0x38);

        public ushort State { get => U16(0x3A); set => BinaryHelpers.PutU16(Raw, 0x3A, value); }

        public uint RevLevel => U32(0x4C);

        public uint FirstInode => RevLevel == 0 ? 11u : U32(0x54);

        public int InodeSize => RevLevel == 0 ? 128 : U16(0x58);

        public ushort BlockGroupNumber { get => U16(0x5A); set => BinaryHelpers.PutU16(Raw, 0x5A, value); }

        public uint FeatureCompat { get => U32(0x5C); set => Put(0x5C, value); }

        public uint FeatureIncompat { get => U32(0x60); set => Put(0x60, value); }

        public uint FeatureRoCompat { get => U32(0x64); set => Put(0x64, value); }

        public byte[] Uuid
        {
            get
            {
                var uuid = new byte[16];
                Buffer.BlockCopy(Raw, 0x68, uuid, 0, 16);
                return uuid;
            }
        }

        public string VolumeName => Encoding.UTF8.GetString(Raw, 0x78, 16).TrimEnd('\0');

        public ushort ReservedGdtBlocks { get => U16(0xCE); set => BinaryHelpers.PutU16(Raw, 0xCE, value); }

        public uint JournalInode => U32(0xE0);

        public uint JournalDevice => U32(0xE4);

        public uint LastOrphan { get => U32(0xE8); set => Put(0xE8, value); }

        public byte JournalBackupType { get => Raw[0xFD]; set => Raw[0xFD] = value; }

        public int DescriptorSize => Is64Bit ? Math.Max(32, (int)U16(0xFE)) : 32;

        public uint FirstMetaBg { get => U32(0x104); set => Put(0x104, value); }

        public uint GetJournalBackup(int index) => U32(0x10C + (index * 4));

        public void SetJournalBackup(int index, uint value) => Put(0x10C + (index * 4), value);

        public int LogGroupsPerFlex => Raw[0x174];

        public uint ErrorCount { get => U32(0x194); set => Put(0x194, value); }

        public uint ChecksumSeedField => U32(0x270);

        public uint Checksum { get => U32(0x3FC); set => Put(0x3FC, value); }

        public bool Is64Bit => (FeatureIncompat & Ext4Features.Incompat64Bit) != 0;

        public bool HasMetadataCsum => (FeatureRoCompat & Ext4Features.RoCompatMetadataCsum) != 0;

        public bool HasGdtCsum => (FeatureRoCompat & Ext4Features.RoCompatGdtCsum) != 0;

        public bool HasCsumSeed => (FeatureIncompat & Ext4Features.IncompatCsumSeed) != 0;

        public bool HasExtents => (FeatureIncompat & Ext4Features.IncompatExtents) != 0;

        public bool HasFileType => (FeatureIncompat & Ext4Features.IncompatFileType) != 0;

        public bool HasFlexBg => (FeatureIncompat & Ext4Features.IncompatFlexBg) != 0;

        public bool HasJournal => (FeatureCompat & Ext4Features.CompatHasJournal) != 0;

        public bool HasResizeInode => (FeatureCompat & Ext4Features.CompatResizeInode) != 0;

        public bool HasSparseSuper => (FeatureRoCompat & Ext4Features.RoCompatSparseSuper) != 0;

        public bool HasHugeFile => (FeatureRoCompat & Ext4Features.RoCompatHugeFile) != 0;

        public bool HasDirNlink => (FeatureRoCompat & Ext4Features.RoCompatDirNlink) != 0;

        public bool NeedsRecovery => (FeatureIncompat & Ext4Features.IncompatRecover) != 0;

        /// <summary>Seed for all metadata checksums except the superblock's own.</summary>
        public uint MetadataSeed => HasCsumSeed ? ChecksumSeedField : Crc32c.Append(~0u, Raw, 0x68, 16);

        public uint ComputeChecksum() => Crc32c.Append(~0u, Raw, 0, 0x3FC);

        public Ext4Superblock Clone() => new Ext4Superblock((byte[])Raw.Clone());

        private ushort U16(int o) => BinaryHelpers.U16(Raw, o);

        private uint U32(int o) => BinaryHelpers.U32(Raw, o);

        private void Put(int o, uint v) => BinaryHelpers.PutU32(Raw, o, v);
    }

    /// <summary>Block group descriptor (32 bytes, or 64 bytes with the 64bit feature).</summary>
    internal sealed class Ext4GroupDescriptor
    {
        public const ushort FlagInodeUninit = 0x1;
        public const ushort FlagBlockUninit = 0x2;
        public const ushort FlagItableZeroed = 0x4;

        public Ext4GroupDescriptor(byte[] raw)
        {
            Raw = raw;
        }

        public byte[] Raw { get; }

        private bool Wide => Raw.Length >= 64;

        public long BlockBitmap { get => Lo32Hi32(0x00, 0x20); set => SetLo32Hi32(0x00, 0x20, value); }

        public long InodeBitmap { get => Lo32Hi32(0x04, 0x24); set => SetLo32Hi32(0x04, 0x24, value); }

        public long InodeTable { get => Lo32Hi32(0x08, 0x28); set => SetLo32Hi32(0x08, 0x28, value); }

        public uint FreeBlocks { get => Lo16Hi16(0x0C, 0x2C); set => SetLo16Hi16(0x0C, 0x2C, value); }

        public uint FreeInodes { get => Lo16Hi16(0x0E, 0x2E); set => SetLo16Hi16(0x0E, 0x2E, value); }

        public uint UsedDirs { get => Lo16Hi16(0x10, 0x30); set => SetLo16Hi16(0x10, 0x30, value); }

        public ushort Flags { get => BinaryHelpers.U16(Raw, 0x12); set => BinaryHelpers.PutU16(Raw, 0x12, value); }

        public uint BlockBitmapChecksum { get => Lo16Hi16(0x18, 0x38); set => SetLo16Hi16(0x18, 0x38, value); }

        public uint InodeBitmapChecksum { get => Lo16Hi16(0x1A, 0x3A); set => SetLo16Hi16(0x1A, 0x3A, value); }

        public uint ItableUnused { get => Lo16Hi16(0x1C, 0x32); set => SetLo16Hi16(0x1C, 0x32, value); }

        public ushort Checksum { get => BinaryHelpers.U16(Raw, 0x1E); set => BinaryHelpers.PutU16(Raw, 0x1E, value); }

        /// <summary>metadata_csum: crc32c; uninit_bg (gdt_csum): crc16; otherwise 0.</summary>
        public ushort ComputeChecksum(Ext4Superblock sb, uint group)
        {
            if (sb.HasMetadataCsum)
            {
                uint crc = Crc32c.AppendU32(sb.MetadataSeed, group);
                crc = Crc32c.Append(crc, Raw, 0, 0x1E);
                crc = Crc32c.Append(crc, new byte[2], 0, 2);
                if (Raw.Length > 0x20)
                {
                    crc = Crc32c.Append(crc, Raw, 0x20, Raw.Length - 0x20);
                }

                return (ushort)crc;
            }

            if (sb.HasGdtCsum)
            {
                ushort crc = Crc16.Append(0xFFFF, sb.Raw, 0x68, 16);
                var le = new byte[4];
                BinaryHelpers.PutU32(le, 0, group);
                crc = Crc16.Append(crc, le, 0, 4);
                crc = Crc16.Append(crc, Raw, 0, 0x1E);
                if (Raw.Length > 0x20)
                {
                    crc = Crc16.Append(crc, Raw, 0x20, Raw.Length - 0x20);
                }

                return crc;
            }

            return 0;
        }

        private long Lo32Hi32(int lo, int hi) => BinaryHelpers.U32(Raw, lo) | (Wide ? (long)BinaryHelpers.U32(Raw, hi) << 32 : 0);

        private void SetLo32Hi32(int lo, int hi, long value)
        {
            BinaryHelpers.PutU32(Raw, lo, (uint)value);
            if (Wide)
            {
                BinaryHelpers.PutU32(Raw, hi, (uint)(value >> 32));
            }
        }

        private uint Lo16Hi16(int lo, int hi) => BinaryHelpers.U16(Raw, lo) | (Wide ? (uint)BinaryHelpers.U16(Raw, hi) << 16 : 0);

        private void SetLo16Hi16(int lo, int hi, uint value)
        {
            BinaryHelpers.PutU16(Raw, lo, (ushort)value);
            if (Wide)
            {
                BinaryHelpers.PutU16(Raw, hi, (ushort)(value >> 16));
            }
        }
    }

    /// <summary>On-disk inode; all fields live in <see cref="Raw"/> (inode size bytes).</summary>
    internal sealed class Ext4Inode
    {
        public const ushort TypeMask = 0xF000;
        public const ushort TypeFifo = 0x1000;
        public const ushort TypeChar = 0x2000;
        public const ushort TypeDir = 0x4000;
        public const ushort TypeBlock = 0x6000;
        public const ushort TypeFile = 0x8000;
        public const ushort TypeSymlink = 0xA000;
        public const ushort TypeSocket = 0xC000;

        public const uint FlagIndex = 0x1000;
        public const uint FlagHugeFile = 0x40000;
        public const uint FlagExtents = 0x80000;
        public const uint FlagEaInode = 0x200000;
        public const uint FlagInlineData = 0x10000000;

        public const int BlockArrayOffset = 0x28;
        public const int BlockArraySize = 60;

        public Ext4Inode(uint number, byte[] raw)
        {
            Number = number;
            Raw = raw;
        }

        public uint Number { get; }

        public byte[] Raw { get; }

        public ushort Mode { get => BinaryHelpers.U16(Raw, 0x00); set => BinaryHelpers.PutU16(Raw, 0x00, value); }

        public ushort Type => (ushort)(Mode & TypeMask);

        public bool IsDirectory => Type == TypeDir;

        public bool IsRegular => Type == TypeFile;

        public bool IsSymlink => Type == TypeSymlink;

        public uint Uid => BinaryHelpers.U16(Raw, 0x02) | ((uint)BinaryHelpers.U16(Raw, 0x78) << 16);

        public uint Gid => BinaryHelpers.U16(Raw, 0x18) | ((uint)BinaryHelpers.U16(Raw, 0x7A) << 16);

        public long Size
        {
            get => BinaryHelpers.U32(Raw, 0x04) | ((long)BinaryHelpers.U32(Raw, 0x6C) << 32);
            set { BinaryHelpers.PutU32(Raw, 0x04, (uint)value); BinaryHelpers.PutU32(Raw, 0x6C, (uint)(value >> 32)); }
        }

        public uint Atime => BinaryHelpers.U32(Raw, 0x08);

        public uint Ctime => BinaryHelpers.U32(Raw, 0x0C);

        public uint Mtime => BinaryHelpers.U32(Raw, 0x10);

        public uint Dtime { get => BinaryHelpers.U32(Raw, 0x14); set => BinaryHelpers.PutU32(Raw, 0x14, value); }

        public ushort LinksCount { get => BinaryHelpers.U16(Raw, 0x1A); set => BinaryHelpers.PutU16(Raw, 0x1A, value); }

        /// <summary>i_blocks in 512-byte units (or file system blocks when the huge-file flag is set).</summary>
        public long BlocksRaw
        {
            get => BinaryHelpers.U32(Raw, 0x1C) | ((long)BinaryHelpers.U16(Raw, 0x74) << 32);
            set { BinaryHelpers.PutU32(Raw, 0x1C, (uint)value); BinaryHelpers.PutU16(Raw, 0x74, (ushort)(value >> 32)); }
        }

        public uint Flags { get => BinaryHelpers.U32(Raw, 0x20); set => BinaryHelpers.PutU32(Raw, 0x20, value); }

        public uint Generation => BinaryHelpers.U32(Raw, 0x64);

        public long FileAcl
        {
            get => BinaryHelpers.U32(Raw, 0x68) | ((long)BinaryHelpers.U16(Raw, 0x76) << 32);
            set { BinaryHelpers.PutU32(Raw, 0x68, (uint)value); BinaryHelpers.PutU16(Raw, 0x76, (ushort)(value >> 32)); }
        }

        public int ExtraIsize => Raw.Length > 128 ? BinaryHelpers.U16(Raw, 0x80) : 0;

        public bool IsAllZero
        {
            get
            {
                foreach (var b in Raw)
                {
                    if (b != 0)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        public byte[] GetBlockArray()
        {
            var array = new byte[BlockArraySize];
            Buffer.BlockCopy(Raw, BlockArrayOffset, array, 0, BlockArraySize);
            return array;
        }

        public void SetBlockArray(byte[] array)
        {
            Buffer.BlockCopy(array, 0, Raw, BlockArrayOffset, BlockArraySize);
        }

        /// <summary>Allocated size in file system blocks (including extent tree and xattr blocks).</summary>
        public long AllocatedBlocks(Ext4Superblock sb)
        {
            long raw = BlocksRaw;
            return sb.HasHugeFile && (Flags & FlagHugeFile) != 0 ? raw : raw * 512 / sb.BlockSize;
        }

        public Ext4Inode CloneAs(uint number) => new Ext4Inode(number, (byte[])Raw.Clone());

        public uint ChecksumSeed(Ext4Superblock sb) => Crc32c.AppendU32(Crc32c.AppendU32(sb.MetadataSeed, Number), Generation);

        public bool HasChecksumHi => Raw.Length > 128 && ExtraIsize >= 4;

        public uint ComputeChecksum(Ext4Superblock sb)
        {
            var copy = (byte[])Raw.Clone();
            copy[0x7C] = 0;
            copy[0x7D] = 0;
            if (HasChecksumHi)
            {
                copy[0x82] = 0;
                copy[0x83] = 0;
            }

            uint crc = Crc32c.Append(ChecksumSeed(sb), copy, 0, copy.Length);
            return HasChecksumHi ? crc : crc & 0xFFFF;
        }

        public uint StoredChecksum => BinaryHelpers.U16(Raw, 0x7C) | (HasChecksumHi ? (uint)BinaryHelpers.U16(Raw, 0x82) << 16 : 0);

        public void UpdateChecksum(Ext4Superblock sb)
        {
            if (!sb.HasMetadataCsum)
            {
                return;
            }

            uint crc = ComputeChecksum(sb);
            BinaryHelpers.PutU16(Raw, 0x7C, (ushort)crc);
            if (HasChecksumHi)
            {
                BinaryHelpers.PutU16(Raw, 0x82, (ushort)(crc >> 16));
            }
        }
    }

    /// <summary>One run of a file's block mapping.</summary>
    internal struct Ext4Extent
    {
        public Ext4Extent(long logical, long physical, int count, bool uninitialized)
        {
            Logical = logical;
            Physical = physical;
            Count = count;
            Uninitialized = uninitialized;
        }

        public long Logical { get; }

        public long Physical { get; }

        public int Count { get; }

        /// <summary>Allocated but unwritten (reads as zeros).</summary>
        public bool Uninitialized { get; }
    }

    internal sealed class Ext4DirEntry
    {
        public Ext4DirEntry(uint inode, byte fileType, byte[] name)
        {
            Inode = inode;
            FileType = fileType;
            Name = name;
        }

        public uint Inode { get; }

        public byte FileType { get; }

        /// <summary>Raw name bytes (ext4 names are byte strings, usually UTF-8).</summary>
        public byte[] Name { get; }

        public string NameText => Encoding.UTF8.GetString(Name);

        public bool IsDot => Name.Length == 1 && Name[0] == (byte)'.';

        public bool IsDotDot => Name.Length == 2 && Name[0] == (byte)'.' && Name[1] == (byte)'.';

        public static byte FileTypeFor(ushort type)
        {
            switch (type)
            {
                case Ext4Inode.TypeFile: return 1;
                case Ext4Inode.TypeDir: return 2;
                case Ext4Inode.TypeChar: return 3;
                case Ext4Inode.TypeBlock: return 4;
                case Ext4Inode.TypeFifo: return 5;
                case Ext4Inode.TypeSocket: return 6;
                case Ext4Inode.TypeSymlink: return 7;
                default: return 0;
            }
        }
    }

    internal sealed class Ext4Xattr
    {
        public Ext4Xattr(byte nameIndex, byte[] name, byte[] value, bool inInode)
        {
            NameIndex = nameIndex;
            Name = name;
            Value = value;
            InInode = inInode;
        }

        public byte NameIndex { get; }

        public byte[] Name { get; }

        public byte[] Value { get; }

        public bool InInode { get; }

        /// <summary>Full attribute name as seen by Linux tools (e.g. "user.comment").</summary>
        public string FullName
        {
            get
            {
                string suffix = Encoding.UTF8.GetString(Name);
                switch (NameIndex)
                {
                    case 1: return "user." + suffix;
                    case 2: return "system.posix_acl_access";
                    case 3: return "system.posix_acl_default";
                    case 4: return "trusted." + suffix;
                    case 6: return "security." + suffix;
                    case 7: return "system." + suffix;
                    case 8: return "system.richacl";
                    default: return "#" + NameIndex + "." + suffix;
                }
            }
        }
    }
}
