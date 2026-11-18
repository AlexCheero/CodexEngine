using System;

namespace CodexNetworking.Transport
{
    /// <summary>
    /// Per-connection sequence numbering and acknowledgement (reliable.io style). Every outgoing packet
    /// gets the next sequence; every outgoing header carries the newest received sequence plus a 64-bit
    /// bitfield of the ones before it, so each ack is repeated across many packets and survives loss.
    /// Each sent packet is resolved exactly once as acked or lost; loss is declared when a packet
    /// <see cref="NetConfig.LossReorderWindow"/> sequences newer has been acked or after
    /// <see cref="NetConfig.LossTimeout"/>, whichever comes first.
    /// </summary>
    public sealed class PacketAcks
    {
        private struct SentEntry
        {
            public double Time;
            public int Bytes;
            public bool Notify;
            public bool Acked;
            public bool Lost;
        }

        private struct ReceivedEntry
        {
            public double Time;
            public int Bytes;
        }

        private const int AckBitCount = 64;

        private readonly SequenceBuffer<SentEntry> _sent;
        private readonly SequenceBuffer<ReceivedEntry> _received;
        private readonly BandwidthMeter _sendMeter = new BandwidthMeter();
        private readonly BandwidthMeter _receiveMeter = new BandwidthMeter();
        private readonly double _lossTimeout;
        private readonly int _reorderWindow;
        private ushort _nextSequence;
        // Oldest sent sequence not yet resolved as acked or lost.
        private ushort _lossCursor;
        private ushort _newestAcked;
        private bool _anyAcked;
        private ConnectionStats _stats;

        public PacketAcks(int bufferSize, double lossTimeout, int lossReorderWindow)
        {
            _sent = new SequenceBuffer<SentEntry>(bufferSize);
            _received = new SequenceBuffer<ReceivedEntry>(bufferSize);
            _lossTimeout = lossTimeout;
            _reorderWindow = lossReorderWindow;
        }

        public ConnectionStats Stats => _stats;

        /// <summary>Sequence the next sent packet will get.</summary>
        public ushort NextSequence => _nextSequence;

        public bool HasReceivedAny => _received.Any;

        public ushort NewestReceived => _received.Newest;

        public void Reset()
        {
            _sent.Clear();
            _received.Clear();
            _sendMeter.Reset();
            _receiveMeter.Reset();
            _nextSequence = 0;
            _lossCursor = 0;
            _newestAcked = 0;
            _anyAcked = false;
            _stats = default;
        }

        /// <summary>
        /// Registers an outgoing packet and returns its sequence. When <paramref name="notify"/> is set
        /// the listener later hears OnPacketAcked or OnPacketLost for it.
        /// </summary>
        public ushort OnSend(double now, int bytes, bool notify, NetListener listener, int connection)
        {
            // Never overwrite an unresolved entry: resolve it as lost first so every payload is reported.
            while ((ushort)(_nextSequence - _lossCursor) >= _sent.Size)
                ResolveCursorAsLost(listener, connection);

            ushort sequence = _nextSequence++;
            ref SentEntry entry = ref _sent.Insert(sequence);
            entry.Time = now;
            entry.Bytes = bytes;
            entry.Notify = notify;
            entry.Acked = false;
            entry.Lost = false;
            _stats.PacketsSent++;
            _stats.BytesSent += bytes;
            _sendMeter.Add(bytes, now);
            return sequence;
        }

        /// <summary>Registers an incoming sequence. False when it is stale or a duplicate.</summary>
        public bool OnReceive(ushort sequence, double now, int bytes)
        {
            if (_received.IsStale(sequence) || _received.Exists(sequence))
                return false;
            ref ReceivedEntry entry = ref _received.Insert(sequence);
            entry.Time = now;
            entry.Bytes = bytes;
            _stats.PacketsReceived++;
            _stats.BytesReceived += bytes;
            _receiveMeter.Add(bytes, now);
            return true;
        }

