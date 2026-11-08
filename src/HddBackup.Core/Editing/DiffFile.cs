using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HddBackup.Core.Devices;
using HddBackup.Core.Ext4;

namespace HddBackup.Core.Editing
{
    /// <summary>
    /// Which version of which image a diff or redo file belongs to: the file length, the partition, the file system
    /// UUID and a hash of the superblock and group descriptors (research R-9). It detects "the image changed since".
    /// </summary>
    public sealed class ImageIdentity
    {
        public long FileLength { get; set; }

        public int PartitionNumber { get; set; }

        public long Offset { get; set; }

        public long Length { get; set; }

        public byte[] Uuid { get; set; } = new byte[16];

        public byte[] MetaHash { get; set; } = new byte[32];

        public bool Matches(ImageIdentity o) =>
            FileLength == o.FileLength && PartitionNumber == o.PartitionNumber && Offset == o.Offset && Length == o.Length
            && Uuid.SequenceEqual(o.Uuid) && MetaHash.SequenceEqual(o.MetaHash);

        /// <summary>Reads the identity of the file system on <paramref name="partition"/>.</summary>
        internal static ImageIdentity Of(IBlockDevice partition, long fileLength, int number, long offset)
        {
            using (var reader = new Ext4Reader(partition))
            {
                var sb = reader.Superblock;
                int gdtBytes = (int)(((long)reader.GroupCount * sb.DescriptorSize + reader.BlockSize - 1) / reader.BlockSize * reader.BlockSize);
                var data = new byte[Ext4Superblock.Size + gdtBytes];
                Buffer.BlockCopy(sb.Raw, 0, data, 0, Ext4Superblock.Size);
                reader.ReadBlocks(sb.FirstDataBlock + 1, gdtBytes / reader.BlockSize, data, Ext4Superblock.Size);
                using (var sha = SHA256.Create())
                {
                    return new ImageIdentity
                    {
                        FileLength = fileLength,
                        PartitionNumber = number,
                        Offset = offset,
                        Length = partition.Size,
                        Uuid = sb.Uuid,
                        MetaHash = sha.ComputeHash(data)
                    };
                }
            }
        }

        internal void Write(BinaryWriter w)
        {
            w.Write(FileLength);
            w.Write(PartitionNumber);
            w.Write(Offset);
            w.Write(Length);
            w.Write(Uuid);
            w.Write(MetaHash);
        }

        internal static ImageIdentity Read(BinaryReader r)
        {
            return new ImageIdentity
            {
                FileLength = r.ReadInt64(),
                PartitionNumber = r.ReadInt32(),
                Offset = r.ReadInt64(),
                Length = r.ReadInt64(),
                Uuid = r.ReadBytes(16),
                MetaHash = r.ReadBytes(32)
            };
        }
    }

    public enum DiffStatus
    {
        None,
        Matches,
        ImageChanged,
        Corrupt,
        Unsupported
    }

    /// <summary>The pending-changes file next to an image (research R-9): written whole and replaced atomically after every change.</summary>
    public sealed class DiffFile
    {
        public const string Extension = ".hddbackup-diff";
        public const int MaxContent = 1 << 20;
        public const int MaxChanges = 10000;
        public const long MaxTotal = 256L * 1024 * 1024;
        private const string Magic = "HDDDIFF1";

        public DiffFile(ImageIdentity identity, IEnumerable<EditChange> changes)
        {
            Identity = identity;
            Changes = changes.ToList();
        }

        public ImageIdentity Identity { get; }

        public List<EditChange> Changes { get; }

        public static string PathFor(string imagePath) => imagePath + Extension;

