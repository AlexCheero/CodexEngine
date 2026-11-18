using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Crypto
{
    /// <summary>
    /// ChaCha20 stream cipher (RFC 8439): 256-bit key, 96-bit nonce, 32-bit block counter. Pure C#,
    /// no allocations, no platform crypto, so every engine and platform produces the same bytes.
    /// Never reuse a (key, nonce) pair for two different messages.
    /// </summary>
    public static class ChaCha20
    {
        public const int KeySize = 32;
        public const int NonceSize = 12;
        public const int BlockSize = 64;

        /// <summary>Writes one 64-byte keystream block for the counter.</summary>
        public static void Block(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
        {
            if (key.Length != KeySize || nonce.Length != NonceSize || output.Length < BlockSize)
                throw new ArgumentException("ChaCha20 block needs a 32-byte key, a 12-byte nonce and 64 bytes of output");
            Span<uint> state = stackalloc uint[16];
            Span<uint> working = stackalloc uint[16];
            Init(state, key, nonce, counter);
            Rounds(state, working);
            for (int i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4), working[i]);
        }

        /// <summary>XORs the keystream starting at the counter into the data, in place (encrypts or decrypts).</summary>
        public static void Xor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> data)
        {
            if (key.Length != KeySize || nonce.Length != NonceSize)
                throw new ArgumentException("ChaCha20 needs a 32-byte key and a 12-byte nonce");
            Span<uint> state = stackalloc uint[16];
            Span<uint> working = stackalloc uint[16];
            Init(state, key, nonce, counter);
            int offset = 0;
            while (offset < data.Length)
            {
                Rounds(state, working);
                state[12]++;
                int length = Math.Min(BlockSize, data.Length - offset);
                int whole = length & ~3;
                for (int i = 0; i < whole; i += 4)
                {
                    uint word = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + i)) ^ working[i >> 2];
                    BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset + i), word);
                }
                for (int i = whole; i < length; i++)
                    data[offset + i] ^= (byte)(working[i >> 2] >> ((i & 3) * 8));
                offset += length;
            }
        }

        private static void Init(Span<uint> state, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter)
        {
            state[0] = 0x61707865;
            state[1] = 0x3320646e;
            state[2] = 0x79622d32;
            state[3] = 0x6b206574;
            for (int i = 0; i < 8; i++)
                state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4));
            state[12] = counter;
            state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce);
            state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4));
            state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8));
        }

        // 20 rounds over a copy of the state, then the feed-forward addition.
        private static void Rounds(ReadOnlySpan<uint> state, Span<uint> x)
        {
            state.CopyTo(x);
            for (int i = 0; i < 10; i++)
            {
                QuarterRound(x, 0, 4, 8, 12);
                QuarterRound(x, 1, 5, 9, 13);
                QuarterRound(x, 2, 6, 10, 14);
                QuarterRound(x, 3, 7, 11, 15);
                QuarterRound(x, 0, 5, 10, 15);
                QuarterRound(x, 1, 6, 11, 12);
                QuarterRound(x, 2, 7, 8, 13);
                QuarterRound(x, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++)
                x[i] += state[i];
        }

        private static void QuarterRound(Span<uint> x, int a, int b, int c, int d)
        {
            x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 16);
            x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 12);
            x[a] += x[b]; x[d] = RotateLeft(x[d] ^ x[a], 8);
            x[c] += x[d]; x[b] = RotateLeft(x[b] ^ x[c], 7);
        }

        private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
    }
}
