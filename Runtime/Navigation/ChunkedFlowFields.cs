using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace CodexEngine.Navigation;

/// <summary>
/// One square chunk of a ground-plane walk grid, as a streamed world loads it: <see cref="Size"/>² cells of
/// <see cref="CellSize"/> metres whose <see cref="Cost"/> is the price of stepping into the cell (0 = blocked,
/// 1 = open pavement; a road or lawn costs more). Chunks of one world share size and cell size and tile it from
/// <see cref="ChunkedFlowGrid.Origin"/>, so chunk (x,z) covers cells [x*Size, (x+1)*Size).
/// </summary>
public sealed class FlowChunk
{
    public readonly int X, Z, Size;
    public readonly byte[] Cost;
    /// <summary>Baked fields: for each, a direction code per cell (see <see cref="FlowDirections"/>).</summary>
    public readonly byte[][] Fields;
    public FlowChunk(int x, int z, int size, byte[] cost, byte[][] fields = null)
    {
        if (cost.Length != size * size) throw new ArgumentException("A chunk's cost grid must be Size x Size.");
        X = x; Z = z; Size = size; Cost = cost; Fields = fields ?? [];
    }
}

/// <summary>Direction codes: 0-7 step to one of the eight neighbours, <see cref="None"/> where there is no way on.</summary>
public static class FlowDirections
{
    public const byte None = 255;
    public static readonly int[] Dx = [1, 1, 0, -1, -1, -1, 0, 1];
    public static readonly int[] Dz = [0, 1, 1, 1, 0, -1, -1, -1];
    /// <summary>Unit ground-plane direction (X, Z) of a code, zero for <see cref="None"/>.</summary>
    public static Vector2 Vector(byte code) => code > 7 ? Vector2.Zero : Vector2.Normalize(new Vector2(Dx[code], Dz[code]));
}

/// <summary>
/// The walk grid of a streamed world: chunks come and go with the cells that hold them. Lookups are lock-free
/// (a concurrent map of immutable chunks), so crowd jobs read it while the game thread adds and removes chunks.
/// </summary>
public sealed class ChunkedFlowGrid
{
    public readonly Vector2 Origin;
    public readonly int ChunkSize;
    public readonly float CellSize;
    readonly ConcurrentDictionary<(int, int), FlowChunk> chunks = new();
    int revision;

    public ChunkedFlowGrid(Vector2 origin, int chunkSize, float cellSize) { Origin = origin; ChunkSize = chunkSize; CellSize = cellSize; }

    /// <summary>Changes whenever a chunk is added or removed.</summary>
    public int Revision => Volatile.Read(ref revision);
    public int Count => chunks.Count;
    public IEnumerable<FlowChunk> Chunks => chunks.Values;

    public void Add(FlowChunk chunk) { chunks[(chunk.X, chunk.Z)] = chunk; Interlocked.Increment(ref revision); }
    public void Remove(int x, int z) { if (chunks.TryRemove((x, z), out _)) Interlocked.Increment(ref revision); }
    public bool TryGet(int x, int z, out FlowChunk chunk) => chunks.TryGetValue((x, z), out chunk);

    /// <summary>The global cell under a ground-plane point.</summary>
    public (int X, int Z) Cell(Vector3 p) => ((int)MathF.Floor((p.X - Origin.X) / CellSize), (int)MathF.Floor((p.Z - Origin.Y) / CellSize));
    public Vector3 Center(int x, int z) => new(Origin.X + (x + .5f) * CellSize, 0, Origin.Y + (z + .5f) * CellSize);

    public (int X, int Z) ChunkOf(int cx, int cz) => (FloorDiv(cx, ChunkSize), FloorDiv(cz, ChunkSize));
    static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    /// <summary>The cost of a global cell: 0 where blocked or not loaded.</summary>
    public byte Cost(int cx, int cz)
    {
        var (x, z) = ChunkOf(cx, cz);
        if (!chunks.TryGetValue((x, z), out var c)) return 0;
        return c.Cost[(cz - z * ChunkSize) * ChunkSize + cx - x * ChunkSize];
    }

