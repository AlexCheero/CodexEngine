using System;
using CodexECS;
using CodexNetworking.Serialization;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>Header of a snapshot packet.</summary>
    public struct SnapshotHeader
    {
        public int Tick;
        public int Client;
        /// <summary>Ticks the client's newest input arrived before the server needed it; negative means late.</summary>
        public int InputLead;
        public int GhostCount;
        /// <summary>Bits per ghost id in this packet.</summary>
        public int IdBits;
    }

    /// <summary>
    /// Client side of state replication: applies snapshot packets from <see cref="SnapshotWriter"/>
    /// to the local world, spawning, updating and despawning ghosts through a <see cref="GhostMap"/>.
    /// Out-of-order packets are applied too; the caller decides whether an older tick is still
    /// wanted (see <see cref="SnapshotHeader.Tick"/>). Malformed packets return false and may have
    /// been partially applied.
    /// </summary>
    public sealed class SnapshotReader
    {
        private readonly ReplicationRegistry _registry;
        private readonly ReplicationRegistry.ReceivedHistory _history;
        private readonly bool[] _mask;

        public SnapshotReader(ReplicationRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            if (!registry.IsSealed)
                throw new ArgumentException("registry must be sealed", nameof(registry));
            _history = registry.CreateReceivedHistory();
            _mask = new bool[registry.Count];
        }

        /// <summary>The values received per ghost and component, also the source for interpolation.</summary>
        public ReplicationRegistry.ReceivedHistory History => _history;

        /// <summary>
        /// Ghost component updates that could not be decoded for lack of a baseline since the last
        /// reset. Must stay zero: the server only uses baselines this client acknowledged.
        /// </summary>
        public int MissingBaselines { get; private set; }

        /// <summary>
        /// Deltas in out-of-order snapshots (recorded, not applied) whose baseline was already
        /// forgotten: the ghost was despawned or hidden by a newer snapshot in the meantime. Normal
        /// under reordering, and harmless because the server never uses such a tick as a baseline.
        /// </summary>
        public int StaleDeltas { get; private set; }

        /// <summary>Details of the most recent missing baseline, for diagnostics.</summary>
        public uint LastMissingNetId { get; private set; }
        public int LastMissingTick { get; private set; }
        public int LastMissingBaselineTick { get; private set; }
        public bool LastMissingWasKnown { get; private set; }
        public bool LastMissingWasApply { get; private set; }

        public void ResetStats()
        {
            MissingBaselines = 0;
            StaleDeltas = 0;
        }

        private void NoteMissing(uint netId, int tick, int baselineTick, bool known, bool apply)
        {
            if (!apply)
            {
                StaleDeltas++;
                return;
            }
            MissingBaselines++;
            LastMissingNetId = netId;
            LastMissingTick = tick;
            LastMissingBaselineTick = baselineTick;
            LastMissingWasKnown = known;
            LastMissingWasApply = apply;
        }

        public static bool TryReadHeader(ReadOnlySpan<byte> payload, out SnapshotHeader header)
        {
            header = default;
            var reader = new BitReader(payload);
            return ReadHeader(ref reader, out header);
        }

        private static bool ReadHeader(ref BitReader reader, out SnapshotHeader header)
        {
            header = default;
            if (!reader.ReadInt32(out header.Tick) || !reader.ReadBits(16, out uint client) ||
                !reader.ReadSignedBits(8, out header.InputLead) || !reader.ReadBits(16, out uint count) ||
                !reader.ReadBits(SnapshotWriter.IdBitsBits, out uint idBits) || idBits < 1 || idBits > 32)
                return false;
            header.Client = (int)client;
            header.GhostCount = (int)count;
            header.IdBits = (int)idBits;
            return true;
        }

        /// <summary>
        /// Applies a snapshot to the world. <paramref name="apply"/> false parses and stores received
        /// values without touching entities (used while a client is not yet reconciling).
        /// </summary>
        public bool Apply(EcsWorld world, GhostMap map, ReadOnlySpan<byte> payload, out SnapshotHeader header, bool apply = true)
        {
            var reader = new BitReader(payload);
            if (!ReadHeader(ref reader, out header))
                return false;
            int componentCount = _registry.Count;

            for (int g = 0; g < header.GhostCount; g++)
            {
                if (!reader.ReadBits(header.IdBits, out uint netId) || !reader.ReadBits(2, out uint kind) || netId == 0)
                    return false;
                if (kind == 0)
                {
                    Despawn(world, map, netId, apply);
                    continue;
                }
                int baselineTick = -1;
                if (kind == 2)
                {
                    // The baseline travels as its age in ticks; zero would be the snapshot itself.
                    if (!reader.ReadBits(SnapshotWriter.BaselineAgeBits, out uint age) || age == 0)
                        return false;
                    baselineTick = header.Tick - (int)age;
                }
                for (int i = 0; i < componentCount; i++)
                {
                    if (!reader.ReadBool(out _mask[i]))
                        return false;
                }

                int entityId = -1;
                bool known = map.TryGetEntity(netId, out Entity entity);
                if (apply)
                {
                    if (known)
                    {
                        entityId = (int)(uint)entity.Val;
                    }
                    else
                    {
                        entityId = world.Create();
                        world.Add(entityId, new NetworkId { Value = netId });
                        map.Set(netId, world.GetById(entityId));
                    }
                }
                _registry.EnsureGhostCapacity(_history, netId);

                for (int i = 0; i < componentCount; i++)
                {
                    object store = _history.Stores[i];
                    if (!_mask[i])
                    {
                        // Absent on the server: remove here too. Events are never in snapshots and
                        // live on the client by their own one-frame rule, so they are left alone.
                        if (apply && !_registry.IsEventAt(i))
                            _registry.RemoverAt(i)(world, entityId);
                        continue;
                    }
                    bool isDelta = false;
                    bool changed = true;
                    if (kind == 2)
                    {
                        if (!reader.ReadBool(out isDelta))
                            return false;
                        if (isDelta && !reader.ReadBool(out changed))
                            return false;
                    }
                    if (isDelta && !changed)
                    {
                        // Unchanged since the baseline: nothing on the wire, the value is the baseline's.
                        if (!_registry.CarrierAt(i)(world, entityId, netId, header.Tick, baselineTick, apply, store))
                            NoteMissing(netId, header.Tick, baselineTick, known, apply);
                        continue;
                    }
                    if (!_registry.ApplierAt(i)(ref reader, world, entityId, netId, header.Tick, isDelta, baselineTick, apply, map, store))
                    {
                        if (reader.Overflowed)
                            return false;
                        NoteMissing(netId, header.Tick, baselineTick, known, apply);
                    }
                }
            }
            return !reader.Overflowed;
        }

        private void Despawn(EcsWorld world, GhostMap map, uint netId, bool apply)
        {
            // A despawn in a snapshot that is only recorded (it arrived out of order) changes
            // nothing: either a newer snapshot already removed the ghost, or the ghost came back
            // into view since (see SnapshotWriter.DespawnIrrelevant) and its newer values, the
            // baselines of the deltas to come, must stay.
            if (!apply)
                return;
            _registry.ForgetGhost(_history, netId);
            if (map.TryGetEntity(netId, out Entity entity))
            {
                world.Delete((int)(uint)entity.Val);
                map.Remove(netId);
            }
        }
    }
}
