using System;
using HddBackup.Core.Progress;
using Xunit;

namespace HddBackup.Core.Tests.Progress
{
    public class ProgressModelTests
    {
        private sealed class FakeClock
        {
            public TimeSpan Now;
            public TimeSpan Get() => Now;
        }

        [Fact]
        public void Percent_IsMonotonic_AndCappedBelow100UntilCompleted()
        {
            var clock = new FakeClock();
            var model = new PhaseModel(hasVerify: false, clock: clock.Get);
            model.SetState(OperationState.Running);
            model.BeginPhase(OperationPhase.Transferring, 1000, 1000);

            double last = 0;
            for (int i = 1; i <= 10; i++)
            {
                clock.Now = TimeSpan.FromSeconds(i);
                model.Advance(100, 100);
                var p = model.Snapshot().Percent;
                Assert.True(p >= last);
                last = p;
            }

            model.SetState(OperationState.Finalizing);
            model.BeginPhase(OperationPhase.Flushing, 0, null);
            model.BeginPhase(OperationPhase.Releasing, 0, null);
            model.BeginPhase(OperationPhase.Ejecting, 0, null);
            var beforeCompletion = model.Snapshot().Percent;
            Assert.True(beforeCompletion < 100.0);
            Assert.True(beforeCompletion >= last);

            model.SetState(OperationState.Completed);
            Assert.Equal(100.0, model.Snapshot().Percent);
        }

        [Fact]
        public void Percent_NeverGoesBackwards_WhenUnitsAreSetLower()
        {
            var clock = new FakeClock();
            var model = new PhaseModel(false, clock.Get);
            model.BeginPhase(OperationPhase.Transferring, 1000, null);
            model.SetUnits(600, 600);
            var high = model.Snapshot().Percent;
            model.SetUnits(400, 400);
            Assert.True(model.Snapshot().Percent >= high);
        }

        [Fact]
        public void FailedOrCancelled_NeverShows100()
        {
            var model = new PhaseModel(false, () => TimeSpan.Zero);
            model.BeginPhase(OperationPhase.Transferring, 10, 10);
            model.Advance(10, 10);
            model.SetState(OperationState.Failed);
            Assert.True(model.Snapshot().Percent < 100.0);
            model.SetState(OperationState.Cancelled);
            Assert.True(model.Snapshot().Percent < 100.0);
        }

        [Fact]
        public void Remaining_IsUnknownDuringFirstSeconds()
        {
            var clock = new FakeClock();
            var model = new PhaseModel(false, clock.Get);
            model.SetState(OperationState.Running);
            model.BeginPhase(OperationPhase.Transferring, 1000, 1000);
            clock.Now = TimeSpan.FromSeconds(1);
            model.Advance(10, 10);
            Assert.Null(model.Snapshot().Remaining);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Remaining_IsWithin15Percent_After10SecondsAtConstantRate(bool hasVerify)
        {
            var clock = new FakeClock();
            var model = new PhaseModel(hasVerify, clock.Get);
            model.SetState(OperationState.Running);
            const long total = 100000;
            const long perTick = 100; // 1000 ticks of 0.25 s => 250 s transfer
            model.BeginPhase(OperationPhase.Transferring, total, total);

            int ticks = 0;
            for (; ticks < 40; ticks++)
            {
                clock.Now = TimeSpan.FromSeconds(ticks * 0.25);
                model.Advance(perTick, perTick);
                model.Snapshot();
            }

            for (; ticks < 1000; ticks += 50)
            {
                clock.Now = TimeSpan.FromSeconds(ticks * 0.25);
                model.SetUnits(ticks * perTick, ticks * perTick);
                var snapshot = model.Snapshot();
                Assert.NotNull(snapshot.Remaining);
                double actualTransferRemaining = (1000 - ticks) * 0.25;
                if (actualTransferRemaining < 10)
                {
                    continue;
                }

                // Without verification the transfer is the whole remaining work; with verification
                // the model reserves extra time, so only the transfer case is checked strictly.
                if (!hasVerify)
                {
                    double error = Math.Abs(snapshot.Remaining.Value.TotalSeconds - actualTransferRemaining) / actualTransferRemaining;
                    Assert.True(error <= 0.15, $"ETA error {error:P1} at tick {ticks}");
                }
                else
                {
                    Assert.True(snapshot.Remaining.Value.TotalSeconds >= actualTransferRemaining * 0.85);
                }
            }
        }

        [Fact]
        public void Speeds_AreReported()
        {
            var clock = new FakeClock();
            var model = new PhaseModel(false, clock.Get);
            model.SetState(OperationState.Running);
            model.BeginPhase(OperationPhase.Transferring, 100 << 20, 100 << 20);
            for (int i = 1; i <= 20; i++)
            {
                clock.Now = TimeSpan.FromSeconds(i);
                model.Advance(1 << 20, 1 << 20);
                model.Snapshot();
            }

            var s = model.Snapshot();
            Assert.InRange(s.CurrentSpeed, 0.9 * (1 << 20), 1.1 * (1 << 20));
            Assert.InRange(s.AverageSpeed, 0.9 * (1 << 20), 1.1 * (1 << 20));
            Assert.Equal(20L << 20, s.BytesProcessed);
            Assert.Equal(100L << 20, s.BytesTotal);
            Assert.Equal(OperationPhase.Transferring, s.Phase);
        }
    }
}
