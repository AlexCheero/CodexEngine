using System;

namespace CodexNetworking.History
{
    /// <summary>
    /// Ring of per-tick inputs for one player. An entry is confirmed when it is the player's real
    /// input, predicted when the simulation guessed it (usually by repeating the last confirmed one).
    /// A prediction never overwrites a confirmation. Capacity is a power of two and bounds how far
    /// back inputs can be corrected, so it should be at least the rollback window.
    /// </summary>
    public sealed class InputBuffer<TInput> where TInput : unmanaged
    {
        private struct Entry
        {
            public TInput Input;
            public int Tick;
            public bool Used;
            public bool Confirmed;
        }

        private readonly Entry[] _entries;
        private readonly int _mask;
        private int _latestConfirmedTick;
        private bool _anyConfirmed;

        public InputBuffer(int capacity)
        {
            if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two", nameof(capacity));
            _entries = new Entry[capacity];
            _mask = capacity - 1;
        }

        public int Capacity => _entries.Length;

        public bool HasConfirmed => _anyConfirmed;

        /// <summary>Newest confirmed tick; meaningless while <see cref="HasConfirmed"/> is false.</summary>
        public int LatestConfirmedTick => _latestConfirmedTick;

        /// <summary>
        /// Stores an input. Returns false when a confirmed entry for the tick already exists and this
        /// call was a prediction, or when the tick is older than the window.
        /// </summary>
        public bool Set(int tick, in TInput input, bool confirmed)
        {
            if (_anyConfirmed && tick <= _latestConfirmedTick - _entries.Length)
                return false;
            ref Entry entry = ref _entries[tick & _mask];
            if (entry.Used && entry.Tick == tick && entry.Confirmed && !confirmed)
                return false;
            entry.Input = input;
            entry.Tick = tick;
            entry.Used = true;
            entry.Confirmed = confirmed;
            if (confirmed && (!_anyConfirmed || tick > _latestConfirmedTick))
            {
                _latestConfirmedTick = tick;
                _anyConfirmed = true;
            }
            return true;
        }

        public bool TryGet(int tick, out TInput input, out bool confirmed)
        {
            ref Entry entry = ref _entries[tick & _mask];
            if (entry.Used && entry.Tick == tick)
            {
                input = entry.Input;
                confirmed = entry.Confirmed;
                return true;
            }
            input = default;
            confirmed = false;
            return false;
        }

        public bool IsConfirmed(int tick)
        {
            ref Entry entry = ref _entries[tick & _mask];
            return entry.Used && entry.Tick == tick && entry.Confirmed;
        }

        /// <summary>
        /// The input to use for <paramref name="tick"/>: the confirmed one if present, otherwise the
        /// newest confirmed input before it (the usual prediction), otherwise default. Returns whether
        /// the result is confirmed for that tick.
        /// </summary>
        public bool Resolve(int tick, out TInput input)
        {
            if (TryGet(tick, out input, out bool confirmed) && confirmed)
                return true;
            for (int t = tick - 1; t > tick - _entries.Length; t--)
            {
                ref Entry entry = ref _entries[t & _mask];
                if (entry.Used && entry.Tick == t && entry.Confirmed)
                {
                    input = entry.Input;
                    return false;
                }
            }
            input = default;
            return false;
        }

        public void Clear()
        {
            Array.Clear(_entries, 0, _entries.Length);
            _anyConfirmed = false;
            _latestConfirmedTick = 0;
        }
    }
}
