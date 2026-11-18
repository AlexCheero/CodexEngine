using System;
using System.Collections.Generic;
using System.Reflection;
using CodexECS;
using CodexNetworking.History;
using CodexNetworking.Serialization;
using CodexNetworking.Transport.Reliable;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// The replicated components, in a stable order shared by server and clients (sorted by type
    /// name, like <see cref="SimulationRegistry"/>). Every replicated component must also be in the
    /// simulation registry, because the writer reads values from world snapshots. Each entry carries
    /// the replication flags from <see cref="ReplicatedAttribute"/> (or the flags given at
    /// registration) and type-specific delegates for writing from a snapshot and applying to a world,
    /// created without reflection at the generic registration call.
    /// </summary>
    public sealed class ReplicationRegistry
    {
        /// <summary>Writes the full value of the component at a slot of a captured state.</summary>
        public delegate void FullWriter(ref BitWriter writer, WorldState state, int slot, INetEntityMap map);

        /// <summary>Writes the value at a slot of <paramref name="current"/> as a delta against a slot of <paramref name="baseline"/>; returns false when unchanged (nothing written).</summary>
        public delegate bool DeltaWriter(ref BitWriter writer, WorldState baseline, int baselineSlot, WorldState current, int slot, INetEntityMap map);

        /// <summary>
        /// Reads a component value for a ghost: full, or delta against the value this client
        /// received for that ghost at <paramref name="baselineTick"/> (looked up in <paramref name="store"/>,
        /// the component's slot of a <see cref="ReceivedHistory"/>). Stores the result under
        /// <paramref name="tick"/> and writes it into the entity when <paramref name="apply"/> is set.
        /// Returns false when the stream is malformed or the baseline is missing (the value is then
        /// parsed but neither stored nor applied).
        /// </summary>
        public delegate bool Applier(ref BitReader reader, EcsWorld world, int entityId, uint netId, int tick, bool isDelta, int baselineTick, bool apply, INetEntityMap map, object store);

        /// <summary>Removes the component from an entity if present.</summary>
        public delegate void Remover(EcsWorld world, int entityId);

        /// <summary>Whether the entity currently owns the component.</summary>
        public delegate bool Presence(EcsWorld world, int entityId);

        /// <summary>
        /// Component unchanged since <paramref name="baselineTick"/>: re-stores the baseline value under
        /// <paramref name="tick"/> and applies it. False when the baseline is missing.
        /// </summary>
        public delegate bool Carrier(EcsWorld world, int entityId, uint netId, int tick, int baselineTick, bool apply, object store);

        /// <summary>
        /// Writes <c>Interpolated&lt;T&gt;</c> on the entity from the received values around
        /// <paramref name="renderTick"/>. Returns false when nothing was received for the ghost.
        /// </summary>
        public delegate bool Interpolator(EcsWorld world, int entityId, uint netId, double renderTick, object store);

        /// <summary>Server: queues one reliable event message per entity that currently owns the Event component.</summary>
        public delegate int EventWriter(EcsWorld world, int componentIndex, int tick, IRelevance relevance, int client, WorldState state, ReliableChannel channel, byte[] scratch, INetEntityMap map);

        /// <summary>Client: reads an event value and adds it to the entity for one tick.</summary>
        public delegate bool EventApplier(ref BitReader reader, EcsWorld world, int entityId, INetEntityMap map);

        /// <summary>Removes the Event component from every entity (end of its one-tick life).</summary>
        public delegate void EventClearer(EcsWorld world);

        /// <summary>
        /// A client's record of the values it received per ghost, component and tick: the baselines
        /// for incoming deltas. One per <see cref="SnapshotReader"/>; create with
        /// <see cref="CreateReceivedHistory"/>. The registry itself holds no per-client state, so one
        /// registry serves a server and any number of clients in the same process.
        /// </summary>
        public sealed class ReceivedHistory
        {
            internal readonly object[] Stores;

            internal ReceivedHistory(object[] stores) => Stores = stores;
        }

        /// <summary>
        /// Received values kept per ghost and component, by tick. The server never uses a baseline
        /// older than this many ticks, so a delta can always find its baseline here. Power of two.
        /// </summary>
        public const int ReceivedHistoryLength = 32;

        private struct Entry
        {
            public int ComponentId;
            public string Name;
            public ReplicationFlags Flags;
            public FullWriter WriteFull;
            public DeltaWriter WriteDelta;
            public Applier Apply;
            public Remover Remove;
            public Presence Has;
            public Carrier Carry;
            public Interpolator Interpolate;
            public EventWriter WriteEvents;
            public EventApplier ApplyEvent;
            public EventClearer ClearEvents;
            public Func<object> CreateStore;
            public Action<object, int> EnsureGhostCapacity;
            public Action<object, uint> ForgetGhost;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private bool _sealed;

        public bool IsSealed => _sealed;

        public int Count => _entries.Count;

        /// <summary>Registers a component with the flags from its <see cref="ReplicatedAttribute"/>, or None.</summary>
        public void Register<T>() where T : unmanaged, IComponent
        {
            var attribute = typeof(T).GetCustomAttribute<ReplicatedAttribute>();
            Register<T>(attribute == null ? ReplicationFlags.None : attribute.Flags);
        }

        public void Register<T>(ReplicationFlags flags) where T : unmanaged, IComponent
        {
            if (_sealed)
                throw new InvalidOperationException("ReplicationRegistry is sealed");
            int id = ComponentMeta<T>.Id;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ComponentId == id)
                    return;
            }
            _entries.Add(new Entry
            {
                ComponentId = id,
                Name = typeof(T).FullName,
                Flags = flags,
                WriteFull = TypedEntry<T>.WriteFull,
                WriteDelta = TypedEntry<T>.WriteDelta,
                Apply = TypedEntry<T>.Apply,
                Remove = TypedEntry<T>.Remove,
                Has = TypedEntry<T>.Has,
                Carry = TypedEntry<T>.Carry,
                Interpolate = TypedEntry<T>.Interpolate,
                WriteEvents = TypedEntry<T>.WriteEvents,
                ApplyEvent = TypedEntry<T>.ApplyEvent,
                ClearEvents = TypedEntry<T>.ClearEvents,
                CreateStore = TypedEntry<T>.CreateStore,
                EnsureGhostCapacity = TypedEntry<T>.EnsureGhostCapacity,
                ForgetGhost = TypedEntry<T>.ForgetGhost,
            });
        }

        /// <summary>Allocates a client's received-value history for every registered component.</summary>
        public ReceivedHistory CreateReceivedHistory()
        {
            if (!_sealed)
                throw new InvalidOperationException("seal the registry first");
            var stores = new object[_entries.Count];
            for (int i = 0; i < stores.Length; i++)
                stores[i] = _entries[i].CreateStore();
            return new ReceivedHistory(stores);
        }

        /// <summary>Freezes and sorts by type name. Verifies every component is simulation state.</summary>
        public void Seal(SimulationRegistry simulation)
        {
            if (_sealed)
                return;
            if (simulation == null || !simulation.IsSealed)
                throw new ArgumentException("a sealed SimulationRegistry is required", nameof(simulation));
            for (int i = 0; i < _entries.Count; i++)
            {
                // Events live one tick outside the simulation state: they are read from the live world
                // and delivered reliably, so they need not (and should not) be captured in snapshots.
                if ((_entries[i].Flags & ReplicationFlags.Event) != 0)
                    continue;
                if (!simulation.Contains(_entries[i].ComponentId))
                    throw new InvalidOperationException(_entries[i].Name + " is replicated but not registered as simulation state");
            }
            if (!simulation.Contains(ComponentMeta<NetworkId>.Id))
                throw new InvalidOperationException("NetworkId must be registered as simulation state");
            _entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            _sealed = true;
        }

        public int IndexOf(int componentId)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].ComponentId == componentId)
                    return i;
            }
            return -1;
        }

        public int ComponentIdAt(int index) => _entries[index].ComponentId;
        public string NameAt(int index) => _entries[index].Name;
        public ReplicationFlags FlagsAt(int index) => _entries[index].Flags;
        public FullWriter FullWriterAt(int index) => _entries[index].WriteFull;
        public DeltaWriter DeltaWriterAt(int index) => _entries[index].WriteDelta;
        public Applier ApplierAt(int index) => _entries[index].Apply;
        public Remover RemoverAt(int index) => _entries[index].Remove;
        public Presence PresenceAt(int index) => _entries[index].Has;
        public Carrier CarrierAt(int index) => _entries[index].Carry;
        public Interpolator InterpolatorAt(int index) => _entries[index].Interpolate;
        public EventWriter EventWriterAt(int index) => _entries[index].WriteEvents;
        public EventApplier EventApplierAt(int index) => _entries[index].ApplyEvent;
        public EventClearer EventClearerAt(int index) => _entries[index].ClearEvents;
        public bool IsEventAt(int index) => (_entries[index].Flags & ReplicationFlags.Event) != 0;
        public bool IsInterpolatedAt(int index) => (_entries[index].Flags & ReplicationFlags.Interpolated) != 0;

        /// <summary>Client side: makes room in every component's received history for a ghost id.</summary>
        public void EnsureGhostCapacity(ReceivedHistory history, uint netId)
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].EnsureGhostCapacity(history.Stores[i], (int)netId);
        }

        /// <summary>Client side: drops the received history of a despawned ghost.</summary>
        public void ForgetGhost(ReceivedHistory history, uint netId)
        {
            for (int i = 0; i < _entries.Count; i++)
                _entries[i].ForgetGhost(history.Stores[i], netId);
        }

        /// <summary>Bits per ghost component mask.</summary>
        public int MaskBits => _entries.Count;

        /// <summary>Per client and component: ring of the last values received per ghost, keyed by tick.</summary>
        private sealed class ReceivedStore<T> where T : unmanaged
        {
            public struct Received
            {
                public int Tick;
                public T Value;
                public bool Used;
            }

            public Received[] Entries = Array.Empty<Received>();

            public void EnsureGhostCapacity(int netId)
            {
                int needed = (netId + 1) * ReceivedHistoryLength;
                if (Entries.Length < needed)
                    Array.Resize(ref Entries, Math.Max(needed, Entries.Length * 2));
            }

            public void Forget(uint netId)
            {
                int start = (int)netId * ReceivedHistoryLength;
                if (start >= Entries.Length)
                    return;
                for (int i = 0; i < ReceivedHistoryLength; i++)
                    Entries[start + i].Used = false;
            }

            public void Store(uint netId, int tick, in T value)
            {
                EnsureGhostCapacity((int)netId);
                int start = (int)netId * ReceivedHistoryLength;
                ref Received slot = ref Entries[start + (tick & (ReceivedHistoryLength - 1))];
                slot.Tick = tick;
                slot.Value = value;
                slot.Used = true;
            }

            public bool TryGet(uint netId, int tick, out T value)
            {
                int start = (int)netId * ReceivedHistoryLength;
                if (start + ReceivedHistoryLength <= Entries.Length)
                {
                    ref Received slot = ref Entries[start + (tick & (ReceivedHistoryLength - 1))];
                    if (slot.Used && slot.Tick == tick)
                    {
                        value = slot.Value;
                        return true;
                    }
                }
                value = default;
                return false;
            }
        }

        private static class TypedEntry<T> where T : unmanaged, IComponent
        {
            private static readonly int ComponentId = ComponentMeta<T>.Id;

            public static object CreateStore() => new ReceivedStore<T>();

            public static void WriteFull(ref BitWriter writer, WorldState state, int slot, INetEntityMap map)
            {
                ReadOnlySpan<T> page = state.Payload<T>(ComponentId, slot >> 6);
                NetSerializer<T>.Instance.Write(ref writer, page[slot & 63], map);
            }

            public static bool WriteDelta(ref BitWriter writer, WorldState baseline, int baselineSlot, WorldState current, int slot, INetEntityMap map)
            {
                T before = baseline.Payload<T>(ComponentId, baselineSlot >> 6)[baselineSlot & 63];
                T after = current.Payload<T>(ComponentId, slot >> 6)[slot & 63];
                if (SameBytes(before, after))
                    return false;
                NetSerializer<T>.Instance.WriteDelta(ref writer, before, after, map);
                return true;
            }

            public static bool Apply(ref BitReader reader, EcsWorld world, int entityId, uint netId, int tick, bool isDelta, int baselineTick, bool apply, INetEntityMap map, object store)
            {
                var received = (ReceivedStore<T>)store;
                T value;
                bool usable = true;
                if (isDelta)
                {
                    bool haveBaseline = received.TryGet(netId, baselineTick, out T baseline);
                    if (!NetSerializer<T>.Instance.ReadDelta(ref reader, baseline, out value, map))
                        return false;
                    usable = haveBaseline;
                }
                else if (!NetSerializer<T>.Instance.Read(ref reader, out value, map))
                {
                    return false;
                }
                if (!usable)
                    return false;
                received.Store(netId, tick, value);
                if (apply)
                    Write(world, entityId, value);
                return true;
            }

            public static void Remove(EcsWorld world, int entityId)
            {
                if (world.Have<T>(entityId))
                    world.Remove<T>(entityId);
            }

            public static bool Has(EcsWorld world, int entityId) => world.Have<T>(entityId);

            public static bool Carry(EcsWorld world, int entityId, uint netId, int tick, int baselineTick, bool apply, object store)
            {
                var received = (ReceivedStore<T>)store;
                if (!received.TryGet(netId, baselineTick, out T value))
                    return false;
                received.Store(netId, tick, value);
                if (apply)
                    Write(world, entityId, value);
                return true;
            }

            public static void EnsureGhostCapacity(object store, int netId) => ((ReceivedStore<T>)store).EnsureGhostCapacity(netId);

            public static void ForgetGhost(object store, uint netId) => ((ReceivedStore<T>)store).Forget(netId);

            public static bool Interpolate(EcsWorld world, int entityId, uint netId, double renderTick, object store)
            {
                var received = (ReceivedStore<T>)store;
                int start = (int)netId * ReceivedHistoryLength;
                if (start + ReceivedHistoryLength > received.Entries.Length)
                    return false;
                // The two samples around the render time: newest at or below, oldest at or above.
                int fromTick = int.MinValue;
                int toTick = int.MaxValue;
                T from = default;
                T to = default;
                for (int i = 0; i < ReceivedHistoryLength; i++)
                {
                    ref ReceivedStore<T>.Received entry = ref received.Entries[start + i];
                    if (!entry.Used)
                        continue;
                    if (entry.Tick <= renderTick && entry.Tick > fromTick)
                    {
                        fromTick = entry.Tick;
                        from = entry.Value;
                    }
                    if (entry.Tick >= renderTick && entry.Tick < toTick)
                    {
                        toTick = entry.Tick;
                        to = entry.Value;
                    }
                }
                if (fromTick == int.MinValue && toTick == int.MaxValue)
                    return false;
                T value;
                if (fromTick == int.MinValue)
                {
                    value = to;
                    fromTick = toTick;
                }
                else if (toTick == int.MaxValue || toTick == fromTick)
                {
                    value = from;
                    toTick = fromTick;
                }
                else
                {
                    float alpha = (float)((renderTick - fromTick) / (toTick - fromTick));
                    IInterpolator<T> interpolator = NetInterpolator<T>.Instance;
                    value = interpolator != null ? interpolator.Interpolate(from, to, alpha) : alpha < 0.5f ? from : to;
                }
                var copy = new Interpolated<T> { Value = value, FromTick = fromTick, ToTick = toTick };
                if (world.Have<Interpolated<T>>(entityId))
                    world.Get<Interpolated<T>>(entityId) = copy;
                else
                    world.Add(entityId, copy);
                return true;
            }

            public static int WriteEvents(EcsWorld world, int componentIndex, int tick, IRelevance relevance, int client, WorldState state,
                ReliableChannel channel, byte[] scratch, INetEntityMap map)
            {
                int sent = 0;
                ComponentStorage storage = world.GetComponentStorage<T>();
                ComponentMembershipPages pages = storage.CaptureMembershipPages();
                int pageCount = world.StoragePageCount;
                for (int page = 0; page < pageCount; page++)
                {
                    ulong bits = pages[page];
                    while (bits != 0)
                    {
                        int bit = ComponentMembershipPages.TakeFirst(ref bits);
                        int slot = (page << 6) | bit;
                        if (!world.Have<NetworkId>(slot))
                            continue;
                        uint netId = world.Get<NetworkId>(slot).Value;
                        if (relevance != null && !relevance.IsRelevant(client, state, slot, netId))
                            continue;
                        var writer = new BitWriter(scratch);
                        writer.WriteUInt32(netId);
                        writer.WriteBits((uint)componentIndex, 8);
                        writer.WriteInt32(tick);
                        NetSerializer<T>.Instance.Write(ref writer, world.Get<T>(slot), map);
                        if (writer.Overflowed)
                            continue;
                        if (channel.TrySend(new ReadOnlySpan<byte>(scratch, 0, writer.Flush())))
                            sent++;
                    }
                }
                return sent;
            }

            public static bool ApplyEvent(ref BitReader reader, EcsWorld world, int entityId, INetEntityMap map)
            {
                if (!NetSerializer<T>.Instance.Read(ref reader, out T value, map))
                    return false;
                Write(world, entityId, value);
                return true;
            }

            public static void ClearEvents(EcsWorld world) => world.RemoveAll<T>();

            private static void Write(EcsWorld world, int entityId, in T value)
            {
                if (world.Have<T>(entityId))
                    world.Get<T>(entityId) = value;
                else
                    world.Add(entityId, value);
            }

            private static bool SameBytes(in T a, in T b)
            {
                T x = a;
                T y = b;
                return System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref x, 1))
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref y, 1)));
            }
        }
    }
}
