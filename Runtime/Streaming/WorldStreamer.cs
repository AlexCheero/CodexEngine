using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace CodexEngine.Streaming;

/// <summary>A square cell of one detail level. Level 0 is the finest; each level has its own cell size.</summary>
public readonly record struct CellKey(int X, int Z, int Level)
{
    public override string ToString() => $"L{Level}:{X},{Z}";
}

/// <summary>Where a cell stands. Only <see cref="Active"/> cells are in the game's world.</summary>
public enum CellState : byte { Queued, Loading, Loaded, Active, Failed }

/// <summary>
/// Supplies a world's cells to <see cref="WorldStreamer{T}"/>. <see cref="Load"/> runs on the streamer's worker
/// threads and does everything that needs no device or simulation: file reads, decoding, mesh and collision-shape
/// preparation. The other members run on the thread that calls <see cref="WorldStreamer{T}.Update"/>.
/// </summary>
public interface ICellSource<T>
{
    /// <summary>Whether the world has content in this cell. Called often, so answer from a manifest.</summary>
    bool Exists(CellKey key);
    /// <summary>Reads and prepares a cell. Throwing marks the cell failed; it is not retried until it streams out and back in.</summary>
    T Load(CellKey key, CancellationToken cancel);
    /// <summary>Puts a loaded cell into the world (GPU uploads, physics bodies). Returns its cost in the units of
    /// <see cref="StreamingSettings.ActivationBudget"/>, so a frame stops activating once the budget is spent.</summary>
    int Activate(CellKey key, T payload);
    /// <summary>Takes an active cell out of the world and releases it.</summary>
    void Deactivate(CellKey key, T payload);
    /// <summary>Releases a cell that loaded but was no longer wanted before it was activated.</summary>
    void Discard(CellKey key, T payload);
}

/// <summary>One ring of detail: cells of <see cref="CellSize"/> stream in within <see cref="LoadRadius"/> of the focus
/// and out beyond <see cref="UnloadRadius"/>, which is larger so a focus moving along a border does not thrash.</summary>
public readonly record struct StreamingLevel(float CellSize, float LoadRadius, float UnloadRadius)
{
    /// <summary>An inner radius inside which this level is not wanted, because a finer level covers it.
    /// Zero keeps the level everywhere in its ring, so a coarse cell can stand in while finer ones load.</summary>
    public float InnerRadius { get; init; }
}

public sealed class StreamingSettings
{
    /// <summary>Detail levels, finest first.</summary>
    public StreamingLevel[] Levels { get; init; } = [new(128, 400, 480)];
    /// <summary>World-space origin of cell (0,0) on the ground plane (X and Z).</summary>
    public Vector2 Origin { get; init; }
    /// <summary>Worker threads reading cells.</summary>
    public int Workers { get; init; } = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
    /// <summary>Cost units <see cref="ICellSource{T}.Activate"/> may spend per <see cref="WorldStreamer{T}.Update"/>.
    /// At least one cell is activated per update whatever its cost.</summary>
    public int ActivationBudget { get; init; } = 1;
    /// <summary>Seconds of the focus's velocity added to its position to find the cells wanted next.</summary>
    public float Lookahead { get; init; } = 1.5f;
}

/// <summary>A point the world streams around: a player, or a server's view of one. Velocity is per second.</summary>
public readonly record struct StreamingFocus(Vector3 Position, Vector3 Velocity);

/// <summary>Counters for an overlay or a log line.</summary>
public readonly record struct StreamingStats(int Active, int Loaded, int Loading, int Queued, int Failed,
    long LoadsCompleted, double LastLoadMilliseconds, double AverageLoadMilliseconds);

/// <summary>
/// Streams a large world around a moving focus in square cells of one or more detail levels. Each <see cref="Update"/>
/// works out the cells wanted around the focus (and where its velocity takes it), queues the missing ones nearest
/// first for the worker threads, activates finished cells within a per-frame budget and deactivates cells that fell
/// outside their unload radius. Cells no longer wanted while queued are dropped, and while loading are cancelled.
/// The streamer owns no content: an <see cref="ICellSource{T}"/> reads, activates and releases it.
/// </summary>
public sealed class WorldStreamer<T> : IDisposable
{
    sealed class Cell
    {
        public CellKey Key;
        public CellState State;
        public float Distance;
        public bool Wanted;
        public T Payload;
        public CancellationTokenSource Cancel;
        public Exception Error;
    }

