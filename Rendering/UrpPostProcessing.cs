using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// The blended URP volume stack a Unity camera sees: the pipeline's default profile overridden by scene volumes.
/// Values use Unity's units (EV, percent, gamma-space colours). Tools/UnityFidelity records them per scene.
/// </summary>
public sealed class UrpVolumeStack
{
    public int Tonemap { get; set; }                 // 0 none, 1 neutral, 2 ACES
    public float PostExposure { get; set; }
    public float Contrast { get; set; }
    public float Saturation { get; set; }
    public float[] ColorFilter { get; set; } = [1, 1, 1, 1];
    public float Temperature { get; set; }
    public float Tint { get; set; }
    public float[] Lift { get; set; } = [1, 1, 1, 0];
    public float[] Gamma { get; set; } = [1, 1, 1, 0];
    public float[] Gain { get; set; } = [1, 1, 1, 0];
    public float[] Shadows { get; set; } = [1, 1, 1, 0];
    public float[] Midtones { get; set; } = [1, 1, 1, 0];
    public float[] Highlights { get; set; } = [1, 1, 1, 0];
    public float[] SmhLimits { get; set; } = [0, .3f, .55f, 1];
    public float[] Bloom { get; set; } = [0, .9f, .7f, 65472];   // intensity, threshold, scatter, clamp
    public float[] BloomTint { get; set; } = [1, 1, 1, 1];
    public int BloomHQ { get; set; }
    public int BloomMax { get; set; } = 6;
    public int BloomDownscale { get; set; }                       // 0 half, 1 quarter
    public float[] Vignette { get; set; } = [0, .2f, 0, .5f, .5f]; // intensity, smoothness, rounded, center
    public float[] VignetteColor { get; set; } = [0, 0, 0, 1];
    public float Chromatic { get; set; }

    /// <summary>A stack that leaves the scene unchanged apart from sRGB clamping.</summary>
    public static UrpVolumeStack Identity => new() { Bloom = [0, 1, .5f, 65472], Vignette = [0, 0, 0, .5f, .5f] };

    public static Dictionary<string, UrpVolumeStack> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, UrpVolumeStack>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
            : [];
}

/// <summary>
/// URP's post-processing chain for an sRGB-encoded HDR scene texture: bloom mip pyramid, chromatic aberration,
/// vignette, HDR colour grading (LutBuilderHdr) and tonemapping, written to a target or the back buffer.
/// </summary>
public sealed class UrpPostProcessing : IDisposable
{
    readonly GraphicsDevice gpu;
    readonly Effect effect;
    readonly EffectParameter scene, sourceSize, low, lowSize, glow;
    readonly VertexPositionTexture[] triangle =
        [new(new(-1, 1, 0), new(0, 0)), new(new(3, 1, 0), new(2, 0)), new(new(-1, -3, 0), new(0, 2))];
    RenderTarget2D[] down = [], up = [];

    public UrpPostProcessing(GraphicsDevice gpu, string effectPath)
    {
        this.gpu = gpu;
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        scene = effect.Parameters["Scene"]; sourceSize = effect.Parameters["SourceSize"];
        low = effect.Parameters["LowTexture"]; lowSize = effect.Parameters["LowSize"]; glow = effect.Parameters["GlowTexture"];
    }

