using CodexECS;

namespace CodexNetworking.Serialization
{
    /// <summary>Helpers for hand-written serializers that carry <see cref="Entity"/> fields.</summary>
    public static class EntitySerialization
    {
        /// <summary>
        /// Writes an entity as a presence bit plus a 32-bit network id. An entity the map does not
        /// know (or a null handle) is written as absent and reads back as the null entity.
        /// </summary>
        public static bool WriteEntity(ref BitWriter writer, Entity entity, INetEntityMap entities)
        {
            if (entity.Val == 0 || entities == null || !entities.TryToNet(entity, out uint netId))
                return writer.WriteBool(false);
            return writer.WriteBool(true) && writer.WriteUInt32(netId);
        }

        public static bool ReadEntity(ref BitReader reader, INetEntityMap entities, out Entity entity)
        {
            entity = default;
            if (!reader.ReadBool(out bool present))
                return false;
            if (!present)
                return true;
            if (!reader.ReadUInt32(out uint netId))
                return false;
            if (entities == null || !entities.TryToLocal(netId, out entity))
                entity = default; // unknown on this side: the field becomes null rather than failing the packet
            return true;
        }
    }
}
