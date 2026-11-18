using System;

namespace CodexNetworking.History
{
    /// <summary>
    /// Marks a component struct as simulation state: part of snapshots, rollback, state hashes and
    /// (when also replicated) network traffic. Components without it are presentation state and are
    /// never captured. The struct must be unmanaged. The attribute is documentation plus input for
    /// <see cref="SimulationRegistry.RegisterFromAttributes"/>; explicit
    /// <see cref="SimulationRegistry.Register{T}"/> calls are the AOT-safe way to build the registry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, Inherited = false, AllowMultiple = false)]
    public sealed class SimulationStateAttribute : Attribute
    {
    }
}
