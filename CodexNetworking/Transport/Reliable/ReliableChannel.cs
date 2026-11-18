using System;
using System.Buffers.Binary;

namespace CodexNetworking.Transport.Reliable
{
    /// <summary>Tuning for one <see cref="ReliableChannel"/>. Both ends must use the same Window.</summary>
    public sealed class ReliableChannelConfig
    {
        /// <summary>Messages that may be unacknowledged at once. Power of two, at most 16384. The receive
        /// side is sized the same, which is what keeps every in-flight message inside the receiver's window.</summary>
        public int Window = 64;

        /// <summary>Largest message. Must fit in one packet with the 5 bytes of framing.</summary>
        public int MaxMessageSize = 512;

        /// <summary>Messages packed into one packet at most.</summary>
        public int MaxMessagesPerPacket = 32;

        /// <summary>Lower bound of the resend interval for messages whose packet was neither acked nor lost.</summary>
        public double MinResendInterval = 0.1;

        /// <summary>Resend interval is max(MinResendInterval, this * smoothed RTT).</summary>
        public double ResendRttMultiplier = 2.0;

        /// <summary>Sent packets remembered for ack and loss mapping. Power of two. Match NetConfig.AckBufferSize.</summary>
        public int PacketHistory = 256;

        public void Validate()
        {
            if (!IsPowerOfTwo(Window) || Window > 16384)
                throw new ArgumentOutOfRangeException(nameof(Window));
            if (MaxMessageSize < 1 || MaxMessageSize > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(MaxMessageSize));
            if (MaxMessagesPerPacket < 1 || MaxMessagesPerPacket > 255)
                throw new ArgumentOutOfRangeException(nameof(MaxMessagesPerPacket));
            if (!IsPowerOfTwo(PacketHistory))
                throw new ArgumentOutOfRangeException(nameof(PacketHistory));
            if (MinResendInterval <= 0 || ResendRttMultiplier <= 0)
                throw new ArgumentOutOfRangeException(nameof(MinResendInterval));
        }

        private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
    }

    /// <summary>
    /// Reliable, ordered messages on top of the unreliable packet layer, for handshake data, spawn
    /// tables and one-shot events. The channel owns no socket: the application asks it to fill a
    /// payload with <see cref="TryWritePacket"/>, sends that payload through <see cref="NetHost"/> or
    /// <see cref="NetClient"/>, reports the resulting sequence with <see cref="PacketSent"/>, and routes
    /// the transport's acked/lost callbacks and received payloads back in. Messages are delivered in
    /// send order with <see cref="TryDequeue"/>. One instance per connection, serving both directions.
    /// All buffers are allocated in the constructor.
    /// </summary>
    public sealed class ReliableChannel
    {
        private struct SendEntry
        {
            public bool Used;
            public bool Acked;
            public bool NeedsSend;
            public ushort Id;
            public int Length;
            public double LastSent;
            public byte[] Data;
        }

        private struct ReceiveEntry
        {
            public bool Used;
            public ushort Id;
            public int Length;
            public byte[] Data;
        }

        private struct PacketEntry
        {
            public int Start;
            public int Count;
        }

        /// <summary>Per packet: message count. Per message: u16 id, u16 length.</summary>
        public const int PacketHeaderSize = 1;
        public const int MessageHeaderSize = 4;

        private readonly ReliableChannelConfig _config;
        private readonly SendEntry[] _send;
        private readonly ReceiveEntry[] _receive;
        private readonly int _windowMask;
        private readonly SequenceBuffer<PacketEntry> _packets;
        private readonly ushort[] _packetIds;
        private readonly int _packetIdMask;
        private readonly ushort[] _staged;
        private int _packetIdCursor;
        private int _stagedCount;
        private bool _stagedPending;
        private ushort _nextSendId;
        private ushort _oldestUnacked;
        private ushort _nextDeliverId;

        public ReliableChannel(ReliableChannelConfig config)
        {
            config.Validate();
            _config = config;
            _windowMask = config.Window - 1;
            _send = new SendEntry[config.Window];
            _receive = new ReceiveEntry[config.Window];
            for (int i = 0; i < config.Window; i++)
            {
                _send[i].Data = new byte[config.MaxMessageSize];
                _receive[i].Data = new byte[config.MaxMessageSize];
            }
            _packets = new SequenceBuffer<PacketEntry>(config.PacketHistory);
            int idCapacity = NextPowerOfTwo(config.PacketHistory * config.MaxMessagesPerPacket);
            _packetIds = new ushort[idCapacity];
            _packetIdMask = idCapacity - 1;
            _staged = new ushort[config.MaxMessagesPerPacket];
        }

