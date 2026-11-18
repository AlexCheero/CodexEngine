using System;
using System.Runtime.CompilerServices;

namespace CodexNetworking.Serialization
{
    /// <summary>
    /// Packs values of arbitrary bit width into a byte span, least significant bit first. Never
    /// throws on a full buffer: a write that does not fit returns false, leaves the stream unchanged
    /// and sets <see cref="Overflowed"/>, so a packet builder can stop and send what fits. Call
    /// <see cref="Flush"/> before using the bytes; it writes the pending partial byte and returns the
    /// byte count. The layout is the protocol: <see cref="BitReader"/> mirrors it exactly.
    /// </summary>
    public ref struct BitWriter
    {
        private readonly Span<byte> _buffer;
        private ulong _scratch;
        private int _scratchBits;
        private int _byteIndex;
        private bool _overflowed;

        public BitWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _scratch = 0;
            _scratchBits = 0;
            _byteIndex = 0;
            _overflowed = false;
        }

        public int CapacityBits => _buffer.Length * 8;

        public int BitsWritten => _byteIndex * 8 + _scratchBits;

        public int BitsRemaining => CapacityBits - BitsWritten;

        /// <summary>Bytes needed to hold everything written so far, partial byte included.</summary>
        public int BytesWritten => (BitsWritten + 7) >> 3;

        public bool Overflowed => _overflowed;

        public bool IsAligned => _scratchBits == 0;

        /// <summary>Writes the low <paramref name="bits"/> bits of <paramref name="value"/> (1..32).</summary>
        public bool WriteBits(uint value, int bits)
        {
            if (bits < 1 || bits > 32)
                throw new ArgumentOutOfRangeException(nameof(bits));
            if (BitsRemaining < bits)
            {
                _overflowed = true;
                return false;
            }
            if (bits < 32)
                value &= (1u << bits) - 1;
            _scratch |= (ulong)value << _scratchBits;
            _scratchBits += bits;
            while (_scratchBits >= 8)
            {
                _buffer[_byteIndex++] = (byte)_scratch;
                _scratch >>= 8;
                _scratchBits -= 8;
            }
            return true;
        }

        /// <summary>Writes a two's complement value in <paramref name="bits"/> bits (2..32); the range is [-2^(bits-1), 2^(bits-1)-1].</summary>
        public bool WriteSignedBits(int value, int bits)
        {
            if (bits < 2)
                throw new ArgumentOutOfRangeException(nameof(bits));
            return WriteBits((uint)value, bits);
        }

        public bool WriteBool(bool value) => WriteBits(value ? 1u : 0u, 1);

        public bool WriteByte(byte value) => WriteBits(value, 8);

        public bool WriteUInt16(ushort value) => WriteBits(value, 16);

        public bool WriteUInt32(uint value) => WriteBits(value, 32);

        public bool WriteInt32(int value) => WriteBits((uint)value, 32);

        public bool WriteUInt64(ulong value)
        {
            if (BitsRemaining < 64)
            {
                _overflowed = true;
                return false;
            }
            WriteBits((uint)value, 32);
            WriteBits((uint)(value >> 32), 32);
            return true;
        }

        public bool WriteInt64(long value) => WriteUInt64((ulong)value);

        /// <summary>Writes an integer known to lie in [min, max] using the fewest bits for that range.</summary>
        public bool WriteRange(int value, int min, int max)
        {
            if (min > max)
                throw new ArgumentException("min must not exceed max");
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(nameof(value));
            int bits = BitsRequired((uint)(max - min));
            return bits == 0 || WriteBits((uint)(value - min), bits);
        }

        /// <summary>Pads with zero bits up to the next byte boundary.</summary>
        public bool Align()
        {
            if (_scratchBits == 0)
                return true;
            return WriteBits(0, 8 - _scratchBits);
        }

        /// <summary>Writes raw bytes; bulk copies when aligned, bit-packs otherwise.</summary>
        public bool WriteBytes(ReadOnlySpan<byte> bytes)
        {
            if (BitsRemaining < bytes.Length * 8)
            {
                _overflowed = true;
                return false;
            }
            if (_scratchBits == 0)
            {
                bytes.CopyTo(_buffer.Slice(_byteIndex));
                _byteIndex += bytes.Length;
                return true;
            }
            for (int i = 0; i < bytes.Length; i++)
                WriteBits(bytes[i], 8);
            return true;
        }

        /// <summary>
        /// Writes the pending partial byte into the buffer and returns the number of bytes used.
        /// Writing may continue afterwards; the partial byte is rewritten as it fills.
        /// </summary>
        public int Flush()
        {
            if (_scratchBits > 0)
                _buffer[_byteIndex] = (byte)_scratch;
            return BytesWritten;
        }

        /// <summary>
        /// Moves the write position back to an earlier <see cref="BitsWritten"/> value and clears the
        /// overflow flag, discarding everything written after it. Lets a packet builder drop the last
        /// record when it did not fit.
        /// </summary>
        public void Rewind(int bitsWritten)
        {
            if (bitsWritten > BitsWritten)
                throw new ArgumentOutOfRangeException(nameof(bitsWritten));
            Seek(bitsWritten);
            _overflowed = false;
        }

        /// <summary>
        /// Moves the write position to any bit offset inside the buffer, keeping the bytes already
        /// there and the overflow flag. Used to patch a value written earlier (seek back, write, seek
        /// to the end again). Writes after a forward seek assume the bytes in between were written
        /// before. A failed write only reports failure for that call: later, smaller writes may still
        /// fit, which is why <see cref="Overflowed"/> stays set until a <see cref="Rewind"/>.
        /// </summary>
        public void Seek(int bitPosition)
        {
            if (bitPosition < 0 || bitPosition > CapacityBits)
                throw new ArgumentOutOfRangeException(nameof(bitPosition));
            Flush();
            _byteIndex = bitPosition >> 3;
            _scratchBits = bitPosition & 7;
            _scratch = _scratchBits == 0 ? 0UL : (ulong)(_buffer[_byteIndex] & ((1 << _scratchBits) - 1));
        }

        /// <summary>Bits needed to represent any value in [0, maxValue]; 0 for maxValue 0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BitsRequired(uint maxValue)
        {
            int bits = 0;
            while (maxValue != 0)
            {
                maxValue >>= 1;
                bits++;
            }
            return bits;
        }
    }
}
