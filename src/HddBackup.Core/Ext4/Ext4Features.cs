using System.Collections.Generic;

namespace HddBackup.Core.Ext4
{
    /// <summary>ext4 feature flags and the set this utility can rebuild (research R-2).</summary>
    internal static class Ext4Features
    {
        public const uint CompatDirPrealloc = 0x1;
        public const uint CompatImagicInodes = 0x2;
        public const uint CompatHasJournal = 0x4;
        public const uint CompatExtAttr = 0x8;
        public const uint CompatResizeInode = 0x10;
        public const uint CompatDirIndex = 0x20;
        public const uint CompatLazyBg = 0x40;
        public const uint CompatExcludeBitmap = 0x100;
        public const uint CompatSparseSuper2 = 0x200;
        public const uint CompatFastCommit = 0x400;
        public const uint CompatStableInodes = 0x800;
        public const uint CompatOrphanFile = 0x1000;

        public const uint IncompatCompression = 0x1;
        public const uint IncompatFileType = 0x2;
        public const uint IncompatRecover = 0x4;
        public const uint IncompatJournalDev = 0x8;
        public const uint IncompatMetaBg = 0x10;
        public const uint IncompatExtents = 0x40;
        public const uint Incompat64Bit = 0x80;
        public const uint IncompatMmp = 0x100;
        public const uint IncompatFlexBg = 0x200;
        public const uint IncompatEaInode = 0x400;
        public const uint IncompatDirData = 0x1000;
        public const uint IncompatCsumSeed = 0x2000;
        public const uint IncompatLargeDir = 0x4000;
        public const uint IncompatInlineData = 0x8000;
        public const uint IncompatEncrypt = 0x10000;
        public const uint IncompatCasefold = 0x20000;

        public const uint RoCompatSparseSuper = 0x1;
        public const uint RoCompatLargeFile = 0x2;
        public const uint RoCompatBtreeDir = 0x4;
        public const uint RoCompatHugeFile = 0x8;
        public const uint RoCompatGdtCsum = 0x10;
        public const uint RoCompatDirNlink = 0x20;
        public const uint RoCompatExtraIsize = 0x40;
        public const uint RoCompatHasSnapshot = 0x80;
        public const uint RoCompatQuota = 0x100;
        public const uint RoCompatBigalloc = 0x200;
        public const uint RoCompatMetadataCsum = 0x400;
        public const uint RoCompatReplica = 0x800;
        public const uint RoCompatReadonly = 0x1000;
        public const uint RoCompatProject = 0x2000;
        public const uint RoCompatSharedBlocks = 0x4000;
        public const uint RoCompatVerity = 0x8000;
        public const uint RoCompatOrphanPresent = 0x10000;

        public const uint SupportedCompat = CompatHasJournal | CompatExtAttr | CompatResizeInode | CompatDirIndex;

        public const uint SupportedIncompat = IncompatFileType | IncompatExtents | Incompat64Bit | IncompatFlexBg | IncompatCsumSeed;

        public const uint SupportedRoCompat = RoCompatSparseSuper | RoCompatLargeFile | RoCompatHugeFile | RoCompatGdtCsum
            | RoCompatDirNlink | RoCompatExtraIsize | RoCompatMetadataCsum;

        private static readonly Dictionary<uint, string> CompatNames = new Dictionary<uint, string>
        {
            { CompatDirPrealloc, "dir_prealloc" }, { CompatImagicInodes, "imagic_inodes" }, { CompatHasJournal, "has_journal" },
            { CompatExtAttr, "ext_attr" }, { CompatResizeInode, "resize_inode" }, { CompatDirIndex, "dir_index" },
            { CompatLazyBg, "lazy_bg" }, { CompatExcludeBitmap, "exclude_bitmap" }, { CompatSparseSuper2, "sparse_super2" },
            { CompatFastCommit, "fast_commit" }, { CompatStableInodes, "stable_inodes" }, { CompatOrphanFile, "orphan_file" }
        };

        private static readonly Dictionary<uint, string> IncompatNames = new Dictionary<uint, string>
        {
            { IncompatCompression, "compression" }, { IncompatFileType, "filetype" }, { IncompatRecover, "needs_recovery" },
            { IncompatJournalDev, "journal_dev" }, { IncompatMetaBg, "meta_bg" }, { IncompatExtents, "extent" },
            { Incompat64Bit, "64bit" }, { IncompatMmp, "mmp" }, { IncompatFlexBg, "flex_bg" }, { IncompatEaInode, "ea_inode" },
            { IncompatDirData, "dirdata" }, { IncompatCsumSeed, "metadata_csum_seed" }, { IncompatLargeDir, "large_dir" },
            { IncompatInlineData, "inline_data" }, { IncompatEncrypt, "encrypt" }, { IncompatCasefold, "casefold" }
        };

        private static readonly Dictionary<uint, string> RoCompatNames = new Dictionary<uint, string>
        {
            { RoCompatSparseSuper, "sparse_super" }, { RoCompatLargeFile, "large_file" }, { RoCompatBtreeDir, "btree_dir" },
            { RoCompatHugeFile, "huge_file" }, { RoCompatGdtCsum, "uninit_bg" }, { RoCompatDirNlink, "dir_nlink" },
            { RoCompatExtraIsize, "extra_isize" }, { RoCompatHasSnapshot, "snapshot" }, { RoCompatQuota, "quota" },
            { RoCompatBigalloc, "bigalloc" }, { RoCompatMetadataCsum, "metadata_csum" }, { RoCompatReplica, "replica" },
            { RoCompatReadonly, "read-only" }, { RoCompatProject, "project" }, { RoCompatSharedBlocks, "shared_blocks" },
            { RoCompatVerity, "verity" }, { RoCompatOrphanPresent, "orphan_present" }
        };

        /// <summary>
        /// Names of features present but not supported. needs_recovery is reported separately
        /// (it is a state, not a feature choice).
        /// </summary>
        public static List<string> Unsupported(Ext4Superblock sb)
        {
            var result = new List<string>();
            Collect(sb.FeatureCompat & ~SupportedCompat, CompatNames, "compat", result);
            Collect(sb.FeatureIncompat & ~SupportedIncompat & ~IncompatRecover, IncompatNames, "incompat", result);
            Collect(sb.FeatureRoCompat & ~SupportedRoCompat, RoCompatNames, "ro_compat", result);
            if (!sb.HasExtents)
            {
                result.Add("no extents (ext2/ext3 file system)");
            }

            if (!sb.HasSparseSuper)
            {
                result.Add("no sparse_super");
            }

            return result;
        }

        /// <summary>Names of all features present (for diagnostics).</summary>
        public static List<string> All(Ext4Superblock sb)
        {
            var result = new List<string>();
            Collect(sb.FeatureCompat, CompatNames, "compat", result);
            Collect(sb.FeatureIncompat, IncompatNames, "incompat", result);
            Collect(sb.FeatureRoCompat, RoCompatNames, "ro_compat", result);
            return result;
        }

        private static void Collect(uint bits, Dictionary<uint, string> names, string kind, List<string> result)
        {
            for (int i = 0; i < 32; i++)
            {
                uint bit = 1u << i;
                if ((bits & bit) != 0)
                {
                    result.Add(names.TryGetValue(bit, out var name) ? name : kind + " 0x" + bit.ToString("X"));
                }
            }
        }
    }
}
