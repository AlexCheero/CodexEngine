using System;
using System.Security.Cryptography;

namespace CodexNetworking.Transport
{
    /// <summary>Source of 64-bit random values for salts and the simulator. Injected so tests are deterministic.</summary>
    public interface IRandomSource
    {
        ulong NextUInt64();
    }

    /// <summary>Deterministic xorshift64* generator for tests and the network simulator.</summary>
    public sealed class XorShiftRandom : IRandomSource
    {
        private ulong _state;

        public XorShiftRandom(ulong seed)
        {
            _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        }

        public ulong NextUInt64()
        {
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>Uniform value in [0, 1).</summary>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>Cryptographic random for connection salts in real deployments.</summary>
    public sealed class CryptoRandom : IRandomSource, IDisposable
    {
        private readonly RandomNumberGenerator _generator = RandomNumberGenerator.Create();
        private readonly byte[] _bytes = new byte[8];

        public ulong NextUInt64()
        {
            _generator.GetBytes(_bytes);
            return BitConverter.ToUInt64(_bytes, 0);
        }

        public void Dispose() => _generator.Dispose();
    }
}
