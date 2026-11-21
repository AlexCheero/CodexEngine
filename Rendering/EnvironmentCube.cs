using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Graphics.PackedVector;

namespace CodexEngine.Rendering;

/// <summary>
/// A cubemap of the surroundings for reflections, with each smaller level convolved for a rougher surface (GGX),
/// as a reflection probe stores it. Sample it with <c>texCUBElod(cube, float4(reflection, UrpReflectionMip(brdf)))</c>.
/// It holds linear radiance in half floats, so the shader neither decodes it nor applies a sky gain.
/// <para>
/// The surroundings come from a sky image (<see cref="FromCross"/>) or from the scene itself, drawn around a point
/// (<see cref="Capture"/>). Either way they are six faces of linear radiance, <see cref="Size"/> texels on a side,
/// in the GPU's face order (+X,-X,+Y,-Y,+Z,-Z) and orientation; <see cref="Convolve"/> turns those into the
/// cube's levels on any thread and <see cref="Create"/> uploads them.
/// </para>
/// </summary>
public static class EnvironmentCube
{
    /// <summary>The edge of Unity's default reflection cubemap, whose levels URP's roughness-to-level curve assumes.</summary>
    public const int Size = 128;

    /// <summary>
    /// Builds the cube from a horizontal-cross sky image (4×3 faces: -X,+Z,+X,-Z in the middle row, as
    /// <see cref="CrossCubeSkybox"/> draws it). <paramref name="rotation"/> is Unity's skybox rotation in degrees
    /// and <paramref name="gain"/> the display-encoded gain the sky is drawn with; both are baked in.
    /// The convolution runs on the workers and takes a few tens of milliseconds.
    /// </summary>
    public static TextureCube FromCross(GraphicsDevice gpu, byte[] rgba, int width, int height, float rotation, Vector3 gain) =>
        Create(gpu, Convolve(CrossRadiance(rgba, width, height, rotation, gain)));

    /// <summary>The faces of a horizontal-cross sky image as linear radiance, for <see cref="Convolve"/>.</summary>
    public static Vector3[] CrossRadiance(byte[] rgba, int width, int height, float rotation, Vector3 gain)
    {
        float angle = MathHelper.ToRadians(rotation), cos = MathF.Cos(angle), sin = MathF.Sin(angle);
        var decode = new float[256 * 3];
        for (int i = 0; i < 256; i++)
            for (int c = 0; c < 3; c++)
            {
                float v = i / 255f * (c == 0 ? gain.X : c == 1 ? gain.Y : gain.Z);
                decode[i * 3 + c] = v <= .04045f ? v / 12.92f : MathF.Pow((v + .055f) / 1.055f, 2.4f);
            }
        float faceW = width / 4f, faceH = height / 3f;

        // Radiance from the cross for a world direction, as UnityLighting.hlsl's CrossCubeUV finds it.
        Vector3 Sky(Vector3 d)
        {
            (d.X, d.Z) = (d.X * cos + d.Z * sin, d.Z * cos - d.X * sin);
            float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z), x, y; int column, row;
            if (ax >= MathF.Max(az, ay)) { x = -MathF.Sign(d.X) * d.Z / ax; y = d.Y / ax; column = d.X > 0 ? 2 : 0; row = 1; }
            else if (ay >= az) { x = d.X / ay; y = -MathF.Sign(d.Y) * d.Z / ay; column = 1; row = d.Y > 0 ? 0 : 2; }
            else { x = MathF.Sign(d.Z) * d.X / az; y = d.Y / az; column = d.Z > 0 ? 1 : 3; row = 1; }
            // Bilinear within the face: its neighbours in the cross are other faces or nothing.
            float px = Math.Clamp((.5f + x * .5f) * faceW - .5f, 0, faceW - 1.001f), py = Math.Clamp((.5f - y * .5f) * faceH - .5f, 0, faceH - 1.001f);
            int x0 = (int)px, y0 = (int)py; float fx = px - x0, fy = py - y0;
            int origin = ((int)(row * faceH) + y0) * width + (int)(column * faceW) + x0;
            Vector3 Texel(int i) => new(decode[rgba[i * 4] * 3], decode[rgba[i * 4 + 1] * 3 + 1], decode[rgba[i * 4 + 2] * 3 + 2]);
            return Vector3.Lerp(Vector3.Lerp(Texel(origin), Texel(origin + 1), fx), Vector3.Lerp(Texel(origin + width), Texel(origin + width + 1), fx), fy);
        }

