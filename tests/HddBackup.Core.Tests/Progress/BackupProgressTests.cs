using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using HddBackup.Core.Devices;
using HddBackup.Core.Imaging;
using HddBackup.Core.Progress;
using HddBackup.Core.Tests.Fakes;
using HddBackup.Core.Tests.Imaging;
using Xunit;

namespace HddBackup.Core.Tests.Progress
{
    public class BackupProgressTests
    {
        /// <summary>Records the progress seen at the moment the device is ejected.</summary>
        private sealed class ObservingAccess : IDeviceAccess
        {
            private readonly FakeDeviceAccess _inner;

            public ObservingAccess(FakeDeviceAccess inner)
            {
                _inner = inner;
            }

            public IImagingEngine Engine { get; set; }

            public OperationProgress AtEject { get; private set; }

            public string ImagePath { get; set; }

            public bool ImageExistedAtEject { get; private set; }

            public IBlockDevice OpenForRead(DeviceInfo device) => _inner.OpenForRead(device);

            public IExclusiveDevice OpenExclusiveForWrite(DeviceInfo device) => _inner.OpenExclusiveForWrite(device);

            public EjectResult Eject(DeviceInfo device)
            {
                AtEject = Engine.CurrentProgress;
                ImageExistedAtEject = ImagePath != null && File.Exists(ImagePath);
                return _inner.Eject(device);
            }

            public PathInfo GetPathInfo(string path) => _inner.GetPathInfo(path);
        }

        [Fact]
        public async System.Threading.Tasks.Task PercentStaysBelow100UntilCompleted_AndReaches100()
        {
            var fake = new FakeDeviceAccess();
            var info = fake.Add(1, BackupDeviceTests.BuildDevice("MBR", 512, 16 << 20));
            var access = new ObservingAccess(fake);
            var engine = EngineHarness.NewEngine(access);
            access.Engine = engine;
            var path = Path.Combine(TestPaths.NewTempDirectory(), "disk.img.gz");
            access.ImagePath = path;

            var samples = new ConcurrentQueue<OperationProgress>();
            var task = engine.RunAsync(new OperationRequest { Kind = OperationKind.BackupDevice, Device = info, ImagePath = path }, CancellationToken.None);
            while (!task.IsCompleted)
            {
                samples.Enqueue(engine.CurrentProgress);
                Thread.Sleep(1);
            }

            var result = await task;
            Assert.Equal(OperationState.Completed, result.State);

            double last = 0;
            foreach (var s in samples)
            {
                Assert.True(s.Percent >= last, "Percent went backwards");
                last = s.Percent;
                if (s.State != OperationState.Completed)
                {
                    Assert.True(s.Percent < 100.0);
                }
            }

            // At eject time the image is committed, the device handle is closed, but we are not done.
            Assert.NotNull(access.AtEject);
            Assert.True(access.ImageExistedAtEject);
            Assert.True(access.AtEject.Percent < 100.0);
            Assert.Equal(OperationPhase.Ejecting, access.AtEject.Phase);
            Assert.Equal(1, fake.ReadReleases);

            var final = engine.CurrentProgress;
            Assert.Equal(100.0, final.Percent);
            Assert.Equal(OperationState.Completed, final.State);
        }
    }
}
