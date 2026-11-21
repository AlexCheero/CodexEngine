using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CodexECS;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LightFlash = CodexEngine.Runtime.LightFlash;
using LightTraits = CodexEngine.Runtime.LightTraits;
using LocalLight = CodexEngine.Runtime.LocalLight;

namespace CodexEngine.Rendering;

/// <summary>
/// The frame's local lights for Shaders/LocalLights.hlsl, sorted into clusters. The view frustum is cut into
/// columns, rows and depth slices, and each cluster lists the lights that reach it, so a pixel finds its cluster
/// from its world position and loops over those lights alone, however many the level or the frame holds.
/// Lights may move, appear and vanish every frame: gather them with <see cref="Begin"/>, <see cref="Add"/> and
/// <see cref="Collect"/>, then call <see cref="Build"/> for the view about to be drawn and <see cref="Bind"/> for
/// each effect that draws in it. A second view of the same frame (a mirror, a reflection probe's face) calls
/// <see cref="Build"/> again before its own draws. Building sorts on the job threads it is given.
/// </summary>
public sealed class ClusteredLights : IDisposable
{
    public const int Columns = 16, Rows = 9, Slices = 24;
    /// <summary>The most lights a cluster can list. A cluster more lights reach keeps the ones nearest for their range.</summary>
    public const int MaxPerCluster = 16;

    // One texture holds everything, in whole floats: a list per cluster, four lights to a texel, and below the
    // lists the light table at four texels a light. A list names the lights outside the baked lighting first and
    // then, as negative numbers, the baked ones, so a surface that already has baked light stops at the first of those.
    const int Pages = MaxPerCluster / 4, Width = Columns * Pages, LightsPerRow = Width / 4;
    const int ListRows = Rows * Slices, ListFloats = ListRows * Width * 4, ClusterFloats = MaxPerCluster;
    // Slack on a light's radius, for what the cluster bounds leave out: a projection offset by a sub-pixel jitter.
    const float Slack = 1.01f;

    struct Gathered
    {
        public Vector3 Position, Color, Direction;
        public float Range, OuterCos, ConeScale, ShadowChannel;
        public bool Baked, CastsShadows;
        public long Key;
    }

    struct Seen
    {
        // The bounding sphere and the light itself in view space: x, y and the depth along the view axis.
        public Vector3 Bound, At;
        public float Radius, InverseRangeSquared;
        public int X0, X1, Y0, Y1, Z0, Z1;
        public bool Baked;
    }

    sealed class Source(EcsWorld world)
    {
        public readonly FilterRows<LocalLight> Lights = world.Filter().With<LocalLight>().Without<LightFlash>().Build(false).Rows<LocalLight>();
        public readonly FilterRows<LocalLight, LightFlash> Flashes = world.Filter().With<LocalLight>().With<LightFlash>().Build(false).Rows<LocalLight, LightFlash>();
        public readonly long Tag = Interlocked.Increment(ref tags) << 32;
    }

    static long tags;

    readonly GraphicsDevice gpu;
    readonly Texture2D texture;
    readonly Vector4[] texels;
    readonly float[] scores = new float[ListFloats];
    // Per cluster: lights listed, and how many of them are outside the baked lighting.
    readonly byte[] counts = new byte[2 * ListRows * Columns];
    readonly int capacity, perCluster, height;
    readonly ConditionalWeakTable<EcsWorld, Source> sources = new();
    readonly BoundingFrustum frustum = new(Matrix.Identity);
    readonly float[] columnSlope = new float[Columns + 1], rowSlope = new float[Rows + 1], sliceDepth = new float[Slices + 1];
    readonly Action<int, int> sort;
    Gathered[] gathered = new Gathered[64];
    (float Order, int Index)[] order = new (float, int)[64];
    readonly Seen[] seen;
    readonly int[] seenSource;
    readonly int[] shadowSlots;
    int gatheredCount, seenCount, crowded;
    Matrix viewProjection = Matrix.Identity;

    /// <summary>
    /// Where the depth slices begin and end, in world units from the eye. Everything nearer shares the first
    /// slice and everything farther the last, so lights are still found there, only less finely.
    /// </summary>
    public float Near { get; set; } = .5f;
    public float Far { get; set; } = 250;

