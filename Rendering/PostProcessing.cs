using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// What post-processing does to one camera's frame: a default profile overridden by the scene's own settings.
/// Values are in the units an artist sets them in: EV, percent and display-space colours.
/// </summary>
public sealed class PostStack
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
    /// <summary>
    /// Adaptive exposure: how far, in EV, the exposure may fall below and rise above <see cref="PostExposure"/> to
    /// bring the frame's average brightness to <see cref="ExposureKey"/>. Both zero keeps the exposure fixed.
    /// </summary>
    public float[] ExposureRange { get; set; } = [0, 0];
    /// <summary>The average luminance adaptive exposure aims for, as linear radiance after exposure. 0.18 is mid grey.</summary>
    public float ExposureKey { get; set; } = .18f;
    /// <summary>How fast adaptive exposure follows, in EV per second: opening up for a darker view, closing down for a brighter one.</summary>
    public float[] ExposureSpeed { get; set; } = [1, 3];
    /// <summary>
    /// Depth of field: the blur radius of what is wholly out of focus, in pixels of a frame 1080 high (0 for none),
    /// then four eye distances. The near blur is whole up to the first and gone at the second; the far blur begins
    /// at the third and is whole from the fourth.
    /// </summary>
    public float[] DepthOfField { get; set; } = [0, 0, 0, 10, 30];
    /// <summary>
    /// Motion blur from the camera's own movement: the share of a frame the shutter stays open (0 for none) and the
    /// longest streak, as a share of the frame's height. What moves on its own is not blurred.
    /// </summary>
    public float[] MotionBlur { get; set; } = [0, .04f];

    /// <summary>A stack that leaves the scene unchanged apart from sRGB clamping.</summary>
    public static PostStack Identity => new() { Bloom = [0, 1, .5f, 65472], Vignette = [0, 0, 0, .5f, .5f] };

    public static Dictionary<string, PostStack> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, PostStack>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
            : [];
}

/// <summary>
/// The post-processing chain for an HDR scene texture, sRGB-encoded or linear: motion blur and depth of field where
/// the stack asks for them, bloom mip pyramid, chromatic aberration, vignette, fixed or adaptive exposure, HDR
/// colour grading and tonemapping, written to a target or the back buffer.
/// </summary>
public sealed class PostProcessing : IDisposable
{
    readonly GraphicsDevice gpu;
    readonly Effect effect;
    readonly EffectParameter scene, sourceSize, low, lowSize, glow;
    readonly VertexPositionTexture[] triangle =
        [new(new(-1, 1, 0), new(0, 0)), new(new(3, 1, 0), new(2, 0)), new(new(-1, -3, 0), new(0, 2))];
    RenderTarget2D[] down = [], up = [];
    RenderTarget2D encoded, shaftA, shaftB;
    RenderTarget2D moved, focusA, focusB, focused, exposureA, exposureB;
    RenderTarget2D[] averages = [];
    Matrix lastViewProjection, reproject;
    bool cameraSet, cameraBefore, exposed;

    /// <summary>
    /// Light shafts for one frame: sky near the sun streaks toward the viewer past whatever stands in front of it.
    /// <paramref name="Sun"/> is the sun's position on screen (0 to 1, may lie outside), <paramref name="Tint"/>
    /// scales the sky's own radiance and <paramref name="Reach"/> is how far toward the sun a streak is gathered
    /// from, as a share of the distance. Sky dimmer than <paramref name="Threshold"/> (linear radiance) feeds no
    /// shaft. Costs three quarter-size passes.
    /// </summary>
    public readonly record struct LightShafts(Vector2 Sun, Vector3 Tint, float Reach, float Threshold);

    /// <summary>
    /// Set each frame before <see cref="Render"/>, or to null for none. The scene's alpha must then tell sky from
    /// surface: 1 where the sky shows, lower on anything drawn over it (an eye distance d stored as d/(32+d) does).
    /// </summary>
    public LightShafts? Shafts { get; set; }

    /// <summary>True when the scene texture holds linear radiance; false (default) when it is sRGB-encoded.</summary>
    public bool LinearScene { get; set; }

