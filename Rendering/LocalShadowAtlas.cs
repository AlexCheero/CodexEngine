using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// Real-time shadows for the few local lights that matter most to the view (Shaders/LocalShadows.hlsl reads them).
/// Each chosen light holds a row of the atlas: six square tiles around a point light, the first one for a spot.
/// Every frame, call <see cref="Select"/> once the frame's <see cref="ClusteredLights"/> are built for the main
/// view, then <see cref="Begin"/> and draw the casters of each tile <see cref="NextFace"/> hands out, from
/// <see cref="ViewProjection"/> and with a technique that writes clip z as the sun's shadow map takes it. A frame
/// redraws only <see cref="FacesPerFrame"/> tiles, stalest first, and never one the view cannot see, so the cost
/// stays bounded however the lights are placed. <see cref="Apply"/> then tells the lights which row is theirs;
/// call it again after every later <see cref="ClusteredLights.Build"/> of the frame.
/// </summary>
public sealed class LocalShadowAtlas : IDisposable
{
    // Extra view angle around a tile, in texels on each side, so the filter never reads the neighbouring tile.
    const float BorderTexels = 3;
    // The near plane as a share of the far one.
    const float NearShare = .01f;

    struct Row
    {
        public long Key;
        public Vector3 Position, Direction;
        public float Far, OuterCos;
        public bool Kept, Masked;
    }

    struct Face
    {
        public Matrix ViewProjection;
        public BoundingFrustum Frustum;
        public int Drawn;      // the frame it was last drawn in, Empty before the first, or Stale
        public bool Seen;      // touches the view this frame
    }

    readonly GraphicsDevice gpu;
    readonly Effect effect;
    readonly RenderTarget2D depth;
    readonly Texture2D none;
    readonly int tile, rows;
    readonly Row[] lights;
    readonly Face[] faces;
    readonly List<int> queue = new();
    readonly List<(float Score, int Light)> candidates = new();
    readonly Dictionary<long, int> held = new();
    readonly VertexPositionTexture[] triangle =
        [new(new(-1, 1, 1), new(0, 0)), new(new(3, 1, 1), new(2, 0)), new(new(-1, -3, 1), new(0, 2))];
    readonly DepthStencilState always = new() { DepthBufferEnable = true, DepthBufferWriteEnable = true, DepthBufferFunction = CompareFunction.Always };
    readonly float spread;
    // A tile no frame has drawn yet reads as lit. One that still holds another light's shadows must be emptied first.
    const int Empty = -1, Stale = int.MinValue;
    int frame, cursor, current = -1;
    BoundingSphere reach;

    /// <summary>Lights that may hold shadows at once, up to the rows the atlas was made with.</summary>
    public int MaxLights { get; set; }
    /// <summary>Tiles redrawn in one frame. A point light in full view needs up to five.</summary>
    public int FacesPerFrame { get; set; } = 8;
    /// <summary>Shadows end this far from their light, which bounds the casters a tile can hold.</summary>
    public float MaxRange { get; set; } = 30;
    /// <summary>How dark a shadow is: 1 takes all of the light away.</summary>
    public float Strength { get; set; } = 1;

    /// <summary>The tile being drawn.</summary>
    public Matrix ViewProjection => faces[current].ViewProjection;
    /// <summary>
    /// Whether the light of the tile being drawn has a baked shadow mask. Where a level bakes its static shadows
    /// into masks, lightmapped casters can then be left out of the tile: they shadow through the mask.
    /// </summary>
    public bool Masked => lights[current / 6].Masked;
    /// <summary>Tiles this frame will draw, after <see cref="Select"/>.</summary>
    public int Pending => queue.Count;