    /// <summary>A baked field's direction code at a global cell, <see cref="FlowDirections.None"/> off the grid.</summary>
    public byte Baked(int field, int cx, int cz)
    {
        var (x, z) = ChunkOf(cx, cz);
        if (!chunks.TryGetValue((x, z), out var c) || field >= c.Fields.Length) return FlowDirections.None;
        return c.Fields[field][(cz - z * ChunkSize) * ChunkSize + cx - x * ChunkSize];
    }

    /// <summary>A baked field's direction at a point, blended over the four nearest cells so agents turn smoothly.</summary>
    public Vector2 Sample(int field, Vector3 p) => Blend(p, (x, z) => Baked(field, x, z));

    internal Vector2 Blend(Vector3 p, Func<int, int, byte> code)
    {
        float fx = (p.X - Origin.X) / CellSize - .5f, fz = (p.Z - Origin.Y) / CellSize - .5f;
        int x0 = (int)MathF.Floor(fx), z0 = (int)MathF.Floor(fz);
        float tx = fx - x0, tz = fz - z0;
        var sum = FlowDirections.Vector(code(x0, z0)) * (1 - tx) * (1 - tz) + FlowDirections.Vector(code(x0 + 1, z0)) * tx * (1 - tz)
                + FlowDirections.Vector(code(x0, z0 + 1)) * (1 - tx) * tz + FlowDirections.Vector(code(x0 + 1, z0 + 1)) * tx * tz;
        float len = sum.Length();
        return len > 1e-4f ? sum / len : FlowDirections.Vector(code((int)MathF.Round(fx), (int)MathF.Round(fz)));
    }
}

/// <summary>
/// Integrates cost fields over a walk grid: a multi-seed Dijkstra over eight-connected cells, a diagonal step
/// costing √2 times its cell's cost and never cutting a blocked corner. Static: safe on any thread.
/// </summary>
public static class FlowIntegrator
{
    public const int Unreached = int.MaxValue;
    static readonly int[] StepCost = [10, 14, 10, 14, 10, 14, 10, 14];

    /// <summary>
    /// Distances from the seeds over a w×h grid of costs (0 blocked). <paramref name="distance"/> holds the seeds'
    /// starting values on entry (<see cref="Unreached"/> elsewhere) and every cell's integrated cost on return.
    /// <paramref name="penalty"/>, when given, adds a price per cell (danger nearby, say).
    /// </summary>
    public static void Integrate(int w, int h, ReadOnlySpan<byte> cost, Span<int> distance, ReadOnlySpan<int> penalty = default)
    {
        var queue = new PriorityQueue<int, int>();
        for (int i = 0; i < w * h; i++) if (distance[i] != Unreached && cost[i] != 0) queue.Enqueue(i, distance[i]);
        while (queue.TryDequeue(out int i, out int d))
        {
            if (d != distance[i]) continue;
            int x = i % w, z = i / w;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + FlowDirections.Dx[k], nz = z + FlowDirections.Dz[k];
                if ((uint)nx >= (uint)w || (uint)nz >= (uint)h) continue;
                int j = nz * w + nx;
                byte c = cost[j];
                if (c == 0) continue;
                if ((k & 1) == 1 && (cost[z * w + nx] == 0 || cost[nz * w + x] == 0)) continue;
                int nd = d + StepCost[k] * c + (penalty.IsEmpty ? 0 : penalty[j]);
                if (nd < distance[j]) { distance[j] = nd; queue.Enqueue(j, nd); }
            }
        }
    }

    /// <summary>For each cell the neighbour to step to: downhill toward the seeds, or uphill (away) with <paramref name="ascend"/>.</summary>
    public static void Directions(int w, int h, ReadOnlySpan<byte> cost, ReadOnlySpan<int> distance, Span<byte> directions, bool ascend = false)
    {
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                byte best = FlowDirections.None;
                long bestValue = distance[i];
                if (cost[i] == 0 || bestValue == Unreached) { directions[i] = best; continue; }
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + FlowDirections.Dx[k], nz = z + FlowDirections.Dz[k];
                    if ((uint)nx >= (uint)w || (uint)nz >= (uint)h) continue;
                    int j = nz * w + nx;
                    if (cost[j] == 0 || distance[j] == Unreached) continue;
                    if ((k & 1) == 1 && (cost[z * w + nx] == 0 || cost[nz * w + x] == 0)) continue;
                    long v = distance[j];
                    if (ascend ? v > bestValue : v < bestValue) { bestValue = v; best = (byte)k; }
                }
                directions[i] = best;
            }
    }
}