    /// <summary>
    /// Smooths edges in the graded frame with one more full-screen pass (FXAA). Unlike multisampling it also
    /// reaches alpha cutouts, dithered fades and shading detail, at the price of a slightly softer image.
    /// </summary>
    public bool EdgeSmoothing { get; set; }

    /// <summary>Adds one display step of noise before the 8-bit write, so dark gradients show no bands.</summary>
    public bool Dither { get; set; } = true;

    /// <summary>
    /// The alpha a surface writes into the scene to stand outside the stack, or 0 when the scene has no such surface.
    /// A held weapon is one such surface, drawn after post-processing: it shows as lit, without bloom, vignette, exposure or grade,
    /// and casts no bloom itself. Pick a value far below the scene's other alphas, such as -15: after a multisample
    /// resolve the share of it left on an edge pixel tells how much of that pixel the surface covers.
    /// </summary>
    public float OutsideAlpha { get; set; }

    /// <summary>
    /// The scene's eye depth along the view axis, in alpha: the surface target of
    /// <see cref="ScreenSpaceAmbientOcclusion"/>. Depth of field and motion blur need it and are skipped without.
    /// </summary>
    public Texture2D Depth { get; set; }

    /// <summary>Seconds since the frame before, which adaptive exposure moves by.</summary>
    public float FrameSeconds { get; set; } = 1f / 60;

    /// <summary>
    /// Tells the stack the camera of the frame it is about to render, for motion blur: the frame's own view and
    /// projection, without any jitter. A frame that does not call it is not blurred.
    /// </summary>
    public void Camera(Matrix view, Matrix projection)
    {
        var viewProjection = view * projection;
        // As TemporalAntialiasing has it: (ndc·w, w, 1) to the last frame's clip space.
        float a = projection.M33 / projection.M34, b = projection.M43 - projection.M44 * a;
        reproject = new Matrix(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, a, 1, 0, 0, b, 0) * Matrix.Invert(viewProjection) * lastViewProjection;
        lastViewProjection = viewProjection;
        cameraSet = true;
    }

    public PostProcessing(GraphicsDevice gpu, string effectPath)
    {
        this.gpu = gpu;
        effect = new Effect(gpu, File.ReadAllBytes(effectPath));
        scene = effect.Parameters["Scene"]; sourceSize = effect.Parameters["SourceSize"];
        low = effect.Parameters["LowTexture"]; lowSize = effect.Parameters["LowSize"]; glow = effect.Parameters["GlowTexture"];
    }

