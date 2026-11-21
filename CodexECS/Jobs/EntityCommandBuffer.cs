using System;
using System.Collections.Generic;

namespace CodexECS
{
    /// <summary>An entity a command buffer will create at playback. Usable only with the buffer that made it.</summary>
    public readonly struct DeferredEntity
    {
        internal readonly int Lane, Index;

        internal DeferredEntity(int lane, int index)
        {
            Lane = lane;
            Index = index;
        }
    }

    /// <summary>
    /// Structural changes recorded now and applied later on the world's owning thread: jobs cannot create or
    /// delete entities or add or remove components themselves.
    /// Every thread records into its own lane. Playback orders commands by sort key, then by the order they were
    /// recorded, so it does not depend on which thread ran which batch as long as each sort key is used by one
    /// batch: pass the entity id, or the batch index. A deferred entity's commands need a key at or above the
    /// key it was created with. Commands for an entity that has died meanwhile are skipped.
    /// </summary>
    public sealed class EntityCommandBuffer
    {
        private enum Op : byte { Create, Delete, Add, Set, Remove }

        private struct Command
        {
            public int SortKey, Sequence, TypeId, Value;
            public long Target;
            public Op Op;
            public bool Deferred;
            public byte Lane;
        }

        private interface IPayload
        {
            void Apply(EcsWorld world, int id, int index, bool replace);
            void Clear();
        }

        private sealed class Payload<T> : IPayload
        {
            private T[] _values = new T[8];
            private int _count;

            public int Store(in T value)
            {
                if (_count == _values.Length)
                    Array.Resize(ref _values, _count * 2);
                _values[_count] = value;
                return _count++;
            }

            public void Apply(EcsWorld world, int id, int index, bool replace)
            {
                if (ComponentMeta<T>.IsTag)
                {
                    world.TryAdd<T>(id);
                    return;
                }
                if (world.Have<T>(id))
                {
                    if (replace)
                        world.Get<T>(id) = _values[index];
                    return;
                }
                world.Add(id, _values[index]);
            }

            public void Clear()
            {
                Array.Clear(_values, 0, _count);
                _count = 0;
            }
        }

        private sealed class Lane
        {
            public Command[] Commands = new Command[16];
            public int Count, Created, LastKey;
            // False once a key below an earlier one was recorded: playback must sort.
            public bool Ordered = true;
            public IPayload[] Payloads = Array.Empty<IPayload>();
            public int[] Entities = Array.Empty<int>();

            public ref Command Next(byte lane, int sortKey)
            {
                if (Count == Commands.Length)
                    Array.Resize(ref Commands, Count * 2);
                ref var command = ref Commands[Count];
                command = default;
                command.Sequence = Count++;
                command.Lane = lane;
                command.SortKey = sortKey;
                if (sortKey < LastKey)
                    Ordered = false;
                LastKey = sortKey;
                return ref command;
            }

            public Payload<T> For<T>()
            {
                int type = ComponentMeta<T>.Id;
                if (type >= Payloads.Length)
                    Array.Resize(ref Payloads, Math.Max(type + 1, Payloads.Length * 2));
                return (Payload<T>)(Payloads[type] ??= new Payload<T>());
            }
        }

        private sealed class Order : IComparer<Command>
        {
            public static readonly Order Instance = new Order();

            public int Compare(Command x, Command y)
            {
                if (x.SortKey != y.SortKey)
                    return x.SortKey < y.SortKey ? -1 : 1;
                if (x.Sequence != y.Sequence)
                    return x.Sequence < y.Sequence ? -1 : 1;
                return x.Lane.CompareTo(y.Lane);
            }
        }

        private readonly EcsWorld _world;
        private readonly Lane[] _lanes;
        private Command[] _merged = Array.Empty<Command>();

        /// <param name="world">The world whose entities the commands name.</param>
        /// <param name="threads">Threads that may record: the scheduler's ThreadCount, or 1 without jobs.</param>
        public EntityCommandBuffer(EcsWorld world, int threads = 1)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            if (threads < 1 || threads > 255)
                throw new ArgumentOutOfRangeException(nameof(threads));
            _lanes = new Lane[threads];
            for (int i = 0; i < threads; i++)
                _lanes[i] = new Lane();
        }

        public bool IsEmpty
        {
            get
            {
                foreach (var lane in _lanes)
                    if (lane.Count != 0)
                        return false;
                return true;
            }
        }

        private Lane Current(out byte index)
        {
            int thread = JobScheduler.CurrentThreadIndex;
            if (thread >= _lanes.Length)
                throw new InvalidOperationException("This command buffer has no lane for the calling worker thread.");
            index = (byte)thread;
            return _lanes[thread];
        }