    /// <summary>Lights in the view the last <see cref="Build"/> sorted.</summary>
    public int Visible => seenCount;

    /// <summary>
    /// How many times the last <see cref="Build"/> had a light reach a cluster that was already full and had to
    /// leave one out. Zero means every pixel is lit by every light that reaches it. A level that keeps this above
    /// zero has more lights overlapping in one place than a cluster lists: shorten their ranges or thin them out.
    /// </summary>
    public int Crowded => crowded;

    /// <summary>
    /// Leaves the clusters empty, for a view that no light should reach: a menu's preview, a view drawn before the
    /// frame's lights are known. <see cref="Bind"/> then binds nothing lit.
    /// </summary>
    public void BuildEmpty()
    {
        seenCount = 0;
        crowded = 0;
    }

    /// <param name="capacity">The most lights one view can hold. Past it the view keeps the nearest for their range.</param>
    /// <param name="perCluster">The most lights one cluster lists, up to <see cref="MaxPerCluster"/>.</param>
    public ClusteredLights(GraphicsDevice gpu, int capacity = 256, int perCluster = MaxPerCluster)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        this.gpu = gpu;
        this.capacity = (capacity + LightsPerRow - 1) / LightsPerRow * LightsPerRow;
        this.perCluster = Math.Clamp(perCluster, 1, MaxPerCluster);
        height = ListRows + this.capacity / LightsPerRow;
        texels = new Vector4[Width * height];
        texture = new Texture2D(gpu, Width, height, false, SurfaceFormat.Vector4);
        texture.SetData(texels);
        seen = new Seen[this.capacity];
        seenSource = new int[this.capacity];
        shadowSlots = new int[this.capacity];
        sort = Sort;
    }

    /// <summary>Starts the frame's list of lights.</summary>
    public void Begin() => gatheredCount = 0;

    /// <summary>
    /// Adds one light to the frame. <paramref name="scale"/> dims its colour. <paramref name="key"/> names the
    /// light from frame to frame, for what keeps something per light (its shadow map); zero for a light with no
    /// such name.
    /// </summary>
    public void Add(in LocalLight light, float scale = 1, long key = 0)
    {
        if (!(light.Range > 0) || !(scale > 0)) return;
        var color = new Vector3(light.Color.X, light.Color.Y, light.Color.Z) * scale;
        if (color == Vector3.Zero) return;
        if (gatheredCount == gathered.Length) Array.Resize(ref gathered, gatheredCount * 2);
        ref var g = ref gathered[gatheredCount++];
        g.Position = new(light.Position.X, light.Position.Y, light.Position.Z);
        g.Color = color;
        g.Range = light.Range;
        g.Baked = (light.Traits & LightTraits.Baked) != 0;
        g.CastsShadows = (light.Traits & LightTraits.CastsShadows) != 0;
        g.ShadowChannel = light.ShadowChannel;
        g.Key = key;
        if ((light.Traits & LightTraits.Spot) != 0)
        {
            float outer = MathF.Cos(MathHelper.ToRadians(light.OuterAngle * .5f));
            float inner = MathF.Cos(MathHelper.ToRadians(light.InnerAngle * .5f));
            g.Direction = new(light.Direction.X, light.Direction.Y, light.Direction.Z);
            g.OuterCos = outer;
            g.ConeScale = 1 / MathF.Max(.001f, inner - outer);
        }
        else { g.Direction = Vector3.Zero; g.OuterCos = -2; g.ConeScale = 1; }
    }

    /// <summary>
    /// Adds every <see cref="LocalLight"/> of a world. A light with a <see cref="LightFlash"/> is dimmed by where
    /// <paramref name="now"/> falls in the flash, and left out once it has ended.
    /// </summary>
    public void Collect(EcsWorld world, float now = 0)
    {
        var source = sources.GetValue(world, static w => new Source(w));
        foreach (var row in source.Lights) Add(in row.Item1, 1, source.Tag | (uint)row.Id);
        foreach (var row in source.Flashes) Add(in row.Item1, row.Item2.Strength(now), source.Tag | (uint)row.Id);
    }

    /// <summary>
    /// Sorts the frame's lights into the clusters of one view and uploads them. <paramref name="view"/> and
    /// <paramref name="projection"/> are that view's own; <paramref name="jobs"/> shares the sorting between its
    /// threads, slice by slice, with the same result on any number of them.
    /// </summary>
    public void Build(Matrix view, Matrix projection, JobScheduler jobs = null)
    {
        viewProjection = view * projection;
        frustum.Matrix = viewProjection;
        float near = MathF.Max(Near, 1e-3f), far = MathF.Max(Far, near * 2);
        float slicesPerOctave = Slices / MathF.Log2(far / near);
        for (int i = 0; i <= Columns; i++) columnSlope[i] = (2f * i / Columns - 1) / projection.M11;
        for (int i = 0; i <= Rows; i++) rowSlope[i] = (2f * i / Rows - 1) / projection.M22;
        for (int i = 0; i <= Slices; i++) sliceDepth[i] = near * MathF.Pow(far / near, (float)i / Slices);

        // The lights this view sees, nearest for their range first: past the capacity the far ones are left out.
        int candidates = 0;
        var eye = Matrix.Invert(view).Translation;
        if (order.Length < gatheredCount) order = new (float, int)[gathered.Length];
        for (int i = 0; i < gatheredCount; i++)
        {
            ref readonly var g = ref gathered[i];
            if (frustum.Intersects(new BoundingSphere(g.Position, g.Range)))
                order[candidates++] = (Vector3.Distance(g.Position, eye) - g.Range, i);
        }
        if (candidates > capacity) Array.Sort(order, 0, candidates);
        seenCount = Math.Min(candidates, capacity);
        var table = texels.AsSpan(ListRows * Width);
        for (int i = 0; i < seenCount; i++)
        {
            int source = order[i].Index;
            ref readonly var g = ref gathered[source];
            seenSource[i] = source;
            ref var s = ref seen[i];
            s.At = ToView(g.Position, in view, in projection);
            s.InverseRangeSquared = 1 / (g.Range * g.Range);
            s.Baked = g.Baked;
            // A spot is bounded by the sphere around its cone, which is smaller than the one around its range.
            Vector3 centre = g.Position; float radius = g.Range;
            if (g.OuterCos > .7072f) { radius = g.Range / (2 * g.OuterCos * g.OuterCos); centre += g.Direction * radius; }
            else if (g.OuterCos > 0) { radius = g.Range * MathF.Sqrt(1 - g.OuterCos * g.OuterCos); centre += g.Direction * (g.Range * g.OuterCos); }
            radius *= Slack;
            s.Bound = ToView(centre, in view, in projection);
            s.Radius = radius;
            Extent(s.Bound.X, s.Bound.Z, radius, projection.M11, Columns, out s.X0, out s.X1);
            Extent(s.Bound.Y, s.Bound.Z, radius, projection.M22, Rows, out s.Y0, out s.Y1);
            s.Z0 = SliceOf(s.Bound.Z - radius, near, slicesPerOctave);
            s.Z1 = SliceOf(s.Bound.Z + radius, near, slicesPerOctave);
            int slot = i * 4;
            table[slot] = new Vector4(g.Position, s.InverseRangeSquared);
            // What the light needs beyond its place and colour: 1 to 4 for a shadow mask channel, 8 for a cone.
            // A real-time shadow adds 16 (SetShadowSlot). Below 8 a pixel reads two texels and is done.
            table[slot + 1] = new Vector4(g.Color, Math.Clamp(g.ShadowChannel, -1, 3) + 1 + (g.OuterCos > -1 ? 8 : 0));
            table[slot + 2] = new Vector4(g.Direction, g.OuterCos);
            table[slot + 3] = new Vector4(g.ConeScale, shadowSlots[i] = -1, 0, 0);
        }
        crowded = 0;
        if (seenCount == 0) return;
        if (jobs != null) jobs.Run(Slices, 1, sort);
        else Sort(0, Slices);
        gridInfo = new Vector4(Columns, Rows, Slices, slicesPerOctave);
        texelInfo = new Vector4(1f / Width, 1f / height, ListRows, 1 / near);
        uploaded = false;
    }

    Vector4 gridInfo, texelInfo;
    bool uploaded = true;

    /// <summary>
    /// Names the shadow map a light of the last <see cref="Build"/> reads (LocalShadows.hlsl), or -1 for none.
    /// <paramref name="index"/> counts the view's lights as <see cref="Visible"/> does.
    /// </summary>
    public void SetShadowSlot(int index, int slot)
    {
        if (shadowSlots[index] == slot) return;
        int row = ListRows * Width + index * 4;
        ref float kind = ref texels[row + 1].W;
        if (shadowSlots[index] >= 0) kind -= 16;
        if (slot >= 0) kind += 16;
        shadowSlots[index] = slot;
        texels[row + 3].Y = slot;
        uploaded = false;
    }

    /// <summary>One light of the last <see cref="Build"/>, for whoever hands out shadow maps.</summary>
    public void Describe(int index, out Vector3 position, out float range, out Vector3 direction, out float outerCos,
        out bool castsShadows, out long key)
    {
        ref readonly var g = ref gathered[seenSource[index]];
        position = g.Position; range = g.Range; direction = g.Direction; outerCos = g.OuterCos;
        castsShadows = g.CastsShadows; key = g.Key;
    }

    /// <summary>Whether a light of the last <see cref="Build"/> has its shadow in a baked shadow mask.</summary>
    public bool HasShadowMask(int index) => gathered[seenSource[index]].ShadowChannel >= 0;

    static Vector3 ToView(Vector3 world, in Matrix view, in Matrix projection)
    {
        var v = Vector3.Transform(world, view);
        // Depth along the view axis is the w the projection gives the point, whatever its handedness.
        return new Vector3(v.X, v.Y, v.X * projection.M14 + v.Y * projection.M24 + v.Z * projection.M34 + projection.M44);
    }

    // The columns (or rows) a sphere can touch: the tangents from the eye to its circle in the plane of one screen
    // axis and the view axis. A tangent that passes behind the eye leaves that side open.
    static void Extent(float centre, float depth, float radius, float scale, int count, out int first, out int last)
    {
        float lo = -1, hi = 1, l2 = centre * centre + depth * depth;
        if (l2 > radius * radius)
        {
            float t = MathF.Sqrt(l2 - radius * radius);
            float below = depth * t + radius * centre, above = depth * t - radius * centre;
            if (below > 0) lo = (centre * t - radius * depth) / below * scale;
            if (above > 0) hi = (centre * t + radius * depth) / above * scale;
        }
        first = Math.Clamp((int)MathF.Floor((lo * .5f + .5f) * count), 0, count - 1);
        last = Math.Clamp((int)MathF.Floor((hi * .5f + .5f) * count), 0, count - 1);
    }

    static int SliceOf(float depth, float near, float slicesPerOctave) =>
        depth <= near ? 0 : Math.Clamp((int)MathF.Floor(MathF.Log2(depth / near) * slicesPerOctave), 0, Slices - 1);

    // Fills the clusters of the depth slices [first, end): nothing outside them is touched, so slices sort in parallel.
    void Sort(int first, int end)
    {
        var lists = MemoryMarshal.Cast<Vector4, float>(texels.AsSpan(0, ListRows * Width));
        lists.Slice(first * Rows * Width * 4, (end - first) * Rows * Width * 4).Clear();
        counts.AsSpan(first * Rows * Columns * 2, (end - first) * Rows * Columns * 2).Clear();
        int left = 0;
        // The lights outside the baked lighting come first in every list, so they are sorted first.
        for (int baked = 0; baked < 2; baked++)
        for (int i = 0; i < seenCount; i++)
        {
            ref readonly var s = ref seen[i];
            if (s.Baked != (baked == 1)) continue;
            int z0 = Math.Max(s.Z0, first), z1 = Math.Min(s.Z1, end - 1);
            float number = baked == 1 ? -(i + 1) : i + 1, reach2 = s.Radius * s.Radius;
            for (int z = z0; z <= z1; z++)
            {
                // The first slice reaches back to the eye and the last one has no far end.
                float front = z == 0 ? 0 : sliceDepth[z], back = z == Slices - 1 ? float.MaxValue : sliceDepth[z + 1];
                float dz = MathF.Max(0, MathF.Max(front - s.Bound.Z, s.Bound.Z - back));
                if (dz > s.Radius) continue;
                float middle = .5f * (sliceDepth[z] + sliceDepth[z + 1]);
                for (int y = s.Y0; y <= s.Y1; y++)
                {
                    float y0 = rowSlope[y], y1 = rowSlope[y + 1];
                    float dy = MathF.Max(0, MathF.Max(MathF.Min(y0 * front, y0 * back) - s.Bound.Y, s.Bound.Y - MathF.Max(y1 * front, y1 * back)));
                    float dyz = dy * dy + dz * dz;
                    if (dyz > reach2) continue;
                    // Along a row the clusters a sphere touches are one run: find its two ends, and what lies
                    // between needs no test. A large light fills whole rows this way.
                    float span = MathF.Sqrt(reach2 - dyz), low = s.Bound.X - span, high = s.Bound.X + span;
                    int xa = s.X0, xb = s.X1;
                    while (xa <= xb && MathF.Max(columnSlope[xa + 1] * front, columnSlope[xa + 1] * back) < low) xa++;
                    while (xb >= xa && MathF.Min(columnSlope[xb] * front, columnSlope[xb] * back) > high) xb--;
                    float cy = .5f * (y0 + y1) * middle - s.At.Y, cz = middle - s.At.Z, cyz = cy * cy + cz * cz;
                    int cluster = (z * Rows + y) * Columns + xa;
                    for (int x = xa; x <= xb; x++, cluster++)
                    {
                        // Where a cluster has more lights than it can list, it keeps those it sits deepest inside.
                        float cx = .5f * (columnSlope[x] + columnSlope[x + 1]) * middle - s.At.X;
                        if (!Insert(lists, cluster, number, (cx * cx + cyz) * s.InverseRangeSquared)) left++;
                    }
                }
            }
        }
        if (left != 0) Interlocked.Add(ref crowded, left);
    }

    // A cluster's list is sixteen floats in a row: light numbers from 1, the baked ones negative and last, and a
    // zero after the last. A full list gives up its worst light for a better one, but never an unbaked light for a
    // baked one: the unbaked lights are the ones every surface needs.
    // False when a light had to be left out: the new one, or the one it took the place of.
    bool Insert(Span<float> lists, int cluster, float number, float score)
    {
        int count = counts[cluster * 2], unbaked = counts[cluster * 2 + 1], start = cluster * ClusterFloats;
        int from = number > 0 ? 0 : unbaked;
        if (count < perCluster)
        {
            lists[start + count] = number;
            scores[start + count] = score;
            counts[cluster * 2] = (byte)(count + 1);
            if (number > 0) counts[cluster * 2 + 1] = (byte)(unbaked + 1);
            return true;
        }
        int worst = -1; float worstScore = score;
        for (int i = from; i < count; i++)
            if (scores[start + i] > worstScore) { worstScore = scores[start + i]; worst = i; }
        if (worst < 0) return false;
        lists[start + worst] = number;
        scores[start + worst] = score;
        return false;
    }

    /// <summary>
    /// Gives an effect that includes Shaders/LocalLights.hlsl the clusters of the last <see cref="Build"/>.
    /// An effect without that include is left alone.
    /// </summary>
    public void Bind(Effect effect)
    {
        var p = effect.Parameters;
        var clusters = p["LightClusters"];
        if (clusters == null) return;
        if (!uploaded) { texture.SetData(texels); uploaded = true; }
        clusters.SetValue(texture);
        p["ClusterView"].SetValue(viewProjection);
        p["ClusterGrid"].SetValue(gridInfo);
        p["ClusterTexel"].SetValue(texelInfo);
        p["ClusterLights"].SetValue((float)seenCount);
    }

    public void Dispose() => texture.Dispose();
}