    static float GammaToLinear(float c) => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f);
    static float Luminance(Vector3 c) => c.X * .2126729f + c.Y * .7151522f + c.Z * .072175f;
    static Vector3 V3(float[] v) => new(v[0], v[1], v[2]);
    // ColorUtils.PrepareLiftGammaGain (core RP 17): colours are linearized and centred on their luminance; w is an offset.
    static Vector3 Centered(float[] c, float scale)
    {
        var s = new Vector3(GammaToLinear(c[0]), GammaToLinear(c[1]), GammaToLinear(c[2])) * scale;
        return s - new Vector3(Luminance(s));
    }
    static Vector3 Lift(float[] c) => Centered(c, .15f) + new Vector3(c[3]);
    static Vector3 Gain(float[] c) => Centered(c, .8f) + new Vector3(c[3] + 1);
    static Vector3 InverseGamma(float[] c)
    {
        var s = Centered(c, .8f) + new Vector3(c[3] + 1);
        return new(1 / MathF.Max(s.X, .001f), 1 / MathF.Max(s.Y, .001f), 1 / MathF.Max(s.Z, .001f));
    }
    // ColorUtils.PrepareShadowsMidtonesHighlights
    static Vector3 Tone(float[] c)
    {
        float offset = c[3] * (c[3] < 0 ? 1 : 4);
        return new(MathF.Max(GammaToLinear(c[0]) + offset, 0), MathF.Max(GammaToLinear(c[1]) + offset, 0), MathF.Max(GammaToLinear(c[2]) + offset, 0));
    }
    // ColorUtils.ColorBalanceToLMSCoeffs
    static Vector3 Balance(float temperature, float tint)
    {
        float t1 = temperature / 65f, t2 = tint / 65f;
        float x = .31271f - t1 * (t1 < 0 ? .1f : .05f), y = 2.87f * x - 3 * x * x - .27509507f + t2 * .05f;
        float X = x / y, Z = (1 - x - y) / y;
        var w1 = new Vector3(.949237f, 1.03542f, 1.08728f);
        var w2 = new Vector3(.7328f * X + .4296f - .1624f * Z, -.7036f * X + 1.6975f + .0061f * Z, .003f * X + .0136f + .9834f * Z);
        return new(w1.X / w2.X, w1.Y / w2.Y, w1.Z / w2.Z);
    }

    void Pass(Texture2D from, RenderTarget2D to, string technique)
    {
        gpu.SetRenderTarget(to);
        // Rebinding the back buffer can keep a sub-rectangle viewport left by an earlier pass.
        if (to == null) gpu.Viewport = new(0, 0, gpu.PresentationParameters.BackBufferWidth, gpu.PresentationParameters.BackBufferHeight);
        scene.SetValue(from);
        sourceSize.SetValue(new Vector4(1f / from.Width, 1f / from.Height, from.Width, from.Height));
        effect.CurrentTechnique = effect.Techniques[technique]; effect.CurrentTechnique.Passes[0].Apply();
        gpu.DrawUserPrimitives(PrimitiveType.TriangleList, triangle, 0, 1);
    }

    /// <summary>Renders <paramref name="source"/> through the stack into <paramref name="destination"/> (null: back buffer).</summary>
    public void Render(Texture2D source, UrpVolumeStack s, RenderTarget2D destination = null)
    {
        gpu.BlendState = BlendState.Opaque; gpu.DepthStencilState = DepthStencilState.None; gpu.RasterizerState = RasterizerState.CullNone;
        // Instanced scene passes can leave a second vertex stream bound.
        gpu.SetVertexBuffer(null);
        var p = effect.Parameters;
        int shift = s.BloomDownscale == 0 ? 1 : 2;
        int w = Math.Max(1, source.Width >> shift), h = Math.Max(1, source.Height >> shift);
        int mips = Math.Clamp((int)MathF.Floor(MathF.Log2(Math.Max(w, h)) - 1), 1, s.BloomMax);
        if (down.Length != mips || down[0].Width != w || down[0].Height != h)
        {
            ReleaseChain(); down = new RenderTarget2D[mips]; up = new RenderTarget2D[mips];
            for (int i = 0; i < mips; i++)
            {
                int mw = Math.Max(1, w >> i), mh = Math.Max(1, h >> i);
                down[i] = new(gpu, mw, mh, false, SurfaceFormat.HalfVector4, DepthFormat.None);
                up[i] = new(gpu, mw, mh, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            }
        }
        float threshold = GammaToLinear(s.Bloom[1]);
        p["BloomParams"].SetValue(new Vector4(threshold, threshold * .5f, s.Bloom[3], MathHelper.Lerp(.05f, .95f, s.Bloom[2])));
        var tint = new Vector3(GammaToLinear(s.BloomTint[0]), GammaToLinear(s.BloomTint[1]), GammaToLinear(s.BloomTint[2]));
        float luma = Luminance(tint); tint = luma > 0 ? tint / luma : Vector3.One;
        p["BloomComposite"].SetValue(new Vector4(tint * s.Bloom[0], s.BloomHQ));
        // The chain also runs at zero intensity: skipping it can leave the composite reading stale device state.
        Pass(source, down[0], "Prefilter");
        for (int i = 1; i < mips; i++) { Pass(down[i - 1], up[i], "BlurH"); Pass(up[i], down[i], "BlurV"); }
        for (int i = mips - 2; i >= 0; i--)
        {
            var lower = i == mips - 2 ? down[i + 1] : up[i + 1];
            low.SetValue(lower); lowSize.SetValue(new Vector4(1f / lower.Width, 1f / lower.Height, lower.Width, lower.Height));
            Pass(down[i], up[i], "Upsample");
        }
        var bloom = mips > 1 ? up[0] : down[0];
        glow.SetValue(bloom); lowSize.SetValue(new Vector4(1f / bloom.Width, 1f / bloom.Height, bloom.Width, bloom.Height));
        p["GradeParams"].SetValue(new Vector4(MathF.Pow(2, s.PostExposure), s.Contrast / 100 + 1, s.Saturation / 100 + 1, s.Tonemap));
        p["GradeFilter"].SetValue(new Vector3(GammaToLinear(s.ColorFilter[0]), GammaToLinear(s.ColorFilter[1]), GammaToLinear(s.ColorFilter[2])));
        p["GradeBalance"].SetValue(Balance(s.Temperature, s.Tint));
        p["GradeLift"].SetValue(Lift(s.Lift)); p["GradeGamma"].SetValue(InverseGamma(s.Gamma)); p["GradeGain"].SetValue(Gain(s.Gain));
        p["GradeShadows"].SetValue(Tone(s.Shadows)); p["GradeMidtones"].SetValue(Tone(s.Midtones)); p["GradeHighlights"].SetValue(Tone(s.Highlights));
        p["GradeLimits"].SetValue(new Vector4(s.SmhLimits[0], s.SmhLimits[1], s.SmhLimits[2], s.SmhLimits[3]));
        float aspect = (float)source.Width / source.Height;
        p["VignetteParams"].SetValue(new Vector4(s.Vignette[3], s.Vignette[4], s.Vignette[0] * 3, s.Vignette[1] * 5));
        p["VignetteColor"].SetValue(new Vector4(s.VignetteColor[0], s.VignetteColor[1], s.VignetteColor[2], s.Vignette[2] > 0 ? aspect : 1));
        p["ChromaAmount"].SetValue(s.Chromatic * .05f);
        Pass(source, destination, "Composite");
    }

    void ReleaseChain() { foreach (var t in down) t.Dispose(); foreach (var t in up) t.Dispose(); down = up = []; }
    public void Dispose() { ReleaseChain(); effect.Dispose(); }
}
