using System;

namespace CodexNetworking.Serialization
{
    [Flags]
    public enum ReplicationFlags
    {
        None = 0,
        /// <summary>The owning client simulates this component ahead of the server and reconciles it.</summary>
        Predicted = 1 << 0,
        /// <summary>Clients interpolate this component between snapshots instead of simulating it.</summary>
        Interpolated = 1 << 1,
        /// <summary>Sent only to the owning client.</summary>
        OwnerOnly = 1 << 2,
        /// <summary>Never sent; exists on the server only (still simulation state).</summary>
        ServerOnly = 1 << 3,
        /// <summary>Lives one tick and is delivered reliably: a one-shot event rather than state.</summary>
        Event = 1 << 4,
    }

    /// <summary>
    /// Marks a simulation component for network replication. Replicated components are a subset of
    /// simulation state. Flags describe who receives it and how clients treat it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class ReplicatedAttribute : Attribute
    {
        public ReplicationFlags Flags { get; }

        public ReplicatedAttribute(ReplicationFlags flags = ReplicationFlags.None) => Flags = flags;
    }

    /// <summary>A float or fixed-point field sent as a bounded integer at the given step.</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class QuantizeAttribute : Attribute
    {
        public float Min { get; }
        public float Max { get; }
        public float Precision { get; }

        public QuantizeAttribute(float min, float max, float precision)
        {
            Min = min;
            Max = max;
            Precision = precision;
        }
    }

    /// <summary>An integer field sent with exactly this many bits.</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NetBitsAttribute : Attribute
    {
        public int Bits { get; }

        public NetBitsAttribute(int bits) => Bits = bits;
    }

    /// <summary>An integer field known to lie in [Min, Max].</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NetRangeAttribute : Attribute
    {
        public int Min { get; }
        public int Max { get; }

        public NetRangeAttribute(int min, int max)
        {
            Min = min;
            Max = max;
        }
    }

    /// <summary>An <see cref="CodexECS.Entity"/> field translated through the entity map.</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
    public sealed class NetEntityAttribute : Attribute
    {
    }
}
