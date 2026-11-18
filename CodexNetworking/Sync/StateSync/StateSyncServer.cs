using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using CodexECS;
using CodexNetworking.History;
using CodexNetworking.Serialization;
using CodexNetworking.Transport;
using CodexNetworking.Transport.Reliable;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// One authoritative tick: inputs are indexed by client slot; <paramref name="connected"/> tells
    /// which slots hold a client. An unconnected or silent client's input is the default value or a
    /// repeat of its last one.
    /// </summary>
    public interface IServerStep<TInput> where TInput : unmanaged
    {
        void Step(EcsWorld world, int tick, ReadOnlySpan<TInput> inputs, ReadOnlySpan<bool> connected);
    }

    /// <summary>First byte of every state-sync payload.</summary>
    public static class StateSyncPacket
    {
        /// <summary>Server to client: snapshot. Client to server: inputs.</summary>
        public const byte Primary = 0;
        /// <summary>Reliable channel blob (events).</summary>
        public const byte Reliable = 1;
    }

    /// <summary>
    /// Server endpoint of state sync: owns the transport host, the authoritative world's loop and
    /// history, per-client input buffers, the snapshot writer and one reliable channel per client
    /// for event components. Call <see cref="Update"/> every frame and <see cref="Step"/> once per
    /// simulation tick. Event components added during a tick are delivered reliably once and
    /// removed from the server world at the start of the next tick.
    /// </summary>
    public sealed class StateSyncServer<TInput> : NetListener, IDisposable where TInput : unmanaged
    {
        private sealed class StepAdapter : ISimulationStep
        {
            public StateSyncServer<TInput> Owner;

            public void Step(EcsWorld world, int tick, bool resimulating) =>
                Owner._step.Step(world, tick, Owner._frame, Owner._connected);
        }

        private readonly NetHost _host;
        private readonly IServerStep<TInput> _step;
        private readonly SimulationLoop _loop;
        private readonly SnapshotWriter _writer;
        private readonly StateSyncConfig _config;
        private readonly InputBuffer<TInput>[] _inputs;
        private readonly ReliableChannel[] _events;
        private readonly bool[] _connected;
        private readonly TInput[] _frame;
        private readonly sbyte[] _lead;
        private readonly byte[] _scratch;
        private readonly byte[] _eventScratch;
        private readonly SnapshotPacket[] _packets;
        private readonly int[] _recipients;
        private readonly EncodingLane[] _lanes;
        private readonly object _encodingGate = new object();
        private int _recipientCount, _laneCount, _remainingLanes;
        private ExceptionDispatchInfo _encodingError;

        private struct SnapshotPacket
        {
            public byte[] Data;
            public int Budget, Size;
        }

        private sealed class EncodingLane : IThreadPoolWorkItem
        {
            private readonly StateSyncServer<TInput> _server;
            private readonly int _index;

            public EncodingLane(StateSyncServer<TInput> server, int index)
            {
                _server = server;
                _index = index;
            }

            public void Execute()
            {
                try { _server.EncodeLane(_index); }
                catch (Exception ex) { Interlocked.CompareExchange(ref _server._encodingError, ExceptionDispatchInfo.Capture(ex), null); }
                finally
                {
                    lock (_server._encodingGate)
                        if (--_server._remainingLanes == 0)
                            Monitor.Pulse(_server._encodingGate);
                }
            }
        }

        public StateSyncServer(NetConfig netConfig, IUdpSocket socket, IRandomSource random, EcsWorld world, IServerStep<TInput> step,
            SimulationRegistry simulation, ReplicationRegistry replication, StateSyncConfig config)
        {
            config.Validate();
            _config = config;
            _step = step ?? throw new ArgumentNullException(nameof(step));
            _host = new NetHost(netConfig, socket, random, this);
            _loop = new SimulationLoop(world, new StepAdapter { Owner = this }, simulation, config.ServerHistoryCapacity);
            _writer = new SnapshotWriter(replication, simulation, netConfig.MaxClients, netConfig.AckBufferSize);
            _inputs = new InputBuffer<TInput>[netConfig.MaxClients];
            _events = new ReliableChannel[netConfig.MaxClients];
            for (int i = 0; i < _inputs.Length; i++)
            {
                _inputs[i] = new InputBuffer<TInput>(config.InputBufferCapacity);
                _events[i] = new ReliableChannel(config.EventChannel);
            }
            _connected = new bool[netConfig.MaxClients];
            _frame = new TInput[netConfig.MaxClients];
            _lead = new sbyte[netConfig.MaxClients];
            _scratch = new byte[netConfig.MaxPayloadSize];
            _eventScratch = new byte[config.EventChannel.MaxMessageSize];
            _packets = new SnapshotPacket[netConfig.MaxClients];
            _recipients = new int[netConfig.MaxClients];
            _lanes = new EncodingLane[Math.Min(netConfig.MaxClients, Math.Min(Environment.ProcessorCount, config.SnapshotEncodingLanes))];
            for (int lane = 0; lane < _lanes.Length; lane++)
                _lanes[lane] = new EncodingLane(this, lane);
        }

        public NetHost Host => _host;
        public EcsWorld World => _loop.World;
        public SimulationLoop Loop => _loop;
        public SnapshotWriter Writer => _writer;
        public int Tick => _loop.Tick;
        public int ConnectedCount => _host.ConnectedCount;

        /// <summary>Event messages queued for delivery so far.</summary>
        public long EventsQueued { get; private set; }

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;

        /// <summary>Snapshots the initial world as tick 0.</summary>
        public void Start() => _loop.Start(0);

        public void Update(double now) => _host.Update(now);

        public bool IsClientConnected(int client) => (uint)client < (uint)_connected.Length && _connected[client];

        /// <summary>Ticks the client's newest input arrived before it was needed; negative means it was predicted.</summary>
        public int InputLead(int client) => _lead[client];

        /// <summary>Runs one authoritative tick with the inputs at hand, then sends snapshots and events.</summary>
        public void Step()
        {
            _writer.ClearEvents(_loop.World); // last tick's events have lived their one tick
            int tick = _loop.Tick;
            for (int client = 0; client < _inputs.Length; client++)
            {
                if (!_connected[client])
                {
                    _frame[client] = default;
                    _lead[client] = 0;
                    continue;
                }
                _inputs[client].Resolve(tick, out _frame[client]);
                int lead = _inputs[client].HasConfirmed ? _inputs[client].LatestConfirmedTick - tick : -1;
                _lead[client] = (sbyte)Math.Max(-127, Math.Min(127, lead));
            }
            _loop.Advance();
            if (_loop.Tick % _config.SnapshotInterval == 0)
                SendSnapshots();
            else if (_loop.History.TryGet(_loop.Tick, out WorldState state))
                _writer.BeginTick(state, _loop.Tick); // events need the tick index even without a snapshot
            SendEvents();
        }

        private void SendSnapshots()
        {
            if (!_loop.History.TryGet(_loop.Tick, out WorldState state))
                return;
            _writer.BeginTick(state, _loop.Tick);
            int congestedBudget = Math.Max(64, (int)(_scratch.Length * _config.CongestedBudgetFraction));
            _recipientCount = 0;
            for (int client = 0; client < _connected.Length; client++)
            {
                if (!_connected[client])
                    continue;
                // A congested link gets a smaller packet; the priority accumulator spreads the rest over later ticks.
                _recipients[_recipientCount++] = client;
                ref SnapshotPacket packet = ref _packets[client];
                packet.Data ??= new byte[_scratch.Length];
                packet.Budget = (_host.IsCongested(client) ? congestedBudget : _scratch.Length) - 1;
            }
            _laneCount = Math.Min(_recipientCount, _lanes.Length);
            if (_laneCount == 0)
                return;
            try
            {
                EncodeSnapshots();
                SendEncodedPackets();
            }
            catch
            {
                for (int index = 0; index < _recipientCount; index++)
                    _writer.DiscardStaged(_recipients[index]);
                throw;
            }
        }

        private void EncodeSnapshots()
        {
            if (_laneCount == 1)
            {
                EncodeLane(0);
                return;
            }
            _encodingError = null;
            _remainingLanes = _laneCount;
            for (int lane = 1; lane < _laneCount; lane++)
                ThreadPool.UnsafeQueueUserWorkItem(_lanes[lane], preferLocal: false);
            _lanes[0].Execute();
            lock (_encodingGate)
                while (_remainingLanes != 0)
                    Monitor.Wait(_encodingGate);
            _encodingError?.Throw();
        }

        private void SendEncodedPackets()
        {
            for (int index = 0; index < _recipientCount; index++)
            {
                int client = _recipients[index];
                ref SnapshotPacket packet = ref _packets[client];
                if (packet.Size == 0)
                    continue;
                if (_host.TrySend(client, new ReadOnlySpan<byte>(packet.Data, 0, packet.Size + 1), out ushort sequence))
                    _writer.OnPacketSent(client, sequence);
                else
                    _writer.DiscardStaged(client);
            }
        }

        private void EncodeLane(int lane)
        {
            for (int index = lane; index < _recipientCount; index += _laneCount)
            {
                int client = _recipients[index];
                ref SnapshotPacket packet = ref _packets[client];
                packet.Data[0] = StateSyncPacket.Primary;
                packet.Size = _writer.WriteSnapshot(client, _loop.History, _lead[client], new Span<byte>(packet.Data, 1, packet.Budget));
            }
        }

        private void SendEvents()
        {
            double now = _host.Now;
            for (int client = 0; client < _connected.Length; client++)
            {
                if (!_connected[client])
                    continue;
                EventsQueued += _writer.WriteEvents(client, _loop.World, _events[client], _eventScratch);
                ReliableChannel channel = _events[client];
                double rtt = _host.GetStats(client).SmoothedRtt;
                for (int i = 0; i < _config.MaxEventPacketsPerTick; i++)
                {
                    if (!channel.TryWritePacket(new Span<byte>(_scratch, 1, _scratch.Length - 1), now, rtt, out int written))
                        break;
                    _scratch[0] = StateSyncPacket.Reliable;
                    if (_host.TrySend(client, new ReadOnlySpan<byte>(_scratch, 0, written + 1), out ushort sequence))
                        channel.PacketSent(sequence);
                    else
                        channel.PacketDropped();
                }
            }
        }

        public override void OnConnected(int connection)
        {
            _connected[connection] = true;
            _inputs[connection].Clear();
            _events[connection].Reset();
            _writer.ResetClient(connection);
            ClientConnected?.Invoke(connection);
        }

        public override void OnDisconnected(int connection, DisconnectReason reason)
        {
            _connected[connection] = false;
            ClientDisconnected?.Invoke(connection);
        }

        public override void OnPacket(int connection, ushort sequence, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1 || payload[0] != StateSyncPacket.Primary)
                return;
            // Input packet: newest tick, count, then the inputs oldest first.
            var reader = new BitReader(payload.Slice(1));
            if (!reader.ReadInt32(out int newest) || !reader.ReadBits(7, out uint rawCount))
                return;
            int count = (int)rawCount;
            if (count < 1 || count > 64)
                return;
            INetSerializer<TInput> serializer = NetSerializer<TInput>.Instance;
            InputBuffer<TInput> buffer = _inputs[connection];
            for (int t = newest - count + 1; t <= newest; t++)
            {
                if (!serializer.Read(ref reader, out TInput input, IdentityEntityMap.Instance))
                    return;
                if (t >= 0)
                    buffer.Set(t, input, true);
            }
        }

        public override void OnPacketAcked(int connection, ushort sequence)
        {
            _writer.OnPacketAcked(connection, sequence);
            _events[connection].OnPacketAcked(sequence);
        }

        public override void OnPacketLost(int connection, ushort sequence)
        {
            _writer.OnPacketLost(connection, sequence);
            _events[connection].OnPacketLost(sequence);
        }

        public void Dispose() => _host.Dispose();
    }
}
