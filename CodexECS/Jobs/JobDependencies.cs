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

        /// <summary>Finishes every job of the world's scheduler and forgets them.</summary>
        public void CompleteAll()
        {
            if (_world.Jobs == null)
                return;
            try
            {
                _world.Jobs.CompleteAll();
            }
            finally
            {
                foreach (int type in _touched)
                {
                    _writers[type] = default;
                    _readers[type]?.Clear();
                }
                _touched.Clear();
            }
        }
    }
}
