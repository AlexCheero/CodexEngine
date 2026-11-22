using System.Runtime.InteropServices;
using SphereSet = CodexEngine.Runtime.SphereSet;
using SphereTests = CodexEngine.Runtime.SphereTests;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simd = System.Numerics.Vector;
using Wide = System.Numerics.Vector<float>;

namespace CodexEngine.Rendering;

/// <summary>
/// Orthographic sun shadows around the camera, in one to four nested cascades that share an atlas. Each cascade
/// spends the same texels on a third of the next one's width, so shadows near the eye are sharp without a larger
/// map. Direction points from a receiver toward the light.
/// </summary>
public sealed class DirectionalShadowMap : IDisposable
{
    public const int MaxCascades = 4;
    // A tile's outer texels stay unused so the filter never reads its neighbour, and a receiver nearing the edge
    // blends into the next cascade over this share of the tile. DirectionalShadows.hlsl receives both.
    const float BorderTexels = 4, Band = .12f;
    // How far ahead of the eye a cascade sits, in half-sizes. The eye stays inside when it looks along the light.
    const float Lead = .6f;

    readonly GraphicsDevice gpu;
    readonly RenderTarget2D depth;
    readonly int resolution, filter;
    readonly float casterDepth, limit;
    readonly Matrix basis;
    readonly float[] radius;
    readonly Vector3[] origin;
    readonly Matrix[] viewProjection;
    readonly BoundingFrustum widest = new(Matrix.Identity);
    readonly Vector4[] cascadeData = new Vector4[MaxCascades];
    int current;

    public Vector3 Direction { get; }
    public int Cascades => radius.Length;
    /// <summary>The cascade being drawn: the first after <see cref="Begin"/>, then the one given to <see cref="BeginCascade"/>.</summary>
    public Matrix ViewProjection => viewProjection[current];

    /// <param name="resolution">Texels along one side of each cascade.</param>
    /// <param name="radius">Half the width of the widest cascade, in world units.</param>
    /// <param name="casterDepth">Light-space depth kept on either side of a cascade's centre.</param>
    /// <param name="cascades">Each cascade is a third as wide as the next.</param>
    /// <param name="filter">Comparisons per lookup along one axis: 2, 3 or 4. The cost grows with its square.</param>
    public DirectionalShadowMap(GraphicsDevice gpu, Vector3 direction, int resolution, float radius, float casterDepth,
        int cascades = 1, int filter = 4)
    {
        this.gpu = gpu;
        this.resolution = resolution;
        this.casterDepth = casterDepth;
        this.filter = Math.Clamp(filter, 2, 4);
        cascades = Math.Clamp(cascades, 1, MaxCascades);
        limit = 1 - 2 * BorderTexels / resolution;
        Direction = Vector3.Normalize(direction);
        var up = Math.Abs(Direction.Y) > .99f ? Vector3.Forward : Vector3.Up;
        basis = Matrix.CreateLookAt(Vector3.Zero, -Direction, up);
        this.radius = new float[cascades];
        for (int i = cascades - 1; i >= 0; i--) this.radius[i] = i == cascades - 1 ? radius : this.radius[i + 1] / 3;
        origin = new Vector3[cascades];
        viewProjection = new Matrix[cascades];
        depth = new RenderTarget2D(gpu, resolution * cascades, resolution, false, SurfaceFormat.Single, DepthFormat.Depth24);
    }

    /// <summary>Centres every cascade on one point.</summary>
    public void Position(Vector3 center)
    {
        for (int i = 0; i < radius.Length; i++) Place(i, center);
    }

    /// <summary>Places each cascade ahead of the eye by a share of its own size, so most of it lies in view.</summary>
    public void Position(Vector3 eye, Vector3 forward)
    {
        for (int i = 0; i < radius.Length; i++) Place(i, eye + forward * (radius[i] * Lead));
    }

    void Place(int i, Vector3 center)
    {
        float texel = 2 * radius[i] / resolution;
        var o = Vector3.Transform(center, basis);
        // Lock the light-space grid to world texels as the eye translates or turns.
        o.X = MathF.Round(o.X / texel) * texel;
        o.Y = MathF.Round(o.Y / texel) * texel;
        o.Z += casterDepth;
        origin[i] = o;
        viewProjection[i] = basis * Matrix.CreateTranslation(-o) *
            Matrix.CreateOrthographic(2 * radius[i], 2 * radius[i], 0, 2 * casterDepth);
        cascadeData[i] = new Vector4(o, 1 / radius[i]);
        if (i == radius.Length - 1) widest.Matrix = viewProjection[i];
    }

    /// <summary>Whether the bounds reach any cascade. Safe on culling workers once the map is positioned.</summary>
    public bool Reaches(BoundingSphere bounds) => widest.Intersects(bounds);

    /// <summary>The volume <see cref="Reaches"/> tests, for testing many spheres together.</summary>
    public BoundingFrustum Reach => widest;

