using System;
using CodexECS;
using CodexNetworking.History;
using CodexNetworking.Serialization;
using CodexNetworking.Transport;
using CodexNetworking.Transport.Reliable;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// One predicted tick on a client: apply the local input to the entities this client owns
    /// (<see cref="NetworkOwner"/> equal to <see cref="StateSyncClient{TInput}.ClientIndex"/>) and
    /// nothing else; everything else is authoritative and arrives in snapshots.
    /// </summary>
    public interface IPredictionStep<TInput> where TInput : unmanaged
    {
        void Step(EcsWorld world, int tick, in TInput input, bool resimulating);
    }

    /// <summary>
    /// Client endpoint of state sync: connects, applies snapshots, predicts the owned entities ahead
    /// of the server and reconciles by rolling back to the snapshot tick, applying it and replaying
    /// local inputs. Event components arrive reliably, are added to their ghost and removed on the
    /// first <see cref="Advance"/> after the frame that received them, so presentation sees each
    /// exactly once. Call <see cref="Update"/> every frame; per simulation tick call
    /// <see cref="SubmitInput"/> then <see cref="Advance"/>; call <see cref="SendInputs"/> every frame
    /// and <see cref="UpdateInterpolation"/> before rendering.
    /// </summary>
    public sealed class StateSyncClient<TInput> : NetListener, IDisposable where TInput : unmanaged
    {
        private sealed class StepAdapter : ISimulationStep
        {
            public StateSyncClient<TInput> Owner;

            public void Step(EcsWorld world, int tick, bool resimulating)
            {
                Owner._inputs.Resolve(tick, out TInput input);
                Owner._step.Step(world, tick, input, resimulating);
            }
        }

        private struct EventRemoval
        {
            public int EntityId;
            public int ComponentIndex;
            public int ReceivedAtTick;
        }

        private readonly NetClient _client;
        private readonly IPredictionStep<TInput> _step;
        private readonly SimulationLoop _loop;
        private readonly GhostMap _map;
        private readonly SnapshotReader _reader;
        private readonly ReplicationRegistry _registry;
        private readonly StateSyncConfig _config;
        private readonly InputBuffer<TInput> _inputs;
        private readonly ReliableChannel _events;
        private readonly byte[] _scratch;
        private readonly byte[] _pendingSnapshot;
        private readonly Action _applyPending;
        private EventRemoval[] _eventRemovals = new EventRemoval[64];
        private int _eventRemovalCount;
        private int _pendingLength;
        private bool _started;
        private int _lastAppliedTick = -1;

        public StateSyncClient(NetConfig netConfig, IUdpSocket socket, IRandomSource random, EcsWorld world, IPredictionStep<TInput> step,
            SimulationRegistry simulation, ReplicationRegistry replication, StateSyncConfig config)
        {
            config.Validate();
            _config = config;
            _step = step ?? throw new ArgumentNullException(nameof(step));
            _registry = replication;
            _client = new NetClient(netConfig, socket, random, this);
            _loop = new SimulationLoop(world, new StepAdapter { Owner = this }, simulation, config.ClientHistoryCapacity);
            _map = new GhostMap(world);
            _reader = new SnapshotReader(replication);
            _inputs = new InputBuffer<TInput>(config.InputBufferCapacity);
            _events = new ReliableChannel(config.EventChannel);
            _scratch = new byte[netConfig.MaxPayloadSize];
            _pendingSnapshot = new byte[netConfig.MaxMessageSize];
            _applyPending = ApplyPendingSnapshot;
        }

        public NetClient Client => _client;
        public EcsWorld World => _loop.World;
        public SimulationLoop Loop => _loop;
        public GhostMap Ghosts => _map;
        public SnapshotReader Reader => _reader;

        /// <summary>True once the first snapshot arrived and the local simulation runs.</summary>
        public bool IsReady => _started;

        /// <summary>Client slot on the server, or -1 before the first snapshot.</summary>
        public int ClientIndex { get; private set; } = -1;

        public int Tick => _loop.Tick;

        /// <summary>Server tick of the newest applied snapshot.</summary>
        public int LastAppliedTick => _lastAppliedTick;

        /// <summary>From the newest snapshot: how early this client's inputs arrive at the server, in ticks.</summary>
        public int LastInputLead { get; private set; }

        /// <summary>
        /// Rate for the game's <see cref="TickClock"/>: above 1 when inputs arrive late, below 1 when
        /// they arrive needlessly early, 1 inside the target band.
        /// </summary>
        public double RecommendedRateScale { get; private set; } = 1.0;

        /// <summary>
        /// Raised with the server tick right before a snapshot corrects the predicted world (rollback
        /// or hard snap) and right after it has been re-predicted. Presentation smoothing hooks in
        /// here: capture the predicted transforms before, measure the error after, and ease the
        /// visible offset out over the next frames instead of letting the view jump.
        /// </summary>
        public event Action<int> BeforeCorrection;
        public event Action<int> AfterCorrection;

        public int Reconciliations { get; private set; }

        /// <summary>Snapshots applied without rollback because their tick left the history.</summary>
        public int HardSnaps { get; private set; }

        /// <summary>Event components received and applied.</summary>
        public long EventsReceived { get; private set; }

        /// <summary>Events dropped because their ghost is not on this client (see <see cref="StateSyncConfig.EventsCreateGhosts"/>).</summary>
        public long EventsDropped { get; private set; }

        public void Connect(in Address server, double now) => _client.Connect(server, now);

        public void Update(double now) => _client.Update(now);

        /// <summary>Records the local input for the tick about to be simulated.</summary>
        public void SubmitInput(in TInput input) => _inputs.Set(_loop.Tick, input, true);

        /// <summary>Simulates one predicted tick. No-op before the first snapshot.</summary>
        public void Advance()
        {
            if (!_started)
                return;
            RemoveExpiredEvents();
            DrainEvents(); // events that arrived before the first snapshot, or between frames
            _loop.Advance();
        }

        /// <summary>Sends the newest local inputs to the server.</summary>
        public void SendInputs()
        {
            if (!_started || !_client.IsConnected || !_inputs.HasConfirmed)
                return;
            int newest = _inputs.LatestConfirmedTick;
            int count = 0;
            for (int t = newest; t > newest - _config.InputRedundancy && t >= 0 && _inputs.IsConfirmed(t); t--)
                count++;
            if (count == 0)
                return;
            _scratch[0] = StateSyncPacket.Primary;
            var writer = new BitWriter(new Span<byte>(_scratch, 1, _scratch.Length - 1));
            writer.WriteInt32(newest);
            writer.WriteBits((uint)count, 7);
            INetSerializer<TInput> serializer = NetSerializer<TInput>.Instance;
            for (int t = newest - count + 1; t <= newest; t++)
            {
                _inputs.TryGet(t, out TInput input, out _);
                serializer.Write(ref writer, input, IdentityEntityMap.Instance);
            }
            if (!writer.Overflowed)
                _client.TrySend(new ReadOnlySpan<byte>(_scratch, 0, writer.Flush() + 1), out _);
        }

        /// <summary>Render time in server ticks for <see cref="UpdateInterpolation"/>: the applied tick minus the delay plus the frame's fraction.</summary>
        public double RenderTick(double alpha) => _lastAppliedTick - _config.InterpolationDelayTicks + alpha;

        /// <summary>
        /// The render time of the last <see cref="UpdateInterpolation"/>: where in server time the
        /// other ghosts stood in the frame the player is looking at. Stamp inputs with
        /// <see cref="ViewLag.Encode"/> of <see cref="Tick"/> and this so the server can judge shots
        /// against that picture (<see cref="LagCompensator"/>).
        /// </summary>
        public double LastRenderTick { get; private set; }

        /// <summary>
        /// Writes <see cref="Interpolated{T}"/> on every ghost for components flagged Interpolated,
        /// from the received values around <paramref name="renderTick"/>. Call once per frame before rendering.
        /// </summary>
        public void UpdateInterpolation(double renderTick)
        {
            if (!_started)
                return;
            LastRenderTick = renderTick;
            ReplicationRegistry.ReceivedHistory history = _reader.History;
            for (uint netId = 1; netId <= _map.MaxId; netId++)
            {
                if (!_map.TryGetEntity(netId, out Entity entity))
                    continue;
                int entityId = (int)(uint)entity.Val;
                for (int i = 0; i < _registry.Count; i++)
                {
                    if (_registry.IsInterpolatedAt(i))
                        _registry.InterpolatorAt(i)(_loop.World, entityId, netId, renderTick, history.Stores[i]);
                }
            }
        }

        public override void OnPacket(int connection, ushort sequence, ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 1)
                return;
            if (payload[0] == StateSyncPacket.Reliable)
            {
                ReceiveEvents(payload.Slice(1));
                return;
            }
            if (payload[0] != StateSyncPacket.Primary)
                return;
            ReadOnlySpan<byte> snapshot = payload.Slice(1);
            if (!SnapshotReader.TryReadHeader(snapshot, out SnapshotHeader header))
                return;
            if (snapshot.Length > _pendingSnapshot.Length)
                return;
            if (header.Tick <= _lastAppliedTick)
            {
                // Out of order: too old to apply, but the server may already count it as acknowledged
                // and use it as a delta baseline, so its values must still be recorded.
                _reader.Apply(_loop.World, _map, snapshot, out _, apply: false);
                return;
            }
            snapshot.CopyTo(_pendingSnapshot);
            _pendingLength = snapshot.Length;
            ClientIndex = header.Client;
            LastInputLead = header.InputLead;
            UpdateRateScale(header.InputLead);

            bool correcting = _started; // the first snapshot creates the world rather than correcting it
            if (correcting)
                BeforeCorrection?.Invoke(header.Tick);
            if (!_started || header.Tick >= _loop.Tick || !_loop.History.Contains(header.Tick))
            {
                // First snapshot, or the server got ahead of our prediction, or the tick left the
                // history: apply directly and restart the prediction from the server tick.
                ApplyPendingSnapshot();
                _loop.Start(header.Tick);
                if (_started)
                    HardSnaps++;
                _started = true;
                for (int i = 0; i < _config.InitialLead; i++)
                    _loop.Advance();
            }
            else
            {
                _loop.Rollback(header.Tick, _applyPending);
                Reconciliations++;
            }
            _lastAppliedTick = header.Tick;
            if (correcting)
                AfterCorrection?.Invoke(header.Tick);
        }

        private void ReceiveEvents(ReadOnlySpan<byte> blob)
        {
            _events.OnPacket(blob);
            // Before the first snapshot there is no world to apply to: the channel holds them in order.
            if (_started)
                DrainEvents();
        }

        // Applies queued events in order, at most one per entity and component per frame: a second
        // event of the same type for the same entity waits in the channel so presentation sees both.
        private void DrainEvents()
        {
            while (_events.TryPeek(out ReadOnlySpan<byte> message))
            {
                var reader = new BitReader(message);
                if (!reader.ReadUInt32(out uint netId) || !reader.ReadBits(8, out uint rawIndex) || !reader.ReadInt32(out _))
                {
                    _events.Dequeue();
                    continue;
                }
                int index = (int)rawIndex;
                if (index >= _registry.Count || !_registry.IsEventAt(index))
                {
                    _events.Dequeue();
                    continue;
                }
                int entityId;
                if (_map.TryGetEntity(netId, out Entity entity))
                {
                    entityId = (int)(uint)entity.Val;
                    if (_registry.PresenceAt(index)(_loop.World, entityId))
                        return; // this frame's event of that type is still visible: deliver the next one next frame
                }
                else if (!_config.EventsCreateGhosts)
                {
                    _events.Dequeue(); // a ghost this client must not know: the event goes with it
                    EventsDropped++;
                    continue;
                }
                else
                {
                    // The event beat the spawn: create the shell; the snapshot fills it in.
                    entityId = _loop.World.Create();
                    _loop.World.Add(entityId, new NetworkId { Value = netId });
                    _map.Set(netId, _loop.World.GetById(entityId));
                }
                _events.Dequeue();
                if (!_registry.EventApplierAt(index)(ref reader, _loop.World, entityId, _map))
                    continue;
                if (_eventRemovalCount == _eventRemovals.Length)
                    Array.Resize(ref _eventRemovals, _eventRemovals.Length * 2);
                _eventRemovals[_eventRemovalCount++] = new EventRemoval { EntityId = entityId, ComponentIndex = index, ReceivedAtTick = _loop.Tick };
                EventsReceived++;
            }
        }

        // Events received before the previous Advance have been visible for a whole frame: remove them.
        private void RemoveExpiredEvents()
        {
            int kept = 0;
            for (int i = 0; i < _eventRemovalCount; i++)
            {
                EventRemoval removal = _eventRemovals[i];
                if (removal.ReceivedAtTick >= _loop.Tick)
                {
                    _eventRemovals[kept++] = removal;
                    continue;
                }
                if (_loop.World.IsIdValid(removal.EntityId))
                    _registry.RemoverAt(removal.ComponentIndex)(_loop.World, removal.EntityId);
            }
            _eventRemovalCount = kept;
        }

        private void ApplyPendingSnapshot()
        {
            _reader.Apply(_loop.World, _map, new ReadOnlySpan<byte>(_pendingSnapshot, 0, _pendingLength), out _);
        }

        private void UpdateRateScale(int lead)
        {
            int target = _config.TargetInputLead;
            if (lead < target - 1)
                RecommendedRateScale = 1.05;
            else if (lead > target + 2)
                RecommendedRateScale = 0.95;
            else
                RecommendedRateScale = 1.0;
        }

        public void Dispose() => _client.Dispose();
    }
}
