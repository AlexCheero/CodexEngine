namespace CodexNetworking.FixedPoint
{
    /// <summary>
    /// Deterministic xorshift64* generator as a plain unmanaged struct, so it can live inside
    /// simulation state (a component or a singleton) and be snapshotted and rolled back with it.
    /// Never seed or reseed it from wall-clock time inside the simulation.
    /// </summary>
    public struct FixRandom
    {
        public ulong State;

        public FixRandom(ulong seed)
        {
            State = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextUInt64()
        {
            ulong x = State;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            State = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        public uint NextUInt32() => (uint)(NextUInt64() >> 32);

        /// <summary>Uniform integer in [0, maxExclusive); maxExclusive must be positive.</summary>
        public int NextInt(int maxExclusive) => (int)((NextUInt64() >> 32) * (ulong)maxExclusive >> 32);

        /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
        public int NextInt(int minInclusive, int maxExclusive) => minInclusive + NextInt(maxExclusive - minInclusive);

        /// <summary>Uniform value in [0, 1) at full 16-bit resolution.</summary>
        public Fix64 NextFix() => Fix64.FromRaw((long)(NextUInt64() >> (64 - Fix64.FractionBits)));

        /// <summary>Uniform value in [min, max).</summary>
        public Fix64 Range(Fix64 min, Fix64 max) => min + (max - min) * NextFix();

        public bool NextBool() => (NextUInt64() >> 63) != 0;

        /// <summary>Uniformly distributed point on the unit circle.</summary>
        public FixVector2 NextDirection2() => FixVector2.FromAngle(Range(-Fix64.Pi, Fix64.Pi));
    }
}
