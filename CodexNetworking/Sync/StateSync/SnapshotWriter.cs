using System;
using CodexECS;
using CodexNetworking.History;
using CodexNetworking.Serialization;
using CodexNetworking.Transport;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>Decides which ghosts a client receives. Default: all of them.</summary>
    public interface IRelevance
    {
        bool IsRelevant(int client, WorldState state, int slot, uint netId);
    }

    /// <summary>A relevance that indexes the tick's state once before the per-ghost queries (see <see cref="GridRelevance{TPosition}"/>).</summary>
    public interface IPreparedRelevance : IRelevance
    {
        void Prepare(WorldState state);
    }

    /// <summary>
    /// Server side of state replication. Every tick, <see cref="BeginTick"/> indexes the ghosts of
    /// the current snapshot; then <see cref="WriteSnapshot"/> builds one packet per client: despawns
    /// of ghosts the client knows, then every relevant ghost as a delta against the newest tick the
    /// client acknowledged for that ghost, or in full when there is no usable baseline. Records
    /// what each packet carried so transport acks move the per-ghost baselines forward. Packets
    /// are filled until the buffer is full; what did not fit goes next time.
    /// </summary>
    public sealed class SnapshotWriter
    {
        private struct SentRecord
        {
            public int Tick;
            public int Start;
            public int Count;
        }

        private sealed class ClientState
        {
            public Candidate[] Candidates = new Candidate[256];
            // Per net id: newest tick acked for this ghost, or -1 when the client does not know it.
            public int[] LastAcked = Array.Empty<int>();
            public bool[] DespawnPending = Array.Empty<bool>();
            // Per net id: tick of the last despawn or hide record sent, or -1. Snapshots from that
            // tick or earlier are no baseline any more: the client drops what they carried.
            public int[] GoneAt = Array.Empty<int>();
            public readonly SequenceBuffer<SentRecord> Sent;
            public readonly uint[] SentIds;
            public readonly int SentIdMask;
            public int SentIdCursor;
            public int StagedStart;
            public int StagedCount;
            public bool StagedPending;
            public int StagedTick;

            public ClientState(int packetHistory, int maxGhostsPerPacket)
            {
                Sent = new SequenceBuffer<SentRecord>(packetHistory);
                int capacity = 1;
                while (capacity < packetHistory * maxGhostsPerPacket)
                    capacity <<= 1;
                SentIds = new uint[capacity];
                SentIdMask = capacity - 1;
            }

            public void Ensure(uint netId)
            {
                if (netId < LastAcked.Length)
                    return;
                int old = LastAcked.Length;
                int size = Math.Max((int)netId + 1, Math.Max(256, old * 2));
                Array.Resize(ref LastAcked, size);
                Array.Resize(ref DespawnPending, size);
                Array.Resize(ref GoneAt, size);
                for (int i = old; i < size; i++)
                {
                    LastAcked[i] = -1;
                    GoneAt[i] = -1;
                }
            }
        }

        private struct Candidate
        {
            public uint NetId;
            public int Priority;
        }

        // Highest priority first; ties by id so the order is deterministic.
        private static bool Before(in Candidate a, in Candidate b) =>
            a.Priority != b.Priority ? a.Priority > b.Priority : a.NetId < b.NetId;

        // In-place quicksort with an insertion sort for short runs. Array.Sort with a comparer
        // allocates a delegate per call on .NET, which would be one allocation per client per tick.
        private static void SortCandidates(Candidate[] items, int low, int high)
        {
            while (high - low > 12)
            {
                // Median of three as the pivot, moved to the middle.
                int middle = low + ((high - low) >> 1);
                if (Before(items[middle], items[low]))
                    Swap(items, low, middle);
                if (Before(items[high], items[low]))
                    Swap(items, low, high);
                if (Before(items[high], items[middle]))
                    Swap(items, middle, high);
                Candidate pivot = items[middle];
                int i = low;
                int j = high;
                while (i <= j)
                {
                    while (Before(items[i], pivot))
                        i++;
                    while (Before(pivot, items[j]))
                        j--;
                    if (i <= j)
                    {
                        Swap(items, i, j);
                        i++;
                        j--;
                    }
                }
                // Recurse into the smaller part, loop on the larger: the stack stays logarithmic.
                if (j - low < high - i)
                {
                    SortCandidates(items, low, j);
                    low = i;
                }
                else
                {
                    SortCandidates(items, i, high);
                    high = j;
                }
            }
            for (int i = low + 1; i <= high; i++)
            {
                Candidate item = items[i];
                int j = i - 1;
                while (j >= low && Before(item, items[j]))
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

        private static void Swap(Candidate[] items, int a, int b)
        {
            Candidate t = items[a];
            items[a] = items[b];
            items[b] = t;
        }

        private const uint DespawnFlag = 0x80000000u;
        public const int MaxGhostsPerPacket = 1024;
        /// <summary>tick(32) client(16) inputLead(8) ghostCount(16) idBits(6).</summary>
        public const int HeaderBits = 32 + 16 + 8 + 16 + IdBitsBits;

        /// <summary>Width of the header field that says how many bits a ghost id takes in this packet.</summary>
        public const int IdBitsBits = 6;

        /// <summary>
        /// A delta names its baseline by age: how many ticks before the snapshot's own tick. The
        /// writer never uses a baseline as old as the received-history window, so the age fits.
        /// </summary>
        public static readonly int BaselineAgeBits = BitWriter.BitsRequired(ReplicationRegistry.ReceivedHistoryLength - 1);

        private readonly ReplicationRegistry _registry;
        private readonly SimulationRegistry _simulation;
        private readonly ClientState[] _clients;
        private readonly PriorityAccumulator _priority;
        private readonly int _networkIdComponent;
        private readonly int _ownerComponent;
        private int[] _slotByNetId = Array.Empty<int>();
        private int[] _slotStamp = Array.Empty<int>();
        private uint _maxNetId;
        // Highest id ever indexed: every id a packet can carry (live or despawned) is at most this.
        private uint _highestNetId;
        private int _idBits = 1;
        private int _tick = -1;
        private WorldState _state;

        /// <summary>Filters ghosts per client; null sends everything.</summary>
        public IRelevance Relevance;

        /// <summary>Per-ghost priority growth; null means 1 per tick for every ghost.</summary>
        public IPriorityWeight Weight;

        /// <summary>
        /// Send ghosts in order of accumulated priority (default) so a packet budget starves nobody,
        /// or in id order when false.
        /// </summary>
        public bool UsePriority = true;

        /// <summary>
        /// What happens to a ghost a client knows once <see cref="Relevance"/> stops including it.
        /// False (default): nothing is sent, the client keeps the ghost as it last saw it and the
        /// game decides when to remove it; right for distance culling, where a ghost at the edge
        /// of the radius should not flicker. True: the client is told to remove it, with the same
        /// record as a real despawn, and receives it again in full when it becomes relevant; right
        /// when relevance means "must not know", as with line-of-sight culling against wallhacks
        /// or a card that is turned face down. Give such a relevance some hysteresis, because every
        /// re-entry costs a full ghost.
        /// </summary>
        public bool DespawnIrrelevant;

        /// <summary>Hide records written since construction (diagnostics).</summary>
        public long HidesSent => System.Threading.Interlocked.Read(ref _hidesSent);
        private long _hidesSent;

        public SnapshotWriter(ReplicationRegistry registry, SimulationRegistry simulation, int maxClients, int packetHistory = 256)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (!registry.IsSealed)
                throw new ArgumentException("registry must be sealed", nameof(registry));
            _simulation = simulation;
            _clients = new ClientState[maxClients];
            for (int i = 0; i < maxClients; i++)
                _clients[i] = new ClientState(packetHistory, 64);
            _priority = new PriorityAccumulator(maxClients);
            _networkIdComponent = ComponentMeta<NetworkId>.Id;
            _ownerComponent = simulation.Contains(ComponentMeta<NetworkOwner>.Id) ? ComponentMeta<NetworkOwner>.Id : -1;
        }

        public int Tick => _tick;

        /// <summary>Indexes the ghosts of the state for this tick. Call once per tick before writing to clients.</summary>
        public void BeginTick(WorldState state, int tick)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _tick = tick;
            _maxNetId = 0;
            if (Relevance is IPreparedRelevance prepared)
                prepared.Prepare(state);
            ReadOnlySpan<ulong> words = state.MembershipWords(_networkIdComponent);
            for (int page = 0; page < words.Length; page++)
            {
                ulong bits = words[page];
                if (bits == 0)
                    continue;
                ReadOnlySpan<NetworkId> ids = state.Payload<NetworkId>(_networkIdComponent, page);
                while (bits != 0)
                {
                    int bit = TrailingZeros(bits);
                    bits &= bits - 1;
                    uint netId = ids[bit].Value;
                    if (netId == 0)
                        continue;
                    EnsureSlotMap(netId);
                    _slotByNetId[netId] = (page << 6) | bit;
                    _slotStamp[netId] = tick;
                    if (netId > _maxNetId)
                        _maxNetId = netId;
                }
            }
            if (_maxNetId > _highestNetId)
            {
                _highestNetId = _maxNetId;
                _idBits = BitWriter.BitsRequired(_highestNetId);
            }
        }

        /// <summary>Forgets everything sent to a client slot; call when a client connects.</summary>
        public void ResetClient(int client)
        {
            ClientState state = _clients[client];
            for (int i = 0; i < state.LastAcked.Length; i++)
            {
                state.LastAcked[i] = -1;
                state.DespawnPending[i] = false;
                state.GoneAt[i] = -1;
            }
            state.Sent.Clear();
            state.StagedPending = false;
            _priority.ResetClient(client);
        }

        /// <summary>Accumulated send priority of a ghost for a client (diagnostics).</summary>
        public int PriorityOf(int client, uint netId) => _priority.Get(client, netId);

        /// <summary>
        /// Writes a snapshot for the client into <paramref name="buffer"/>. Returns the byte count, or
        /// 0 when nothing needs sending. Follow with <see cref="OnPacketSent"/> or <see cref="DiscardStaged"/>.
        /// </summary>
        public int WriteSnapshot(int client, StateHistory history, sbyte inputLead, Span<byte> buffer)
        {
            if (_state == null)
                throw new InvalidOperationException("BeginTick first");
            ClientState cs = _clients[client];
            if (cs.StagedPending)
                throw new InvalidOperationException("OnPacketSent or DiscardStaged must follow WriteSnapshot");
            cs.Ensure(_maxNetId);
            INetEntityMap map = IdentityMapForWriter.Instance;

            var writer = new BitWriter(buffer);
            writer.WriteInt32(_tick);
            writer.WriteBits((uint)client, 16);
            writer.WriteSignedBits(inputLead, 8);
            int countPosition = writer.BitsWritten;
            writer.WriteBits(0, 16); // ghost count, patched at the end
            // Ids are dense and small: a packet spends only as many bits per id as the highest one needs.
            writer.WriteBits((uint)_idBits, IdBitsBits);
            int count = 0;
            cs.StagedStart = cs.SentIdCursor;
            cs.StagedCount = 0;
            cs.StagedTick = _tick;
            cs.StagedPending = true;

            // 1. Despawns: ghosts the client knows that are gone.
            for (uint netId = 1; netId < cs.LastAcked.Length && count < MaxGhostsPerPacket; netId++)
            {
                if (cs.LastAcked[netId] < 0)
                    continue;
                bool alive = netId < _slotStamp.Length && _slotStamp[netId] == _tick;
                if (alive)
                    continue;
                cs.DespawnPending[netId] = true;
                int before = writer.BitsWritten;
                writer.WriteBits(netId, _idBits);
                writer.WriteBits(0, 2);
                if (writer.Overflowed)
                {
                    writer.Rewind(before);
                    break;
                }
                cs.GoneAt[netId] = _tick;
                Stage(cs, netId | DespawnFlag);
                count++;
            }

            // 2. Live ghosts: collect the relevant ones, order by priority, write until the budget is spent.
            int candidateCount = 0;
            for (uint netId = 1; netId <= _maxNetId; netId++)
            {
                if (_slotStamp[netId] != _tick)
                    continue;
                int slot = _slotByNetId[netId];
                if (Relevance != null && !Relevance.IsRelevant(client, _state, slot, netId))
                {
                    _priority.Reset(client, netId);
                    // Hide a ghost the client holds; repeated every snapshot until one is acknowledged.
                    if (DespawnIrrelevant && cs.LastAcked[netId] >= 0 && count < MaxGhostsPerPacket)
                    {
                        int before = writer.BitsWritten;
                        writer.WriteBits(netId, _idBits);
                        writer.WriteBits(0, 2);
                        if (writer.Overflowed)
                        {
                            writer.Rewind(before); // no room: the next snapshot tries again
                        }
                        else
                        {
                            cs.DespawnPending[netId] = true;
                            cs.GoneAt[netId] = _tick;
                            Stage(cs, netId | DespawnFlag);
                            System.Threading.Interlocked.Increment(ref _hidesSent);
                            count++;
                        }
                    }
                    continue;
                }
                if (cs.DespawnPending[netId])
                {
                    // Back in view before the hide was acknowledged: whichever of the two the client
                    // got, a full ghost is right, and only snapshots from now on count as baselines.
                    cs.DespawnPending[netId] = false;
                    cs.LastAcked[netId] = -1;
                }
                if (candidateCount == cs.Candidates.Length)
                    Array.Resize(ref cs.Candidates, cs.Candidates.Length * 2);
                int priority = UsePriority ? _priority.Accumulate(client, netId, Weight == null ? 1 : Weight.WeightOf(_state, slot, netId)) : 0;
                cs.Candidates[candidateCount++] = new Candidate { NetId = netId, Priority = priority };
            }
            if (UsePriority && candidateCount > 1)
                SortCandidates(cs.Candidates, 0, candidateCount - 1);

            for (int c = 0; c < candidateCount && count < MaxGhostsPerPacket; c++)
            {
                uint netId = cs.Candidates[c].NetId;
                int slot = _slotByNetId[netId];
                int owner = OwnerOf(slot);
                int before = writer.BitsWritten;
                if (!WriteGhost(ref writer, cs, netId, slot, owner == client, history, map) || writer.Overflowed)
                {
                    writer.Rewind(before);
                    break;
                }
                Stage(cs, netId);
                _priority.Reset(client, netId);
                count++;
            }

            if (count == 0)
            {
                cs.StagedPending = false;
                return 0;
            }
            int end = writer.BitsWritten;
            writer.Seek(countPosition);
            writer.WriteBits((uint)count, 16);
            writer.Seek(end);
            return writer.Flush();
        }

        /// <summary>Binds the last written snapshot to the transport sequence it was sent with.</summary>
        public void OnPacketSent(int client, ushort sequence)
        {
            ClientState cs = _clients[client];
            if (!cs.StagedPending)
                return;
            cs.StagedPending = false;
            ref SentRecord record = ref cs.Sent.Insert(sequence);
            record.Tick = cs.StagedTick;
            record.Start = cs.StagedStart;
            record.Count = cs.StagedCount;
        }

        public void DiscardStaged(int client)
        {
            ClientState cs = _clients[client];
            if (!cs.StagedPending)
                return;
            cs.StagedPending = false;
            cs.SentIdCursor = cs.StagedStart;
        }

        /// <summary>Route the transport's acked callback here: moves per-ghost baselines forward.</summary>
        public void OnPacketAcked(int client, ushort sequence)
        {
            ClientState cs = _clients[client];
            if (!cs.Sent.Exists(sequence))
                return;
            ref SentRecord record = ref cs.Sent.Get(sequence);
            for (int i = 0; i < record.Count; i++)
            {
                uint id = cs.SentIds[(record.Start + i) & cs.SentIdMask];
                if ((id & DespawnFlag) != 0)
                {
                    uint netId = id & ~DespawnFlag;
                    cs.Ensure(netId);
                    // A hide that the ghost outlived (it came back and newer data was acknowledged
                    // since) says nothing about what the client holds now.
                    if (cs.LastAcked[netId] <= record.Tick)
                    {
                        cs.LastAcked[netId] = -1;
                        cs.DespawnPending[netId] = false;
                    }
                }
                else
                {
                    cs.Ensure(id);
                    if (!cs.DespawnPending[id] && record.Tick > cs.LastAcked[id] && record.Tick > cs.GoneAt[id])
                        cs.LastAcked[id] = record.Tick;
                }
            }
            cs.Sent.Remove(sequence);
        }

        /// <summary>Route the transport's lost callback here.</summary>
        public void OnPacketLost(int client, ushort sequence) => _clients[client].Sent.Remove(sequence);

        /// <summary>Newest tick the client acknowledged for a ghost, or -1.</summary>
        public int LastAckedTick(int client, uint netId)
        {
            ClientState cs = _clients[client];
            return netId < cs.LastAcked.Length ? cs.LastAcked[netId] : -1;
        }

        private bool WriteGhost(ref BitWriter writer, ClientState cs, uint netId, int slot, bool isOwner, StateHistory history, INetEntityMap map)
        {
            int baselineTick = cs.LastAcked[netId];
            WorldState baseline = null;
            int baselineSlot = -1;
            if (baselineTick >= 0 && !cs.DespawnPending[netId] && _tick - baselineTick < ReplicationRegistry.ReceivedHistoryLength &&
                history.TryGet(baselineTick, out baseline))
            {
                // Same ghost only if the slot held the same entity then (version unchanged).
                if (slot < baseline.SlotCount && baseline.Versions[slot] == _state.Versions[slot] &&
                    (baseline.AliveWords[slot >> 6] & (1UL << (slot & 63))) != 0 &&
                    (baseline.MembershipWords(_networkIdComponent)[slot >> 6] & (1UL << (slot & 63))) != 0 &&
                    baseline.Payload<NetworkId>(_networkIdComponent, slot >> 6)[slot & 63].Value == netId)
                    baselineSlot = slot;
                else
                    baseline = null;
            }
            else
            {
                baseline = null;
            }

            writer.WriteBits(netId, _idBits);
            writer.WriteBits(baseline == null ? 1u : 2u, 2);
            if (baseline != null)
                writer.WriteBits((uint)(_tick - baselineTick), BaselineAgeBits);
            if (writer.Overflowed)
                return false;

            int componentCount = _registry.Count;
            ulong currentBit = 1UL << (slot & 63);
            for (int i = 0; i < componentCount; i++)
                writer.WriteBool(IsSent(i, slot, currentBit, isOwner));
            if (writer.Overflowed)
                return false;
            for (int i = 0; i < componentCount; i++)
            {
                int componentId = _registry.ComponentIdAt(i);
                if (!IsSent(i, slot, currentBit, isOwner))
                    continue;
                bool inBaseline = baseline != null &&
                                  (baseline.MembershipWords(componentId)[baselineSlot >> 6] & (1UL << (baselineSlot & 63))) != 0;
                if (baseline != null)
                    writer.WriteBool(inBaseline);
                if (inBaseline)
                {
                    int changedPosition = writer.BitsWritten;
                    writer.WriteBool(true);
                    if (!_registry.DeltaWriterAt(i)(ref writer, baseline, baselineSlot, _state, slot, map))
                    {
                        // Unchanged: patch the bit without touching the overflow flag.
                        writer.Seek(changedPosition);
                        writer.WriteBool(false);
                    }
                }
                else
                {
                    _registry.FullWriterAt(i)(ref writer, _state, slot, map);
                }
                if (writer.Overflowed)
                    return false;
            }
            return !writer.Overflowed;
        }

        // Component i of the ghost at `slot` travels in this client's snapshot: present, not server-only,
        // owner-only only to the owner, and never an event (events go through the reliable channel).
        private bool IsSent(int index, int slot, ulong currentBit, bool isOwner)
        {
            ReplicationFlags flags = _registry.FlagsAt(index);
            if ((flags & (ReplicationFlags.ServerOnly | ReplicationFlags.Event)) != 0)
                return false;
            if ((flags & ReplicationFlags.OwnerOnly) != 0 && !isOwner)
                return false;
            return (_state.MembershipWords(_registry.ComponentIdAt(index))[slot >> 6] & currentBit) != 0;
        }

        /// <summary>
        /// Queues one reliable message per live entity owning an Event component (relevance applied)
        /// into the client's channel. Call once per tick per client after <see cref="BeginTick"/>.
        /// </summary>
        public int WriteEvents(int client, EcsWorld world, Transport.Reliable.ReliableChannel channel, byte[] scratch)
        {
            int sent = 0;
            for (int i = 0; i < _registry.Count; i++)
            {
                if (_registry.IsEventAt(i))
                    sent += _registry.EventWriterAt(i)(world, i, _tick, Relevance, client, _state, channel, scratch, IdentityMapForWriter.Instance);
            }
            return sent;
        }

        /// <summary>Removes every Event component from the world: call at the start of each tick.</summary>
        public void ClearEvents(EcsWorld world)
        {
            for (int i = 0; i < _registry.Count; i++)
            {
                if (_registry.IsEventAt(i))
                    _registry.EventClearerAt(i)(world);
            }
        }

        private int OwnerOf(int slot)
        {
            if (_ownerComponent < 0)
                return -1;
            if ((_state.MembershipWords(_ownerComponent)[slot >> 6] & (1UL << (slot & 63))) == 0)
                return -1;
            return _state.Payload<NetworkOwner>(_ownerComponent, slot >> 6)[slot & 63].Client;
        }

        private static void Stage(ClientState cs, uint id)
        {
            cs.SentIds[cs.SentIdCursor & cs.SentIdMask] = id;
            cs.SentIdCursor = (cs.SentIdCursor + 1) & cs.SentIdMask;
            cs.StagedCount++;
        }

        private void EnsureSlotMap(uint netId)
        {
            if (netId < _slotByNetId.Length)
                return;
            int old = _slotByNetId.Length;
            int size = Math.Max((int)netId + 1, Math.Max(256, old * 2));
            Array.Resize(ref _slotByNetId, size);
            Array.Resize(ref _slotStamp, size);
            for (int i = old; i < size; i++)
                _slotStamp[i] = -1;
        }

        private static int TrailingZeros(ulong value)
        {
            int count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }

        /// <summary>
        /// Server-side entity map for serializers: entity handles inside snapshots are written as the
        /// ghost's network id, which the writer resolves from the state it is encoding.
        /// </summary>
        private sealed class IdentityMapForWriter : INetEntityMap
        {
            public static readonly IdentityMapForWriter Instance = new IdentityMapForWriter();

            // Hand-written serializers on the server should use a GhostTable; the default blit path
            // never calls this. Returning false writes entity fields as absent.
            public bool TryToNet(Entity entity, out uint netId)
            {
                netId = 0;
                return false;
            }

            public bool TryToLocal(uint netId, out Entity entity)
            {
                entity = default;
                return false;
            }
        }
    }
}
