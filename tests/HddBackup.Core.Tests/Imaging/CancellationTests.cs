using System;
using System.IO;
using System.Threading;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class CancellationTests
    {
        /// <summary>Cancels the operation once a given number of bytes has passed through.</summary>
        private sealed class CancellingDevice : MemoryBlockDevice
        {
            private readonly CancellationTokenSource _cts;
            private readonly long _cancelAt;

            public CancellingDevice(long size, CancellationTokenSource cts, long cancelAt)
                : base(size, 512)
            {
                _cts = cts;
                _cancelAt = cancelAt;
            }

            /// <summary>Set after setup so partition-table probing does not trigger cancellation.</summary>
            public bool Armed { get; set; }

            public override int Read(long offset, byte[] buffer, int index, int count)
            {
                if (Armed && offset >= _cancelAt)
                {
                    _cts.Cancel();
                }

                return base.Read(offset, buffer, index, count);
            }

            public override void Write(long offset, byte[] buffer, int index, int count)
            {
                if (Armed && offset >= _cancelAt)
                {
                    _cts.Cancel();
                }

                base.Write(offset, buffer, index, count);
            }
        }

        public static TheoryData<string, long> CancelPoints()
        {
            var data = new TheoryData<string, long>();
            var random = new Random(2026);
            foreach (var ext in new[] { ".img", ".img.gz" })
            {
                data.Add(ext, 0);
                for (int i = 0; i < 3; i++)
                {
                    data.Add(ext, random.Next(1, 23) * EngineHarness.BufferSize);
                }
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(CancelPoints))]
        public void CancelledBackup_LeavesNoFiles(string extension, long cancelAt)
        {
            using (var cts = new CancellationTokenSource())
            {
                var device = new CancellingDevice(6 << 20, cts, cancelAt);
                device.Fill(4);
                var access = new FakeDeviceAccess();
                var info = access.Add(1, device);
                device.Armed = true;
                var dir = TestPaths.NewTempDirectory();

                var result = EngineHarness.Run(
                    access,
                    new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = Path.Combine(dir, "disk" + extension) },
                    cts.Token);

                Assert.Equal(OperationState.Cancelled, result.State);
                Assert.False(result.ContentComplete);
                Assert.True(result.SafeToRemove);
                Assert.Empty(Directory.GetFiles(dir));
                Assert.Equal(1, access.ReadReleases);
                Assert.Equal(0, access.EjectCalls);
            }
        }

        [Theory]
        [MemberData(nameof(CancelPoints))]
        public void CancelledRestore_FlushesThenReleases(string extension, long cancelAt)
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk" + extension);
            EngineHarness.Backup(access, access.Add(1, source), path);

            using (var cts = new CancellationTokenSource())
            {
                var target = new CancellingDevice(source.Size, cts, cancelAt);
                var info = access.Add(2, target);
                target.Armed = true;
                int ejectsBefore = access.EjectCalls;

                var result = EngineHarness.Run(
                    access,
                    new OperationRequest { Kind = OperationKind.RestoreDevice, Device = info, ImagePath = path, VerifyAfter = true },
                    cts.Token);

                Assert.Equal(OperationState.Cancelled, result.State);
                Assert.False(result.ContentComplete);
                Assert.True(result.SafeToRemove);
                Assert.Equal(1, access.ExclusiveReleases);
                Assert.Equal(target.WriteCalls, target.WritesAtLastFlush < 0 ? 0 : target.WritesAtLastFlush);
                Assert.Equal(ejectsBefore, access.EjectCalls);
            }
        }

        [Fact]
        public void CancelBeforeStart_TouchesNothing()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var info = access.Add(1, source);
            var dir = TestPaths.NewTempDirectory();

            var result = EngineHarness.Run(
                access,
                new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = Path.Combine(dir, "x.img") },
                new CancellationToken(true));

            Assert.Equal(OperationState.Cancelled, result.State);
            Assert.Equal(0, access.ReadOpens);
            Assert.Empty(Directory.GetFiles(dir));
        }
    }
}
