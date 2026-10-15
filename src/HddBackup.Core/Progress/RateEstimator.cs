using System;

namespace HddBackup.Core.Progress
{
    /// <summary>
    /// Exponentially weighted rate of a monotonically growing value, with a time constant in
    /// seconds. Samples closer than 50 ms apart are ignored to avoid noise.
    /// </summary>
    public sealed class RateEstimator
    {
        private const double MinInterval = 0.05;
        private readonly double _tau;
        private double _lastTime;
        private double _lastValue;
        private double _rate;

        public RateEstimator(double tauSeconds)
        {
            _tau = tauSeconds;
        }

        public bool HasRate { get; private set; }

        public double Rate => _rate;

        public void Reset(double time, double value)
        {
            _lastTime = time;
            _lastValue = value;
            _rate = 0;
            HasRate = false;
        }

        public double Update(double time, double value)
        {
            double dt = time - _lastTime;
            if (dt < MinInterval)
            {
                return _rate;
            }

            double instant = Math.Max(0, (value - _lastValue) / dt);
            if (!HasRate)
            {
                _rate = instant;
                HasRate = true;
            }
            else
            {
                double alpha = 1 - Math.Exp(-dt / _tau);
                _rate += alpha * (instant - _rate);
            }

            _lastTime = time;
            _lastValue = value;
            return _rate;
        }
    }
}
