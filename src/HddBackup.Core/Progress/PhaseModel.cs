using System;
using System.Diagnostics;
using System.Threading;

namespace HddBackup.Core.Progress
{
    /// <summary>
    /// Converts phase-level progress into an overall percentage, speeds and an ETA.
    /// Worker threads only touch counters through <see cref="Interlocked"/>; all smoothing
    /// state lives on the reader side (<see cref="Snapshot"/>), so I/O never waits for the UI.
    /// </summary>
    public sealed class PhaseModel
    {
        private const double PrepareShare = 0.005;
        private const double FinalizeShare = 0.01;
        private const double MaxPercentBeforeCompletion = 99.9;
        private const double EtaWarmupSeconds = 3.0;
        private const long UnknownTotal = -1;

        private readonly Func<TimeSpan> _clock;
        private readonly double _transferShare;
        private readonly double _verifyShare;

        // Written by the worker, read by Snapshot.
        private long _units;
        private long _unitsTotal;
        private long _bytes;
        private long _bytesTotal = UnknownTotal;
        private long _phaseStartTicks;
        private long _baseFractionBits;
        private int _phase;
        private int _state;
        private int _phaseVersion;

        // Reader-side state.
        private readonly object _snapshotLock = new object();
        private readonly RateEstimator _speed = new RateEstimator(3.0);
        private readonly RateEstimator _fractionRate = new RateEstimator(5.0);
        private int _seenPhaseVersion = -1;
        private bool _fractionRateStarted;
        private double _lastPercent;

        private readonly System.Collections.Generic.IDictionary<OperationPhase, double> _customShares;

        /// <summary>Explicit phase shares (summing to about 1); phases not listed get 0.</summary>
        public PhaseModel(System.Collections.Generic.IDictionary<OperationPhase, double> shares, Func<TimeSpan> clock = null)
            : this(false, clock)
        {
            _customShares = shares;
        }

        public PhaseModel(bool hasVerify, Func<TimeSpan> clock = null)
        {
            if (clock == null)
            {
                var stopwatch = Stopwatch.StartNew();
                clock = () => stopwatch.Elapsed;
            }

            _clock = clock;
            double dataShare = 1.0 - PrepareShare - FinalizeShare;

            // Reading back is roughly twice as fast as writing, so verification gets a third.
            _verifyShare = hasVerify ? dataShare / 3.0 : 0.0;
            _transferShare = dataShare - _verifyShare;
            _phase = (int)OperationPhase.Preparing;
            _state = (int)OperationState.Created;
        }

        public void SetState(OperationState state)
        {
            Volatile.Write(ref _state, (int)state);
        }

        /// <summary>
        /// Starts a phase. The previous phase counts as fully done. <paramref name="unitsTotal"/>
        /// drives the percentage; <paramref name="bytesTotal"/> is only displayed.
        /// </summary>
        public void BeginPhase(OperationPhase phase, long unitsTotal, long? bytesTotal)
        {
            var previous = (OperationPhase)Volatile.Read(ref _phase);
            double baseFraction = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _baseFractionBits));
            if (previous != phase)
            {
                baseFraction += ShareOf(previous);
            }

            Interlocked.Exchange(ref _baseFractionBits, BitConverter.DoubleToInt64Bits(Math.Min(1.0, baseFraction)));
            Interlocked.Exchange(ref _units, 0);
            Interlocked.Exchange(ref _bytes, 0);
            Interlocked.Exchange(ref _unitsTotal, Math.Max(0, unitsTotal));
            Interlocked.Exchange(ref _bytesTotal, bytesTotal ?? UnknownTotal);
            Interlocked.Exchange(ref _phaseStartTicks, _clock().Ticks);
            Volatile.Write(ref _phase, (int)phase);
            Interlocked.Increment(ref _phaseVersion);
        }

        public void Advance(long unitsDelta, long bytesDelta)
        {
            Interlocked.Add(ref _units, unitsDelta);
            Interlocked.Add(ref _bytes, bytesDelta);
        }

        public void SetUnits(long units, long bytes)
        {
            Interlocked.Exchange(ref _units, units);
            Interlocked.Exchange(ref _bytes, bytes);
        }

        public OperationProgress Snapshot()
        {
            lock (_snapshotLock)
            {
                var now = _clock();
                double seconds = now.TotalSeconds;
                var state = (OperationState)Volatile.Read(ref _state);
                var phase = (OperationPhase)Volatile.Read(ref _phase);
                int phaseVersion = Volatile.Read(ref _phaseVersion);
                long units = Interlocked.Read(ref _units);
                long unitsTotal = Interlocked.Read(ref _unitsTotal);
                long bytes = Interlocked.Read(ref _bytes);
                long bytesTotal = Interlocked.Read(ref _bytesTotal);
                double phaseStart = TimeSpan.FromTicks(Interlocked.Read(ref _phaseStartTicks)).TotalSeconds;
                double baseFraction = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _baseFractionBits));

                double withinPhase = unitsTotal > 0 ? Math.Min(1.0, (double)units / unitsTotal) : 0.0;
                double fraction = Math.Min(1.0, baseFraction + (ShareOf(phase) * withinPhase));

                double percent;
                if (state == OperationState.Completed)
                {
                    percent = 100.0;
                }
                else
                {
                    percent = Math.Max(_lastPercent, Math.Min(MaxPercentBeforeCompletion, fraction * 100.0));
                }

                _lastPercent = percent;

                if (phaseVersion != _seenPhaseVersion)
                {
                    _seenPhaseVersion = phaseVersion;
                    _speed.Reset(phaseStart, 0);
                }

                double currentSpeed = _speed.Update(seconds, bytes);
                double phaseElapsed = seconds - phaseStart;
                double averageSpeed = phaseElapsed > 0 ? bytes / phaseElapsed : 0;

                if (!_fractionRateStarted)
                {
                    _fractionRate.Reset(seconds, fraction);
                    _fractionRateStarted = true;
                }
                else
                {
                    _fractionRate.Update(seconds, fraction);
                }

                TimeSpan? remaining = null;
                if (state == OperationState.Completed)
                {
                    remaining = TimeSpan.Zero;
                }
                else if (state != OperationState.Failed && state != OperationState.Cancelled
                    && seconds >= EtaWarmupSeconds && _fractionRate.HasRate && _fractionRate.Rate > 0)
                {
                    // The short finalization reserve is excluded so the estimate tracks the data phases.
                    double left = Math.Max(0.0, (1.0 - FinalizeShare) - fraction);
                    double eta = left / _fractionRate.Rate;
                    if (eta < TimeSpan.MaxValue.TotalSeconds / 2)
                    {
                        remaining = TimeSpan.FromSeconds(eta);
                    }
                }

                return new OperationProgress(
                    state,
                    phase,
                    percent,
                    bytes,
                    bytesTotal == UnknownTotal ? (long?)null : bytesTotal,
                    currentSpeed,
                    averageSpeed,
                    now,
                    remaining);
            }
        }

        private double ShareOf(OperationPhase phase)
        {
            if (_customShares != null)
            {
                return _customShares.TryGetValue(phase, out double share) ? share : 0;
            }

            switch (phase)
            {
                case OperationPhase.Preparing:
                    return PrepareShare;
                case OperationPhase.Transferring:
                    return _transferShare;
                case OperationPhase.Verifying:
                    return _verifyShare;
                case OperationPhase.Flushing:
                    return FinalizeShare * 0.4;
                case OperationPhase.Releasing:
                case OperationPhase.Ejecting:
                    return FinalizeShare * 0.3;
                default:
                    return 0;
            }
        }
    }
}
