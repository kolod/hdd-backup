using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HddBackup.Core.Browsing;
using HddBackup.Core.Devices;
using HddBackup.Core.Editing;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Fixtures;
using Xunit;

namespace HddBackup.Core.Tests.Editing
{
    /// <summary>A card is edited in place: every change is written at once, only inside the ext4 partition (feature 010, User Story 5).</summary>
    public class CardEditSessionTests
    {
        private const int Prefix = 1 << 20; // stands for the boot (FAT) partition in front of the ext4 one
        private const int Suffix = 1 << 19;

        private sealed class FailingDevice : MemoryBlockDevice
        {
            public FailingDevice(long size) : base(size, 512)
            {
                FailAfter = int.MaxValue;
            }

            public int FailAfter { get; set; }

            public override void Write(long offset, byte[] buffer, int index, int count)
            {
                if (WriteCalls >= FailAfter)
                {
                    throw new IOException("card removed");
                }

                base.Write(offset, buffer, index, count);
            }
        }

        private sealed class Card
        {
            public FailingDevice Disk;
            public FakeDeviceAccess Access;
            public DeviceInfo Info;
            public BrowsePartition Partition;
            public byte[] Original;
        }

        private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

        private static Card MakeCard(string fixture = "edit-basic")
        {
            byte[] fs = File.ReadAllBytes(FixtureImages.RawPath(fixture));
            var disk = new FailingDevice(Prefix + fs.Length + Suffix);
            for (int i = 0; i < Prefix; i++)
            {
                disk.Data[i] = (byte)(i * 7 + 3);
            }

            Buffer.BlockCopy(fs, 0, disk.Data, Prefix, fs.Length);
            for (int i = 0; i < Suffix; i++)
            {
                disk.Data[Prefix + fs.Length + i] = (byte)(i * 11 + 5);
            }

            var access = new FakeDeviceAccess();
            var info = access.Add(7, disk);
            return new Card
            {
                Disk = disk,
                Access = access,
                Info = info,
                Partition = new BrowsePartition { Number = 2, Offset = Prefix, Length = fs.Length, FileSystem = "ext4", IsBrowsable = true },
                Original = (byte[])disk.Data.Clone()
            };
        }

        private static PartitionView Fs(Card card) => new PartitionView(card.Disk, card.Partition.Offset, card.Partition.Length);

        private static string FirstFile(Card card)
        {
            using (var fs = Fs(card))
            {
                return EditAssert.Snapshot(fs).Values.First(e => e.Type == 'F' && e.Size > 0).Path;
            }
        }

        [Fact]
        public void ChangesGoToTheCardAtOnce_OnlyInsideThePartition()
        {
            var card = MakeCard();
            string path = FirstFile(card);
            using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
            {
                Assert.Equal(SaveStatus.Saved, session.Apply(EditChange.Edit(path, Text("on the card\n"), null), null, CancellationToken.None).Status);
                Assert.Equal(SaveStatus.Saved, session.Apply(EditChange.NewDirectory("/cardnew"), null, CancellationToken.None).Status);
                Assert.Equal(SaveStatus.Saved, session.Apply(EditChange.NewFile("/cardnew/x.txt", Text("x")), null, CancellationToken.None).Status);
            }

            using (var fs = Fs(card))
            {
                EditAssert.CheckerClean(fs, "card after changes");
                Assert.Equal(Text("on the card\n"), EditAssert.Content(fs, path));
                Assert.Equal(Text("x"), EditAssert.Content(fs, "/cardnew/x.txt"));
            }

            for (int i = 0; i < Prefix; i++)
            {
                if (card.Disk.Data[i] != card.Original[i])
                {
                    Assert.Fail("the area in front of the partition changed at " + i);
                }
            }

            int tail = Prefix + (int)card.Partition.Length;
            for (int i = tail; i < card.Disk.Data.Length; i++)
            {
                if (card.Disk.Data[i] != card.Original[i])
                {
                    Assert.Fail("the area behind the partition changed at " + i);
                }
            }

            Assert.Equal(0, card.Access.ExclusiveOpens);
            Assert.Equal(0, card.Access.EjectCalls);
            Assert.True(card.Access.PartitionWriteOpens >= 3);
        }

        [Fact]
        public void ACardThatCannotBeEditedIsRefusedWithoutWriting()
        {
            var card = MakeCard();
            card.Info.IsRemovableMedia = false;
            Assert.Equal(EditRefused.NotRemovable, Assert.Throws<EditRefusedException>(() => CardEditSession.Open(card.Access, card.Info, card.Partition)).Reason);
            card.Info.IsRemovableMedia = true;
            card.Info.IsWriteProtected = true;
            Assert.Equal(EditRefused.WriteProtected, Assert.Throws<EditRefusedException>(() => CardEditSession.Open(card.Access, card.Info, card.Partition)).Reason);
            Assert.Equal(0, card.Disk.WriteCalls);
            Assert.Equal(0, card.Access.PartitionWriteOpens);
        }

        [Fact]
        public void ARefusedChangeLeavesTheCardUntouched()
        {
            var card = MakeCard();
            using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
            {
                var result = session.Apply(EditChange.Remove("/does/not/exist", null), null, CancellationToken.None);
                Assert.Equal(SaveStatus.Refused, result.Status);
            }

            Assert.Equal(0, card.Disk.WriteCalls);
            Assert.Equal(card.Original, card.Disk.Data);
        }

