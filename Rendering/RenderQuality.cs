using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// What one quality tier costs the renderer. A game maps its settings choice to a tier and hands the values
/// to the passes that read them, so every game on the engine scales the same way.
/// </summary>
public sealed record RenderQuality
{
    /// <summary>
    /// Samples per pixel of the scene target. 0 leaves every edge to <see cref="TemporalSmoothing"/> or
    /// <see cref="EdgeSmoothing"/>.
    /// </summary>
    public int MultiSampleCount { get; init; }
    /// <summary>
    /// Smooths edges in the finished image (<see cref="PostProcessing.EdgeSmoothing"/>). It reaches what
    /// multisampling cannot (alpha cutouts, dithered fades, shading detail) and softens texture detail with them:
    /// measured on a wall of wood and stone, a tenth of its local contrast. So a tier takes it instead of
    /// multisampling, not on top.
    /// </summary>
    public bool EdgeSmoothing { get; init; }
    /// <summary>Texture samples along the long axis of a pixel's footprint. 1 is plain trilinear filtering.</summary>
    public int Anisotropy { get; init; } = 1;
    /// <summary>
    /// Gathers several jittered frames into one (<see cref="TemporalAntialiasing"/>). It smooths what neither
    /// multisampling nor <see cref="EdgeSmoothing"/> reaches without softening texture detail as the latter does,
    /// and takes the place of both: the scene must not be multisampled.
    /// </summary>
    public bool TemporalSmoothing { get; init; }
    /// <summary>The longest side a surface map is uploaded with. A larger map leaves its finest levels on disk.</summary>
    public int TextureSize { get; init; } = 4096;
    public bool AmbientOcclusion { get; init; }
    /// <summary>Texels along one side of a sun shadow cascade.</summary>
    public int ShadowResolution { get; init; } = 1024;
    /// <summary>Sun shadow cascades, 1 to <see cref="DirectionalShadowMap.MaxCascades"/>.</summary>
    public int ShadowCascades { get; init; } = 1;
    /// <summary>Comparisons per shadow lookup along one axis: 2 is a 2×2 bilinear tap, 4 the 16-tap filter.</summary>
    public int ShadowFilter { get; init; } = 2;
    /// <summary>Light shafts from the sun, where the level asks for them (<see cref="PostProcessing.Shafts"/>).</summary>
    public bool LightShafts { get; init; }
    /// <summary>Local lights that hold a real-time shadow map at once (<see cref="LocalShadowAtlas"/>). 0 for none.</summary>
    public int LocalShadows { get; init; }
    /// <summary>Texels along one side of a local light's shadow tile.</summary>
    public int LocalShadowResolution { get; init; } = 512;
    /// <summary>Shadow tiles of local lights redrawn in one frame.</summary>
    public int LocalShadowFaces { get; init; } = 6;

    // Measured at 1920×1080 on an RTX 3060 Laptop, as time added to a frame: temporal smoothing 0.2 to 0.3 ms,
    // which is why the tiers take it in place of multisampling (0.2 ms on an open level, 3.2 ms on one dense with
    // foliage, for an edge of about the same quality); shadows for four local lights about 1 ms where their tiles
    // hold much, 0.3 ms where they hold little; 4× multisampling 1.3 to 3 ms and
    // 2× about 1 ms (more where foliage is dense), three shadow cascades over one 0.5 to 1.6 ms (more where the
    // sun is real-time for the whole level), ambient occlusion 0.5 to 1 ms, the 16-comparison shadow filter over
    // the 4-comparison one 0.1 to 0.5 ms and light shafts 0.1 to 0.3 ms. Edge smoothing and 16× anisotropy each
    // cost under 0.1 ms.
    public static readonly RenderQuality Ultra = new()
    {
        TemporalSmoothing = true, Anisotropy = 16, AmbientOcclusion = true,
        ShadowResolution = 2048, ShadowCascades = 3, ShadowFilter = 4, LightShafts = true,
        LocalShadows = 4, LocalShadowResolution = 512, LocalShadowFaces = 8
    };
    public static readonly RenderQuality High = new()
    {
        TemporalSmoothing = true, Anisotropy = 16, AmbientOcclusion = true,
        ShadowResolution = 1536, ShadowCascades = 3, ShadowFilter = 4, LightShafts = true,
        LocalShadows = 3, LocalShadowResolution = 384, LocalShadowFaces = 6
    };
    public static readonly RenderQuality Medium = new()
    {
        TemporalSmoothing = true, Anisotropy = 16, ShadowResolution = 1024, ShadowCascades = 2, ShadowFilter = 3, LightShafts = true,
        LocalShadows = 2, LocalShadowResolution = 256, LocalShadowFaces = 4
    };
    public static readonly RenderQuality Low = new()
    {
        Anisotropy = 4, TextureSize = 1024, ShadowResolution = 1536, ShadowCascades = 1, ShadowFilter = 2
    };

    /// <summary>Tiers in menu order: 0 Ultra, 1 High, 2 Medium, anything else Low.</summary>
    public static RenderQuality Tier(int index) => index switch { 0 => Ultra, 1 => High, 2 => Medium, _ => Low };

    static readonly Dictionary<int, SamplerState> samplers = new();

    /// <summary>
    /// The wrapping sampler for surface maps at this tier. Bind it from code: FNA reapplies a filter named in an
    /// effect on every pass and reads an anisotropic one there as linear.
    /// </summary>
    public SamplerState SurfaceSampler
    {
        get
        {
            int level = Math.Clamp(Anisotropy, 1, 16);
            lock (samplers)
            {
                if (!samplers.TryGetValue(level, out var state))
                    samplers.Add(level, state = new SamplerState
                    {
                        Name = "CodexEngine.SurfaceSampler" + level,
                        Filter = level > 1 ? TextureFilter.Anisotropic : TextureFilter.Linear, MaxAnisotropy = level,
                        AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap
                    });
                return state;
            }
        }
    }
}
