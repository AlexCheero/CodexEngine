using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// Keeps the surface target the opaque pass writes beside the scene (ambient radiance and eye depth), and removes
/// occluded ambient radiance from opaque perspective geometry before transparent draws. The same target then serves
/// whatever follows and needs the depth behind a pixel: soft particles, decals, refraction.
/// </summary>
public sealed class ScreenSpaceAmbientOcclusion : IDisposable
{
    /// <summary>
    /// The depth the surface target holds where no surface was drawn. It lies beyond any scene on purpose: a
    /// multisample resolve or a filtered read averages it with the surfaces beside a silhouette, and a far average
    /// leaves what stands in front of that silhouette untouched, where a zero would pull it toward the eye.
    /// </summary>
    public const float SkyDepth = 32768;

    static readonly Vector4 NoSurface = new(0, 0, 0, SkyDepth);

    readonly GraphicsDevice gpu;
    readonly Effect effect;
    readonly EffectParameter surface, mask, camera, settings, maskTexel;
    readonly EffectTechnique estimate, composite;
    readonly VertexBuffer quad;
    readonly RenderTargetBinding[] targets = new RenderTargetBinding[2];
    readonly BlendState subtractAmbient = new()
    {
        ColorBlendFunction = BlendFunction.ReverseSubtract,
        ColorSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        ColorWriteChannels = ColorWriteChannels.Red | ColorWriteChannels.Green | ColorWriteChannels.Blue
    };
    RenderTarget2D ambientDepth, occlusion;

    /// <summary>
    /// The surface target of the frame being drawn: ambient RGB, and in alpha the eye depth along the view axis in
    /// world units (<see cref="SkyDepth"/> where the sky shows). It can be sampled once the opaque pass has ended,
    /// after <see cref="Apply"/> or <see cref="EndOpaque"/>, and holds the opaque surfaces only. On a multisampled
    /// scene each texel is the average of its samples. Null before the first frame.
    /// </summary>
    public Texture2D Surfaces => ambientDepth;

    public ScreenSpaceAmbientOcclusion(GraphicsDevice gpu, string effectPath)
    {
        this.gpu = gpu;
        effect = new Effect(gpu,File.ReadAllBytes(effectPath));
        surface = effect.Parameters["Surface"];
        mask = effect.Parameters["Mask"];
        camera = effect.Parameters["Camera"];
        settings = effect.Parameters["Settings"];
        maskTexel = effect.Parameters["MaskTexel"];
        estimate = effect.Techniques["Estimate"];
        composite = effect.Techniques["Composite"];
        quad = new VertexBuffer(gpu,typeof(VertexPositionTexture),4,BufferUsage.WriteOnly);
        quad.SetData(new VertexPositionTexture[] {
            new(new(-1,1,0),new(0,0)), new(new(1,1,0),new(1,0)),
            new(new(-1,-1,0),new(0,1)), new(new(1,-1,0),new(1,1))
        });
    }

    /// <summary>
    /// Write only the scene target, leaving the ambient-depth target as cleared. For what is drawn after
    /// <see cref="BeginScene"/> and is no surface: the sky.
    /// </summary>
    public static readonly BlendState SceneOnly = new()
    {
        Name = "CodexEngine.SceneOnly", ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.Zero,
        AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.Zero, ColorWriteChannels1 = ColorWriteChannels.None
    };

    /// <summary>
    /// Starts the frame with both targets bound and cleared, in place of clearing the scene, drawing the sky and
    /// calling <see cref="BeginOpaque"/>. Every target switch ends a render pass, and on a multisampled scene
    /// each end resolves all its samples; this start saves two. The scene's colour is left undefined, so draw
    /// a sky that covers it, with <see cref="SceneOnly"/>.
    /// </summary>
    public void BeginScene(RenderTarget2D scene)
    {
        Prepare(scene);
        targets[0] = new RenderTargetBinding(scene);
        gpu.SetRenderTargets(targets);
        gpu.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, NoSurface, 1, 0);
    }

    /// <summary>Scene colour and depth must preserve their contents across target switches.</summary>
    public void BeginOpaque(RenderTarget2D scene)
    {
        Prepare(scene);
        gpu.SetRenderTarget(ambientDepth);
        gpu.Clear(ClearOptions.Target,NoSurface,1,0);
        targets[0] = new RenderTargetBinding(scene);
        gpu.SetRenderTargets(targets);
    }

    /// <summary>
    /// Ends the opaque pass without occlusion: the scene alone stays bound and <see cref="Surfaces"/> can be read.
    /// </summary>
    public void EndOpaque() => gpu.SetRenderTarget((RenderTarget2D)targets[0].RenderTarget);

    void Prepare(RenderTarget2D scene)
    {
        if (ambientDepth == null || ambientDepth.Width != scene.Width || ambientDepth.Height != scene.Height ||
            ambientDepth.MultiSampleCount != scene.MultiSampleCount)
        {
            var nextDepth = new RenderTarget2D(gpu,scene.Width,scene.Height,false,SurfaceFormat.HalfVector4,
                DepthFormat.None,scene.MultiSampleCount,RenderTargetUsage.PreserveContents);
            RenderTarget2D nextMask;
            try
            {
                nextMask = new RenderTarget2D(gpu,(scene.Width+1)/2,(scene.Height+1)/2,false,SurfaceFormat.HalfVector4,DepthFormat.None);
            }
            catch { nextDepth.Dispose(); throw; }
            ambientDepth?.Dispose();
            occlusion?.Dispose();
            ambientDepth = nextDepth;
            occlusion = nextMask;
            targets[1] = new RenderTargetBinding(ambientDepth);
            maskTexel.SetValue(new Vector2(1f/occlusion.Width,1f/occlusion.Height));
        }
    }

    /// <summary>
    /// Ends the opaque pass and darkens the scene's ambient light where surfaces close in on each other. COLOR1
    /// stores ambient RGB and positive axial view depth; <see cref="SkyDepth"/> identifies background.
    /// </summary>
    public void Apply(Matrix projection, float radius, float strength)
    {
        gpu.SetRenderTarget(occlusion);
        var blend = gpu.BlendState;
        var depth = gpu.DepthStencilState;
        var raster = gpu.RasterizerState;
        gpu.BlendState = BlendState.Opaque;
        gpu.DepthStencilState = DepthStencilState.None;
        gpu.RasterizerState = RasterizerState.CullNone;
        gpu.SetVertexBuffer(quad);
        surface.SetValue(ambientDepth);
        camera.SetValue(new Vector4(1f/ambientDepth.Width,1f/ambientDepth.Height,1f/projection.M11,1f/projection.M22));
        settings.SetValue(new Vector2(radius,strength));
        effect.CurrentTechnique = estimate;
        estimate.Passes[0].Apply();
        gpu.DrawPrimitives(PrimitiveType.TriangleStrip,0,2);
        gpu.SetRenderTarget((RenderTarget2D)targets[0].RenderTarget);
        gpu.BlendState = subtractAmbient;
        mask.SetValue(occlusion);
        effect.CurrentTechnique = composite;
        composite.Passes[0].Apply();
        gpu.DrawPrimitives(PrimitiveType.TriangleStrip,0,2);
        gpu.Textures[0] = null;
        gpu.Textures[1] = null;
        gpu.BlendState = blend;
        gpu.DepthStencilState = depth;
        gpu.RasterizerState = raster;
    }

    public void Dispose()
    {
        ambientDepth?.Dispose();
        occlusion?.Dispose();
        quad.Dispose();
        subtractAmbient.Dispose();
        effect.Dispose();
    }
}
