using System.Collections.Generic;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// Inodes on the orphan list (research R-3): files deleted while still open (link count 0),
    /// whose space was never freed, and files whose truncation was interrupted (link count &gt; 0).
    /// </summary>
    internal sealed class OrphanList
    {
        public static readonly OrphanList None = new OrphanList(new HashSet<uint>(), new Dictionary<uint, long>());

        private readonly HashSet<uint> _deleted;

        private OrphanList(HashSet<uint> deleted, Dictionary<uint, long> truncated)
        {
            _deleted = deleted;
            Deleted = deleted;
            Truncated = truncated;
        }

        /// <summary>Deleted while open: not copied (they are not in the directory tree).</summary>
        public IReadOnlyCollection<uint> Deleted { get; }

        /// <summary>Truncate in progress: inode → size; data is copied only up to that size.</summary>
        public IReadOnlyDictionary<uint, long> Truncated { get; }

        public int Count => Deleted.Count + Truncated.Count;

        public bool IsDeleted(uint inode) => _deleted.Contains(inode);

        public static OrphanList Read(Ext4Reader reader)
        {
            var sb = reader.Superblock;
            if ((sb.FeatureCompat & Ext4Features.CompatOrphanFile) != 0)
            {
                throw new Ext4FormatException("File systems with an orphan file are not supported.");
            }

            var deleted = new HashSet<uint>();
            var truncated = new Dictionary<uint, long>();
            var seen = new HashSet<uint>();
            uint next = sb.LastOrphan;
            while (next != 0)
            {
                if (next < sb.FirstInode || next > sb.InodesCount)
                {
                    throw new Ext4FormatException("Orphan list contains invalid inode " + next + ".");
                }

                if (!seen.Add(next))
                {
                    throw new Ext4FormatException("Orphan list loops at inode " + next + ".");
                }

                var inode = reader.ReadInode(next);
                if (inode.LinksCount == 0)
                {
                    deleted.Add(next);
                }
                else
                {
                    truncated[next] = inode.Size;
                }

                next = inode.Dtime; // the next orphan is kept in i_dtime
            }

            return deleted.Count == 0 && truncated.Count == 0 ? None : new OrphanList(deleted, truncated);
        }
    }
}