    readonly ICellSource<T> source;
    readonly StreamingSettings settings;
    readonly Dictionary<CellKey, Cell> cells = new();
    readonly List<Cell> queue = new();      // wanted, not yet taken by a worker; sorted nearest last
    readonly List<Cell> finished = new();   // loaded by a worker, waiting for the game thread
    readonly List<Cell> scratch = new();
    readonly List<(CellKey Key, T Payload)> active = new();
    readonly object gate = new();
    readonly Thread[] workers;
    bool stopping;
    int loading;
    long completed;
    double lastMs, totalMs;

    public WorldStreamer(ICellSource<T> source, StreamingSettings settings)
    {
        this.source = source;
        this.settings = settings;
        if (settings.Levels.Length == 0) throw new ArgumentException("At least one streaming level is needed.");
        workers = new Thread[Math.Max(1, settings.Workers)];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = new Thread(Work) { IsBackground = true, Name = "World streaming " + i, Priority = ThreadPriority.BelowNormal };
            workers[i].Start();
        }
    }

    public StreamingSettings Settings => settings;

    /// <summary>The active cells and their payloads, valid until the next <see cref="Update"/>.</summary>
    public IReadOnlyList<(CellKey Key, T Payload)> Active => active;

    public CellKey CellAt(Vector3 position, int level)
    {
        float size = settings.Levels[level].CellSize;
        return new((int)MathF.Floor((position.X - settings.Origin.X) / size), (int)MathF.Floor((position.Z - settings.Origin.Y) / size), level);
    }

    /// <summary>The ground-plane bounds of a cell: minimum X and Z, and its size.</summary>
    public (Vector2 Min, float Size) Bounds(CellKey key)
    {
        float size = settings.Levels[key.Level].CellSize;
        return (settings.Origin + new Vector2(key.X * size, key.Z * size), size);
    }

    public bool IsActive(CellKey key) => cells.TryGetValue(key, out var c) && c.State == CellState.Active;

    public bool TryGetActive(CellKey key, out T payload)
    {
        if (cells.TryGetValue(key, out var c) && c.State == CellState.Active) { payload = c.Payload; return true; }
        payload = default;
        return false;
    }

    /// <summary>
    /// Whether the finest level has the cell under <paramref name="position"/> active, or the world has nothing there.
    /// Gameplay can hold a body still until its ground has streamed in.
    /// </summary>
    public bool IsReady(Vector3 position)
    {
        var key = CellAt(position, 0);
        return !source.Exists(key) || IsActive(key);
    }

    /// <summary>
    /// For hierarchical levels of detail: how many cells of level <c>key.Level-1</c> span one side of
    /// <paramref name="key"/> (its cell size divided by the finer level's, at most 8).
    /// </summary>
    public int ChildrenPerSide(CellKey key)
    {
        if (key.Level == 0) return 0;
        int n = (int)MathF.Round(settings.Levels[key.Level].CellSize / settings.Levels[key.Level - 1].CellSize);
        if (n < 1 || n > 8) throw new InvalidOperationException("A coarse cell must be 1 to 8 finer cells wide.");
        return n;
    }

    /// <summary>
    /// Which cells of the next finer level inside a coarse cell are active, one bit per child at
    /// <c>z * ChildrenPerSide + x</c>. A renderer draws the coarse cell's content only for children whose bit is clear,
    /// so the two levels never draw the same ground and a coarse cell keeps covering a child until it is in.
    /// A child the world has no content for counts as active only when the coarse cell holds no content for it either,
    /// which the source decides; here it is simply clear.
    /// </summary>
    public ulong ActiveChildren(CellKey key)
    {
        int n = ChildrenPerSide(key);
        ulong mask = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
                if (IsActive(new CellKey(key.X * n + x, key.Z * n + z, key.Level - 1))) mask |= 1UL << (z * n + x);
        return mask;
    }

    public StreamingStats Stats
    {
        get
        {
            int a = 0, l = 0, q = 0, f = 0;
            foreach (var c in cells.Values)
                switch (c.State)
                {
                    case CellState.Active: a++; break;
                    case CellState.Loaded: l++; break;
                    case CellState.Queued: q++; break;
                    case CellState.Failed: f++; break;
                }
            long done = Interlocked.Read(ref completed);
            return new(a, l, Volatile.Read(ref loading), q, f, done, lastMs, done > 0 ? totalMs / done : 0);
        }
    }

    /// <summary>
    /// One streaming step on the game thread. <paramref name="velocity"/> (per second) extends the wanted area toward
    /// where the focus is heading, so a fast car finds the road ahead already loaded.
    /// </summary>
    public void Update(Vector3 focus, Vector3 velocity)
    {
        single[0] = new(focus, velocity);
        Update(single);
    }
    readonly StreamingFocus[] single = new StreamingFocus[1];

    /// <summary>
    /// One streaming step for several foci at once (every player a server simulates): a cell is wanted when any focus
    /// wants it, and its priority is its distance from the nearest.
    /// </summary>
    public void Update(ReadOnlySpan<StreamingFocus> foci)
    {
        DrainDiscarded();
        foreach (var c in cells.Values) { c.Wanted = false; c.Distance = float.MaxValue; }

        foreach (var f in foci)
        {
        var focus = f.Position;
        var ahead = focus + f.Velocity * settings.Lookahead;
        // Wanted cells, with their distance from the nearer of the focus and the lookahead point.
        for (int level = 0; level < settings.Levels.Length; level++)
        {
            var ring = settings.Levels[level];
            float reach = ring.UnloadRadius;
            var lo = CellAt(Vector3.Min(focus, ahead) - new Vector3(reach), level);
            var hi = CellAt(Vector3.Max(focus, ahead) + new Vector3(reach), level);
            for (int z = lo.Z; z <= hi.Z; z++)
                for (int x = lo.X; x <= hi.X; x++)
                {
                    var key = new CellKey(x, z, level);
                    float d = Math.Min(Distance(key, focus), Distance(key, ahead));
                    float far = FarDistance(key, focus);
                    bool exists = cells.TryGetValue(key, out var cell);
                    // A cell already in stays until it passes the unload radius; a new one needs the load radius.
                    float limit = exists ? ring.UnloadRadius : ring.LoadRadius;
                    // Within the inner radius a finer level covers the whole cell, so this level can go.
                    bool covered = ring.InnerRadius > 0 && far < (exists ? ring.InnerRadius * .8f : ring.InnerRadius);
                    if (d > limit || covered) continue;
                    if (!exists)
                    {
                        if (!source.Exists(key)) continue;
                        cells.Add(key, cell = new Cell { Key = key, State = CellState.Queued, Distance = float.MaxValue });
                        lock (gate) queue.Add(cell);
                    }
                    cell.Wanted = true;
                    // Finer levels first at equal distance: they are what the player stands on.
                    cell.Distance = Math.Min(cell.Distance, d + level * 32);
                }
        }
        }

        // Retire cells that are no longer wanted.
        scratch.Clear();
        foreach (var c in cells.Values) if (!c.Wanted) scratch.Add(c);
        lock (gate)
        {
            foreach (var c in scratch)
            {
                switch (c.State)
                {
                    case CellState.Queued: queue.Remove(c); break;
                    case CellState.Loading: c.Cancel?.Cancel(); break; // the worker discards it
                    case CellState.Loaded: finished.Remove(c); source.Discard(c.Key, c.Payload); break;
                    case CellState.Active: source.Deactivate(c.Key, c.Payload); break;
                }
                c.Payload = default;
                cells.Remove(c.Key);
            }
            // Nearest last, so workers take from the end.
            queue.Sort(static (a, b) => b.Distance.CompareTo(a.Distance));
            if (queue.Count > 0) Monitor.PulseAll(gate);
        }

        // Activate finished cells, nearest first, within the budget.
        scratch.Clear();
        lock (gate) { scratch.AddRange(finished); finished.Clear(); }
        scratch.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int spent = 0, i = 0;
        for (; i < scratch.Count; i++)
        {
            var c = scratch[i];
            if (c.Error != null) { c.State = CellState.Failed; continue; }
            if (spent >= settings.ActivationBudget && i > 0) break;
            spent += Math.Max(0, source.Activate(c.Key, c.Payload));
            c.State = CellState.Active;
        }
        if (i < scratch.Count) lock (gate) for (; i < scratch.Count; i++) finished.Add(scratch[i]);

        active.Clear();
        foreach (var c in cells.Values) if (c.State == CellState.Active) active.Add((c.Key, c.Payload));
    }

    /// <summary>
    /// Streams in everything the focus wants and returns once it is all active, for a loading screen or a teleport.
    /// Gives up after <paramref name="timeout"/> and returns false.
    /// </summary>
    public bool Preload(Vector3 focus, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Update(focus, Vector3.Zero);
            bool pending = false;
            foreach (var c in cells.Values) pending |= c.State is CellState.Queued or CellState.Loading or CellState.Loaded;
            if (!pending) return true;
            if (clock.Elapsed > timeout) return false;
            Thread.Sleep(1);
        }
    }

    float Distance(CellKey key, Vector3 p)
    {
        var (min, size) = Bounds(key);
        float dx = Math.Max(0, Math.Max(min.X - p.X, p.X - (min.X + size)));
        float dz = Math.Max(0, Math.Max(min.Y - p.Z, p.Z - (min.Y + size)));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    float FarDistance(CellKey key, Vector3 p)
    {
        var (min, size) = Bounds(key);
        float dx = Math.Max(Math.Abs(min.X - p.X), Math.Abs(min.X + size - p.X));
        float dz = Math.Max(Math.Abs(min.Y - p.Z), Math.Abs(min.Y + size - p.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    void Work()
    {
        while (true)
        {
            Cell cell;
            CancellationToken token;
            lock (gate)
            {
                while (!stopping && queue.Count == 0) Monitor.Wait(gate);
                if (stopping) return;
                cell = queue[^1];
                queue.RemoveAt(queue.Count - 1);
                cell.State = CellState.Loading;
                cell.Cancel = new CancellationTokenSource();
                token = cell.Cancel.Token;
                loading++;
            }
            var clock = Stopwatch.StartNew();
            T payload = default;
            Exception error = null;
            try { payload = source.Load(cell.Key, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) { error = e; Trace.TraceError($"World streaming: {cell.Key} failed: {e}"); }
            double ms = clock.Elapsed.TotalMilliseconds;
            lock (gate)
            {
                loading--;
                cell.Cancel.Dispose(); cell.Cancel = null;
                if (token.IsCancellationRequested || stopping)
                {
                    // Retired while it loaded: the game thread no longer knows it.
                    if (payload != null) discarded.Add((cell.Key, payload));
                    continue;
                }
                cell.Payload = payload;
                cell.Error = error;
                cell.State = CellState.Loaded;
                finished.Add(cell);
                lastMs = ms; totalMs += ms;
                Interlocked.Increment(ref completed);
            }
        }
    }

    // Payloads finished after their cell was retired, released on the game thread by DrainDiscarded.
    readonly List<(CellKey, T)> discarded = new();

    /// <summary>Releases cells that finished loading after they were no longer wanted. <see cref="Update"/> calls it.</summary>
    void DrainDiscarded()
    {
        lock (gate)
        {
            foreach (var (key, payload) in discarded) source.Discard(key, payload);
            discarded.Clear();
        }
    }

    /// <summary>Stops the workers and deactivates every cell.</summary>
    public void Dispose()
    {
        lock (gate) { stopping = true; foreach (var c in cells.Values) c.Cancel?.Cancel(); Monitor.PulseAll(gate); }
        foreach (var t in workers) t.Join();
        DrainDiscarded();
        foreach (var c in cells.Values)
        {
            if (c.State == CellState.Active) source.Deactivate(c.Key, c.Payload);
            else if (c.State == CellState.Loaded) source.Discard(c.Key, c.Payload);
        }
        cells.Clear(); active.Clear(); queue.Clear(); finished.Clear();
    }
}