/// <summary>
/// A flowfield computed at runtime over the chunks loaded now, one chunk at a time on a worker, nearest chunks to
/// its source first: agents near the source get their directions within a frame or two, farther ones as the
/// wave reaches them. Each chunk's result is published as soon as it is done and read lock-free.
/// <para>
/// A flee field (<see cref="StartFlee"/>) first measures the walking cost from the danger outward, with a price
/// on cells near it, and publishes "away" directions (uphill on that cost) chunk by chunk. When every chunk is
/// done it takes, in each of eight directions around the danger, the reachable cell farthest from it as a refuge and
/// integrates a second pass toward the nearest refuge, which replaces the first chunk by chunk: everyone heads for the
/// place as far from the danger as the loaded world allows on their own side, instead of stopping at the nearest dead
/// end or crossing past the danger to one common refuge.
/// </para>
/// </summary>
public sealed class DynamicFlowField : IDisposable
{
    readonly ChunkedFlowGrid grid;
    readonly ConcurrentDictionary<(int, int), byte[]> published = new();
    CancellationTokenSource cancel;
    Task work;
    int generation;

    public DynamicFlowField(ChunkedFlowGrid grid) => this.grid = grid;

    /// <summary>Increases with every start; agents keep the generation they started following.</summary>
    public int Generation => Volatile.Read(ref generation);
    public bool Active { get; private set; }
    /// <summary>Chunks published in the current generation, and how many there are to do.</summary>
    public int ChunksDone => published.Count;
    public int ChunksTotal { get; private set; }
    /// <summary>True once the refuge pass has replaced every chunk.</summary>
    public bool Complete { get; private set; }
    public Vector3 Source { get; private set; }
    public Vector3 Refuge { get; private set; }
    /// <summary>The far points the crowd heads for, one per direction around the danger that has room to run.</summary>
    public Vector3[] Refuges { get; private set; } = [];
    /// <summary>Wall-clock milliseconds from the start until the first chunk and until everything was done.</summary>
    public double FirstChunkMilliseconds { get; private set; }
    public double TotalMilliseconds { get; private set; }

    /// <summary>Direction at a point (unit X,Z), or zero where the field has not reached yet.</summary>
    public Vector2 Sample(Vector3 p) => grid.Blend(p, Code);

    public bool Ready(Vector3 p)
    {
        var (cx, cz) = grid.Cell(p);
        return published.ContainsKey(grid.ChunkOf(cx, cz));
    }

    byte Code(int cx, int cz)
    {
        var (x, z) = grid.ChunkOf(cx, cz);
        if (!published.TryGetValue((x, z), out var d)) return FlowDirections.None;
        return d[(cz - z * grid.ChunkSize) * grid.ChunkSize + cx - x * grid.ChunkSize];
    }

    /// <summary>Stops the field; agents fall back to their baked fields.</summary>
    public void Stop()
    {
        cancel?.Cancel();
        Active = false;
        published.Clear();
    }

    /// <summary>
    /// Starts a flee field from <paramref name="danger"/> over the chunks loaded now. Cells within
    /// <paramref name="dangerRadius"/> cost more the closer they are, so routes bend away from it.
    /// </summary>
    public void StartFlee(Vector3 danger, float dangerRadius = 40)
    {
        Stop();
        var token = (cancel = new CancellationTokenSource()).Token;
        Interlocked.Increment(ref generation);
        Active = true; Complete = false; Source = danger; Refuge = danger;
        // A snapshot: chunks streamed in later join the next start; chunks streamed out keep their data until then.
        var chunks = new List<FlowChunk>(grid.Chunks);
        ChunksTotal = chunks.Count;
        var previous = work;
        work = Task.Run(() =>
        {
            try { previous?.Wait(); } catch { }
            Run(chunks, danger, dangerRadius, token);
        });
    }