        public ReliableChannelConfig Config => _config;

        /// <summary>Messages sent but not yet acknowledged, including ones waiting for their first send.</summary>
        public int UnackedCount => (ushort)(_nextSendId - _oldestUnacked);

        public bool CanSend => UnackedCount < _config.Window;

        /// <summary>Smallest payload <see cref="TryWritePacket"/> can use for a message of the given size.</summary>
        public static int PacketSizeFor(int messageLength) => PacketHeaderSize + MessageHeaderSize + messageLength;

        public void Reset()
        {
            for (int i = 0; i < _send.Length; i++)
            {
                _send[i].Used = false;
                _receive[i].Used = false;
            }
            _packets.Clear();
            _packetIdCursor = 0;
            _stagedCount = 0;
            _stagedPending = false;
            _nextSendId = 0;
            _oldestUnacked = 0;
            _nextDeliverId = 0;
        }

        /// <summary>Queues a message. False when it is empty, too large, or the window is full.</summary>
        public bool TrySend(ReadOnlySpan<byte> message)
        {
            if (message.Length == 0 || message.Length > _config.MaxMessageSize || !CanSend)
                return false;
            ushort id = _nextSendId++;
            ref SendEntry entry = ref _send[id & _windowMask];
            entry.Used = true;
            entry.Acked = false;
            entry.NeedsSend = true;
            entry.Id = id;
            entry.Length = message.Length;
            entry.LastSent = double.NegativeInfinity;
            message.CopyTo(entry.Data);
            return true;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with messages that need (re)sending, oldest first.
        /// Returns false when there is nothing to send right now. After a true result the caller must
        /// call <see cref="PacketSent"/> with the transport sequence, or <see cref="PacketDropped"/>.
        /// </summary>
        public bool TryWritePacket(Span<byte> destination, double now, double smoothedRtt, out int written)
        {
            written = 0;
            if (_stagedPending)
                throw new InvalidOperationException("PacketSent or PacketDropped must follow TryWritePacket");
            if (destination.Length < PacketSizeFor(1))
                return false;

            double interval = Math.Max(_config.MinResendInterval, _config.ResendRttMultiplier * smoothedRtt);
            int offset = PacketHeaderSize;
            int count = 0;
            for (ushort id = _oldestUnacked; id != _nextSendId && count < _config.MaxMessagesPerPacket; id++)
            {
                ref SendEntry entry = ref _send[id & _windowMask];
                if (!entry.Used || entry.Acked)
                    continue;
                if (!entry.NeedsSend && now - entry.LastSent < interval)
                    continue;
                int size = MessageHeaderSize + entry.Length;
                if (offset + size > destination.Length)
                    continue; // try a smaller one; this one goes in the next packet
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset), entry.Id);
                BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset + 2), (ushort)entry.Length);
                new ReadOnlySpan<byte>(entry.Data, 0, entry.Length).CopyTo(destination.Slice(offset + MessageHeaderSize));
                offset += size;
                entry.NeedsSend = false;
                entry.LastSent = now;
                _staged[count++] = id;
            }
            if (count == 0)
                return false;
            destination[0] = (byte)count;
            written = offset;
            _stagedCount = count;
            _stagedPending = true;
            return true;
        }

        /// <summary>Binds the last written packet to the transport sequence it was sent with.</summary>
        public void PacketSent(ushort sequence)
        {
            if (!_stagedPending)
                return;
            _stagedPending = false;
            ref PacketEntry packet = ref _packets.Insert(sequence);
            packet.Start = _packetIdCursor;
            packet.Count = _stagedCount;
            for (int i = 0; i < _stagedCount; i++)
                _packetIds[(_packetIdCursor + i) & _packetIdMask] = _staged[i];
            _packetIdCursor = (_packetIdCursor + _stagedCount) & _packetIdMask;
        }

        /// <summary>The last written packet was not sent; its messages go out again at once.</summary>
        public void PacketDropped()
        {
            if (!_stagedPending)
                return;
            _stagedPending = false;
            for (int i = 0; i < _stagedCount; i++)
                MarkForResend(_staged[i]);
        }

        /// <summary>Route <see cref="NetListener.OnPacketAcked"/> here. Unknown sequences are ignored.</summary>
        public void OnPacketAcked(ushort sequence)
        {
            if (!_packets.Exists(sequence))
                return;
            ref PacketEntry packet = ref _packets.Get(sequence);
            for (int i = 0; i < packet.Count; i++)
            {
                ushort id = _packetIds[(packet.Start + i) & _packetIdMask];
                ref SendEntry entry = ref _send[id & _windowMask];
                if (entry.Used && entry.Id == id)
                    entry.Acked = true;
            }
            _packets.Remove(sequence);
            while (_oldestUnacked != _nextSendId)
            {
                ref SendEntry head = ref _send[_oldestUnacked & _windowMask];
                if (!head.Acked)
                    break;
                head.Used = false;
                _oldestUnacked++;
            }
        }

        /// <summary>Route <see cref="NetListener.OnPacketLost"/> here. Unknown sequences are ignored.</summary>
        public void OnPacketLost(ushort sequence)
        {
            if (!_packets.Exists(sequence))
                return;
            ref PacketEntry packet = ref _packets.Get(sequence);
            for (int i = 0; i < packet.Count; i++)
                MarkForResend(_packetIds[(packet.Start + i) & _packetIdMask]);
            _packets.Remove(sequence);
        }

        /// <summary>
        /// Parses a received channel payload and stores its messages. Returns false when the payload is
        /// malformed; whatever parsed before the error is kept. Call <see cref="TryDequeue"/> afterwards.
        /// </summary>
        public bool OnPacket(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < PacketHeaderSize)
                return false;
            int count = payload[0];
            int offset = PacketHeaderSize;
            for (int i = 0; i < count; i++)
            {
                if (offset + MessageHeaderSize > payload.Length)
                    return false;
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset));
                int length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset + 2));
                offset += MessageHeaderSize;
                if (length == 0 || length > _config.MaxMessageSize || offset + length > payload.Length)
                    return false;
                Store(id, payload.Slice(offset, length));
                offset += length;
            }
            return true;
        }

        /// <summary>
        /// Next message in order, or false. The span stays valid until the next
        /// <see cref="OnPacket"/> or <see cref="TryDequeue"/>.
        /// </summary>
        public bool TryDequeue(out ReadOnlySpan<byte> message)
        {
            if (!TryPeek(out message))
                return false;
            Dequeue();
            return true;
        }

        /// <summary>Next message in order without consuming it, or false.</summary>
        public bool TryPeek(out ReadOnlySpan<byte> message)
        {
            ref ReceiveEntry entry = ref _receive[_nextDeliverId & _windowMask];
            if (!entry.Used || entry.Id != _nextDeliverId)
            {
                message = default;
                return false;
            }
            message = new ReadOnlySpan<byte>(entry.Data, 0, entry.Length);
            return true;
        }

        /// <summary>Consumes the message <see cref="TryPeek"/> returned.</summary>
        public void Dequeue()
        {
            ref ReceiveEntry entry = ref _receive[_nextDeliverId & _windowMask];
            if (!entry.Used || entry.Id != _nextDeliverId)
                return;
            entry.Used = false;
            _nextDeliverId++;
        }

        private void Store(ushort id, ReadOnlySpan<byte> bytes)
        {
            if (SequenceMath.LessThan(id, _nextDeliverId))
                return; // already delivered
            if (SequenceMath.Diff(id, _nextDeliverId) >= _config.Window)
                return; // peer window larger than ours; cannot hold it
            ref ReceiveEntry entry = ref _receive[id & _windowMask];
            if (entry.Used)
                return; // duplicate (the window invariant guarantees entry.Id == id)
            entry.Used = true;
            entry.Id = id;
            entry.Length = bytes.Length;
            bytes.CopyTo(entry.Data);
        }

        private void MarkForResend(ushort id)
        {
            ref SendEntry entry = ref _send[id & _windowMask];
            if (entry.Used && entry.Id == id && !entry.Acked)
                entry.NeedsSend = true;
        }

        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }
    }
}
