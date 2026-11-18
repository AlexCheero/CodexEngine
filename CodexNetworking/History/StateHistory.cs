using System;
using CodexECS;

namespace CodexNetworking.History
{
    /// <summary>
    /// Ring of world snapshots keyed by tick. Saving tick T overwrites the slot of tick T - Capacity,
    /// so the newest Capacity ticks are restorable. Restoring to a tick invalidates every newer entry,
    /// since the resimulation that follows will rewrite them. All snapshot buffers are allocated once.
    /// </summary>
    public sealed class StateHistory
    {
        private readonly SimulationRegistry _registry;
        private readonly WorldState[] _states;
        private readonly int[] _ticks;
        private readonly bool[] _used;
        private readonly int _mask;
        private int _newestTick;
        private bool _any;

        public StateHistory(int capacity, SimulationRegistry registry)
        {
            if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two", nameof(capacity));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (!registry.IsSealed)
                throw new ArgumentException("registry must be sealed", nameof(registry));
            _states = new WorldState[capacity];
            _ticks = new int[capacity];
            _used = new bool[capacity];
            _mask = capacity - 1;
            for (int i = 0; i < capacity; i++)
                _states[i] = new WorldState();
        }

        public int Capacity => _states.Length;

        public SimulationRegistry Registry => _registry;

        public bool IsEmpty => !_any;

        /// <summary>Newest saved tick; meaningless while empty.</summary>
        public int NewestTick => _newestTick;

        /// <summary>Oldest tick that is still restorable; meaningless while empty.</summary>
        public int OldestTick
        {
            get
            {
                int oldest = _newestTick;
                for (int t = _newestTick - 1; t > _newestTick - _states.Length; t--)
                {
                    int slot = t & _mask;
                    if (!_used[slot] || _ticks[slot] != t)
                        break;
                    oldest = t;
                }
                return oldest;
            }
        }

        /// <summary>Captures the world as the state of <paramref name="tick"/>.</summary>
        public void Save(EcsWorld world, int tick)
        {
            int slot = tick & _mask;
            world.CaptureState(_states[slot], _registry.ComponentIds);
            _ticks[slot] = tick;
            _used[slot] = true;
            if (!_any || tick > _newestTick)
                _newestTick = tick;
            _any = true;
        }

        public bool Contains(int tick)
        {
            int slot = tick & _mask;
            return _used[slot] && _ticks[slot] == tick;
        }

        public bool TryGet(int tick, out WorldState state)
        {
            int slot = tick & _mask;
            if (_used[slot] && _ticks[slot] == tick)
            {
                state = _states[slot];
                return true;
            }
            state = null;
            return false;
        }

        /// <summary>Restores the world to <paramref name="tick"/> and forgets every newer snapshot.</summary>
        public bool TryRestore(EcsWorld world, int tick)
        {
            if (!TryGet(tick, out WorldState state))
                return false;
            world.RestoreState(state);
            for (int t = tick + 1; t <= _newestTick; t++)
            {
                int slot = t & _mask;
                if (_ticks[slot] == t)
                    _used[slot] = false;
            }
            _newestTick = tick;
            return true;
        }

        public void Clear()
        {
            Array.Clear(_used, 0, _used.Length);
            _any = false;
            _newestTick = 0;
        }
    }
}
