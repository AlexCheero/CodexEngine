using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace CodexNetworking.History
{
    /// <summary>
    /// Streaming 64-bit hash over integers and byte spans, eight bytes at a time. Pure integer math,
    /// so identical on every runtime; the exact function is part of the protocol (peers compare it).
    /// Not cryptographic.
    /// </summary>
    public struct StateHasher
    {
        private const ulong Prime1 = 11400714785074694791UL;
        private const ulong Prime2 = 14029467366897019727UL;
        private const ulong Prime3 = 1609587929392839161UL;
        private const ulong Prime4 = 9650029242287828579UL;
        private const ulong Prime5 = 2870177450012600261UL;

        private ulong _hash;
        private ulong _length;

        public static StateHasher Create() => new StateHasher { _hash = Prime5, _length = 0 };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ulong value)
        {
            ulong k = value * Prime2;
            k = RotateLeft(k, 31);
            k *= Prime1;
            _hash ^= k;
            _hash = RotateLeft(_hash, 27) * Prime1 + Prime4;
            _length += 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(long value) => Add((ulong)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(int value) => Add((ulong)(uint)value);

        public void Add(ReadOnlySpan<byte> bytes)
        {
            int i = 0;
            for (; i + 8 <= bytes.Length; i += 8)
                Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(i)));
            int tailLength = bytes.Length - i;
            if (tailLength > 0)
            {
                ulong tail = 0;
                for (int shift = 0; i < bytes.Length; i++, shift += 8)
                    tail |= (ulong)bytes[i] << shift;
                // Tag the tail with its length so trailing zero bytes change the hash.
                Add(tail ^ (0x9E3779B97F4A7C15UL * (ulong)tailLength));
            }
        }

        public ulong Hash
        {
            get
            {
                ulong h = _hash ^ _length;
                h ^= h >> 33;
                h *= Prime2;
                h ^= h >> 29;
                h *= Prime3;
                h ^= h >> 32;
                return h;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
    }
}
