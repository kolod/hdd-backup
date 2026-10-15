using System;

namespace HddBackup.Core.Progress
{
    public enum OperationState
    {
        Created,
        Preparing,
        Running,
        Finalizing,
        Completed,
        Cancelled,
        Failed
    }

    public enum OperationPhase
    {
        Preparing,
        Transferring,
        Flushing,
        Verifying,
        Releasing,
        Ejecting
    }

    /// <summary>Immutable progress snapshot for the UI.</summary>
    public sealed class OperationProgress
    {
        public OperationProgress(
            OperationState state,
            OperationPhase phase,
            double percent,
            long bytesProcessed,
            long? bytesTotal,
            double currentSpeed,
            double averageSpeed,
            TimeSpan elapsed,
            TimeSpan? remaining)
        {
            State = state;
            Phase = phase;
            Percent = percent;
            BytesProcessed = bytesProcessed;
            BytesTotal = bytesTotal;
            CurrentSpeed = currentSpeed;
            AverageSpeed = averageSpeed;
            Elapsed = elapsed;
            Remaining = remaining;
        }

        public static OperationProgress Idle { get; } = new OperationProgress(
            OperationState.Created, OperationPhase.Preparing, 0, 0, null, 0, 0, TimeSpan.Zero, null);

        public OperationState State { get; }

        public OperationPhase Phase { get; }

        /// <summary>0..100; monotonic; exactly 100 only when <see cref="State"/> is Completed.</summary>
        public double Percent { get; }

        /// <summary>Bytes processed in the current data phase.</summary>
        public long BytesProcessed { get; }

        /// <summary>Total bytes of the current data phase; null when unknown.</summary>
        public long? BytesTotal { get; }

        /// <summary>Bytes per second, smoothed over about three seconds.</summary>
        public double CurrentSpeed { get; }

        /// <summary>Bytes per second over the current data phase.</summary>
        public double AverageSpeed { get; }

        public TimeSpan Elapsed { get; }

        /// <summary>Estimated time remaining; null while no reliable estimate exists.</summary>
        public TimeSpan? Remaining { get; }

        public bool IsTerminal =>
            State == OperationState.Completed || State == OperationState.Cancelled || State == OperationState.Failed;
    }
}
