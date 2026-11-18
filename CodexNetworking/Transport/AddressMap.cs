using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Fixed-capacity hash map from <see cref="Address"/> to a non-negative int (a connection slot).
    /// Open addressing with linear probing and backward-shift deletion, so there are no tombstones
    /// and lookups stay short however many connects and disconnects happen. Allocates only in the
    /// constructor. Capacity is rounded up to a power of two at least twice the expected entry count.
    /// </summary>
    public sealed class AddressMap
    {
        private readonly Address[] _keys;
        private readonly int[] _values;
        private readonly bool[] _used;
        private readonly int _mask;
        private int _count;

        public AddressMap(int expectedEntries)
        {
            if (expectedEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(expectedEntries));
            int capacity = 16;
            while (capacity < expectedEntries * 2)
                capacity <<= 1;
            _keys = new Address[capacity];
            _values = new int[capacity];
            _used = new bool[capacity];
            _mask = capacity - 1;
        }

        public int Count => _count;

        public int Capacity => _keys.Length;

        public void Clear()
        {
            Array.Clear(_used, 0, _used.Length);
            _count = 0;
        }

        public bool TryGet(in Address key, out int value)
        {
            int index = Find(key);
            if (index < 0)
            {
                value = -1;
                return false;
            }
            value = _values[index];
            return true;
        }

        public bool Contains(in Address key) => Find(key) >= 0;

        /// <summary>Adds or overwrites. Throws when the map is full, which the sizing rule prevents.</summary>
        public void Set(in Address key, int value)
        {
            // A full table has no empty slot to stop the probe, so check before probing.
            if (_count == _keys.Length && Find(key) < 0)
                throw new InvalidOperationException("AddressMap is full");
            int index = Hash(key);
            while (true)
            {
                if (!_used[index])
                {
                    _used[index] = true;
                    _keys[index] = key;
                    _values[index] = value;
                    _count++;
                    return;
                }
                if (_keys[index] == key)
                {
                    _values[index] = value;
                    return;
                }
                index = (index + 1) & _mask;
            }
        }

        public bool Remove(in Address key)
        {
            int hole = Find(key);
            if (hole < 0)
                return false;
            _used[hole] = false;
            _count--;

            // Backward shift: pull later entries of the same probe run into the hole when their
            // ideal position lies at or before it, so no lookup breaks on an empty slot.
            int next = (hole + 1) & _mask;
            while (_used[next])
            {
                int ideal = Hash(_keys[next]);
                // Is the hole between ideal and next (cyclically)? Then the entry may move into it.
                bool movable = next >= ideal ? (hole >= ideal && hole < next) : (hole >= ideal || hole < next);
                if (movable)
                {
                    _keys[hole] = _keys[next];
                    _values[hole] = _values[next];
                    _used[hole] = true;
                    _used[next] = false;
                    hole = next;
                }
                next = (next + 1) & _mask;
            }
            return true;
        }

        private int Find(in Address key)
        {
            int index = Hash(key);
            // Bounded probe: a completely full table has no empty slot to end the run.
            for (int probes = 0; probes < _keys.Length && _used[index]; probes++)
            {
                if (_keys[index] == key)
                    return index;
                index = (index + 1) & _mask;
            }
            return -1;
        }

        private int Hash(in Address key)
        {
            // Spread the port and address bits before masking; ports are often sequential.
            ulong h = key.Lo ^ (key.Hi * 0x9E3779B97F4A7C15UL) ^ ((ulong)key.Port << 48) ^ (ulong)key.Family;
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 29;
            return (int)(h & (ulong)_mask);
        }
    }
}
