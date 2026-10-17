using System.Linq;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class OverlayAndOrphanTests
    {
        [Fact]
        public void Overlay_ServesRecoveredSuperblockAndTree()
        {
            Assert.Empty(Jbd2JournalTests.CompareRecoveredWithReference("dirty-journal"));
        }

        [Fact]
        public void Overlay_IsReadOnly()
        {
            using (var root = FixtureImages.OpenRoot("dirty-journal", out var image))
            using (image)
            {
                var device = new OverlayBlockDevice(root, JournalOverlay.Empty, 4096);
                Assert.False(device.CanWrite);
                Assert.Throws<System.InvalidOperationException>(() => device.Write(0, new byte[512], 0, 512));
            }
        }

        [Fact]
        public void OrphanList_FindsDeletedAndTruncatedInodes()
        {
            using (var root = FixtureImages.OpenRoot("orphans", out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                var orphans = OrphanList.Read(reader);
                Assert.Equal(2, orphans.Deleted.Count);
                var truncated = Assert.Single(orphans.Truncated);
                Assert.Equal(1000000, truncated.Value);
                Assert.All(orphans.Deleted, ino => Assert.True(orphans.IsDeleted(ino)));
            }
        }

        [Fact]
        public void OrphanList_EmptyOnCleanFileSystem()
        {
            using (var root = FixtureImages.OpenRoot("rpi-like-4k", out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                Assert.Equal(0, OrphanList.Read(reader).Count);
            }
        }

        [Fact]
        public void CopyPlan_SkipsDeletedOrphans_AndCapsTruncated()
        {
            using (var root = FixtureImages.OpenRoot("orphans", out var image))
            using (image)
            using (var reader = new Ext4Reader(root))
            {
                var orphans = OrphanList.Read(reader);
                var plan = Ext4CopyPlan.Build(reader, orphans, System.Threading.CancellationToken.None);
                Assert.DoesNotContain(plan.Nodes, n => orphans.IsDeleted(n.SourceInode));
                var truncated = plan.BySource[orphans.Truncated.Keys.Single()];
                Assert.Equal((1000000 + 4095) / 4096, plan.MapForCopy(reader, truncated).Sum(e => (long)e.Count));
            }
        }
    }
}