        public void Write(string path)
        {
            if (Changes.Count > MaxChanges || Changes.Sum(c => (long)(c.Content?.Length ?? 0)) > MaxTotal)
            {
                throw new EditException(EditError.TooLarge, "too many changes");
            }

            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, true))
            {
                w.Write(Encoding.ASCII.GetBytes(Magic));
                w.Write(1);
                Identity.Write(w);
                w.Write(Changes.Count);
                foreach (var c in Changes)
                {
                    w.Write((byte)c.Kind);
                    w.Write(c.Path);
                    w.Write(c.NewName ?? string.Empty);
                    w.Write(c.Original != null);
                    if (c.Original != null)
                    {
                        w.Write(c.Original.Inode);
                        w.Write(c.Original.Size);
                        w.Write(c.Original.Mtime);
                    }

                    w.Write(c.Content == null ? -1 : c.Content.Length);
                    if (c.Content != null)
                    {
                        w.Write(c.Content);
                    }
                }
            }

            AtomicFile.Write(path, Seal(ms.ToArray()));
        }

        /// <summary>Reads a diff file; <paramref name="status"/> tells about a missing, corrupt or unsupported one.</summary>
        public static DiffFile Read(string path, out DiffStatus status)
        {
            status = DiffStatus.None;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                byte[] body = Unseal(File.ReadAllBytes(path));
                if (body == null)
                {
                    status = DiffStatus.Corrupt;
                    return null;
                }

                using (var r = new BinaryReader(new MemoryStream(body), Encoding.UTF8))
                {
                    if (Encoding.ASCII.GetString(r.ReadBytes(8)) != Magic)
                    {
                        status = DiffStatus.Corrupt;
                        return null;
                    }

                    if (r.ReadInt32() != 1)
                    {
                        status = DiffStatus.Unsupported;
                        return null;
                    }

                    var identity = ImageIdentity.Read(r);
                    int count = r.ReadInt32();
                    var changes = new List<EditChange>();
                    for (int i = 0; i < count; i++)
                    {
                        var kind = (EditKind)r.ReadByte();
                        string p = r.ReadString();
                        string newName = r.ReadString();
                        OriginalStamp original = r.ReadBoolean() ? new OriginalStamp(r.ReadUInt32(), r.ReadInt64(), r.ReadUInt32()) : null;
                        int len = r.ReadInt32();
                        byte[] content = len < 0 ? null : r.ReadBytes(len);
                        switch (kind)
                        {
                            case EditKind.EditContent:
                                changes.Add(EditChange.Edit(p, content, original));
                                break;
                            case EditKind.CreateFile:
                                changes.Add(EditChange.NewFile(p, content));
                                break;
                            case EditKind.CreateDirectory:
                                changes.Add(EditChange.NewDirectory(p));
                                break;
                            case EditKind.Delete:
                                changes.Add(EditChange.Remove(p, original));
                                break;
                            case EditKind.Rename:
                                changes.Add(EditChange.Move(p, newName, original));
                                break;
                            default:
                                status = DiffStatus.Corrupt;
                                return null;
                        }
                    }

                    status = DiffStatus.Matches;
                    return new DiffFile(identity, changes);
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException || ex is FormatException)
            {
                status = DiffStatus.Corrupt;
                return null;
            }
        }

        internal static byte[] Seal(byte[] body)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(body);
                var all = new byte[body.Length + hash.Length];
                Buffer.BlockCopy(body, 0, all, 0, body.Length);
                Buffer.BlockCopy(hash, 0, all, body.Length, hash.Length);
                return all;
            }
        }

        internal static byte[] Unseal(byte[] all)
        {
            if (all.Length < 32 + 12)
            {
                return null;
            }

            var body = new byte[all.Length - 32];
            Buffer.BlockCopy(all, 0, body, 0, body.Length);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(body);
                for (int i = 0; i < 32; i++)
                {
                    if (hash[i] != all[body.Length + i])
                    {
                        return null;
                    }
                }
            }

            return body;
        }
    }

    /// <summary>Writes a file so that it is either the old one or the new one, never half of it: temporary file, flush, replace.</summary>
    internal static class AtomicFile
    {
        public static void Write(string path, byte[] data)
        {
            string temp = path + ".tmp";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                f.Write(data, 0, data.Length);
                f.Flush(true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
    }
}
