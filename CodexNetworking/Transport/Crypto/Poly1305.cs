using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Crypto
{
    /// <summary>
    /// Poly1305 one-time authenticator (RFC 8439) over 26-bit limbs with 64-bit products. Feed the
    /// message in any pieces with <see cref="Update"/>, then <see cref="Finish"/>. A key must never
    /// authenticate two different messages; the AEAD derives one per packet.
    /// </summary>
    public ref struct Poly1305
    {
        public const int KeySize = 32;
        public const int TagSize = 16;
        private const uint Mask = 0x3ffffff;

        private uint _r0, _r1, _r2, _r3, _r4;
        private uint _s1, _s2, _s3, _s4;
        private uint _h0, _h1, _h2, _h3, _h4;
        private uint _pad0, _pad1, _pad2, _pad3;
        private readonly Span<byte> _buffer; // 16 bytes supplied by the caller: the partial block
        private int _buffered;

        /// <param name="buffer">16 bytes of scratch that live as long as this state (stackalloc them).</param>
        public Poly1305(ReadOnlySpan<byte> key, Span<byte> buffer)
        {
            if (key.Length != KeySize || buffer.Length < 16)
                throw new ArgumentException("Poly1305 needs a 32-byte key and 16 bytes of scratch");
            _buffer = buffer.Slice(0, 16);
            _buffered = 0;
            // r is clamped; the limbs hold it split into 26-bit pieces.
            _r0 = BinaryPrimitives.ReadUInt32LittleEndian(key) & 0x3ffffff;
            _r1 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(3)) >> 2) & 0x3ffff03;
            _r2 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(6)) >> 4) & 0x3ffc0ff;
            _r3 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(9)) >> 6) & 0x3f03fff;
            _r4 = (BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(12)) >> 8) & 0x00fffff;
            _s1 = _r1 * 5;
            _s2 = _r2 * 5;
            _s3 = _r3 * 5;
            _s4 = _r4 * 5;
            _h0 = _h1 = _h2 = _h3 = _h4 = 0;
            _pad0 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(16));
            _pad1 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(20));
            _pad2 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(24));
            _pad3 = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(28));
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            int offset = 0;
            if (_buffered > 0)
            {
                int take = Math.Min(16 - _buffered, data.Length);
                data.Slice(0, take).CopyTo(_buffer.Slice(_buffered));
                _buffered += take;
                offset = take;
                if (_buffered < 16)
                    return;
                Block(_buffer, 1u << 24);
                _buffered = 0;
            }
            while (data.Length - offset >= 16)
            {
                Block(data.Slice(offset, 16), 1u << 24);
                offset += 16;
            }
            if (offset < data.Length)
            {
                data.Slice(offset).CopyTo(_buffer);
                _buffered = data.Length - offset;
            }
        }

        /// <summary>Pads to a 16-byte boundary with zeros, as the AEAD construction requires between its parts.</summary>
        public void PadToBlock()
        {
            if (_buffered == 0)
                return;
            _buffer.Slice(_buffered).Clear();
            Block(_buffer, 1u << 24);
            _buffered = 0;
        }

        public void Finish(Span<byte> tag)
        {
            if (tag.Length < TagSize)
                throw new ArgumentException("tag needs 16 bytes");
            if (_buffered > 0)
            {
                _buffer[_buffered] = 1;
                _buffer.Slice(_buffered + 1).Clear();
                Block(_buffer, 0);
                _buffered = 0;
            }

            uint h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3, h4 = _h4;
            uint c = h1 >> 26; h1 &= Mask; h2 += c;
            c = h2 >> 26; h2 &= Mask; h3 += c;
            c = h3 >> 26; h3 &= Mask; h4 += c;
            c = h4 >> 26; h4 &= Mask; h0 += c * 5;
            c = h0 >> 26; h0 &= Mask; h1 += c;

            // g = h + 5 - 2^130; if it did not go negative, h >= p and g is the reduced value.
            uint g0 = h0 + 5; c = g0 >> 26; g0 &= Mask;
            uint g1 = h1 + c; c = g1 >> 26; g1 &= Mask;
            uint g2 = h2 + c; c = g2 >> 26; g2 &= Mask;
            uint g3 = h3 + c; c = g3 >> 26; g3 &= Mask;
            uint g4 = h4 + c - (1u << 26);
            uint select = (g4 >> 31) - 1; // all ones when g4 is non-negative
            g0 &= select; g1 &= select; g2 &= select; g3 &= select; g4 &= select;
            uint keep = ~select;
            h0 = (h0 & keep) | g0;
            h1 = (h1 & keep) | g1;
            h2 = (h2 & keep) | g2;
            h3 = (h3 & keep) | g3;
            h4 = (h4 & keep) | g4;

            // h mod 2^128 as four 32-bit words
            uint w0 = h0 | (h1 << 26);
            uint w1 = (h1 >> 6) | (h2 << 20);
            uint w2 = (h2 >> 12) | (h3 << 14);
            uint w3 = (h3 >> 18) | (h4 << 8);

            ulong f = (ulong)w0 + _pad0; w0 = (uint)f;
            f = (ulong)w1 + _pad1 + (f >> 32); w1 = (uint)f;
            f = (ulong)w2 + _pad2 + (f >> 32); w2 = (uint)f;
            f = (ulong)w3 + _pad3 + (f >> 32); w3 = (uint)f;
            BinaryPrimitives.WriteUInt32LittleEndian(tag, w0);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(4), w1);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(8), w2);
            BinaryPrimitives.WriteUInt32LittleEndian(tag.Slice(12), w3);
        }

        // h = (h + block) * r mod p, with the block's 2^128 bit supplied as hibit (0 for the padded final block).
        private void Block(ReadOnlySpan<byte> m, uint hibit)
        {
            uint h0 = _h0 + (BinaryPrimitives.ReadUInt32LittleEndian(m) & Mask);
            uint h1 = _h1 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(3)) >> 2) & Mask);
            uint h2 = _h2 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(6)) >> 4) & Mask);
            uint h3 = _h3 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(9)) >> 6) & Mask);
            uint h4 = _h4 + ((BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(12)) >> 8) | hibit);

            ulong d0 = (ulong)h0 * _r0 + (ulong)h1 * _s4 + (ulong)h2 * _s3 + (ulong)h3 * _s2 + (ulong)h4 * _s1;
            ulong d1 = (ulong)h0 * _r1 + (ulong)h1 * _r0 + (ulong)h2 * _s4 + (ulong)h3 * _s3 + (ulong)h4 * _s2;
            ulong d2 = (ulong)h0 * _r2 + (ulong)h1 * _r1 + (ulong)h2 * _r0 + (ulong)h3 * _s4 + (ulong)h4 * _s3;
            ulong d3 = (ulong)h0 * _r3 + (ulong)h1 * _r2 + (ulong)h2 * _r1 + (ulong)h3 * _r0 + (ulong)h4 * _s4;
            ulong d4 = (ulong)h0 * _r4 + (ulong)h1 * _r3 + (ulong)h2 * _r2 + (ulong)h3 * _r1 + (ulong)h4 * _r0;

            uint c = (uint)(d0 >> 26); h0 = (uint)d0 & Mask;
            d1 += c; c = (uint)(d1 >> 26); h1 = (uint)d1 & Mask;
            d2 += c; c = (uint)(d2 >> 26); h2 = (uint)d2 & Mask;
            d3 += c; c = (uint)(d3 >> 26); h3 = (uint)d3 & Mask;
            d4 += c; c = (uint)(d4 >> 26); h4 = (uint)d4 & Mask;
            h0 += c * 5; c = h0 >> 26; h0 &= Mask;
            h1 += c;

            _h0 = h0; _h1 = h1; _h2 = h2; _h3 = h3; _h4 = h4;
        }

        /// <summary>One-shot MAC of a message.</summary>
        public static void Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message, Span<byte> tag)
        {
            Span<byte> scratch = stackalloc byte[16];
            var state = new Poly1305(key, scratch);
            state.Update(message);
            state.Finish(tag);
        }

        /// <summary>Compares two tags without an early exit on the first differing byte.</summary>
        public static bool TagsEqual(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != TagSize || b.Length != TagSize)
                return false;
            int diff = 0;
            for (int i = 0; i < TagSize; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
