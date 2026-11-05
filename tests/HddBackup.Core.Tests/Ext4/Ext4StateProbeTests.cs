using HddBackup.Core.Ext4;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class Ext4StateProbeTests
    {
        private static Ext4Superblock RootSuperblock(string fixture)
        {
            using (var root = FixtureImages.OpenRoot(fixture, out var image))
            using (image)
            {
                var raw = new byte[Ext4Superblock.Size];
                root.Read(Ext4Superblock.ByteOffset, raw, 0, raw.Length);
                return new Ext4Superblock(raw);
            }
        }

        [Fact]
        public void CleanFixture_IsClean()
        {
            var state = Ext4StateProbe.Probe(RootSuperblock("rpi-like-4k"), 2, "rootfs", true);
            Assert.Equal(FileSystemCondition.Clean, state.Condition);
            Assert.Equal(RepairReasons.None, state.Reasons);
            Assert.True(state.IsRoot);
            Assert.Equal(2, state.PartitionNumber);
        }

        [Fact]
        public void NotCleanFixture_NeedsRepairBecauseNotClean()
        {
            var state = Ext4StateProbe.Probe(RootSuperblock("not-clean"), 2, "rootfs", true);
            Assert.Equal(FileSystemCondition.NeedsRepair, state.Condition);
            Assert.True((state.Reasons & RepairReasons.NotClean) != 0);
        }

        [Fact]
        public void DirtyJournalFixture_NeedsRepairBecauseOfTheJournal()
        {
            var state = Ext4StateProbe.Probe(RootSuperblock("dirty-journal"), 2, "rootfs", true);
            Assert.Equal(FileSystemCondition.NeedsRepair, state.Condition);
            Assert.True((state.Reasons & RepairReasons.JournalNeedsRecovery) != 0);
        }

        [Fact]
        public void OrphansFixture_NeedsRepairBecauseOfOrphans()
        {
            var state = Ext4StateProbe.Probe(RootSuperblock("orphans"), 2, "rootfs", true);
            Assert.Equal(FileSystemCondition.NeedsRepair, state.Condition);
            Assert.True((state.Reasons & RepairReasons.OrphanInodes) != 0);
        }

        [Fact]
        public void RecordedErrors_AreDamaged()
        {
            var sb = RootSuperblock("rpi-like-4k");
            sb.State = (ushort)(sb.State | Ext4Superblock.StateErrors);
            var state = Ext4StateProbe.Probe(sb, 1, "x", false);
            Assert.Equal(FileSystemCondition.Damaged, state.Condition);
            Assert.NotEmpty(state.Problems);
            Assert.False(state.IsRoot);

            sb = RootSuperblock("rpi-like-4k");
            sb.ErrorCount = 3;
            Assert.Equal(FileSystemCondition.Damaged, Ext4StateProbe.Probe(sb, 1, null, false).Condition);
        }
    }
}
