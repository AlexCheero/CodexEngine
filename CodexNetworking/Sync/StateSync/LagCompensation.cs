using System;
using CodexECS;
using CodexNetworking.History;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Wire form of "what the shooter was looking at": how far behind its own input tick the client
    /// rendered the other ghosts, in 1/256 ticks. The client stamps every input with
    /// <see cref="Encode"/> of the tick the input is for and the render tick of that frame
    /// (<see cref="StateSyncClient{TInput}.RenderTick"/>); the server turns it back into a view time
    /// with <see cref="Decode"/> when it simulates that input. Sixteen bits cover 255 ticks, far
    /// beyond any history a server keeps.
    /// </summary>
    public static class ViewLag
    {
        public const int SubTicks = 256;

        public static ushort Encode(int inputTick, double renderTick)
        {
            double lag = (inputTick - renderTick) * SubTicks;
            if (!(lag > 0)) // also catches NaN
                return 0;
            return lag >= ushort.MaxValue ? ushort.MaxValue : (ushort)(lag + 0.5);
        }

        /// <summary>View time in server ticks of an input simulated at <paramref name="inputTick"/>.</summary>
        public static double Decode(int inputTick, ushort lag) => inputTick - lag / (double)SubTicks;
    }

    /// <summary>
    /// Server-side lag compensation. A client aims at ghosts it renders in the past (the
    /// interpolation delay plus its lead over the server), so the server judges a shot against the
    /// world the shooter saw: <see cref="Rewind"/> writes the tracked components of every entity back
    /// to their values at the shooter's view time, the game runs its hit test on the live world, and
    /// <see cref="Restore"/> puts the present back. Both happen inside one simulation tick, before
    /// the tick is snapshotted, so nothing of the rewind is ever stored or sent.
    ///
    /// Only components listed with <see cref="Track{T}"/> move; list what the hit test reads
    /// (position, orientation, a crouch or alive flag) and nothing else. They must be simulation
    /// state, because the values come from the server's <see cref="StateHistory"/>. A view time
    /// between two ticks blends them with <see cref="NetInterpolator{T}"/>, the same interpolator the
    /// client's presentation uses, so the rewound value is the one the shooter was shown; without an
    /// interpolator the nearer tick is taken, again like the client.
    ///
    /// The view time is clamped to the newest tick, to <see cref="MaxRewindTicks"/> behind it and to
    /// what the history still holds, so a client cannot claim an arbitrarily old world. An entity is
    /// rewound only when the slot held the same entity then; anything spawned since stays where it
    /// is. Allocation free once the scratch buffers have grown to the number of tracked entities.
    /// The hit test must not create or delete entities or add or remove tracked components.
    /// </summary>
    public sealed class LagCompensator
    {
        private abstract class Tracked
        {
            public int ComponentId;
            public abstract int Rewind(EcsWorld world, WorldState from, WorldState to, float alpha, int excludeEntity);
            public abstract void Restore(EcsWorld world);
        }

        private sealed class Tracked<T> : Tracked where T : unmanaged, IComponent
        {
            private struct Saved
            {
                public int Id;
                public T Value;
            }

            private Saved[] _saved = new Saved[32];
            private int _count;

            public Tracked() => ComponentId = ComponentMeta<T>.Id;

            public override int Rewind(EcsWorld world, WorldState from, WorldState to, float alpha, int excludeEntity)
            {
                _count = 0;
                int pages = Math.Min(from.PageCount, world.StoragePageCount);
                ReadOnlySpan<ulong> words = from.MembershipWords(ComponentId);
                ReadOnlySpan<ulong> alive = from.AliveWords;
                ReadOnlySpan<uint> versions = from.Versions;
                for (int page = 0; page < pages; page++)
                {
                    ulong bits = words[page] & alive[page];
                    if (bits == 0)
                        continue;
                    ReadOnlySpan<T> payload = from.Payload<T>(ComponentId, page);
                    while (bits != 0)
                    {
                        int bit = ComponentMembershipPages.TakeFirst(ref bits);
                        int id = (page << 6) | bit;
                        // The slot must still hold the entity it held then, and still own the component.
                        if (id == excludeEntity || !world.IsIdValid(id) || VersionOf(world, id) != versions[id] || !world.Have<T>(id))
                            continue;
                        T value = Blend(payload[bit], to, alpha, id, versions[id]);
                        ref T live = ref world.Get<T>(id);
                        if (_count == _saved.Length)
                            Array.Resize(ref _saved, _saved.Length * 2);
                        _saved[_count++] = new Saved { Id = id, Value = live };
                        live = value;
                    }
                }
                return _count;
            }

            public override void Restore(EcsWorld world)
            {
                for (int i = 0; i < _count; i++)
                {
                    ref Saved saved = ref _saved[i];
                    if (world.IsIdValid(saved.Id) && world.Have<T>(saved.Id))
                        world.Get<T>(saved.Id) = saved.Value;
                }
                _count = 0;
            }

            public bool TrySample(WorldState from, WorldState to, float alpha, int entityId, uint version, out T value)
            {
                value = default;
                if (entityId <= 0 || entityId >= from.SlotCount || from.Versions[entityId] != version)
                    return false;
                ulong bit = 1UL << (entityId & 63);
                int page = entityId >> 6;
                if ((from.AliveWords[page] & from.MembershipWords(ComponentId)[page] & bit) == 0)
                    return false;
                value = Blend(from.Payload<T>(ComponentId, page)[entityId & 63], to, alpha, entityId, version);
                return true;
            }

            // The value at the view time: `from` when there is nothing to blend with, otherwise the
            // interpolation the client's presentation would have shown.
            private T Blend(in T from, WorldState to, float alpha, int id, uint version)
            {
                if (to == null || id >= to.SlotCount || to.Versions[id] != version)
                    return from;
                int page = id >> 6;
                if ((to.AliveWords[page] & to.MembershipWords(ComponentId)[page] & (1UL << (id & 63))) == 0)
                    return from;
                T next = to.Payload<T>(ComponentId, page)[id & 63];
                IInterpolator<T> interpolator = NetInterpolator<T>.Instance;
                if (interpolator != null)
                    return interpolator.Interpolate(from, next, alpha);
                return alpha < 0.5f ? from : next;
            }
        }

        private Tracked[] _tracked = new Tracked[4];
        private int _trackedCount;
        private int _maxRewindTicks;

        /// <param name="maxRewindTicks">
        /// Furthest a shot may reach into the past, in ticks. The history capacity bounds it as well;
        /// 200 ms worth of ticks is a common choice.
        /// </param>
        public LagCompensator(int maxRewindTicks)
        {
            if (maxRewindTicks < 0)
                throw new ArgumentOutOfRangeException(nameof(maxRewindTicks));
            _maxRewindTicks = maxRewindTicks;
        }

        public int MaxRewindTicks
        {
            get => _maxRewindTicks;
            set => _maxRewindTicks = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>True between <see cref="Rewind"/> and <see cref="Restore"/>.</summary>
        public bool IsRewound { get; private set; }

        /// <summary>View time the last <see cref="Rewind"/> used after clamping, in server ticks.</summary>
        public double RewoundTick { get; private set; }

        /// <summary>Component values the last <see cref="Rewind"/> replaced.</summary>
        public int RewoundValues { get; private set; }

        /// <summary>Rewinds whose view time had to be clamped (older than allowed, or newer than the present).</summary>
        public long ClampedRewinds { get; private set; }

        /// <summary>Adds a component to rewind. Call before the first rewind; data components only.</summary>
        public LagCompensator Track<T>() where T : unmanaged, IComponent
        {
            if (IsRewound)
                throw new InvalidOperationException("cannot change the tracked set while rewound");
            if (ComponentMeta<T>.IsTag)
                throw new ArgumentException(typeof(T).Name + " is a tag: there is no value to rewind");
            int id = ComponentMeta<T>.Id;
            for (int i = 0; i < _trackedCount; i++)
            {
                if (_tracked[i].ComponentId == id)
                    return this;
            }
            if (_trackedCount == _tracked.Length)
                Array.Resize(ref _tracked, _tracked.Length * 2);
            _tracked[_trackedCount++] = new Tracked<T>();
            return this;
        }

        /// <summary>
        /// Writes the tracked components of every entity that already existed at
        /// <paramref name="viewTick"/> back to their values at that time. <paramref name="history"/>
        /// is the server loop's; its newest tick is the present. <paramref name="excludeEntity"/>
        /// (the shooter) is left alone. Returns false, changing nothing, when the history is empty
        /// or a tracked component is not part of it; otherwise follow with <see cref="Restore"/>.
        /// </summary>
        public bool Rewind(EcsWorld world, StateHistory history, double viewTick, int excludeEntity = 0)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));
            if (history == null)
                throw new ArgumentNullException(nameof(history));
            if (IsRewound)
                throw new InvalidOperationException("Restore must follow Rewind");
            if (!Resolve(history, viewTick, out WorldState from, out WorldState to, out float alpha, out double used))
                return false;
            int values = 0;
            for (int i = 0; i < _trackedCount; i++)
                values += _tracked[i].Rewind(world, from, to, alpha, excludeEntity);
            RewoundValues = values;
            RewoundTick = used;
            IsRewound = true;
            return true;
        }

        /// <summary>Puts back the values <see cref="Rewind"/> replaced. Harmless when nothing is rewound.</summary>
        public void Restore(EcsWorld world)
        {
            if (!IsRewound)
                return;
            for (int i = 0; i < _trackedCount; i++)
                _tracked[i].Restore(world);
            IsRewound = false;
        }

        /// <summary>
        /// Reads one entity's tracked component as it was at <paramref name="viewTick"/> without
        /// touching the world: for hit tests that work on values rather than on the live world.
        /// False when the entity did not exist then, did not own the component, or the component is
        /// not tracked.
        /// </summary>
        public bool TrySample<T>(EcsWorld world, StateHistory history, double viewTick, int entityId, out T value) where T : unmanaged, IComponent
        {
            value = default;
            if (world == null || history == null || !world.IsIdValid(entityId))
                return false;
            int id = ComponentMeta<T>.Id;
            Tracked<T> tracked = null;
            for (int i = 0; i < _trackedCount; i++)
            {
                if (_tracked[i].ComponentId == id)
                {
                    tracked = (Tracked<T>)_tracked[i];
                    break;
                }
            }
            if (tracked == null || !Resolve(history, viewTick, out WorldState from, out WorldState to, out float alpha, out _))
                return false;
            return tracked.TrySample(from, to, alpha, entityId, VersionOf(world, entityId), out value);
        }

        // Clamps the view time and finds the snapshot at or below it, the one above, and the blend factor.
        private bool Resolve(StateHistory history, double viewTick, out WorldState from, out WorldState to, out float alpha, out double used)
        {
            from = null;
            to = null;
            alpha = 0f;
            used = 0;
            if (history.IsEmpty)
                return false;
            int newest = history.NewestTick;
            int oldest = Math.Max(history.OldestTick, newest - _maxRewindTicks);
            double clamped = viewTick;
            if (!(clamped <= newest)) // also catches NaN
                clamped = newest;
            if (clamped < oldest)
                clamped = oldest;
            if (clamped != viewTick)
                ClampedRewinds++;
            int baseTick = (int)Math.Floor(clamped);
            if (!history.TryGet(baseTick, out from))
                return false;
            for (int i = 0; i < _trackedCount; i++)
            {
                if (from.IndexOf(_tracked[i].ComponentId) < 0)
                    return false; // not simulation state: nothing to rewind to
            }
            alpha = (float)(clamped - baseTick);
            if (alpha > 0f && baseTick < newest)
                history.TryGet(baseTick + 1, out to);
            else
                alpha = 0f;
            used = clamped;
            return true;
        }

        private static uint VersionOf(EcsWorld world, int id) => (uint)((ulong)world.GetById(id).Val >> 32);
    }
}