    void Run(List<FlowChunk> chunks, Vector3 danger, float dangerRadius, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int n = grid.ChunkSize;
        var byKey = new Dictionary<(int, int), FlowChunk>();
        foreach (var c in chunks) byKey[(c.X, c.Z)] = c;
        var (dx, dz) = grid.Cell(danger);
        var center = (X: (dx + .5f) / n, Z: (dz + .5f) / n);
        float Order(FlowChunk c) { float x = c.X + .5f - center.X, z = c.Z + .5f - center.Z; return x * x + z * z; }
        chunks.Sort((a, b) => Order(a).CompareTo(Order(b)));

        // Pass one: walking cost away from the danger, nearest chunks first, each seeded from its finished neighbours' borders.
        var away = new Dictionary<(int, int), int[]>();
        int padded = n + 2;
        var cost = new byte[padded * padded];
        var dist = new int[padded * padded];
        var penalty = new int[padded * padded];
        var directions = new byte[padded * padded];
        bool first = true;
        foreach (var c in chunks)
        {
            if (token.IsCancellationRequested) return;
            Gather(c, byKey, away, danger, dangerRadius, cost, dist, penalty, seedDanger: true, null);
            FlowIntegrator.Integrate(padded, padded, cost, dist, penalty);
            var own = new int[n * n];
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) own[z * n + x] = dist[(z + 1) * padded + x + 1];
            away[(c.X, c.Z)] = own;
            FlowIntegrator.Directions(padded, padded, cost, dist, directions, ascend: true);
            Publish(c, directions, token);
            if (first) { FirstChunkMilliseconds = clock.Elapsed.TotalMilliseconds; first = false; }
        }

        // The refuges: in each of eight directions around the danger, the reachable cell with the greatest walking cost
        // from it. Each person heads for the far refuge on their own side instead of all crossing to one place.
        const int Sectors = 8;
        var best = new (int Value, int X, int Z)[Sectors];
        for (int k = 0; k < Sectors; k++) best[k] = (-1, 0, 0);
        int farthest = 0;
        foreach (var (key, d) in away)
            for (int i = 0; i < d.Length; i++)
            {
                if (d[i] == FlowIntegrator.Unreached) continue;
                int cx = key.Item1 * n + i % n, cz = key.Item2 * n + i / n;
                float angle = MathF.Atan2(cz - dz, cx - dx);
                int sector = ((int)MathF.Floor((angle + MathF.PI) / MathF.Tau * Sectors) % Sectors + Sectors) % Sectors;
                if (d[i] > best[sector].Value) best[sector] = (d[i], cx, cz);
                farthest = Math.Max(farthest, d[i]);
            }
        var refuges = best.Where(b => b.Value >= farthest / 3).Select(b => (b.X, b.Z)).ToArray();
        if (refuges.Length == 0) { TotalMilliseconds = clock.Elapsed.TotalMilliseconds; Complete = true; return; }
        Refuges = refuges.Select(r => grid.Center(r.X, r.Z)).ToArray();
        Refuge = Refuges[0];

