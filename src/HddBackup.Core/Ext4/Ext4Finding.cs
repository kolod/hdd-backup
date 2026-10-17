using System;

namespace HddBackup.Core.Ext4
{
    internal enum Ext4FindingKind
    {
        /// <summary>
        /// What an unclean shutdown leaves and a rebuild corrects by construction: bitmaps and
        /// their checksums, free counts, directory counts, used but unreachable inodes, a non-empty
        /// journal.
        /// </summary>
        PowerLoss,

        /// <summary>Any other inconsistency (research R-4); not repaired.</summary>
        Structural
    }

    internal sealed class Ext4Finding
    {
        private static readonly string[] PowerLossPrefixes =
        {
            "Inode bitmap:",
            "Block bitmap:",
            "Superblock free inodes",
            "Superblock free blocks",
            "Journal is not empty."
        };

        private static readonly string[] PowerLossGroupParts =
        {
            " free inodes ",
            " free blocks ",
            " directory count ",
            " inode bitmap checksum mismatch",
            " block bitmap checksum mismatch"
        };

        public Ext4Finding(Ext4FindingKind kind, string message)
        {
            Kind = kind;
            Message = message;
        }

        public Ext4FindingKind Kind { get; }

        public string Message { get; }

        public static Ext4Finding Classify(string message)
        {
            foreach (var prefix in PowerLossPrefixes)
            {
                if (message.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return new Ext4Finding(Ext4FindingKind.PowerLoss, message);
                }
            }

            if (message.StartsWith("Group ", StringComparison.Ordinal))
            {
                foreach (var part in PowerLossGroupParts)
                {
                    if (message.IndexOf(part, StringComparison.Ordinal) >= 0)
                    {
                        return new Ext4Finding(Ext4FindingKind.PowerLoss, message);
                    }
                }
            }

            return new Ext4Finding(Ext4FindingKind.Structural, message);
        }

        public override string ToString() => Message;
    }
}
