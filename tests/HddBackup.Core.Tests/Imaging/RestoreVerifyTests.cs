using System.IO;
using HddBackup.Core.Errors;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using Xunit;

namespace HddBackup.Core.Tests.Imaging
{
    public class RestoreVerifyTests
    {
        /// <summary>Silently corrupts one byte of everything written at a given offset.</summary>
        private sealed class LyingDevice : MemoryBlockDevice
        {
            private readonly long _badOffset;

            public LyingDevice(long size, long badOffset)
                : base(size, 512)
            {
                _badOffset = badOffset;
            }

            public override void Write(long offset, byte[] buffer, int index, int count)
            {
                base.Write(offset, buffer, index, count);
                if (_badOffset >= offset && _badOffset < offset + count)
                {
                    Data[_badOffset] ^= 0x55;
                }
            }
        }

        [Fact]
        public void ReadBackMismatch_IsReportedWithExactOffset()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            EngineHarness.Backup(access, access.Add(1, source), path);
            long bad = (3 << 20) + 777;
            var target = new LyingDevice(source.Size, bad);

            var result = EngineHarness.Restore(access, access.Add(2, target), path, verify: true);

            Assert.Equal(OperationState.Failed, result.State);
            Assert.Equal(ErrorCode.VerifyMismatch, result.ErrorCode);
            Assert.Equal(bad, result.ErrorOffset);
            Assert.True(result.SafeToRemove);
            Assert.False(result.ContentComplete);
        }

        /// <summary>Records the engine's progress whenever the target is read (only verification reads it).</summary>
        private sealed class ObservedDevice : MemoryBlockDevice
        {
            public ObservedDevice(long size)
                : base(size, 512)
            {
            }

            public HddBackup.Core.Imaging.IImagingEngine Engine { get; set; }

            public System.Collections.Concurrent.ConcurrentQueue<OperationProgress> SeenDuringReads { get; } =
                new System.Collections.Concurrent.ConcurrentQueue<OperationProgress>();

            public override int Read(long offset, byte[] buffer, int index, int count)
            {
                if (Engine != null)
                {
                    SeenDuringReads.Enqueue(Engine.CurrentProgress);
                }

                return base.Read(offset, buffer, index, count);
            }
        }

        [Fact]
        public async System.Threading.Tasks.Task VerifyPhase_RunsBeforeCompletion()
        {
            var source = BackupDeviceTests.BuildDevice("MBR", 512, 8 << 20);
            var access = new FakeDeviceAccess();
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img");
            EngineHarness.Backup(access, access.Add(1, source), path);
            var target = new ObservedDevice(source.Size);
            var info = access.Add(2, target);
            var engine = EngineHarness.NewEngine(access);
            target.Engine = engine;

            var result = await engine.RunAsync(
                new HddBackup.Core.Imaging.OperationRequest
                {
                    Kind = HddBackup.Core.Imaging.OperationKind.RestoreDevice,
                    Device = info,
                    ImagePath = path,
                    VerifyAfter = true
                },
                System.Threading.CancellationToken.None);

            Assert.Equal(OperationState.Completed, result.State);
            Assert.NotEmpty(target.SeenDuringReads);
            Assert.All(target.SeenDuringReads, p =>
            {
                Assert.Equal(OperationPhase.Verifying, p.Phase);
                Assert.True(p.Percent < 100.0);
            });
            Assert.Equal(100.0, engine.CurrentProgress.Percent);
        }
    }
}
