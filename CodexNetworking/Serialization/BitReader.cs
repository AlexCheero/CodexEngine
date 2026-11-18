using System;

namespace CodexNetworking.Serialization
{
    /// <summary>
    /// Reads values written by <see cref="BitWriter"/>. Reading past the end returns false, yields
    /// zero and sets <see cref="Overflowed"/>; it never throws on malformed input, so a receiver can
    /// drop a bad packet after decoding instead of catching exceptions.
    /// </summary>
    public ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private ulong _scratch;
        private int _scratchBits;
        private int _byteIndex;
        private int _bitsRead;
        private bool _overflowed;

        public BitReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _scratch = 0;
            _scratchBits = 0;
            _byteIndex = 0;
            _bitsRead = 0;
            _overflowed = false;
        }

        public int CapacityBits => _buffer.Length * 8;

        public int BitsRead => _bitsRead;

        public int BitsRemaining => CapacityBits - _bitsRead;

        public bool Overflowed => _overflowed;

        public bool IsAligned => (_bitsRead & 7) == 0;

        public bool ReadBits(int bits, out uint value)
        {
            if (bits < 1 || bits > 32)
                throw new ArgumentOutOfRangeException(nameof(bits));
            if (BitsRemaining < bits)
            {
                _overflowed = true;
                value = 0;
                return false;
            }
            while (_scratchBits < bits)
            {
                _scratch |= (ulong)_buffer[_byteIndex++] << _scratchBits;
                _scratchBits += 8;
            }
            value = bits == 32 ? (uint)_scratch : (uint)(_scratch & ((1UL << bits) - 1));
            _scratch >>= bits;
            _scratchBits -= bits;
            _bitsRead += bits;
            return true;
        }

        /// <summary>Reads a two's complement value of <paramref name="bits"/> bits (2..32), sign extended.</summary>
        public bool ReadSignedBits(int bits, out int value)
        {
            if (bits < 2)
                throw new ArgumentOutOfRangeException(nameof(bits));
            if (!ReadBits(bits, out uint raw))
            {
                value = 0;
                return false;
            }
            int shift = 32 - bits;
            value = (int)(raw << shift) >> shift;
            return true;
        }

        public bool ReadBool(out bool value)
        {
            bool ok = ReadBits(1, out uint raw);
            value = raw != 0;
            return ok;
        }

        public bool ReadByte(out byte value)
        {
            bool ok = ReadBits(8, out uint raw);
            value = (byte)raw;
            return ok;
        }

        public bool ReadUInt16(out ushort value)
        {
            bool ok = ReadBits(16, out uint raw);
            value = (ushort)raw;
            return ok;
        }

        public bool ReadUInt32(out uint value) => ReadBits(32, out value);

        public bool ReadInt32(out int value)
        {
            bool ok = ReadBits(32, out uint raw);
            value = (int)raw;
            return ok;
        }

        public bool ReadUInt64(out ulong value)
        {
            if (BitsRemaining < 64)
            {
                _overflowed = true;
                value = 0;
                return false;
            }
            ReadBits(32, out uint low);
            ReadBits(32, out uint high);
            value = low | ((ulong)high << 32);
            return true;
        }

        public bool ReadInt64(out long value)
        {
            bool ok = ReadUInt64(out ulong raw);
            value = (long)raw;
            return ok;
        }

        /// <summary>Reads an integer written with <see cref="BitWriter.WriteRange"/> for the same bounds.</summary>
        public bool ReadRange(int min, int max, out int value)
        {
            if (min > max)
                throw new ArgumentException("min must not exceed max");
            int bits = BitWriter.BitsRequired((uint)(max - min));
            if (bits == 0)
            {
                value = min;
                return true;
            }
            if (!ReadBits(bits, out uint raw) || raw > (uint)(max - min))
            {
                _overflowed = _overflowed || BitsRemaining < 0;
                value = min;
                return false;
            }
            value = min + (int)raw;
            return true;
        }

        /// <summary>Skips padding up to the next byte boundary. False when the padding bits are not zero or missing.</summary>
        public bool Align()
        {
            int padding = (8 - (_bitsRead & 7)) & 7;
            if (padding == 0)
                return true;
            return ReadBits(padding, out uint pad) && pad == 0;
        }

        public bool ReadBytes(Span<byte> destination)
        {
            if (BitsRemaining < destination.Length * 8)
            {
                _overflowed = true;
                return false;
            }
            if (_scratchBits == 0)
            {
                _buffer.Slice(_byteIndex, destination.Length).CopyTo(destination);
                _byteIndex += destination.Length;
                _bitsRead += destination.Length * 8;
                return true;
            }
            for (int i = 0; i < destination.Length; i++)
            {
                ReadBits(8, out uint raw);
                destination[i] = (byte)raw;
            }
            return true;
        }
    }
}
