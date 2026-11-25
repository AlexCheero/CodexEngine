using System;
using System.Collections.Generic;

namespace CodexECS
{
    /// <summary>
    /// What a system or job reads and writes, by component type. A type that is never added to an entity can
    /// stand for a shared resource such as a physics world.
    /// </summary>
    public sealed class ComponentAccess
    {
        internal readonly List<int> Reads = new List<int>(4), Writes = new List<int>(4);

        public ComponentAccess Read<T>() => Read(ComponentMeta<T>.Id);
        public ComponentAccess Write<T>() => Write(ComponentMeta<T>.Id);

        public ComponentAccess Read(int typeId)
        {
            if (!Reads.Contains(typeId) && !Writes.Contains(typeId))
                Reads.Add(typeId);
            return this;
        }

        public ComponentAccess Write(int typeId)
        {
            Reads.Remove(typeId);
            if (!Writes.Contains(typeId))
                Writes.Add(typeId);
            return this;
        }

        /// <summary>True when one side writes a type the other reads or writes.</summary>
        public bool ConflictsWith(ComponentAccess other)
        {
            foreach (int type in Writes)
                if (other.Writes.Contains(type) || other.Reads.Contains(type))
                    return true;
            foreach (int type in Reads)
                if (other.Writes.Contains(type))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// The jobs still touching each component type. A new job waits for the last writer of everything it uses
    /// and for the readers of everything it writes; jobs that only share reads run together.
    /// </summary>
    public sealed class JobDependencies
    {
        private readonly EcsWorld _world;
        private JobHandle[] _writers = Array.Empty<JobHandle>();
        private List<JobHandle>[] _readers = Array.Empty<List<JobHandle>>();
        private readonly List<int> _touched = new List<int>(16);
        private readonly List<JobHandle> _scratch = new List<JobHandle>(8);
        // Jobs that walk the world's storage: a sync point finishes them whether or not their access was recorded.
        private readonly List<JobHandle> _walking = new List<JobHandle>(8);

        internal JobDependencies(EcsWorld world) => _world = world;

        private void Reserve(int typeId)
        {
            if (typeId < _writers.Length)
                return;
            int size = Math.Max(typeId + 1, Math.Max(32, _writers.Length * 2));
            Array.Resize(ref _writers, size);
            Array.Resize(ref _readers, size);
        }

        /// <summary>A handle for every unfinished job this access must wait for.</summary>
        public JobHandle Get(ComponentAccess access)
        {
            if (_touched.Count == 0)
                return default;
            _scratch.Clear();
            foreach (int type in access.Reads)
                if (type < _writers.Length && !_writers[type].IsCompleted)
                    _scratch.Add(_writers[type]);
            foreach (int type in access.Writes)
            {
                if (type >= _writers.Length)
                    continue;
                if (!_writers[type].IsCompleted)
                    _scratch.Add(_writers[type]);
                var readers = _readers[type];
                if (readers == null)
                    continue;
                foreach (var reader in readers)
                    if (!reader.IsCompleted)
                        _scratch.Add(reader);
            }
            if (_scratch.Count == 0)
                return default;
            var combined = _world.Jobs.Combine(_scratch);
            _scratch.Clear();
            return combined;
        }

        /// <summary>Records handle as the newest job using this access.</summary>
        public void Add(ComponentAccess access, JobHandle handle)
        {
            if (handle.IsCompleted)
                return;
            foreach (int type in access.Writes)
            {
                Reserve(type);
                if (_writers[type].Job == null && (_readers[type] == null || _readers[type].Count == 0))
                    _touched.Add(type);
                _writers[type] = handle;
                _readers[type]?.Clear();
            }
            foreach (int type in access.Reads)
            {
                Reserve(type);
                var readers = _readers[type] ??= new List<JobHandle>(4);
                if (_writers[type].Job == null && readers.Count == 0)
                    _touched.Add(type);
                for (int i = readers.Count - 1; i >= 0; i--)
                    if (readers[i].IsCompleted)
                        readers.RemoveAt(i);
                readers.Add(handle);
            }
        }

        /// <summary>Finishes every job this access must wait for.</summary>
        public void Complete(ComponentAccess access) => Get(access).Complete();

        /// <summary>True while a recorded job may be unfinished.</summary>
        public bool Any => _touched.Count != 0 || _walking.Count != 0;

        // A filter job reads membership words and payload arrays, so no structural change may happen under it.
        internal void Track(JobHandle handle)
        {
            if (handle.IsCompleted)
                return;
            for (int i = _walking.Count - 1; i >= 0; i--)
                if (_walking[i].IsCompleted)
                    _walking.RemoveAt(i);
            _walking.Add(handle);
        }

        /// <summary>
        /// Finishes every recorded job and forgets them. A job that was scheduled without being recorded here
        /// touches no component data, so it keeps running: whoever scheduled it completes it.
        /// </summary>
        public void CompleteAll()
        {
            // Completing a handle runs queued jobs on this thread while it waits, and such a job may sync the world
            // again or schedule more work. So each round takes the recorded handles out first, forgets them, and only
            // then completes them; anything recorded meanwhile is finished by the next round. A nested call works on
            // its own snapshot and never sees this one's lists change under it.
            while (_touched.Count != 0 || _walking.Count != 0)
            {
                int count = _walking.Count;
                foreach (int type in _touched)
                    count += 1 + (_readers[type]?.Count ?? 0);
                var handles = System.Buffers.ArrayPool<JobHandle>.Shared.Rent(count);
                int taken = 0;
                foreach (var walking in _walking)
                    handles[taken++] = walking;
                foreach (int type in _touched)
                {
                    handles[taken++] = _writers[type];
                    _writers[type] = default;
                    var readers = _readers[type];
                    if (readers == null)
                        continue;
                    foreach (var reader in readers)
                        handles[taken++] = reader;
                    readers.Clear();
                }
                _touched.Clear();
                _walking.Clear();
                try
                {
                    for (int i = 0; i < taken; i++)
                        handles[i].Complete();
                }
                finally
                {
                    Array.Clear(handles, 0, taken);
                    System.Buffers.ArrayPool<JobHandle>.Shared.Return(handles);
                }
            }
        }
    }
}
