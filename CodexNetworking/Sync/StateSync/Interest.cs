using System;
using CodexECS;
using CodexNetworking.FixedPoint;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>How quickly a ghost's send priority grows per tick while it waits. Default: 1 for everything.</summary>
    public interface IPriorityWeight
    {
        int WeightOf(WorldState state, int slot, uint netId);
    }

    /// <summary>
    /// Priority accumulator (the Halo and Tribes scheme): every relevant ghost gains its weight per
    /// tick and is reset when sent. The writer sends the highest priorities first, so a packet
    /// budget starves nobody: a ghost left out keeps growing until it wins.
    /// </summary>
    public sealed class PriorityAccumulator
    {
        private readonly int[][] _priorities;

        public PriorityAccumulator(int maxClients) => _priorities = new int[maxClients][];

        public int Accumulate(int client, uint netId, int weight)
        {
            ref int[] table = ref _priorities[client];
            if (table == null)
                table = new int[256];
            if (netId >= table.Length)
                Array.Resize(ref table, Math.Max((int)netId + 1, table.Length * 2));
            table[netId] += weight;
            return table[netId];
        }

        public void Reset(int client, uint netId)
        {
            int[] table = _priorities[client];
            if (table != null && netId < table.Length)
                table[netId] = 0;
        }

        public int Get(int client, uint netId)
        {
            int[] table = _priorities[client];
            return table != null && netId < table.Length ? table[netId] : 0;
        }

        public void ResetClient(int client)
        {
            int[] table = _priorities[client];
            if (table != null)
                Array.Clear(table, 0, table.Length);
        }
    }

    /// <summary>
    /// Reads a position out of a game's position component. The first two coordinates are the
    /// plane the grid is built on (the ground plane in a 3D game); the third is height, or
    /// <see cref="Fix64.Zero"/> in a flat game. Distances are spherical over all three.
    /// </summary>
    public delegate void PositionAccessor<TPosition>(in TPosition value, out Fix64 x, out Fix64 y, out Fix64 z) where TPosition : unmanaged;

    /// <summary>
    /// Relevance by distance: a ghost is relevant to a client when it lies within the client's
    /// radius of its viewpoint, or has no position component (global objects). The game sets each
    /// client's viewpoint every tick. O(ghosts × clients); <see cref="GridRelevance{TPosition}"/>
    /// replaces the scan when counts grow.
    /// </summary>
    public sealed class RadiusRelevance<TPosition> : IRelevance where TPosition : unmanaged, IComponent
    {
        private struct Viewpoint
        {
            public Fix64 X;
            public Fix64 Y;
            public Fix64 Z;
            public Fix64 RadiusSquared;
            public bool Set;
        }

        private readonly Viewpoint[] _viewpoints;
        private readonly PositionAccessor<TPosition> _accessor;
        private readonly int _positionComponent;

        public RadiusRelevance(int maxClients, PositionAccessor<TPosition> accessor)
        {
            _viewpoints = new Viewpoint[maxClients];
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _positionComponent = ComponentMeta<TPosition>.Id;
        }

        /// <summary>Clients without a viewpoint see everything. Flat games: height 0.</summary>
        public void SetViewpoint(int client, Fix64 x, Fix64 y, Fix64 radius) => SetViewpoint(client, x, y, Fix64.Zero, radius);

        public void SetViewpoint(int client, Fix64 x, Fix64 y, Fix64 z, Fix64 radius)
        {
            _viewpoints[client] = new Viewpoint { X = x, Y = y, Z = z, RadiusSquared = radius * radius, Set = true };
        }

        public void ClearViewpoint(int client) => _viewpoints[client].Set = false;

        public bool IsRelevant(int client, WorldState state, int slot, uint netId)
        {
            ref Viewpoint view = ref _viewpoints[client];
            if (!view.Set)
                return true;
            if ((state.MembershipWords(_positionComponent)[slot >> 6] & (1UL << (slot & 63))) == 0)
                return true;
            _accessor(state.Payload<TPosition>(_positionComponent, slot >> 6)[slot & 63], out Fix64 x, out Fix64 y, out Fix64 z);
            Fix64 dx = x - view.X;
            Fix64 dy = y - view.Y;
            Fix64 dz = z - view.Z;
            return dx * dx + dy * dy + dz * dz <= view.RadiusSquared;
        }
    }
}
