using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Bytes per second over a sliding one-second window made of ten 100 ms buckets. Time is the
    /// caller's; buckets that fall out of the window are cleared as time advances. No allocations.
    /// </summary>
    public sealed class BandwidthMeter
    {
        private const int BucketCount = 10;
        private const double BucketSeconds = 0.1;

        private readonly long[] _buckets = new long[BucketCount];
        private long _current;
        private bool _any;

        public void Reset()
        {
            Array.Clear(_buckets, 0, _buckets.Length);
            _any = false;
        }

        public void Add(long bytes, double now)
        {
            Advance(now);
            _buckets[(int)(_current % BucketCount)] += bytes;
        }

        /// <summary>
        /// Bytes per second over the window ending at <paramref name="now"/>: nine full buckets plus the
        /// elapsed part of the current one, so the estimate does not sag right after a bucket boundary.
        /// Accuracy is about ±10% because the window edge is bucket aligned.
        /// </summary>
        public double Rate(double now)
        {
            Advance(now);
            long sum = 0;
            for (int i = 0; i < BucketCount; i++)
                sum += _buckets[i];
            if (sum == 0)
                return 0;
            double elapsedInCurrent = now - _current * BucketSeconds;
            if (elapsedInCurrent < 0)
                elapsedInCurrent = 0;
            else if (elapsedInCurrent > BucketSeconds)
                elapsedInCurrent = BucketSeconds;
            return sum / ((BucketCount - 1) * BucketSeconds + elapsedInCurrent);
        }

        private void Advance(double now)
        {
            long index = (long)Math.Floor(now / BucketSeconds);
            if (index < 0)
                index = 0;
            if (!_any)
            {
                _any = true;
                _current = index;
                return;
            }
            if (index <= _current)
                return;
            long gap = index - _current;
            if (gap >= BucketCount)
            {
                Array.Clear(_buckets, 0, _buckets.Length);
            }
            else
            {
                for (long k = 1; k <= gap; k++)
                    _buckets[(int)((_current + k) % BucketCount)] = 0;
            }
            _current = index;
        }
    }
}
