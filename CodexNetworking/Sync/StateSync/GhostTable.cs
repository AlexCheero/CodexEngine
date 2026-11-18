using System;
using CodexECS;
using CodexNetworking.Serialization;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Server-side allocation of network ids and lookup from id to entity. Ids are dense and
    /// monotonic, so the lookup is an array that grows only when a new maximum is reached.
    /// Also serves as the server's <see cref="INetEntityMap"/>.
    /// </summary>
    public sealed class GhostTable : INetEntityMap
    {
        private readonly EcsWorld _world;
        private Entity[] _entities = new Entity[256];
        private uint _next = 1;

        public GhostTable(EcsWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

        /// <summary>Highest id handed out so far.</summary>
        public uint LastId => _next - 1;

        /// <summary>Makes the entity a ghost: adds <see cref="NetworkId"/> and records the mapping.</summary>
        public uint Register(int entityId)
        {
            uint id = _next++;
            if (id >= _entities.Length)
                Array.Resize(ref _entities, Math.Max((int)id + 1, _entities.Length * 2));
            _entities[id] = _world.GetById(entityId);
            _world.Add(entityId, new NetworkId { Value = id });
            return id;
        }

        /// <summary>Forgets a ghost; call when its entity is deleted. The id is never reused.</summary>
        public void Release(uint id)
        {
            if (id < _entities.Length)
                _entities[id] = default;
        }

        public bool TryGetEntity(uint id, out Entity entity)
        {
            if (id == 0 || id >= _entities.Length)
            {
                entity = default;
                return false;
            }
            entity = _entities[id];
            return entity.Val != 0 && _world.IsEntityValid(entity);
        }

        public bool TryToNet(Entity entity, out uint netId)
        {
            netId = 0;
            if (!_world.IsEntityValid(entity))
                return false;
            int slot = (int)(uint)entity.Val;
            if (!_world.Have<NetworkId>(slot))
                return false;
            netId = _world.Get<NetworkId>(slot).Value;
            return netId != 0;
        }

        public bool TryToLocal(uint netId, out Entity entity) => TryGetEntity(netId, out entity);
    }

    /// <summary>
    /// Client-side mapping from network id to the local ghost entity. Filled by the snapshot reader.
    /// </summary>
    public sealed class GhostMap : INetEntityMap
    {
        private readonly EcsWorld _world;
        private Entity[] _entities = new Entity[256];
        private int _count;
        private uint _maxId;

        public GhostMap(EcsWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

        /// <summary>Ghosts currently mapped.</summary>
        public int Count => _count;

        public int Capacity => _entities.Length;

        /// <summary>Highest id ever mapped; iterate 1..MaxId with <see cref="TryGetEntity"/>.</summary>
        public uint MaxId => _maxId;

        public bool TryGetEntity(uint netId, out Entity entity)
        {
            if (netId == 0 || netId >= _entities.Length)
            {
                entity = default;
                return false;
            }
            entity = _entities[netId];
            if (entity.Val == 0)
                return false;
            if (_world.IsEntityValid(entity))
                return true;
            // The game deleted the local entity behind our back: drop the mapping.
            _entities[netId] = default;
            _count--;
            entity = default;
            return false;
        }

        public void Set(uint netId, Entity entity)
        {
            if (netId >= _entities.Length)
                Array.Resize(ref _entities, Math.Max((int)netId + 1, _entities.Length * 2));
            if (_entities[netId].Val == 0)
                _count++;
            _entities[netId] = entity;
            if (netId > _maxId)
                _maxId = netId;
        }

        public void Remove(uint netId)
        {
            if (netId < _entities.Length && _entities[netId].Val != 0)
            {
                _entities[netId] = default;
                _count--;
            }
        }

        public bool TryToNet(Entity entity, out uint netId)
        {
            netId = 0;
            if (!_world.IsEntityValid(entity))
                return false;
            int slot = (int)(uint)entity.Val;
            if (!_world.Have<NetworkId>(slot))
                return false;
            netId = _world.Get<NetworkId>(slot).Value;
            return netId != 0;
        }

        public bool TryToLocal(uint netId, out Entity entity) => TryGetEntity(netId, out entity);
    }
}
