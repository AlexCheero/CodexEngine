using CodexECS;
using CodexNetworking.History;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Identity of a replicated entity (a ghost) across machines. Allocated by the server through
    /// <see cref="GhostTable"/>, never reused within a session. Part of the simulation state so it
    /// is captured in snapshots and found there by the writer.
    /// </summary>
    [SimulationState]
    public struct NetworkId : IComponent
    {
        public uint Value;
    }

    /// <summary>
    /// Client slot that owns a ghost (its avatar), or -1. Components flagged OwnerOnly are sent to
    /// this client only; Predicted components are simulated ahead by this client.
    /// </summary>
    [SimulationState]
    public struct NetworkOwner : IComponent
    {
        public int Client;
    }
}