    /// <param name="effectPath">ShadowTiles.fxb.</param>
    /// <param name="tileSize">Texels along one side of a tile.</param>
    /// <param name="maxLights">Rows of the atlas: the most lights that can hold shadows.</param>
    public LocalShadowAtlas(GraphicsDevice gpu, string effectPath, int tileSize = 512, int maxLights = 4)
    {
        this.gpu = gpu;
        tile = tileSize;
        rows = Math.Max(1, maxLights);
        MaxLights = rows;
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        depth = new RenderTarget2D(gpu, tile * 6, tile * rows, false, SurfaceFormat.Single, DepthFormat.Depth24, 0, RenderTargetUsage.PreserveContents);
        none = new Texture2D(gpu, 1, 1, false, SurfaceFormat.Single);
        none.SetData(new[] { float.MaxValue });
        lights = new Row[rows];
        faces = new Face[rows * 6];
        for (int i = 0; i < faces.Length; i++) { faces[i].Frustum = new BoundingFrustum(Matrix.Identity); faces[i].Drawn = Stale; }
        spread = 1 + 2 * BorderTexels / tile;
    }

    /// <summary>
    /// Chooses the lights that hold shadows this frame and the tiles to redraw. <paramref name="lights"/> must be
    /// built for the view <paramref name="view"/> bounds, seen from <paramref name="eye"/>. Only lights with the
    /// CastsShadows trait and a key take part. A light keeps its row while it stays among the strongest at the eye.
    /// </summary>
    public void Select(ClusteredLights lights, Vector3 eye, BoundingFrustum view)
    {
        frame++;
        queue.Clear();
        candidates.Clear();
        int limit = Math.Clamp(MaxLights, 0, rows);
        for (int i = 0; i < lights.Visible && limit > 0; i++)
        {
            lights.Describe(i, out var position, out float range, out _, out float outerCos, out bool casts, out long key);
            // A cone wider than a half-space does not fit one tile.
            if (!casts || key == 0 || outerCos > -1 && outerCos < .05f) continue;
            // What the light would put on a surface at the eye, with a floor on the distance: a lamp the eye stands
            // beside is no more worth a shadow than one a few steps away.
            float d2 = MathF.Max(Vector3.DistanceSquared(position, eye), range * range * .02f), window = d2 / (range * range);
            float score = MathF.Max(0, 1 - window * window) / d2 + 1e-6f / d2;
            if (held.ContainsKey(key)) score *= 1.5f;
            candidates.Add((score, i));
        }
        candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        if (candidates.Count > limit) candidates.RemoveRange(limit, candidates.Count - limit);

        for (int r = 0; r < rows; r++) this.lights[r].Kept = false;
        // Lights that keep their row first, then the newcomers into the rows left over.
        foreach (var (_, i) in candidates)
        {
            lights.Describe(i, out var position, out float range, out var direction, out float outerCos, out _, out long key);
            if (held.TryGetValue(key, out int row)) Place(row, key, position, range, direction, outerCos, lights.HasShadowMask(i));
        }
        foreach (var (_, i) in candidates)
        {
            lights.Describe(i, out var position, out float range, out var direction, out float outerCos, out _, out long key);
            if (held.ContainsKey(key)) continue;
            int row = Array.FindIndex(this.lights, static l => !l.Kept);
            if (this.lights[row].Key != 0) held.Remove(this.lights[row].Key);
            this.lights[row].Key = 0;
            held[key] = row;
            Place(row, key, position, range, direction, outerCos, lights.HasShadowMask(i));
        }
        for (int r = 0; r < rows; r++)
            if (!this.lights[r].Kept && this.lights[r].Key != 0) { held.Remove(this.lights[r].Key); this.lights[r].Key = 0; }

        // The tiles to draw: those the view can see, the longest undrawn first. A tile the view cannot see shadows
        // nothing in it, so it waits. Tiles of equal age take turns from a moving start, not from the first row.
        for (int r = 0; r < rows; r++)
        {
            if (!this.lights[r].Kept) continue;
            int count = this.lights[r].OuterCos > -1 ? 1 : 6;
            for (int f = 0; f < count; f++)
            {
                ref var face = ref faces[r * 6 + f];
                face.Seen = face.Frustum.Intersects(view);
                if (face.Seen) queue.Add(r * 6 + f);
            }
        }
        int start = cursor;
        queue.Sort((a, b) =>
        {
            int age = faces[a].Drawn.CompareTo(faces[b].Drawn);
            return age != 0 ? age : ((a - start + faces.Length) % faces.Length).CompareTo((b - start + faces.Length) % faces.Length);
        });
        int budget = Math.Max(1, FacesPerFrame);
        if (queue.Count > budget) queue.RemoveRange(budget, queue.Count - budget);
        if (queue.Count > 0) cursor = (queue[^1] + 1) % faces.Length;
        next = 0;
    }

