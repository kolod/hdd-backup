using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Ext4
{
    /// <summary>
    /// What a superblock says about the state of an ext4 file system: clean, left behind by an
    /// unclean shutdown (repairable, feature 006) or with recorded errors. Cheap: no journal
    /// replay and no consistency check. Shared by the shrink planner and the file browser.
    /// </summary>
    internal static class Ext4StateProbe
    {
        /// <summary>What an unclean shutdown left in this file system.</summary>
        public static RepairReasons ReasonsOf(Ext4Superblock sb)
        {
            var reasons = RepairReasons.None;
            if (sb.NeedsRecovery)
            {
                reasons |= RepairReasons.JournalNeedsRecovery;
            }

            if (sb.LastOrphan != 0 || (sb.State & Ext4Superblock.StateOrphans) != 0)
            {
                reasons |= RepairReasons.OrphanInodes;
            }

            if ((sb.State & Ext4Superblock.StateValid) == 0)
            {
                reasons |= RepairReasons.NotClean;
            }

            return reasons;
        }

        public static FileSystemState Probe(Ext4Superblock sb, int partitionNumber, string name, bool isRoot)
        {
            var state = new FileSystemState { PartitionNumber = partitionNumber, Name = name ?? string.Empty, IsRoot = isRoot };
            state.Reasons = ReasonsOf(sb);
            if ((sb.State & Ext4Superblock.StateErrors) != 0 || sb.ErrorCount != 0)
            {
                state.Condition = FileSystemCondition.Damaged;
                state.Problems = new[] { "The kernel recorded file system errors." };
            }
            else
            {
                state.Condition = state.Reasons == RepairReasons.None ? FileSystemCondition.Clean : FileSystemCondition.NeedsRepair;
            }

            return state;
        }
    }
}
