using System;
using System.Collections.Generic;

namespace CodexNetworking.Transport.Simulator
{
    /// <summary>
    /// In-process network with deterministic loss, latency, jitter and duplication. Time is explicit:
    /// datagrams sent after <see cref="Update"/>(t) are stamped with t and delivered by a later Update
    /// once their delivery time has passed. Packets with equal delivery time keep send order.
    /// All storage is preallocated; a full network drops datagrams.
    /// </summary>
    public sealed class SimulatedNetwork
    {
        internal struct Packet
        {
            public byte[] Data;
            public int Length;
            public Address From;
            public Address To;
            public double DeliverAt;
            // Send order, so packets due at the same time are delivered first-in first-out.
            public ulong Serial;
        }

        private readonly Packet[] _packets;
        private readonly int[] _free;
        private readonly int[] _inFlight;
        private readonly int[] _due;
        private readonly List<SimulatedSocket> _sockets = new List<SimulatedSocket>();
        private readonly XorShiftRandom _random;
        private readonly Comparison<int> _byDeliveryTime;
        private readonly int _maxDatagramSize;
        private int _freeCount;
        private int _inFlightCount;
        private ulong _nextSerial;

        public NetworkConditions Conditions;

        public SimulatedNetwork(ulong seed, int capacity = 4096, int maxDatagramSize = 1500)
        {
            _random = new XorShiftRandom(seed);
            _maxDatagramSize = maxDatagramSize;
            _packets = new Packet[capacity];
            _free = new int[capacity];
            _inFlight = new int[capacity];
            _due = new int[capacity];
            _byDeliveryTime = CompareDelivery;
            for (int i = 0; i < capacity; i++)
            {
                _packets[i].Data = new byte[maxDatagramSize];
                _free[i] = capacity - 1 - i;
            }
            _freeCount = capacity;
        }

        public int Capacity => _packets.Length;
        public int MaxDatagramSize => _maxDatagramSize;
        public double Now { get; private set; }
        public int InFlight => _inFlightCount;
        public long Delivered { get; private set; }
        public long Dropped { get; private set; }
        public long Duplicated { get; private set; }

        /// <summary>Creates a socket at a unique virtual address.</summary>
        public SimulatedSocket CreateSocket(in Address address)
        {
            if (address.IsNone)
                throw new ArgumentException("address required", nameof(address));
            if (FindSocket(address) != null)
                throw new InvalidOperationException("address already in use: " + address);
            var socket = new SimulatedSocket(this, address, _packets.Length);
            _sockets.Add(socket);
            return socket;
        }

        /// <summary>Delivers every datagram due at or before <paramref name="now"/>, in delivery-time order.</summary>
        public void Update(double now)
        {
            Now = now;
            int kept = 0;
            int dueCount = 0;
            for (int k = 0; k < _inFlightCount; k++)
            {
                int index = _inFlight[k];
                if (_packets[index].DeliverAt <= now)
                    _due[dueCount++] = index;
                else
                    _inFlight[kept++] = index;
            }
            _inFlightCount = kept;
            if (dueCount == 0)
                return;
            // Jitter makes later sends due earlier; sort so the receiver sees real reordering.
            Array.Sort(_due, 0, dueCount, Comparer<int>.Create(_byDeliveryTime));
            for (int k = 0; k < dueCount; k++)
                Deliver(_due[k]);
        }

        private int CompareDelivery(int a, int b)
        {
            int byTime = _packets[a].DeliverAt.CompareTo(_packets[b].DeliverAt);
            return byTime != 0 ? byTime : _packets[a].Serial.CompareTo(_packets[b].Serial);
        }

        /// <summary>Drops everything currently in flight.</summary>
        public void DropInFlight()
        {
            for (int k = 0; k < _inFlightCount; k++)
                Release(_inFlight[k]);
            Dropped += _inFlightCount;
            _inFlightCount = 0;
        }

        internal bool Enqueue(ReadOnlySpan<byte> data, in Address from, in Address to)
        {
            if (data.Length == 0 || data.Length > _maxDatagramSize)
                return false;
            if (Conditions.LossChance > 0 && _random.NextDouble() < Conditions.LossChance)
            {
                Dropped++;
                return true; // the sender cannot tell
            }
            Schedule(data, from, to);
            if (Conditions.DuplicateChance > 0 && _random.NextDouble() < Conditions.DuplicateChance)
            {
                Duplicated++;
                Schedule(data, from, to);
            }
            return true;
        }

        internal ref Packet GetPacket(int index) => ref _packets[index];

        internal void Release(int index) => _free[_freeCount++] = index;

        internal void Remove(SimulatedSocket socket) => _sockets.Remove(socket);

        private void Schedule(ReadOnlySpan<byte> data, in Address from, in Address to)
        {
            if (_freeCount == 0)
            {
                Dropped++;
                return;
            }
            int index = _free[--_freeCount];
            ref Packet packet = ref _packets[index];
            data.CopyTo(packet.Data);
            packet.Length = data.Length;
            packet.From = from;
            packet.To = to;
            packet.DeliverAt = Now + Conditions.Latency + (Conditions.Jitter > 0 ? Conditions.Jitter * _random.NextDouble() : 0);
            packet.Serial = _nextSerial++;
            _inFlight[_inFlightCount++] = index;
        }

        private void Deliver(int index)
        {
            SimulatedSocket target = FindSocket(_packets[index].To);
            if (target == null || !target.Enqueue(index))
            {
                Dropped++;
                Release(index);
                return;
            }
            Delivered++;
        }

        private SimulatedSocket FindSocket(in Address address)
        {
            for (int i = 0; i < _sockets.Count; i++)
            {
                if (_sockets[i].LocalAddress == address)
                    return _sockets[i];
            }
            return null;
        }
    }

    /// <summary><see cref="IUdpSocket"/> bound to a virtual address of a <see cref="SimulatedNetwork"/>.</summary>
    public sealed class SimulatedSocket : IUdpSocket
    {
        private readonly SimulatedNetwork _network;
        private readonly int[] _inbox;
        private int _head;
        private int _count;
        private bool _disposed;

        internal SimulatedSocket(SimulatedNetwork network, in Address address, int capacity)
        {
            _network = network;
            LocalAddress = address;
            _inbox = new int[capacity];
        }

        public Address LocalAddress { get; }

        public int Pending => _count;

        public bool TryReceive(Span<byte> buffer, out int received, out Address from)
        {
            received = 0;
            from = Address.None;
            while (_count > 0)
            {
                int index = _inbox[_head];
                _head = (_head + 1) % _inbox.Length;
                _count--;
                ref SimulatedNetwork.Packet packet = ref _network.GetPacket(index);
                if (packet.Length > buffer.Length)
                {
                    _network.Release(index);
                    continue;
                }
                new ReadOnlySpan<byte>(packet.Data, 0, packet.Length).CopyTo(buffer);
                received = packet.Length;
                from = packet.From;
                _network.Release(index);
                return true;
            }
            return false;
        }

        public bool Send(ReadOnlySpan<byte> data, in Address to) => !_disposed && _network.Enqueue(data, LocalAddress, to);

        internal bool Enqueue(int packetIndex)
        {
            if (_disposed || _count == _inbox.Length)
                return false;
            _inbox[(_head + _count) % _inbox.Length] = packetIndex;
            _count++;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            while (_count > 0)
            {
                _network.Release(_inbox[_head]);
                _head = (_head + 1) % _inbox.Length;
                _count--;
            }
            _network.Remove(this);
        }
    }
}
