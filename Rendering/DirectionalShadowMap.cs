using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>A camera-centred orthographic sun map. Direction points from a receiver toward the light.</summary>
public sealed class DirectionalShadowMap : IDisposable
{
    readonly GraphicsDevice gpu;
    readonly RenderTarget2D depth;
    readonly BoundingFrustum frustum = new(Matrix.Identity);
    readonly float radius, casterDepth, texel;
    readonly Matrix basis;
    Vector3 center;
    public Vector3 Direction { get; }
    public Matrix ViewProjection { get; private set; }

    public DirectionalShadowMap(GraphicsDevice gpu, Vector3 direction, int resolution, float radius, float casterDepth)
    {
        this.gpu = gpu;
        this.radius = radius;
        this.casterDepth = casterDepth;
        Direction = Vector3.Normalize(direction);
        var up = Math.Abs(Direction.Y) > .99f ? Vector3.Forward : Vector3.Up;
        basis = Matrix.CreateLookAt(Vector3.Zero, -Direction, up);
        texel = 2 * radius / resolution;
        depth = new RenderTarget2D(gpu, resolution, resolution, false, SurfaceFormat.Single, DepthFormat.Depth24);
    }

    public void Position(Vector3 center)
    {
        this.center = center;
        var origin = Vector3.Transform(center, basis);
        // Lock the light-space grid to world texels as the eye translates or turns.
        origin.X = MathF.Round(origin.X / texel) * texel;
        origin.Y = MathF.Round(origin.Y / texel) * texel;
        origin.Z += casterDepth;
        ViewProjection = basis * Matrix.CreateTranslation(-origin) *
            Matrix.CreateOrthographic(2 * radius, 2 * radius, 0, 2 * casterDepth);
        frustum.Matrix = ViewProjection;
    }

    public bool Intersects(BoundingSphere bounds) => frustum.Intersects(bounds);

    public void Begin()
    {
        // DirectionalShadows.hlsl reserves pixel sampler 1 for this target.
        if (gpu.Textures[1] == depth) gpu.Textures[1] = null;
        gpu.SetRenderTarget(depth);
        gpu.BlendState = BlendState.Opaque;
        gpu.DepthStencilState = DepthStencilState.Default;
        gpu.RasterizerState = RasterizerState.CullNone;
        gpu.Clear(Color.White);
    }

    /// <summary>Call after switching to the colour target. Disable for independently lit preview scenes.</summary>
    public void BindReceiver(Effect material, bool enabled)
    {
        material.Parameters["SunDirection"].SetValue(Direction);
        material.Parameters["ShadowCenter"].SetValue(center);
        material.Parameters["ShadowViewProjection"].SetValue(ViewProjection);
        material.Parameters["ShadowTexture"].SetValue(enabled ? depth : null);
        material.Parameters["ShadowMetrics"].SetValue(new Vector4(1f / depth.Width, texel, radius, enabled ? 1 : 0));
    }

    public void Dispose() => depth.Dispose();
}
