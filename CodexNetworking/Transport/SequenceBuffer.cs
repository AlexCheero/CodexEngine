using System;
using System.Runtime.CompilerServices;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Ring of entries indexed by 16-bit sequence number (reliable.io style). Inserting a sequence newer
    /// than the newest one clears every slot in between, so an old sequence from the previous wrap is
    /// never mistaken for a present entry. Entries are not cleared on insert: the caller overwrites the
    /// fields it uses, which lets entries keep reusable buffers.
    /// </summary>
    public sealed class SequenceBuffer<T> where T : struct
    {
        private const uint Empty = 0xFFFFFFFF;

        private readonly uint[] _sequences;
        private readonly T[] _entries;
        private readonly int _mask;
        private ushort _newest;
        private bool _any;

        public SequenceBuffer(int size)
        {
            if (size <= 0 || (size & (size - 1)) != 0)
                throw new ArgumentException("size must be a power of two", nameof(size));
            _sequences = new uint[size];
            _entries = new T[size];
            _mask = size - 1;
            Array.Fill(_sequences, Empty);
        }

        public int Size => _sequences.Length;

        /// <summary>Newest sequence ever inserted. Only meaningful when <see cref="Any"/> is true.</summary>
        public ushort Newest => _newest;

        public bool Any => _any;

        /// <summary>True when the sequence is older than the window the buffer can still represent.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsStale(ushort sequence)
        {
            return _any && SequenceMath.LessThan(sequence, (ushort)(_newest - (ushort)(_mask)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Exists(ushort sequence) => _sequences[sequence & _mask] == sequence;

        /// <summary>Reserves the slot for <paramref name="sequence"/>. The sequence must not be stale.</summary>
        public ref T Insert(ushort sequence)
        {
            if (!_any)
            {
                _any = true;
                _newest = sequence;
            }
            else if (SequenceMath.GreaterThan(sequence, _newest))
            {
                ClearRange((ushort)(_newest + 1), sequence);
                _newest = sequence;
            }
            int index = sequence & _mask;
            _sequences[index] = sequence;
            return ref _entries[index];
        }

        /// <summary>Entry for an existing sequence. Check <see cref="Exists"/> first.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T Get(ushort sequence) => ref _entries[sequence & _mask];

        public void Remove(ushort sequence)
        {
            int index = sequence & _mask;
            if (_sequences[index] == sequence)
                _sequences[index] = Empty;
        }

        public void Clear()
        {
            Array.Fill(_sequences, Empty);
            _any = false;
            _newest = 0;
        }

        // Clears [from, to) handling wrap-around; clears everything when the range covers the buffer.
        private void ClearRange(ushort from, ushort to)
        {
            int count = (ushort)(to - from);
            if (count >= _sequences.Length)
            {
                Array.Fill(_sequences, Empty);
                return;
            }
            for (int i = 0; i < count; i++)
                _sequences[(from + i) & _mask] = Empty;
        }
    }
}
