using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Two-state congestion hint (Gaffer On Games scheme). The connection is <see cref="IsCongested"/>
    /// as soon as the smoothed RTT exceeds the threshold, and recovers once the RTT has stayed below
    /// it for the current recovery time. A quick relapse after recovering doubles the recovery time
    /// (up to the maximum); a long calm period halves it (down to the minimum). Upper layers use the
    /// flag to send less, for example half the snapshot rate; the transport itself does not throttle.
    /// </summary>
    public sealed class CongestionControl
    {
        private readonly double _rttThreshold;
        private readonly double _minRecovery;
        private readonly double _maxRecovery;
        private readonly double _goodPeriod;
        private readonly double _initialRecovery;
        private double _recovery;
        private double _goodModeSince;
        private double _lastHalve;
        private double _belowThresholdSince;
        private bool _belowThreshold;

        public CongestionControl(double rttThreshold, double initialRecovery, double minRecovery, double maxRecovery, double goodPeriod)
        {
            _rttThreshold = rttThreshold;
            _initialRecovery = initialRecovery;
            _minRecovery = minRecovery;
            _maxRecovery = maxRecovery;
            _goodPeriod = goodPeriod;
            _recovery = initialRecovery;
        }

        public bool IsCongested { get; private set; }

        /// <summary>Seconds the RTT must stay below the threshold before leaving the congested state.</summary>
        public double RecoveryTime => _recovery;

        public void Reset(double now)
        {
            IsCongested = false;
            _recovery = _initialRecovery;
            _goodModeSince = now;
            _lastHalve = now;
            _belowThreshold = false;
        }

        /// <summary>Call once per update with the connection's smoothed RTT (0 before the first ack is fine).</summary>
        public void Update(double now, double smoothedRtt)
        {
            bool bad = smoothedRtt > _rttThreshold;
            if (!IsCongested)
            {
                if (bad)
                {
                    IsCongested = true;
                    _belowThreshold = false;
                    // Relapsing soon after recovering means the recovery time was too short.
                    if (now - _goodModeSince < _goodPeriod)
                        _recovery = Math.Min(_recovery * 2, _maxRecovery);
                }
                else if (now - _lastHalve >= _goodPeriod)
                {
                    _recovery = Math.Max(_recovery * 0.5, _minRecovery);
                    _lastHalve = now;
                }
                return;
            }

            if (bad)
            {
                _belowThreshold = false;
                return;
            }
            if (!_belowThreshold)
            {
                _belowThreshold = true;
                _belowThresholdSince = now;
            }
            if (now - _belowThresholdSince >= _recovery)
            {
                IsCongested = false;
                _goodModeSince = now;
                _lastHalve = now;
            }
        }
    }
}
