using System;
using CodexECS;

namespace CodexNetworking.Serialization
{
    /// <summary>
    /// Translates entity handles between the local world and network ids. State sync maps local
    /// slots to ghost ids; input sync uses <see cref="IdentityEntityMap"/> because every peer has
    /// the same slots. A serializer receives the map so components holding <see cref="Entity"/>
    /// fields can translate them; a serializer without entity fields ignores it.
    /// </summary>
    public interface INetEntityMap
    {
        /// <summary>Network id of a local entity, or false when it is not known to the network.</summary>
        bool TryToNet(Entity entity, out uint netId);

        /// <summary>Local entity for a network id, or false when it does not exist locally.</summary>
        bool TryToLocal(uint netId, out Entity entity);
    }

    /// <summary>Lockstep mapping: the handle's bits travel unchanged (slot + version in 64 bits via two 32-bit halves).</summary>
    public sealed class IdentityEntityMap : INetEntityMap
    {
        public static readonly IdentityEntityMap Instance = new IdentityEntityMap();

        /// <summary>Only the 32-bit slot id travels; the version is implied by lockstep.</summary>
        public bool TryToNet(Entity entity, out uint netId)
        {
            netId = (uint)entity.Val;
            return true;
        }

        public bool TryToLocal(uint netId, out Entity entity)
        {
            entity = new Entity(netId);
            return true;
        }
    }

    /// <summary>
    /// Wire format of one component type. Write and Read are the full form; WriteDelta and ReadDelta
    /// encode against a baseline the receiver is known to hold. Implementations are stateless and
    /// allocation free. Read methods return false on malformed input and leave the stream in an
    /// undefined position; the caller drops the packet.
    /// </summary>
    public interface INetSerializer<T> where T : unmanaged
    {
        void Write(ref BitWriter writer, in T value, INetEntityMap entities);

        bool Read(ref BitReader reader, out T value, INetEntityMap entities);

        void WriteDelta(ref BitWriter writer, in T baseline, in T value, INetEntityMap entities);

        bool ReadDelta(ref BitReader reader, in T baseline, out T value, INetEntityMap entities);
    }

    /// <summary>
    /// The serializer a component type uses. Defaults to <see cref="BlitSerializer{T}"/>; a game
    /// assigns a hand-written or generated serializer at startup for components that need
    /// quantization or entity translation. Static per type, so lookups cost nothing.
    /// </summary>
    public static class NetSerializer<T> where T : unmanaged
    {
        private static INetSerializer<T> _instance = BlitSerializer<T>.Default;

        public static INetSerializer<T> Instance
        {
            get => _instance;
            set => _instance = value ?? throw new ArgumentNullException(nameof(value));
        }
    }
}