    int next;

    void Place(int row, long key, Vector3 position, float range, Vector3 direction, float outerCos, bool masked)
    {
        ref var light = ref lights[row];
        light.Masked = masked;
        float far = MathF.Min(range, MaxRange);
        bool taken = light.Key != key;
        bool moved = taken || light.Position != position || light.Far != far || light.Direction != direction || light.OuterCos != outerCos;
        light.Key = key; light.Kept = true;
        if (!moved) return;
        light.Position = position; light.Far = far; light.Direction = direction; light.OuterCos = outerCos;
        bool spot = outerCos > -1;
        // A cone's tile spans its outer angle; a point light's six span a right angle each.
        float tangent = spot ? MathF.Sqrt(MathF.Max(1 - outerCos * outerCos, 0)) / outerCos : 1;
        for (int f = 0; f < 6; f++)
        {
            ref var face = ref faces[row * 6 + f];
            // A row taken over holds the last light's shadows. A light that only moved keeps its own, a little
            // out of place, until its tiles come round again, which is soon: undrawn tiles go first.
            face.Drawn = taken ? Stale : Math.Min(face.Drawn, Empty);
            if (spot && f > 0) continue;
            Basis(spot ? -1 : f, direction, out var right, out var up, out var forward);
            face.ViewProjection = Projection(position, right, up, forward, tangent * spread, far * NearShare, far);
            face.Frustum.Matrix = face.ViewProjection;
        }
    }

    // The axes of a tile, as LocalShadows.hlsl derives them from the direction to a surface.
    static void Basis(int face, Vector3 direction, out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        switch (face)
        {
            case 0: forward = Vector3.UnitX; right = -Vector3.UnitZ; up = Vector3.UnitY; break;
            case 1: forward = -Vector3.UnitX; right = Vector3.UnitZ; up = Vector3.UnitY; break;
            case 2: forward = Vector3.UnitY; right = Vector3.UnitX; up = -Vector3.UnitZ; break;
            case 3: forward = -Vector3.UnitY; right = Vector3.UnitX; up = Vector3.UnitZ; break;
            case 4: forward = Vector3.UnitZ; right = Vector3.UnitX; up = Vector3.UnitY; break;
            case 5: forward = -Vector3.UnitZ; right = -Vector3.UnitX; up = Vector3.UnitY; break;
            default:
                forward = direction;
                right = Vector3.Normalize(Vector3.Cross(MathF.Abs(forward.Y) < .99f ? Vector3.UnitY : Vector3.UnitX, forward));
                up = Vector3.Cross(forward, right);
                break;
        }
    }

    // A perspective view along `forward` whose clip z runs from 0 at the near plane to `far` at the far one and
    // whose clip w is the distance along the axis: what a caster's depth technique writes and the receiver rebuilds.
    static Matrix Projection(Vector3 eye, Vector3 right, Vector3 up, Vector3 forward, float tangent, float near, float far)
    {
        float k = far / (far - near);
        right /= tangent; up /= tangent;
        return new Matrix(
            right.X, up.X, forward.X * k, forward.X,
            right.Y, up.Y, forward.Y * k, forward.Y,
            right.Z, up.Z, forward.Z * k, forward.Z,
            -Vector3.Dot(eye, right), -Vector3.Dot(eye, up), -Vector3.Dot(eye, forward) * k - near * k, -Vector3.Dot(eye, forward));
    }