        /// <summary>Counts bytes of a datagram that belongs to an already registered sequence (later fragments).</summary>
        public void AddReceivedBytes(int bytes, double now)
        {
            _stats.BytesReceived += bytes;
            _receiveMeter.Add(bytes, now);
        }

        public bool HasReceived(ushort sequence) => _received.Exists(sequence);

        public bool IsStale(ushort sequence) => _received.IsStale(sequence);

        /// <summary>Ack fields for the next outgoing header.</summary>
        public void GetAckHeader(out ushort ack, out ulong ackBits)
        {
            ackBits = 0;
            if (!_received.Any)
            {
                // Nothing to ack. Bits are zero so the peer acks nothing; the ack value is irrelevant.
                ack = 0xFFFF;
                return;
            }
            ack = _received.Newest;
            for (int i = 0; i < AckBitCount; i++)
            {
                if (_received.Exists((ushort)(ack - i)))
                    ackBits |= 1UL << i;
            }
        }

        /// <summary>Applies the ack fields of an incoming header, then runs loss detection.</summary>
        public void ProcessAcks(ushort ack, ulong ackBits, double now, NetListener listener, int connection)
        {
            for (int i = 0; i < AckBitCount && ackBits != 0; i++)
            {
                ulong bit = 1UL << i;
                if ((ackBits & bit) == 0)
                    continue;
                ackBits &= ~bit;

                ushort sequence = (ushort)(ack - i);
                if (!_sent.Exists(sequence))
                    continue;
                ref SentEntry entry = ref _sent.Get(sequence);
                if (entry.Acked || entry.Lost)
                    continue;

                entry.Acked = true;
                _stats.PacketsAcked++;
                double sample = now - entry.Time;
                if (sample >= 0)
                    UpdateRtt(sample);
                if (!_anyAcked || SequenceMath.GreaterThan(sequence, _newestAcked))
                {
                    _newestAcked = sequence;
                    _anyAcked = true;
                }
                if (entry.Notify)
                    listener.OnPacketAcked(connection, sequence);
            }
            DetectLoss(now, listener, connection);
        }

        /// <summary>Time-based loss detection and rate sampling; call once per update even when nothing was received.</summary>
        public void Update(double now, NetListener listener, int connection)
        {
            DetectLoss(now, listener, connection);
            _stats.SendRateBytesPerSecond = _sendMeter.Rate(now);
            _stats.ReceiveRateBytesPerSecond = _receiveMeter.Rate(now);
        }

        private void DetectLoss(double now, NetListener listener, int connection)
        {
            while (_lossCursor != _nextSequence)
            {
                if (!_sent.Exists(_lossCursor))
                {
                    _lossCursor++;
                    continue;
                }
                ref SentEntry entry = ref _sent.Get(_lossCursor);
                if (entry.Acked || entry.Lost)
                {
                    _lossCursor++;
                    continue;
                }
                bool byReorder = _anyAcked && SequenceMath.Diff(_newestAcked, _lossCursor) > _reorderWindow;
                bool byTimeout = now - entry.Time > _lossTimeout;
                if (!byReorder && !byTimeout)
                    break;
                ResolveCursorAsLost(listener, connection);
            }
        }

        private void ResolveCursorAsLost(NetListener listener, int connection)
        {
            if (_sent.Exists(_lossCursor))
            {
                ref SentEntry entry = ref _sent.Get(_lossCursor);
                if (!entry.Acked && !entry.Lost)
                {
                    entry.Lost = true;
                    _stats.PacketsLost++;
                    if (entry.Notify)
                        listener.OnPacketLost(connection, _lossCursor);
                }
            }
            _lossCursor++;
        }

        private void UpdateRtt(double sample)
        {
            if (_stats.SmoothedRtt <= 0)
                _stats.SmoothedRtt = sample;
            else
                _stats.SmoothedRtt += (sample - _stats.SmoothedRtt) * 0.1;
        }
    }
}
