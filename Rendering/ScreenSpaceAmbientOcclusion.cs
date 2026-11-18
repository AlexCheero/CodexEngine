using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>Removes occluded ambient radiance from opaque perspective geometry before transparent draws.</summary>
public sealed class ScreenSpaceAmbientOcclusion : IDisposable
{
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

    /// <summary>Scene colour and depth must preserve their contents across target switches.</summary>
    public void BeginOpaque(RenderTarget2D scene)
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
        gpu.SetRenderTarget(ambientDepth);
        gpu.Clear(ClearOptions.Target,Color.Transparent,1,0);
        targets[0] = new RenderTargetBinding(scene);
        gpu.SetRenderTargets(targets);
    }

    /// <summary>COLOR1 stores ambient RGB and positive axial view depth. Zero depth identifies background.</summary>
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
