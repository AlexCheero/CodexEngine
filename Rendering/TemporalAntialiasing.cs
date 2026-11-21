using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// Smooths edges by gathering several frames into one. Each frame draws the scene a fraction of a pixel aside
/// (<see cref="Jitter"/>), and <see cref="Resolve"/> blends it into the frames before it, each pixel fetched from
/// where its surface was a frame ago. It reaches what multisampling cannot: alpha cutouts, dithered fades and
/// shading detail. It needs the scene's depth (the surface target of <see cref="ScreenSpaceAmbientOcclusion"/>)
/// and a scene target without multisampling. What moves on its own leaves no motion to follow, so its history is
/// held to the colours around it, which keeps it from trailing.
/// </summary>
public sealed class TemporalAntialiasing : IDisposable
{
    readonly GraphicsDevice gpu;
    readonly Effect effect;
    readonly EffectParameter scene, history, surfaces, sourceSize, reproject, settings;
    readonly VertexPositionTexture[] triangle =
        [new(new(-1, 1, 0), new(0, 0)), new(new(3, 1, 0), new(2, 0)), new(new(-1, -3, 0), new(0, 2))];
    RenderTarget2D previous, next;
    Matrix lastViewProjection;
    bool hasHistory;
    int index;

    /// <summary>The share of each new frame in the result. Lower is smoother and slower to follow change.</summary>
    public float Weight { get; set; } = .1f;
    /// <summary>How far history may stray from the colours around a pixel, in their standard deviations.</summary>
    public float Tolerance { get; set; } = 1.25f;
    /// <summary>
    /// A pixel this many times brighter than its history starts to replace it, and at three times this ratio
    /// replaces it whole. It keeps the light of what glows and moves on its own, which no history follows.
    /// </summary>
    public float Burst { get; set; } = 3;
    /// <summary>
    /// The alpha a surface writes into the scene to say it is fixed to the screen, or 0 when the scene has none.
    /// A held weapon is one: its history is read from the same pixel, not from where the world behind it was.
    /// </summary>
    public float ScreenFixedAlpha { get; set; }
    /// <summary>This frame's offset in pixels, each axis within half a pixel.</summary>
    public Vector2 Offset { get; private set; }

    /// <param name="effectPath">Temporal.fxb.</param>
    public TemporalAntialiasing(GraphicsDevice gpu, string effectPath)
    {
        this.gpu = gpu;
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        var p = effect.Parameters;
        scene = p["Scene"]; history = p["History"]; surfaces = p["Surfaces"];
        sourceSize = p["SourceSize"]; reproject = p["Reproject"]; settings = p["Settings"];
    }

    static float Halton(int i, int b)
    {
        float f = 1, r = 0;
        while (i > 0) { f /= b; r += f * (i % b); i /= b; }
        return r;
    }

    /// <summary>
    /// Starts a frame: returns <paramref name="projection"/> moved aside by the frame's share of a pixel. Use the
    /// result for everything drawn into the scene, and the projection as it was for <see cref="Resolve"/>.
    /// </summary>
    public Matrix Jitter(Matrix projection, int width, int height)
    {
        index = index % 8 + 1;
        Offset = new Vector2(Halton(index, 2) - .5f, Halton(index, 3) - .5f);
        return Shift(projection, 2 * Offset.X / width, 2 * Offset.Y / height);
    }

    /// <summary>A projection whose image is moved by (<paramref name="x"/>, <paramref name="y"/>) in clip units, whatever its kind.</summary>
    public static Matrix Shift(Matrix projection, float x, float y)
    {
        projection.M31 += x * projection.M34; projection.M41 += x * projection.M44;
        projection.M32 += y * projection.M34; projection.M42 += y * projection.M44;
        return projection;
    }

    /// <summary>Forgets the frames so far, for a cut to another view.</summary>
    public void Reset() => hasHistory = false;

    /// <summary>
    /// Blends the finished scene into its history and returns the result, to be post-processed in the scene's
    /// place. <paramref name="depth"/> holds eye depth along the view axis in alpha, as the surface target does.
    /// <paramref name="view"/> and <paramref name="projection"/> are the frame's own, without the jitter.
    /// </summary>
    public Texture2D Resolve(Texture2D source, Texture2D depth, Matrix view, Matrix projection)
    {
        if (previous == null || previous.Width != source.Width || previous.Height != source.Height)
        {
            previous?.Dispose(); next?.Dispose();
            previous = new(gpu, source.Width, source.Height, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            next = new(gpu, source.Width, source.Height, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            hasHistory = false;
        }
        var viewProjection = view * projection;
        // A pixel's clip position is (ndc·w, z, w) and z follows from w, so (ndc·w, w, 1) is enough to find it,
        // and from it the place the same point had on the last frame's screen.
        float a = projection.M33 / projection.M34, b = projection.M43 - projection.M44 * a;
        var fromDepth = new Matrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, a, 1, 0, 0, b, 0);
        reproject.SetValue(fromDepth * Matrix.Invert(viewProjection) * lastViewProjection);
        lastViewProjection = viewProjection;

        gpu.BlendState = BlendState.Opaque; gpu.DepthStencilState = DepthStencilState.None; gpu.RasterizerState = RasterizerState.CullNone;
        gpu.SetVertexBuffer(null);
        gpu.SetRenderTarget(next);
        scene.SetValue(source); history.SetValue(previous); surfaces.SetValue(depth);
        sourceSize.SetValue(new Vector4(1f / source.Width, 1f / source.Height, source.Width, source.Height));
        settings.SetValue(new Vector4(hasHistory ? Weight : 1, Tolerance, ScreenFixedAlpha != 0 ? 1 / ScreenFixedAlpha : 0, MathF.Max(Burst, 1.01f)));
        effect.CurrentTechnique.Passes[0].Apply();
        gpu.DrawUserPrimitives(PrimitiveType.TriangleList, triangle, 0, 1);
        gpu.SetRenderTarget(null);
        hasHistory = true;
        (previous, next) = (next, previous);
        return previous;
    }

    public void Dispose()
    {
        previous?.Dispose(); next?.Dispose(); effect.Dispose();
    }
}
