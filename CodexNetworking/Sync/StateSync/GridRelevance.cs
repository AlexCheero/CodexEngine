using System;
using CodexECS;
using CodexNetworking.FixedPoint;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Relevance by distance for thousands of ghosts: once per tick the ghosts with a position are
    /// binned into a uniform grid (cells of <c>cellSize</c>, hashed, no allocation once warm), then
    /// each client's relevant set is gathered from the cells its radius touches into a bitset, so
    /// the per-ghost query the writer makes is a bit test. Ghosts without a position component are
    /// relevant to everyone; clients without a viewpoint see everything. Pick a cell size close to
    /// the typical radius: a radius spanning many cells visits them all each tick.
    /// </summary>
    public sealed class GridRelevance<TPosition> : IPreparedRelevance where TPosition : unmanaged, IComponent
    {
        private struct Viewpoint
        {
            public Fix64 X;
            public Fix64 Y;
            public Fix64 Z;
            public Fix64 Radius;
            public bool Set;
        }

        private readonly Viewpoint[] _viewpoints;
        private readonly ulong[][] _relevant;
        private readonly PositionAccessor<TPosition> _accessor;
        private readonly int _positionComponent;
        private readonly long _cellRaw;
        // Hash table of cells: open addressing keyed by (cx, cy), each cell heads a chain of slots.
        private long[] _cellKeys = Array.Empty<long>();
        private int[] _cellHeads = Array.Empty<int>();
        private int[] _cellStamp = Array.Empty<int>();
        private int[] _nextInCell = Array.Empty<int>();
        private int _mask;
        private int _generation;
        private int _slotCount;

        /// <summary>Cells indexed in the last <see cref="Prepare"/>.</summary>
        public int CellCount { get; private set; }

        public GridRelevance(int maxClients, Fix64 cellSize, PositionAccessor<TPosition> accessor)
        {
            if (cellSize.Raw <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            _viewpoints = new Viewpoint[maxClients];
            _relevant = new ulong[maxClients][];
            _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
            _positionComponent = ComponentMeta<TPosition>.Id;
            _cellRaw = cellSize.Raw;
        }

        /// <summary>Clients without a viewpoint see everything. Flat games: height 0.</summary>
        public void SetViewpoint(int client, Fix64 x, Fix64 y, Fix64 radius) => SetViewpoint(client, x, y, Fix64.Zero, radius);

        /// <summary>Cells are laid over the first two coordinates; the distance test is spherical over all three.</summary>
        public void SetViewpoint(int client, Fix64 x, Fix64 y, Fix64 z, Fix64 radius)
        {
            _viewpoints[client] = new Viewpoint { X = x, Y = y, Z = z, Radius = radius, Set = true };
        }

        public void ClearViewpoint(int client) => _viewpoints[client].Set = false;

        public void Prepare(WorldState state)
        {
            _slotCount = state.SlotCount;
            EnsureCapacity(_slotCount);
            _generation++;
            CellCount = 0;

            ReadOnlySpan<ulong> words = state.MembershipWords(_positionComponent);
            for (int page = 0; page < words.Length; page++)
            {
                ulong word = words[page];
                if (word == 0)
                    continue;
                ReadOnlySpan<TPosition> payload = state.Payload<TPosition>(_positionComponent, page);
                while (word != 0)
                {
                    int bit = TrailingZeros(word);
                    word &= word - 1;
                    int slot = (page << 6) + bit;
                    _accessor(payload[bit], out Fix64 x, out Fix64 y, out _);
                    int cell = CellFor(CellCoordinate(x), CellCoordinate(y), true);
                    _nextInCell[slot] = _cellHeads[cell];
                    _cellHeads[cell] = slot;
                }
            }

            for (int client = 0; client < _viewpoints.Length; client++)
            {
                ref Viewpoint view = ref _viewpoints[client];
                if (!view.Set)
                    continue;
                ulong[] bits = _relevant[client];
                if (bits == null || bits.Length < words.Length)
                    _relevant[client] = bits = new ulong[Math.Max(words.Length, bits == null ? 0 : bits.Length * 2)];
                Array.Clear(bits, 0, bits.Length);
                Fix64 radiusSquared = view.Radius * view.Radius;
                long minX = CellCoordinate(view.X - view.Radius);
                long maxX = CellCoordinate(view.X + view.Radius);
                long minY = CellCoordinate(view.Y - view.Radius);
                long maxY = CellCoordinate(view.Y + view.Radius);
                for (long cy = minY; cy <= maxY; cy++)
                {
                    for (long cx = minX; cx <= maxX; cx++)
                    {
                        int cell = CellFor(cx, cy, false);
                        if (cell < 0)
                            continue;
                        for (int slot = _cellHeads[cell]; slot >= 0; slot = _nextInCell[slot])
                        {
                            _accessor(state.Payload<TPosition>(_positionComponent, slot >> 6)[slot & 63], out Fix64 x, out Fix64 y, out Fix64 z);
                            Fix64 dx = x - view.X;
                            Fix64 dy = y - view.Y;
                            Fix64 dz = z - view.Z;
                            if (dx * dx + dy * dy + dz * dz <= radiusSquared)
                                bits[slot >> 6] |= 1UL << (slot & 63);
                        }
                    }
                }
            }
        }

        public bool IsRelevant(int client, WorldState state, int slot, uint netId)
        {
            ref Viewpoint view = ref _viewpoints[client];
            if (!view.Set)
                return true;
            if ((state.MembershipWords(_positionComponent)[slot >> 6] & (1UL << (slot & 63))) == 0)
                return true;
            ulong[] bits = _relevant[client];
            return bits != null && (slot >> 6) < bits.Length && (bits[slot >> 6] & (1UL << (slot & 63))) != 0;
        }

        private long CellCoordinate(Fix64 value)
        {
            long raw = value.Raw;
            long q = raw / _cellRaw;
            if ((raw % _cellRaw != 0) && ((raw < 0) != (_cellRaw < 0)))
                q--; // floor division
            return q;
        }

        // Finds the cell's table index, creating it when asked; -1 when absent and not created.
        private int CellFor(long cx, long cy, bool create)
        {
            long key = (cx << 32) ^ (cy & 0xFFFFFFFFL);
            int index = (int)(((ulong)(cx * 73856093L) ^ (ulong)(cy * 19349663L)) & (ulong)_mask);
            for (int probe = 0; probe <= _mask; probe++)
            {
                if (_cellStamp[index] != _generation)
                {
                    if (!create)
                        return -1;
                    _cellStamp[index] = _generation;
                    _cellKeys[index] = key;
                    _cellHeads[index] = -1;
                    CellCount++;
                    return index;
                }
                if (_cellKeys[index] == key)
                    return index;
                index = (index + 1) & _mask;
            }
            return -1; // cannot happen: the table holds two entries per slot
        }

        private void EnsureCapacity(int slots)
        {
            if (_nextInCell.Length >= slots && _cellKeys.Length > 0)
                return;
            int cells = 64;
            while (cells < slots * 2)
                cells <<= 1;
            _cellKeys = new long[cells];
            _cellHeads = new int[cells];
            _cellStamp = new int[cells];
            _nextInCell = new int[Math.Max(slots, 64)];
            _mask = cells - 1;
            _generation = 0;
        }

        private static int TrailingZeros(ulong value)
        {
            int count = 0;
            while ((value & 1) == 0)
            {
                value >>= 1;
                count++;
            }
            return count;
        }
    }
}
