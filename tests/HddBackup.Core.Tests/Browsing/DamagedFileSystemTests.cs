using System;
using System.IO;
using System.Linq;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Errors;
using HddBackup.Core.Ext4;
using HddBackup.Core.Shrinking;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Browsing
{
    /// <summary>
    /// File systems that a power cut left behind, or that are damaged (feature 008, User Story 5):
    /// they open as they are on disk with the right state, unreadable entries are marked, and
    /// nothing is replayed, repaired or written.
    /// </summary>
    public sealed class DamagedFileSystemTests : IDisposable
    {
        private readonly TempDirs _temp = new TempDirs();

        public void Dispose() => _temp.Dispose();

        private static (BrowseSource Source, Ext4FileSystemView View) Open(string path)
        {
            var source = BrowseSource.OpenImage(path);
            return (source, source.OpenFileSystem(source.Browsable.Last()));
        }

        private string CopyOf(string fixture)
        {
            var path = Path.Combine(_temp.New(), fixture + ".img");
            File.Copy(FixtureImages.RawPath(fixture), path);
            return path;
        }

        /// <summary>Byte offset of an inode in an image whose ext4 partition starts at <paramref name="partitionOffset"/>.</summary>
        private static long InodeOffset(string image, long partitionOffset, uint inode)
        {
            using (var device = FileBlockDevice.OpenRead(image))
            using (var window = new PartitionView(device, partitionOffset, device.Size - partitionOffset))
            using (var reader = new Ext4Reader(window))
            {
                uint index = inode - 1;
                int group = (int)(index / reader.Superblock.InodesPerGroup);
                long inTable = (long)(index % reader.Superblock.InodesPerGroup) * reader.Superblock.InodeSize;
                return partitionOffset + (reader.Groups[group].InodeTable * reader.BlockSize) + inTable;
            }
        }

        private static void Overwrite(string image, long offset, byte[] data)
        {
            using (var stream = new FileStream(image, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                stream.Position = offset;
                stream.Write(data, 0, data.Length);
            }
        }

        [Theory]
        [InlineData("dirty-journal", RepairReasons.JournalNeedsRecovery)]
        [InlineData("orphans", RepairReasons.OrphanInodes)]
        [InlineData("not-clean", RepairReasons.NotClean)]
        public void PowerLossFileSystems_OpenWithTheirState_AndTheImageStaysUntouched(string fixture, RepairReasons reason)
        {
            string path = FixtureImages.RawPath(fixture);
            string before = BrowseSourceTests.Sha256Of(path);
            var (source, view) = Open(path);
            using (source)
            using (view)
            {
                var partition = source.Browsable.Last();
                Assert.Equal(FileSystemCondition.NeedsRepair, partition.State.Condition);
                Assert.True((partition.State.Reasons & reason) != 0);

                var tree = FileSystemViewTests.Walk(view);
                Assert.NotEmpty(tree);
                Assert.All(tree.Values, e => Assert.Null(e.Unreadable));
                Assert.True(view.Counts.Approximate);
            }

            Assert.Equal(before, BrowseSourceTests.Sha256Of(path));
        }

        [Fact]
        public void TheJournalIsNotReplayed_SoTheContentIsTheOneOnDisk()
        {
            var (dirtySource, dirty) = Open(FixtureImages.RawPath("dirty-journal"));
            var (referenceSource, reference) = Open(FixtureImages.RawPath("dirty-journal.reference"));
            using (dirtySource)
            using (dirty)
            using (referenceSource)
            using (reference)
            {
                // Written after boot, so it exists only in the journal of the dirty image.
                Assert.Null(dirty.GetEntry("/home/pi/new-after-boot.log"));
                Assert.NotNull(reference.GetEntry("/home/pi/new-after-boot.log"));

                // Rewritten after boot: the dirty image still has the old content on disk.
                var oldLib = dirty.GetEntry("/usr/lib/lib7.so");
                var newLib = reference.GetEntry("/usr/lib/lib7.so");
                Assert.NotEqual(FileSystemViewTests.Sha256(reference, newLib), FileSystemViewTests.Sha256(dirty, oldLib));
            }
        }

        [Fact]
        public void ADamagedDirectoryEntry_IsMarkedUnreadable_AndTheRestWorks()
        {
            var (source, view) = Open(FixtureImages.RawPath("damaged-dirent"));
            using (source)
            using (view)
            {
                var etc = view.GetEntry("/etc");
                var children = view.GetChildren(etc, CancellationToken.None);
                Assert.Contains(children, c => c.Name == "passwd");
                var fstab = children.Single(c => c.Name == "fstab");
                view.Describe(fstab);
                Assert.NotNull(fstab.Unreadable);

                var passwd = children.Single(c => c.Name == "passwd");
                view.Describe(passwd);
                Assert.Null(passwd.Unreadable);
                Assert.True(FileSystemViewTests.Sha256(view, passwd).Length == 64);

                var summary = ExtractorTests.Extract(view, etc, _temp.New());
                var failed = Assert.Single(summary.Issues, i => i.Kind == ExtractionIssueKind.Failed);
                Assert.Equal("/etc/fstab", failed.Path);
                Assert.Equal(ExtractionIssueReason.Unreadable, failed.Reason);
                Assert.True(summary.FilesExtracted >= 2);
            }
        }

        [Fact]
        public void ARecordedFileSystemError_IsDamaged_StillBrowsable_WithApproximateCounts()
        {
            string image = CopyOf("rpi-like-4k");
            long offset;
            using (var source = BrowseSource.OpenImage(image))
            {
                offset = source.Browsable.Single().Offset;
            }

            var raw = new byte[Ext4Superblock.Size];
            using (var stream = new FileStream(image, FileMode.Open, FileAccess.ReadWrite))
            {
                stream.Position = offset + Ext4Superblock.ByteOffset;
                stream.Read(raw, 0, raw.Length);
                var sb = new Ext4Superblock(raw);
                sb.ErrorCount = 2;
                sb.Checksum = sb.ComputeChecksum();
                stream.Position = offset + Ext4Superblock.ByteOffset;
                stream.Write(sb.Raw, 0, sb.Raw.Length);
            }

            var (source2, view) = Open(image);
            using (source2)
            using (view)
            {
                Assert.Equal(FileSystemCondition.Damaged, source2.Browsable.Last().State.Condition);
                Assert.True(view.Counts.Approximate);
                Assert.NotEmpty(view.GetChildren(view.Root, CancellationToken.None));
            }
        }

        [Theory]
        [InlineData(false)] // cleared
        [InlineData(true)] // garbage with a wrong checksum
        public void ADamagedRootDirectory_MeansNothingToBrowse(bool garbage)
        {
            string image = CopyOf("browse-names");
            var inodeSize = 256;
            var data = Enumerable.Repeat(garbage ? (byte)0xFF : (byte)0, inodeSize).ToArray();
            Overwrite(image, InodeOffset(image, 0, 2), data);

            using (var source = BrowseSource.OpenImage(image))
            {
                Assert.Equal(BrowseProblem.NoExt4, source.Problem);
                var partition = Assert.Single(source.Partitions);
                Assert.False(partition.IsBrowsable);
                Assert.Contains(garbage ? "damaged file system" : "root directory", partition.Unsupported);
                Assert.Throws<ImagingException>(() => source.OpenFileSystem(partition));
            }
        }

        [Fact]
        public void AFileWithADamagedInode_IsUnreadable_AndExtractionContinues()
        {
            string image = CopyOf("browse-names");
            uint inode;
            var (probeSource, probe) = Open(image);
            using (probeSource)
            using (probe)
            {
                inode = probe.GetEntry("/etc/hostname").Inode;
            }

            Overwrite(image, InodeOffset(image, 0, inode), Enumerable.Repeat((byte)0xFF, 256).ToArray());

            var (source, view) = Open(image);
            using (source)
            using (view)
            {
                var etc = view.GetEntry("/etc");
                var hostname = view.GetChildren(etc, CancellationToken.None).Single(c => c.Name == "hostname");
                view.Describe(hostname);
                Assert.Contains("checksum", hostname.Unreadable);
                var buffer = new byte[16];
                var read = Assert.Throws<ImagingException>(() => view.ReadRange(hostname, 0, buffer, 0, buffer.Length));
                Assert.Equal(ErrorCode.CorruptImage, read.Code);

                var dest = _temp.New();
                var summary = ExtractorTests.Extract(view, etc, dest);
                Assert.Equal("/etc/hostname", Assert.Single(summary.Issues, i => i.Kind == ExtractionIssueKind.Failed).Path);
                Assert.True(File.Exists(Path.Combine(dest, "etc", "passwd")));
                Assert.False(File.Exists(Path.Combine(dest, "etc", "hostname")));
            }
        }

        [Theory]
        [InlineData(0x10000000u, "inline")] // inline data
        [InlineData(0x800u, "encrypted")]
        public void FilesThatTheReaderCannotServe_AreNotViewableWithTheReason(uint flag, string word)
        {
            string image = CopyOf("browse-names");
            uint number;
            var (probeSource, probe) = Open(image);
            using (probeSource)
            using (probe)
            {
                number = probe.GetEntry("/etc/hostname").Inode;
            }

            long offset = InodeOffset(image, 0, number);
            using (var device = FileBlockDevice.OpenRead(image))
            using (var window = new PartitionView(device, 0, device.Size))
            using (var reader = new Ext4Reader(window))
            {
                var node = reader.ReadInode(number);
                node.Flags |= flag;
                node.UpdateChecksum(reader.Superblock);
                device.Dispose();
                Overwrite(image, offset, node.Raw);
            }

            var (source, view) = Open(image);
            using (source)
            using (view)
            {
                var entry = view.GetEntry("/etc/hostname");
                Assert.NotNull(entry.NotViewable);
                Assert.Contains(word, entry.NotViewable);
                Assert.False(entry.HasContent);
                var preview = FilePreview.Create(view, entry, PreviewMode.Auto, CancellationToken.None);
                Assert.Equal(string.Empty, preview.Text);

                var summary = ExtractorTests.Extract(view, view.GetEntry("/etc"), _temp.New());
                Assert.Contains(summary.Issues, i => i.Reason == ExtractionIssueReason.NotExtractable && i.Path == "/etc/hostname");
            }
        }
    }
}