        // Pass two: toward the nearest refuge, chunks nearest a refuge first so each chunk is seeded from finished
        // neighbours. Until a chunk is redone its away-from-danger directions stand.
        var toward = new Dictionary<(int, int), int[]>();
        var refugeChunks = refuges.Select(r => grid.ChunkOf(r.X, r.Z)).ToArray();
        int RefugeDistance(FlowChunk c) => refugeChunks.Min(k => ChunkDistance(c, k));
        chunks.Sort((a, b) => RefugeDistance(a).CompareTo(RefugeDistance(b)));
        foreach (var c in chunks)
        {
            if (token.IsCancellationRequested) return;
            Gather(c, byKey, toward, danger, dangerRadius * 2, cost, dist, penalty, seedDanger: false, refuges);
            FlowIntegrator.Integrate(padded, padded, cost, dist, penalty);
            var own = new int[n * n];
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++) own[z * n + x] = dist[(z + 1) * padded + x + 1];
            toward[(c.X, c.Z)] = own;
            FlowIntegrator.Directions(padded, padded, cost, dist, directions);
            // Where pass two found no way (cut off from every refuge), keep running away.
            if (published.TryGetValue((c.X, c.Z), out var old))
                for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                    if (directions[(z + 1) * padded + x + 1] == FlowDirections.None) directions[(z + 1) * padded + x + 1] = old[z * n + x];
            Publish(c, directions, token);
        }
        TotalMilliseconds = clock.Elapsed.TotalMilliseconds;
        Complete = true;
    }

    static bool IsRefuge((int X, int Z)[] refuges, int x, int z)
    {
        foreach (var r in refuges) if (r.X == x && r.Z == z) return true;
        return false;
    }

    static int ChunkDistance(FlowChunk c, (int X, int Z) k) => Math.Max(Math.Abs(c.X - k.X), Math.Abs(c.Z - k.Z));

    /// <summary>Fills the padded working grid of a chunk: its costs, a one-cell ring of its neighbours' costs, the
    /// danger price, and seeds from the source and from the finished neighbours' distances on that ring.</summary>
    void Gather(FlowChunk c, Dictionary<(int, int), FlowChunk> byKey, Dictionary<(int, int), int[]> done, Vector3 danger, float radius,
        byte[] cost, int[] dist, int[] penalty, bool seedDanger, (int X, int Z)[] refuges)
    {
        int n = grid.ChunkSize, padded = n + 2;
        var (dx, dz) = grid.Cell(danger);
        float radiusCells = radius / grid.CellSize;
        for (int pz = 0; pz < padded; pz++)
            for (int px = 0; px < padded; px++)
            {
                int gx = c.X * n + px - 1, gz = c.Z * n + pz - 1;
                int i = pz * padded + px;
                var (kx, kz) = grid.ChunkOf(gx, gz);
                byKey.TryGetValue((kx, kz), out var owner);
                int local = owner == null ? -1 : (gz - kz * n) * n + gx - kx * n;
                cost[i] = owner == null ? (byte)0 : owner.Cost[local];
                dist[i] = FlowIntegrator.Unreached;
                bool ring = px == 0 || pz == 0 || px == padded - 1 || pz == padded - 1;
                // A finished neighbour's distances seed the ring; an unfinished one leaves it to be filled by this chunk.
                if (ring && owner != null && done.TryGetValue((kx, kz), out var d)) dist[i] = d[local];
                float ddx = gx - dx, ddz = gz - dz, r = MathF.Sqrt(ddx * ddx + ddz * ddz);
                penalty[i] = r < radiusCells ? (int)(400 * (1 - r / radiusCells)) : 0;
                if (seedDanger ? (gx == dx && gz == dz) : IsRefuge(refuges, gx, gz)) dist[i] = 0;
            }
        // When the danger stands on a blocked cell (inside a car, on a wall) the nearest open cells take the seed.
        if (seedDanger && c.X * n <= dx && dx < c.X * n + n && c.Z * n <= dz && dz < c.Z * n + n)
        {
            int i = (dz - c.Z * n + 1) * padded + dx - c.X * n + 1;
            if (cost[i] == 0)
                for (int r = 1; r < 6; r++)
                {
                    bool any = false;
                    for (int oz = -r; oz <= r; oz++) for (int ox = -r; ox <= r; ox++)
                    {
                        int x = dx - c.X * n + 1 + ox, z = dz - c.Z * n + 1 + oz;
                        if ((uint)x < (uint)padded && (uint)z < (uint)padded && cost[z * padded + x] != 0) { dist[z * padded + x] = r * 10; any = true; }
                    }
                    if (any) break;
                }
        }
    }

    void Publish(FlowChunk c, byte[] padded, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        int n = grid.ChunkSize, p = n + 2;
        var own = new byte[n * n];
        for (int z = 0; z < n; z++) Array.Copy(padded, (z + 1) * p + 1, own, z * n, n);
        published[(c.X, c.Z)] = own;
    }

    public void Dispose() { cancel?.Cancel(); try { work?.Wait(1000); } catch { } }
}