        public DeferredEntity Create(int sortKey = 0)
        {
            var lane = Current(out byte index);
            ref var command = ref lane.Next(index, sortKey);
            command.Op = Op.Create;
            command.Value = lane.Created;
            return new DeferredEntity(index, lane.Created++);
        }

        public void Delete(int id, int sortKey = 0)
        {
            var lane = Current(out byte index);
            long target = Handle(id);
            ref var command = ref lane.Next(index, sortKey);
            command.Op = Op.Delete;
            command.Target = target;
        }

        /// <summary>Adds the component. An entity that already owns it keeps its value.</summary>
        public void Add<T>(int id, in T value = default, int sortKey = 0) => Record(Op.Add, Handle(id), false, value, sortKey);

        /// <summary>Adds the component or replaces its value.</summary>
        public void Set<T>(int id, in T value, int sortKey = 0) => Record(Op.Set, Handle(id), false, value, sortKey);

        public void Add<T>(DeferredEntity entity, in T value = default, int sortKey = 0) =>
            Record(Op.Add, Own(entity), true, value, sortKey);

        public void Remove<T>(int id, int sortKey = 0)
        {
            var lane = Current(out byte index);
            long target = Handle(id);
            ref var command = ref lane.Next(index, sortKey);
            command.Op = Op.Remove;
            command.Target = target;
            command.TypeId = ComponentMeta<T>.Id;
        }

        private long Own(DeferredEntity entity)
        {
            if (entity.Lane != JobScheduler.CurrentThreadIndex)
                throw new InvalidOperationException("A deferred entity belongs to the thread that created it.");
            return entity.Index;
        }

        private void Record<T>(Op op, long target, bool deferred, in T value, int sortKey)
        {
            var lane = Current(out byte index);
            var payload = lane.For<T>();
            int stored = ComponentMeta<T>.IsTag ? 0 : payload.Store(in value);
            ref var command = ref lane.Next(index, sortKey);
            command.Op = op;
            command.Target = target;
            command.Deferred = deferred;
            command.TypeId = ComponentMeta<T>.Id;
            command.Value = stored;
        }

        // The versioned handle of the entity as it is now: playback skips it if the slot was reused since.
        private long Handle(int id) => _world.GetById(id).Val;

        /// <summary>Applies the commands in order and empties the buffer. Call on the world's owning thread.</summary>
        public void Playback()
        {
            int total = 0;
            foreach (var lane in _lanes)
                total += lane.Count;
            if (total == 0)
                return;
            if (_merged.Length < total)
                _merged = new Command[Math.Max(total, _merged.Length * 2)];
            int at = 0, recording = 0;
            bool ordered = true;
            foreach (var lane in _lanes)
            {
                if (lane.Count != 0)
                {
                    recording++;
                    ordered &= lane.Ordered;
                }
                Array.Copy(lane.Commands, 0, _merged, at, lane.Count);
                at += lane.Count;
                if (lane.Entities.Length < lane.Created)
                    lane.Entities = new int[Math.Max(lane.Created, lane.Entities.Length * 2)];
                Array.Clear(lane.Entities, 0, lane.Created);
            }
            // One lane whose keys never went down is already in playback order.
            if (recording > 1 || !ordered)
                Array.Sort(_merged, 0, total, Order.Instance);
            var world = _world;
            try
            {
                for (int i = 0; i < total; i++)
                {
                    ref var command = ref _merged[i];
                    var lane = _lanes[command.Lane];
                    if (command.Op == Op.Create)
                    {
                        lane.Entities[command.Value] = world.Create();
                        continue;
                    }
                    int id;
                    if (command.Deferred)
                    {
                        id = lane.Entities[(int)command.Target];
                        if (id == 0)
                            throw new EcsException("a command names a deferred entity before the command that creates it");
                    }
                    else
                    {
                        var entity = new Entity(command.Target);
                        if (!world.IsEntityValid(entity))
                            continue;
                        id = entity.GetId();
                    }
                    switch (command.Op)
                    {
                        case Op.Delete:
                            world.Delete(id);
                            break;
                        case Op.Remove:
                            if (world.Have(command.TypeId, id))
                                world.Remove_Dynamic(ComponentMapping.GetTypeForId(command.TypeId), id);
                            break;
                        default:
                            lane.Payloads[command.TypeId].Apply(world, id, command.Value, command.Op == Op.Set);
                            break;
                    }
                }
            }
            finally
            {
                foreach (var lane in _lanes)
                {
                    lane.Count = lane.Created = lane.LastKey = 0;
                    lane.Ordered = true;
                    foreach (var payload in lane.Payloads)
                        payload?.Clear();
                }
            }
        }
    }
}