    static float GammaToLinear(float c) => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f);
    static float Luminance(Vector3 c) => c.X * .2126729f + c.Y * .7151522f + c.Z * .072175f;
    static Vector3 V3(float[] v) => new(v[0], v[1], v[2]);
    // Lift, gamma and gain: colours are linearized and centred on their luminance; w is an offset.
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
    // Shadows, midtones and highlights: a display-space colour with an offset in w.
    static Vector3 Tone(float[] c)
    {
        float offset = c[3] * (c[3] < 0 ? 1 : 4);
        return new(MathF.Max(GammaToLinear(c[0]) + offset, 0), MathF.Max(GammaToLinear(c[1]) + offset, 0), MathF.Max(GammaToLinear(c[2]) + offset, 0));
    }
    // White balance as LMS coefficients, from temperature and tint.
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
    public void Render(Texture2D source, PostStack s, RenderTarget2D destination = null)
    {
        gpu.BlendState = BlendState.Opaque; gpu.DepthStencilState = DepthStencilState.None; gpu.RasterizerState = RasterizerState.CullNone;
        // Instanced scene passes can leave a second vertex stream bound.
        gpu.SetVertexBuffer(null);
        var p = effect.Parameters;
        p["OutsideScale"].SetValue(OutsideAlpha < 0 ? 1 / OutsideAlpha : 0);
        p["SceneIsLinear"].SetValue(LinearScene ? 1f : 0f);
        source = Moved(source, s);
        source = Focused(source, s);
        Expose(source, s);
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
        p["OutsideScale"].SetValue(OutsideAlpha < 0 ? 1 / OutsideAlpha : 0);
        float threshold = GammaToLinear(s.Bloom[1]);
        p["BloomParams"].SetValue(new Vector4(threshold, threshold * .5f, s.Bloom[3], MathHelper.Lerp(.05f, .95f, s.Bloom[2])));
        var tint = new Vector3(GammaToLinear(s.BloomTint[0]), GammaToLinear(s.BloomTint[1]), GammaToLinear(s.BloomTint[2]));
        float luma = Luminance(tint); tint = luma > 0 ? tint / luma : Vector3.One;
        p["BloomComposite"].SetValue(new Vector4(tint * s.Bloom[0], s.BloomHQ));
        float aspect = (float)source.Width / source.Height;
        if (Shafts is { } shaft && shaft.Tint != Vector3.Zero)
        {
            int sw = Math.Max(1, source.Width / 4), sh = Math.Max(1, source.Height / 4);
            if (shaftA == null || shaftA.Width != sw || shaftA.Height != sh)
            {
                shaftA?.Dispose(); shaftB?.Dispose();
                shaftA = new(gpu, sw, sh, false, SurfaceFormat.HalfVector4, DepthFormat.None);
                shaftB = new(gpu, sw, sh, false, SurfaceFormat.HalfVector4, DepthFormat.None);
            }
            p["SceneIsLinear"].SetValue(LinearScene ? 1f : 0f);
            p["ShaftSource"].SetValue(new Vector4(shaft.Sun, shaft.Reach, aspect));
            p["ShaftThreshold"].SetValue(shaft.Threshold);
            Pass(source, shaftA, "ShaftMask");
            Pass(shaftA, shaftB, "ShaftBlur");
            p["ShaftSource"].SetValue(new Vector4(shaft.Sun, shaft.Reach / 8, aspect));
            Pass(shaftB, shaftA, "ShaftBlur");
            p["ShaftTexture"].SetValue(shaftA); p["ShaftLight"].SetValue(new Vector4(shaft.Tint, 1));
        }
        else { p["ShaftTexture"].SetValue(down[0]); p["ShaftLight"].SetValue(Vector4.Zero); }
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
        p["VignetteParams"].SetValue(new Vector4(s.Vignette[3], s.Vignette[4], s.Vignette[0] * 3, s.Vignette[1] * 5));
        p["VignetteColor"].SetValue(new Vector4(s.VignetteColor[0], s.VignetteColor[1], s.VignetteColor[2], s.Vignette[2] > 0 ? aspect : 1));
        p["ChromaAmount"].SetValue(s.Chromatic * .05f);
        p["SceneIsLinear"].SetValue(LinearScene ? 1f : 0f);
        p["Output"].SetValue(new Vector2(Dither ? 1f / 255 : 0, EdgeSmoothing ? 1 : 0));
        // The composite declares the exposure's sampler whether or not it is read.
        p["ExposureTexture"].SetValue(exposed ? exposureA : down[0]);
        if (!EdgeSmoothing) { Pass(source, destination, "Composite"); return; }
        if (encoded == null || encoded.Width != source.Width || encoded.Height != source.Height)
        {
            encoded?.Dispose();
            encoded = new(gpu, source.Width, source.Height, false, SurfaceFormat.Color, DepthFormat.None);
        }
        Pass(source, encoded, "Composite");
        Pass(encoded, destination, "Smooth");
    }

    void ReleaseChain() { foreach (var t in down) t.Dispose(); foreach (var t in up) t.Dispose(); down = up = []; }

    void Fit(ref RenderTarget2D target, int width, int height)
    {
        if (target != null && target.Width == width && target.Height == height) return;
        target?.Dispose();
        target = new(gpu, width, height, false, SurfaceFormat.HalfVector4, DepthFormat.None);
    }

    // The scene streaked along the path the camera moved it by while the shutter was open.
    Texture2D Moved(Texture2D source, PostStack s)
    {
        bool blur = s.MotionBlur[0] > 0 && Depth != null && cameraSet && cameraBefore;
        cameraBefore = cameraSet; cameraSet = false;
        if (!blur) return source;
        var p = effect.Parameters;
        Fit(ref moved, source.Width, source.Height);
        p["DepthTexture"].SetValue(Depth); p["Reproject"].SetValue(reproject);
        p["MotionParams"].SetValue(new Vector4(s.MotionBlur[0], s.MotionBlur[1], (float)source.Width / source.Height, 0));
        Pass(source, moved, "MotionBlur");
        return moved;
    }

    // The scene with what lies outside the focused distances blurred: gathered at half size, then laid over the sharp frame.
    Texture2D Focused(Texture2D source, PostStack s)
    {
        if (!(s.DepthOfField[0] > 0) || Depth == null) return source;
        var p = effect.Parameters;
        int w = Math.Max(1, source.Width / 2), h = Math.Max(1, source.Height / 2);
        Fit(ref focusA, w, h); Fit(ref focusB, w, h); Fit(ref focused, source.Width, source.Height);
        float radius = s.DepthOfField[0] / 1080;
        p["DepthTexture"].SetValue(Depth);
        p["FocusParams"].SetValue(new Vector4(s.DepthOfField[1], s.DepthOfField[2], s.DepthOfField[3], s.DepthOfField[4]));
        p["FocusRadius"].SetValue(new Vector2(radius * source.Height / source.Width, radius));
        Pass(source, focusA, "FocusPrefilter");
        Pass(focusA, focusB, "FocusBlur");
        p["FocusTexture"].SetValue(focusB);
        Pass(source, focused, "FocusComposite");
        return focused;
    }

    // Moves the exposure toward what brings the frame's average brightness to the key. The average is taken of log
    // luminance, at a quarter of the size and then a quarter of that again down to one texel, so it is the
    // geometric mean: one bright lamp does not close the camera down.
    void Expose(Texture2D source, PostStack s)
    {
        var p = effect.Parameters;
        bool adapt = s.ExposureRange[0] != 0 || s.ExposureRange[1] != 0;
        p["ExposureOn"].SetValue(adapt ? 1f : 0f);
        if (!adapt) { exposed = false; return; }
        int w = Math.Max(1, source.Width / 4), h = Math.Max(1, source.Height / 4);
        if (averages.Length == 0 || averages[0].Width != w || averages[0].Height != h)
        {
            foreach (var t in averages) t.Dispose();
            var chain = new List<RenderTarget2D>();
            for (int cw = w, ch = h; ; cw = Math.Max(1, (cw + 3) / 4), ch = Math.Max(1, (ch + 3) / 4))
            {
                chain.Add(new(gpu, cw, ch, false, SurfaceFormat.HalfVector4, DepthFormat.None));
                if (cw == 1 && ch == 1) break;
            }
            averages = chain.ToArray();
        }
        Fit(ref exposureA, 1, 1); Fit(ref exposureB, 1, 1);
        Pass(source, averages[0], "Luminance");
        for (int i = 1; i < averages.Length; i++) Pass(averages[i - 1], averages[i], "Average");
        float seconds = Math.Clamp(FrameSeconds, 0, 1);
        p["LuminanceTexture"].SetValue(averages[^1]);
        p["ExposureTexture"].SetValue(exposureA);
        p["ExposureParams"].SetValue(new Vector4(MathF.Log2(MathF.Max(s.ExposureKey, 1e-4f)) - s.PostExposure,
            -MathF.Abs(s.ExposureRange[0]), MathF.Abs(s.ExposureRange[1]), 0));
        // The first frame takes its target at once: there is nothing to adapt from.
        p["ExposureRates"].SetValue(exposed
            ? new Vector2(1 - MathF.Exp(-seconds * s.ExposureSpeed[0]), 1 - MathF.Exp(-seconds * s.ExposureSpeed[1]))
            : Vector2.One);
        Pass(exposureA, exposureB, "Adapt");
        (exposureA, exposureB) = (exposureB, exposureA);
        exposed = true;
    }
    public void Dispose()
    {
        ReleaseChain(); encoded?.Dispose(); shaftA?.Dispose(); shaftB?.Dispose();
        moved?.Dispose(); focusA?.Dispose(); focusB?.Dispose(); focused?.Dispose(); exposureA?.Dispose(); exposureB?.Dispose();
        foreach (var t in averages) t.Dispose();
        effect.Dispose();
    }
}
