using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Reassembles messages that were split across several datagrams. A few messages can be in flight
    /// at once; a message older than the reassembly window is abandoned, which loses it like any other
    /// unreliable packet. Buffers are allocated once per slot and reused.
    /// </summary>
    public sealed class FragmentAssembler
    {
        private struct Entry
        {
            public ulong ReceivedMask;
            public byte Count;
            public int TotalBytes;
            public byte[] Buffer;
        }

        private readonly SequenceBuffer<Entry> _entries;
        private readonly int _maxFragments;
        private readonly int _fragmentPayloadSize;

        public FragmentAssembler(int bufferSize, int maxFragments, int fragmentPayloadSize)
        {
            if (maxFragments < 2 || maxFragments > Protocol.MaxFragmentCount)
                throw new ArgumentOutOfRangeException(nameof(maxFragments));
            _entries = new SequenceBuffer<Entry>(bufferSize);
            _maxFragments = maxFragments;
            _fragmentPayloadSize = fragmentPayloadSize;
        }

        public void Reset() => _entries.Clear();

        /// <summary>
        /// Adds one fragment. Returns true when the message is complete; <paramref name="message"/> then
        /// points into an internal buffer that stays valid until the next call.
        /// </summary>
        public bool TryAdd(ushort sequence, byte index, byte count, ReadOnlySpan<byte> data, out ReadOnlySpan<byte> message)
        {
            message = default;
            if (count < 2 || count > _maxFragments || index >= count)
                return false;
            bool last = index == count - 1;
            if (last ? (data.Length == 0 || data.Length > _fragmentPayloadSize) : data.Length != _fragmentPayloadSize)
                return false;

            if (!_entries.Exists(sequence))
            {
                if (_entries.IsStale(sequence))
                    return false;
                ref Entry fresh = ref _entries.Insert(sequence);
                fresh.ReceivedMask = 0;
                fresh.Count = count;
                fresh.TotalBytes = -1;
                int capacity = _maxFragments * _fragmentPayloadSize;
                if (fresh.Buffer == null || fresh.Buffer.Length < capacity)
                    fresh.Buffer = new byte[capacity];
            }

            ref Entry entry = ref _entries.Get(sequence);
            ulong bit = 1UL << index;
            if (entry.Count != count || (entry.ReceivedMask & bit) != 0)
                return false;

            data.CopyTo(new Span<byte>(entry.Buffer, index * _fragmentPayloadSize, data.Length));
            entry.ReceivedMask |= bit;
            if (last)
                entry.TotalBytes = index * _fragmentPayloadSize + data.Length;

            ulong complete = count == 64 ? ulong.MaxValue : (1UL << count) - 1;
            if (entry.ReceivedMask != complete || entry.TotalBytes < 0)
                return false;

            message = new ReadOnlySpan<byte>(entry.Buffer, 0, entry.TotalBytes);
            _entries.Remove(sequence);
            return true;
        }
    }
}
