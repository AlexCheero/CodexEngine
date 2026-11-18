using System;

namespace CodexNetworking.History
{
    /// <summary>
    /// Turns variable frame time into a whole number of fixed simulation ticks. <see cref="RateScale"/>
    /// speeds the clock up or slows it down a little, which is how a client stays the right distance
    /// ahead of a server, or peers stay within an input delay of each other. The remaining fraction
    /// of a tick is exposed as <see cref="Alpha"/> for presentation interpolation.
    /// </summary>
    public sealed class TickClock
    {
        private readonly double _tickDuration;
        private readonly int _maxTicksPerAdvance;
        private double _accumulator;
        private double _rateScale = 1.0;

        /// <param name="tickDuration">Seconds per tick, for example 1/60.</param>
        /// <param name="maxTicksPerAdvance">Cap per Advance; a longer stall drops the backlog instead of spiralling.</param>
        public TickClock(double tickDuration, int maxTicksPerAdvance = 8)
        {
            if (tickDuration <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickDuration));
            if (maxTicksPerAdvance < 1)
                throw new ArgumentOutOfRangeException(nameof(maxTicksPerAdvance));
            _tickDuration = tickDuration;
            _maxTicksPerAdvance = maxTicksPerAdvance;
        }

        public double TickDuration => _tickDuration;

        /// <summary>1 is nominal speed; clamped to [0.5, 2].</summary>
        public double RateScale
        {
            get => _rateScale;
            set => _rateScale = Math.Max(0.5, Math.Min(2.0, value));
        }

        /// <summary>Fraction of the next tick already elapsed, in [0, 1).</summary>
        public double Alpha => _accumulator / _tickDuration;

        /// <summary>Ticks dropped because a frame took longer than the cap allowed.</summary>
        public long DroppedTicks { get; private set; }

        /// <summary>Adds real time and returns how many ticks to simulate now.</summary>
        public int Advance(double realSeconds)
        {
            if (realSeconds < 0)
                realSeconds = 0;
            _accumulator += realSeconds * _rateScale;
            int ticks = 0;
            while (_accumulator >= _tickDuration && ticks < _maxTicksPerAdvance)
            {
                _accumulator -= _tickDuration;
                ticks++;
            }
            if (ticks == _maxTicksPerAdvance && _accumulator >= _tickDuration)
            {
                DroppedTicks += (long)(_accumulator / _tickDuration);
                _accumulator %= _tickDuration;
            }
            return ticks;
        }

        public void Reset()
        {
            _accumulator = 0;
            _rateScale = 1.0;
            DroppedTicks = 0;
        }
    }
}