        var faces = new Vector3[6 * Size * Size];
        Parallel.For(0, 6 * Size, line =>
        {
            int face = line / Size, y = line % Size;
            for (int x = 0; x < Size; x++)
            {
                // Four filtered reads, each between four source texels, average the texel's whole footprint in a
                // sky whose faces are four times this size.
                var sum = Vector3.Zero;
                for (int j = 0; j < 2; j++)
                    for (int i = 0; i < 2; i++)
                        sum += Sky(Direction(face, (x + (i + .5f) / 2) / Size, (y + (j + .5f) / 2) / Size));
                faces[(face * Size + y) * Size + x] = sum / 4;
            }
        });
        return faces;
    }

    /// <summary>
    /// The cube's levels from its faces (6·<see cref="Size"/>² texels of linear radiance, face after face, rows from
    /// the top). Level 0 is the faces themselves; each smaller one is their GGX convolution for the roughness URP
    /// maps to it. Touches no graphics device, so it can run off the game thread; it spreads over the workers itself.
    /// </summary>
    public static HalfVector4[][] Convolve(Vector3[] faces)
    {
        if (faces.Length != 6 * Size * Size) throw new ArgumentException($"Expected six faces of {Size}x{Size} texels.", nameof(faces));
        int levels = System.Numerics.BitOperations.Log2(Size) + 1;
        // Plain averages of the surroundings at every size: the convolution reads the one whose texels match a sample's spread.
        var source = new Vector3[levels][];
        source[0] = faces;
        for (int level = 1; level < levels; level++)
        {
            int size = Size >> level, larger = size * 2;
            var from = source[level - 1]; var to = source[level] = new Vector3[6 * size * size];
            for (int face = 0; face < 6; face++)
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int o = (face * larger + y * 2) * larger + x * 2;
                        to[(face * size + y) * size + x] = (from[o] + from[o + 1] + from[o + larger] + from[o + larger + 1]) * .25f;
                    }
        }
        Vector3 Fetch(Vector3 d, int level)
        {
            int size = Size >> level;
            var (face, u, v) = Face(d);
            int x = Math.Clamp((int)(u * size), 0, size - 1), y = Math.Clamp((int)(v * size), 0, size - 1);
            return source[level][(face * size + y) * size + x];
        }

        var result = new HalfVector4[levels][];
        for (int level = 0; level < levels; level++)
        {
            int size = Size >> level;
            var texels = result[level] = new HalfVector4[6 * size * size];
            if (level == 0)
            {
                for (int i = 0; i < texels.Length; i++) texels[i] = new(faces[i].X, faces[i].Y, faces[i].Z, 1);
                continue;
            }
            // The perceptual roughness URP maps to this level: level = r(1.7 - 0.7r)·6, inverted.
            float perceptual = level >= 6 ? 1 : (1.7f - MathF.Sqrt(2.89f - 2.8f * level / 6)) / 1.4f;
            float alpha = perceptual * perceptual, alpha2 = alpha * alpha;
            int samples = level < 3 ? 64 : 128;
            float texelSolidAngle = 4 * MathF.PI / (6 * Size * Size);
            Parallel.For(0, 6 * size, line =>
            {
                int face = line / size, y = line % size;
                for (int x = 0; x < size; x++)
                {
                    var n = Vector3.Normalize(Direction(face, (x + .5f) / size, (y + .5f) / size));
                    var tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < .99f ? Vector3.Up : Vector3.Right, n));
                    var bitangent = Vector3.Cross(n, tangent);
                    var sum = Vector3.Zero; float weight = 0;
                    for (int s = 0; s < samples; s++)
                    {
                        // Hammersley points drawn from the GGX lobe around the normal, viewed along it.
                        float e1 = (s + .5f) / samples, e2 = Radical(s);
                        float cosTheta = MathF.Sqrt((1 - e2) / (1 + (alpha2 - 1) * e2)), sinTheta = MathF.Sqrt(MathF.Max(0, 1 - cosTheta * cosTheta));
                        float phi = MathHelper.TwoPi * e1;
                        var h = tangent * (sinTheta * MathF.Cos(phi)) + bitangent * (sinTheta * MathF.Sin(phi)) + n * cosTheta;
                        var l = h * (2 * cosTheta) - n;
                        float nl = Vector3.Dot(n, l);
                        if (nl <= 0) continue;
                        // Read the averaged surroundings at the size whose texel covers what this sample stands for.
                        float d = cosTheta * cosTheta * (alpha2 - 1) + 1, density = alpha2 / (MathF.PI * d * d) / 4;
                        float spread = 1 / (samples * density + 1e-6f);
                        int from = Math.Clamp((int)(.5f * MathF.Log2(spread / texelSolidAngle) + 1.5f), 0, levels - 1);
                        sum += Fetch(l, from) * nl; weight += nl;
                    }
                    sum /= MathF.Max(weight, 1e-6f);
                    texels[(face * size + y) * size + x] = new(sum.X, sum.Y, sum.Z, 1);
                }
            });
        }
        return result;
    }

    /// <summary>Uploads the levels <see cref="Convolve"/> made.</summary>
    public static TextureCube Create(GraphicsDevice gpu, HalfVector4[][] levels)
    {
        var cube = new TextureCube(gpu, Size, true, SurfaceFormat.HalfVector4);
        try
        {
            for (int level = 0; level < levels.Length; level++)
            {
                int texels = levels[level].Length / 6;
                for (int face = 0; face < 6; face++) cube.SetData((CubeMapFace)face, level, null, levels[level], face * texels, texels);
            }
        }
        catch { cube.Dispose(); throw; }
        return cube;
    }

    /// <summary>
    /// The surroundings' colour along the six axes (+X,-X,+Y,-Y,+Z,-Z) with no detail left: the middle of each
    /// face at level 5. <see cref="Atmosphere.Bind"/> takes these as the sky the fog fades into.
    /// </summary>
    public static Vector3[] Axes(HalfVector4[][] levels)
    {
        const int Level = 5; int size = Size >> Level, middle = size / 2 - 1;
        var axes = new Vector3[6];
        for (int face = 0; face < 6; face++)
        {
            var sum = Vector4.Zero;
            for (int y = middle; y < middle + 2; y++)
                for (int x = middle; x < middle + 2; x++)
                    sum += levels[Level][(face * size + y) * size + x].ToVector4();
            axes[face] = new Vector3(sum.X, sum.Y, sum.Z) / 4;
        }
        return axes;
    }

    /// <summary>
    /// The scene around a point as six faces of linear radiance, for <see cref="Convolve"/>: what a reflection
    /// probe captures. <paramref name="draw"/> is called once per face with a square multisampled HDR target and
    /// the direction to look along and its up. It binds the target and draws the scene from the point with a
    /// 90° field of view and a square aspect, in a view whose right is <c>Cross(up, forward)</c> (the left-handed
    /// convention of <see cref="FromCross"/> and of Unity content); nothing but linear radiance may reach the
    /// target, so no tone mapping. Each face is read back as it is drawn, which waits for the GPU: capture when a
    /// level starts, not during play.
    /// </summary>
    public static Vector3[] Capture(GraphicsDevice gpu, Action<RenderTarget2D, Vector3, Vector3> draw)
    {
        var faces = new Vector3[6 * Size * Size];
        var texels = new HalfVector4[Size * Size];
        using var target = new RenderTarget2D(gpu, Size, Size, false, SurfaceFormat.HalfVector4, DepthFormat.Depth24Stencil8, 4, RenderTargetUsage.PreserveContents);
        for (int face = 0; face < 6; face++)
        {
            var (forward, up) = FaceAxes(face);
            draw(target, forward, up);
            // Leaving the target resolves its samples.
            gpu.SetRenderTarget(null);
            target.GetData(texels);
            for (int i = 0; i < texels.Length; i++)
            {
                var texel = texels[i].ToVector4();
                // One runaway highlight would sparkle through every rougher level.
                faces[face * Size * Size + i] = Vector3.Clamp(new Vector3(texel.X, texel.Y, texel.Z), Vector3.Zero, new Vector3(64));
            }
        }
        return faces;
    }

    /// <summary>The direction a face looks along and the direction that is up in its image, by the GPU's convention.</summary>
    public static (Vector3 Forward, Vector3 Up) FaceAxes(int face) => face switch
    {
        0 => (Vector3.UnitX, Vector3.UnitY), 1 => (-Vector3.UnitX, Vector3.UnitY),
        2 => (Vector3.UnitY, -Vector3.UnitZ), 3 => (-Vector3.UnitY, Vector3.UnitZ),
        4 => (Vector3.UnitZ, Vector3.UnitY), _ => (-Vector3.UnitZ, Vector3.UnitY)
    };

    /// <summary>A cube of one colour, for a level without a sky.</summary>
    public static TextureCube Uniform(GraphicsDevice gpu, Vector3 radiance)
    {
        var cube = new TextureCube(gpu, 1, false, SurfaceFormat.HalfVector4);
        for (int face = 0; face < 6; face++) cube.SetData((CubeMapFace)face, new[] { new HalfVector4(radiance.X, radiance.Y, radiance.Z, 1) });
        return cube;
    }

    // The direction a cube face's point (u right, v down, both 0..1) looks along, by the GPU's convention.
    static Vector3 Direction(int face, float u, float v)
    {
        float x = 2 * u - 1, y = 2 * v - 1;
        return face switch
        {
            0 => new(1, -y, -x), 1 => new(-1, -y, x), 2 => new(x, 1, y),
            3 => new(x, -1, -y), 4 => new(x, -y, 1), _ => new(-x, -y, -1)
        };
    }

    static (int Face, float U, float V) Face(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return d.X > 0 ? (0, .5f - .5f * d.Z / ax, .5f - .5f * d.Y / ax) : (1, .5f + .5f * d.Z / ax, .5f - .5f * d.Y / ax);
        if (ay >= az) return d.Y > 0 ? (2, .5f + .5f * d.X / ay, .5f + .5f * d.Z / ay) : (3, .5f + .5f * d.X / ay, .5f - .5f * d.Z / ay);
        return d.Z > 0 ? (4, .5f + .5f * d.X / az, .5f - .5f * d.Y / az) : (5, .5f - .5f * d.X / az, .5f - .5f * d.Y / az);
    }

    // The base-2 radical inverse: with the sample's index it makes an even point set (Hammersley).
    static float Radical(int index)
    {
        uint bits = (uint)index;
        bits = bits << 16 | bits >> 16;
        bits = (bits & 0x55555555u) << 1 | (bits & 0xAAAAAAAAu) >> 1;
        bits = (bits & 0x33333333u) << 2 | (bits & 0xCCCCCCCCu) >> 2;
        bits = (bits & 0x0F0F0F0Fu) << 4 | (bits & 0xF0F0F0F0u) >> 4;
        bits = (bits & 0x00FF00FFu) << 8 | (bits & 0xFF00FF00u) >> 8;
        return bits * 2.3283064365386963e-10f;
    }
}
