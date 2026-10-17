using System.Linq;
using HddBackup.Core.Ext4;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Ext4
{
    public class CheckerKindTests
    {
        private static System.Collections.Generic.List<Ext4Finding> Check(string name, bool recovered)
        {
            using (var root = FixtureImages.OpenRoot(name, out var image))
            using (image)
            {
                if (!recovered)
                {
                    return Ext4Checker.CheckDetailed(root);
                }

                JournalOverlay overlay;
                using (var raw = new Ext4Reader(root))
                {
                    overlay = Jbd2Journal.Open(raw).Replay();
                }

                return Ext4Checker.CheckDetailed(new OverlayBlockDevice(root, overlay, 4096));
            }
        }

        [Theory]
        [InlineData("not-clean", false)]
        [InlineData("orphans", false)]
        [InlineData("dirty-journal", true)]
        [InlineData("dirty-journal-torn", true)]
        public void PowerLossFixtures_HaveOnlyPowerLossFindings(string name, bool recovered)
        {
            var findings = Check(name, recovered);
            Assert.All(findings, f => Assert.True(f.Kind == Ext4FindingKind.PowerLoss, f.Message));
        }

        [Fact]
        public void NotClean_IsDetected()
        {
            Assert.NotEmpty(Check("not-clean", false));
        }

        [Fact]
        public void DamagedDirectoryEntry_IsStructural()
        {
            Assert.Contains(Check("damaged-dirent", false), f => f.Kind == Ext4FindingKind.Structural);
        }

        [Theory]
        [InlineData("rpi-like-4k")]
        [InlineData("dirty-journal.reference")]
        public void CleanFileSystems_HaveNoFindings(string name)
        {
            Assert.Empty(Check(name, false));
        }

        [Fact]
        public void Classification_OfKnownMessages()
        {
            Assert.Equal(Ext4FindingKind.PowerLoss, Ext4Finding.Classify("Group 0 free blocks 7 should be 12341.").Kind);
            Assert.Equal(Ext4FindingKind.PowerLoss, Ext4Finding.Classify("Block bitmap: block 3730 is in use but marked free.").Kind);
            Assert.Equal(Ext4FindingKind.Structural, Ext4Finding.Classify("Block 100 is used twice (inode 12).").Kind);
            Assert.Equal(Ext4FindingKind.Structural, Ext4Finding.Classify("Inode 12 link count 1 should be 2.").Kind);
        }
    }
}