        private static EditChange[] Repair() => new[]
        {
            EditChange.NewDirectory("/home/pi/.ssh", 0x1C0, 1000, 1000),
            EditChange.NewFile("/home/pi/.ssh/authorized_keys", Text("ssh-ed25519 AAAA pi"), 0x180, 1000, 1000),
            EditChange.NewSymlink("/etc/linked", "/etc/hostname"),
            EditChange.Attributes("/home/pi", 0x1ED, null, null)
        };

        [Fact]
        public void ARepairOfSeveralChangesIsWrittenAsOneSetInsideThePartition()
        {
            var card = MakeCard();
            using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
            {
                var result = session.Apply(Repair(), null, CancellationToken.None);
                Assert.Equal(SaveStatus.Saved, result.Status);
                Assert.Equal(4, result.ChangesWritten);
            }

            Assert.Equal(1, card.Access.PartitionWriteOpens);
            using (var fs = Fs(card))
            {
                EditAssert.CheckerClean(fs, "card after repair");
                var tree = EditAssert.Snapshot(fs);
                Assert.Equal((ushort)0x8180, tree["/home/pi/.ssh/authorized_keys"].Mode);
                Assert.Equal(1000u, tree["/home/pi/.ssh/authorized_keys"].Uid);
                Assert.Equal("/etc/hostname", tree["/etc/linked"].Content);
                Assert.Equal((ushort)0x41ED, tree["/home/pi"].Mode);
            }

            for (int i = 0; i < Prefix; i++)
            {
                if (card.Disk.Data[i] != card.Original[i])
                {
                    Assert.Fail("the area in front of the partition changed at " + i);
                }
            }

            for (int i = Prefix + (int)card.Partition.Length; i < card.Disk.Data.Length; i++)
            {
                if (card.Disk.Data[i] != card.Original[i])
                {
                    Assert.Fail("the area behind the partition changed at " + i);
                }
            }
        }

        [Fact]
        public void ARepairWithOneChangeThatCannotBeMadeWritesNothing()
        {
            var card = MakeCard();
            var changes = new[]
            {
                EditChange.NewDirectory("/home/pi/.ssh", 0x1C0, 1000, 1000),
                EditChange.NewFile("/nowhere/at/all", Text("x"))
            };
            using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
            {
                var result = session.Apply(changes, null, CancellationToken.None);
                Assert.Equal(SaveStatus.Refused, result.Status);
                Assert.Equal(EditError.NotFound, result.Error);
            }

            Assert.Equal(0, card.Disk.WriteCalls);
            Assert.Equal(card.Original, card.Disk.Data);
        }

        [Fact]
        public void AnInterruptionAtAnyWriteOfARepairLeavesAReadableFileSystemWithWholeEntriesOnly()
        {
            var probe = MakeCard();
            using (var session = CardEditSession.Open(probe.Access, probe.Info, probe.Partition))
            {
                Assert.Equal(SaveStatus.Saved, session.Apply(Repair(), null, CancellationToken.None).Status);
            }

            int total = probe.Disk.WriteCalls;
            Assert.True(total > 3);
            for (int k = 0; k < total; k++)
            {
                var card = MakeCard();
                card.Disk.FailAfter = k;
                using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
                {
                    Assert.Equal(SaveStatus.Interrupted, session.Apply(Repair(), null, CancellationToken.None).Status);
                }

                card.Disk.FailAfter = int.MaxValue;
                using (var fs = Fs(card))
                {
                    var tree = EditAssert.Snapshot(fs);
                    Assert.True(tree.Count > 1, "unreadable after the failure at write " + k);
                    if (tree.TryGetValue("/home/pi/.ssh/authorized_keys", out var key))
                    {
                        Assert.Equal(19, key.Size);
                        Assert.Equal((ushort)0x8180, key.Mode);
                    }

                    if (tree.TryGetValue("/etc/linked", out var link))
                    {
                        Assert.Equal("/etc/hostname", link.Content);
                    }
                }
            }
        }

        [Fact]
        public void AnInterruptionAtAnyWriteLeavesTheFileEntirelyOldOrEntirelyNew()
        {
            var probe = MakeCard();
            string path = FirstFile(probe);
            byte[] old;
            using (var fs = Fs(probe))
            {
                old = EditAssert.Content(fs, path);
            }

            var neu = Encoding.UTF8.GetBytes(new string('n', 5000));
            using (var session = CardEditSession.Open(probe.Access, probe.Info, probe.Partition))
            {
                Assert.Equal(SaveStatus.Saved, session.Apply(EditChange.Edit(path, neu, null), null, CancellationToken.None).Status);
            }

            int total = probe.Disk.WriteCalls;
            Assert.True(total > 2);
            for (int k = 0; k < total; k++)
            {
                var card = MakeCard();
                card.Disk.FailAfter = k;
                using (var session = CardEditSession.Open(card.Access, card.Info, card.Partition))
                {
                    var result = session.Apply(EditChange.Edit(path, neu, null), null, CancellationToken.None);
                    Assert.Equal(SaveStatus.Interrupted, result.Status);
                }

                card.Disk.FailAfter = int.MaxValue;
                using (var fs = Fs(card))
                {
                    var now = EditAssert.Content(fs, path);
                    Assert.True(now.SequenceEqual(old) || now.SequenceEqual(neu), "the file is half old and half new after the failure at write " + k);
                    var tree = EditAssert.Snapshot(fs);
                    Assert.True(tree.Count > 1, "the tree is unreadable after the failure at write " + k);
                }
            }
        }
    }
}