    /// <summary>
    /// <see cref="Intersects"/> for a whole set: culls every sphere the current cascade need not draw. The
    /// arithmetic is that of the one-sphere test, so the two always agree.
    /// </summary>
    public void CullCasters(SphereSet spheres, Span<int> culled)
    {
        int padded = spheres.Padded;
        ArgumentOutOfRangeException.ThrowIfLessThan(culled.Length, padded);
        if (!Simd.IsHardwareAccelerated || SphereTests.ForceScalar)
        {
            for (int i = 0; i < spheres.Count; i++)
                if (!Intersects(new BoundingSphere(new Vector3(spheres.X[i], spheres.Y[i], spheres.Z[i]), spheres.Radius[i]))) culled[i] = -1;
            return;
        }
        var o = origin[current];
        Wide m11 = new(basis.M11), m21 = new(basis.M21), m31 = new(basis.M31), m41 = new(basis.M41);
        Wide m12 = new(basis.M12), m22 = new(basis.M22), m32 = new(basis.M32), m42 = new(basis.M42);
        Wide m13 = new(basis.M13), m23 = new(basis.M23), m33 = new(basis.M33), m43 = new(basis.M43);
        Wide ox = new(o.X), oy = new(o.Y), oz = new(o.Z), cascade = new(radius[current]), depth = new(2 * casterDepth);
        // What a smaller cascade holds whole, before the sphere's own radius comes off it.
        Span<float> held = stackalloc float[MaxCascades];
        for (int i = 0; i < current; i++) held[i] = radius[i] * (limit - Band) - 4 * (2 * radius[i + 1] / resolution);
        ref float xs = ref MemoryMarshal.GetArrayDataReference(spheres.X), ys = ref MemoryMarshal.GetArrayDataReference(spheres.Y);
        ref float zs = ref MemoryMarshal.GetArrayDataReference(spheres.Z), rs = ref MemoryMarshal.GetArrayDataReference(spheres.Radius);
        ref int result = ref MemoryMarshal.GetReference(culled);
        for (int at = 0; at < padded; at += Wide.Count)
        {
            var x = Simd.LoadUnsafe(ref xs, (nuint)at); var y = Simd.LoadUnsafe(ref ys, (nuint)at);
            var z = Simd.LoadUnsafe(ref zs, (nuint)at); var r = Simd.LoadUnsafe(ref rs, (nuint)at);
            var px = x * m11 + y * m21 + z * m31 + m41;
            var py = x * m12 + y * m22 + z * m32 + m42;
            var pz = x * m13 + y * m23 + z * m33 + m43;
            var reach = cascade + r; var behind = oz - pz;
            var outside = Simd.GreaterThan(Simd.Abs(px - ox), reach) | Simd.GreaterThan(Simd.Abs(py - oy), reach) |
                Simd.LessThan(behind, -r) | Simd.GreaterThan(behind, depth + r);
            for (int i = 0; i < current; i++)
            {
                var inside = new Wide(held[i]) - r;
                outside |= Simd.LessThan(Simd.Abs(px - new Wide(origin[i].X)), inside) & Simd.LessThan(Simd.Abs(py - new Wide(origin[i].Y)), inside);
            }
            Simd.StoreUnsafe(Simd.LoadUnsafe(ref result, (nuint)at) | outside, ref result, (nuint)at);
        }
    }

    /// <summary>
    /// Whether a caster must be drawn into the current cascade: it reaches the cascade, and no smaller cascade
    /// holds it whole. A shadow falls along the light, so receivers of a caster held whole by a smaller cascade
    /// all read that cascade.
    /// </summary>
    public bool Intersects(BoundingSphere bounds)
    {
        var p = Vector3.Transform(bounds.Center, basis);
        var o = origin[current];
        float reach = radius[current] + bounds.Radius, behind = o.Z - p.Z;
        if (MathF.Abs(p.X - o.X) > reach || MathF.Abs(p.Y - o.Y) > reach ||
            behind < -bounds.Radius || behind > 2 * casterDepth + bounds.Radius) return false;
        for (int i = 0; i < current; i++)
        {
            // Inside the part of the smaller cascade that never blends outward, less the wider filter's footprint.
            float held = radius[i] * (limit - Band) - 4 * (2 * radius[i + 1] / resolution) - bounds.Radius;
            if (MathF.Abs(p.X - origin[i].X) < held && MathF.Abs(p.Y - origin[i].Y) < held) return false;
        }
        return true;
    }

    /// <summary>Binds and clears the atlas and starts the first cascade.</summary>
    public void Begin()
    {
        // DirectionalShadows.hlsl reserves pixel sampler 1 for this target.
        if (gpu.Textures[1] == depth) gpu.Textures[1] = null;
        gpu.SetRenderTarget(depth);
        gpu.BlendState = BlendState.Opaque;
        gpu.DepthStencilState = DepthStencilState.Default;
        gpu.RasterizerState = RasterizerState.CullNone;
        gpu.Clear(Color.White);
        BeginCascade(0);
    }

    /// <summary>Directs drawing to one cascade's tile. Draw its casters with <see cref="ViewProjection"/>.</summary>
    public void BeginCascade(int index)
    {
        current = index;
        gpu.Viewport = new Viewport(index * resolution, 0, resolution, resolution);
    }

    /// <summary>Call after switching to the colour target. Disable for independently lit preview scenes.</summary>
    public void BindReceiver(Effect material, bool enabled)
    {
        var p = material.Parameters;
        p["SunDirection"].SetValue(Direction);
        p["ShadowBasis"].SetValue(basis);
        p["ShadowCascades"].SetValue(cascadeData);
        p["ShadowTexture"].SetValue(enabled ? depth : null);
        p["ShadowMetrics"].SetValue(new Vector4(1f / resolution, 1 / (2 * casterDepth), radius.Length, enabled ? 1 : 0));
        p["ShadowFilter"].SetValue(new Vector4(filter, limit, Band, 0));
    }

    public void Dispose() => depth.Dispose();
}