    /// <summary>Binds the atlas for drawing. Follow with <see cref="NextFace"/> until it returns false.</summary>
    public void Begin()
    {
        bool stale = false;
        for (int r = 0; r < rows && !stale; r++)
            for (int f = 0; f < 6; f++) stale |= lights[r].Kept && faces[r * 6 + f].Drawn == Stale;
        if (queue.Count == 0 && !stale) return;
        for (int i = 0; i < 16; i++) if (gpu.Textures[i] == depth) gpu.Textures[i] = null;
        gpu.SetRenderTarget(depth);
        // Tiles that hold another light's shadows and are not drawn this frame are emptied now, so they read as lit.
        for (int r = 0; r < rows && stale; r++)
            for (int f = 0; f < 6; f++)
                if (lights[r].Kept && faces[r * 6 + f].Drawn == Stale && !queue.Contains(r * 6 + f))
                {
                    EmptyTile(r * 6 + f);
                    faces[r * 6 + f].Drawn = Empty;
                }
    }

    /// <summary>
    /// Moves to the next tile to draw and empties it, or returns false when the frame's tiles are done. Draw the
    /// casters that pass <see cref="Intersects"/> from <see cref="ViewProjection"/>.
    /// </summary>
    public bool NextFace()
    {
        if (next >= queue.Count) { current = -1; return false; }
        current = queue[next++];
        ref readonly var light = ref lights[current / 6];
        reach = new BoundingSphere(light.Position, light.Far);
        faces[current].Drawn = frame;
        EmptyTile(current);
        return true;
    }

    // The whole tile to the far end of everything: a clear would empty the other tiles with it.
    void EmptyTile(int face)
    {
        gpu.Viewport = new Viewport(face % 6 * tile, face / 6 * tile, tile, tile);
        gpu.BlendState = BlendState.Opaque;
        gpu.DepthStencilState = always;
        gpu.RasterizerState = RasterizerState.CullNone;
        gpu.SetVertexBuffer(null);
        effect.CurrentTechnique.Passes[0].Apply();
        gpu.DrawUserPrimitives(PrimitiveType.TriangleList, triangle, 0, 1);
        gpu.DepthStencilState = DepthStencilState.Default;
    }

    /// <summary>Whether a caster can shadow anything through the tile being drawn.</summary>
    public bool Intersects(BoundingSphere bounds) => reach.Intersects(bounds) && faces[current].Frustum.Intersects(bounds);

    /// <summary>Whether a caster is near enough to the tile's light to matter at all: a cheap first test for groups of casters.</summary>
    public bool Reaches(BoundingSphere bounds) => reach.Intersects(bounds);

    /// <summary>
    /// Tells the lights of the last <see cref="ClusteredLights.Build"/> which of them hold a row here. The atlas
    /// is in world space, so the same rows serve every view of the frame.
    /// </summary>
    public void Apply(ClusteredLights lights)
    {
        if (held.Count == 0) return;
        for (int i = 0; i < lights.Visible; i++)
        {
            lights.Describe(i, out _, out _, out _, out _, out _, out long key);
            if (key != 0 && held.TryGetValue(key, out int row)) lights.SetShadowSlot(i, row);
        }
    }

    /// <summary>For an effect that includes Shaders/LocalShadows.hlsl. Call after switching to the colour target.</summary>
    public void BindReceiver(Effect material, bool enabled = true)
    {
        var p = material.Parameters;
        var atlas = p["LocalShadowAtlas"];
        if (atlas == null) return;
        atlas.SetValue(enabled ? depth : none);
        p["LocalShadowGrid"].SetValue(new Vector4(1f / 6, 1f / rows, tile, spread));
        p["LocalShadowDepth"].SetValue(new Vector4(MaxRange, NearShare, enabled ? Strength : 0, 0));
    }

    /// <summary>The same binding for a game that keeps no atlas: every light then reads as unshadowed.</summary>
    public static void BindNone(Effect material, Texture2D white)
    {
        var p = material.Parameters;
        var atlas = p["LocalShadowAtlas"];
        if (atlas == null) return;
        atlas.SetValue(white);
        p["LocalShadowGrid"].SetValue(new Vector4(1f / 6, 1, 1, 1));
        p["LocalShadowDepth"].SetValue(new Vector4(1, NearShare, 0, 0));
    }

    public void Dispose()
    {
        depth.Dispose();
        none.Dispose();
        always.Dispose();
        effect.Dispose();
    }
}
