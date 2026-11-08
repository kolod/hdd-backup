using System.Collections.Generic;
using HddBackup.Core.Ext4;
using HddBackup.Core.Shrinking;

namespace HddBackup.Core.Editing
{
    /// <summary>Why a file system cannot be edited (research R-2).</summary>
    public enum EditRefusal
    {
        None,
        NeedsRecovery,
        HasOrphans,
        HasErrors,
        NotClean,
        UnsupportedFeature
    }

    public sealed class EditSupport
    {
        public EditSupport(EditRefusal reason, IReadOnlyList<string> features)
        {
            Reason = reason;
            Features = features;
        }

        public bool Editable => Reason == EditRefusal.None;

        public EditRefusal Reason { get; }

        /// <summary>For <see cref="EditRefusal.UnsupportedFeature"/>: the feature names.</summary>
        public IReadOnlyList<string> Features { get; }
    }

    /// <summary>
    /// A file system is edited only when it is clean (no journal recovery pending, no orphans, no recorded
    /// errors) and uses only features the reader and the editor understand; everything else is refused with
    /// the reason, as the shrink and repair features do.
    /// </summary>
    internal static class Ext4EditSupportCheck
    {
        public static EditSupport Check(Ext4Superblock sb)
        {
            if (sb.NeedsRecovery)
            {
                return new EditSupport(EditRefusal.NeedsRecovery, new string[0]);
            }

            if (sb.LastOrphan != 0 || (sb.State & Ext4Superblock.StateOrphans) != 0)
            {
                return new EditSupport(EditRefusal.HasOrphans, new string[0]);
            }

            if ((sb.State & Ext4Superblock.StateErrors) != 0 || sb.ErrorCount != 0)
            {
                return new EditSupport(EditRefusal.HasErrors, new string[0]);
            }

            if ((sb.State & Ext4Superblock.StateValid) == 0)
            {
                return new EditSupport(EditRefusal.NotClean, new string[0]);
            }

            var unsupported = Ext4Features.Unsupported(sb);
            if (unsupported.Count > 0)
            {
                return new EditSupport(EditRefusal.UnsupportedFeature, unsupported);
            }

            return new EditSupport(EditRefusal.None, new string[0]);
        }
    }
}
