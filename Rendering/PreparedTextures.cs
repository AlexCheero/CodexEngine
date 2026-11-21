using System.Numerics;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>What a source image holds. It decides how the smaller levels are averaged and whether the image is compressed.</summary>
public enum TextureUse
{
    /// <summary>
    /// Display-encoded colour with optional coverage in alpha. Levels average linear light, and a power-of-two
    /// image is stored as BC7: a quarter of the memory and of the bytes each sample pulls in. A PNG keeps the
    /// colour it holds under zero alpha, which filtering blends into the rim of a cutout.
    /// </summary>
    Colour,
    /// <summary>A number per channel: normals, masks, blend weights. Levels average the stored values.</summary>
    Data,
    /// <summary>RGBM radiance (rgb × alpha × 8), as baked lightmaps store it. Levels average the decoded light.</summary>
    Radiance
}

/// <summary>An image decoded and filtered off the game thread, ready for <see cref="Create"/>.</summary>
public sealed class PreparedTexture
{
    public int Width { get; init; }
    public int Height { get; init; }
    public SurfaceFormat Format { get; init; }
    /// <summary>The full chain, largest first.</summary>
    public byte[][] Levels { get; init; }

    /// <summary>
    /// Uploads the levels no longer than <paramref name="maxSize"/> on their longest side, and always the last one.
    /// Call on the thread that owns the device.
    /// </summary>
    public Texture2D Create(GraphicsDevice gpu, int maxSize = int.MaxValue)
    {
        int first = 0;
        while (first < Levels.Length - 1 && Math.Max(Width, Height) >> first > maxSize) first++;
        var texture = new Texture2D(gpu, Math.Max(1, Width >> first), Math.Max(1, Height >> first), Levels.Length - first > 1, Format);
        try { for (int i = first; i < Levels.Length; i++) texture.SetData(i - first, null, Levels[i], 0, Levels[i].Length); }
        catch { texture.Dispose(); throw; }
        return texture;
    }
}

/// <summary>
/// Turns PNG and JPEG sources into GPU textures without a content step: decode, build the levels in the space
/// that averages correctly, compress colour maps and keep the compressed result on disk for the next run.
/// Every method is safe to call from asset workers.
/// </summary>
public static class TexturePreparation
{
    // Part of every cache name. Raise it when the level filter or the encoder changes its output.
    const int Version = 6;

    /// <summary>
    /// Prepares <paramref name="path"/>. With a <paramref name="cacheDirectory"/>, a compressed image is read from
    /// there when its source is unchanged (same name, size and write time) and written there otherwise.
    /// </summary>
    public static PreparedTexture Prepare(string path, TextureUse use, string cacheDirectory = null)
    {
        string cached = null;
        if (cacheDirectory != null && use == TextureUse.Colour)
        {
            var info = new FileInfo(path);
            cached = Path.Combine(cacheDirectory,
                $"{info.Name.Replace('.', '-')}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}-{Version}.btex");
            if (File.Exists(cached))
            {
                // A file another process is still writing, or one cut short, is simply prepared again.
                try { return Read(cached); }
                catch (Exception e) when (e is IOException or InvalidDataException) { }
            }
        }
        int width, height; byte[] pixels;
        if (!Png.TryRead(path, out width, out height, out pixels))
            using (var stream = File.OpenRead(path)) Texture2D.TextureDataFromStreamEXT(stream, out width, out height, out pixels);
        var texture = Build(pixels, width, height, use);
        if (cached != null && texture.Format == SurfaceFormat.Bc7EXT)
        {
            try { Write(cached, texture); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return texture;
    }

    /// <summary>Builds the level chain for RGBA8 pixels. The array becomes the first level of an uncompressed result.</summary>
    public static PreparedTexture Build(byte[] rgba, int width, int height, TextureUse use)
    {
        int count = BitOperations.Log2((uint)Math.Max(width, height)) + 1;
        bool compress = use == TextureUse.Colour && width >= 4 && height >= 4 &&
            BitOperations.IsPow2(width) && BitOperations.IsPow2(height);
        var levels = new byte[count][];
        levels[0] = compress ? Bc7.Encode(rgba, width, height) : rgba;
        float[] light = null;
        int w = width, h = height;
        for (int level = 1; level < count; level++)
        {
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            light = light == null ? HalveSource(rgba, w, h, nw, nh, use) : Halve(light, w, h, nw, nh);
            w = nw; h = nh;
            var stored = Store(light, w * h, use);
            levels[level] = compress ? Bc7.Encode(stored, w, h) : stored;
        }
        return new PreparedTexture { Width = width, Height = height, Format = compress ? SurfaceFormat.Bc7EXT : SurfaceFormat.Color, Levels = levels };
    }

    static readonly float[] DisplayToLinear = Table(c => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f));
    static readonly float[] Unit = Table(c => c);
    static float[] Table(Func<float, float> f) { var t = new float[256]; for (int i = 0; i < 256; i++) t[i] = f(i / 255f); return t; }

    // The first halving reads the source bytes, so the largest level never exists as floats.
    static float[] HalveSource(byte[] rgba, int w, int h, int nw, int nh, TextureUse use)
    {
        var result = new float[nw * nh * 4];
        var colour = use == TextureUse.Colour ? DisplayToLinear : Unit;
        Span<float> texel = stackalloc float[16];
        for (int y = 0; y < nh; y++)
        {
            int row0 = Math.Min(2 * y, h - 1) * w, row1 = Math.Min(2 * y + 1, h - 1) * w;
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                for (int k = 0; k < 4; k++)
                {
                    int s = ((k < 2 ? row0 : row1) + ((k & 1) == 0 ? x0 : x1)) * 4;
                    float a = Unit[rgba[s + 3]];
                    // Radiance leaves its multiplier here and averages as plain light with full weight.
                    float scale = use == TextureUse.Radiance ? a * 8 : 1;
                    texel[k * 4] = colour[rgba[s]] * scale; texel[k * 4 + 1] = colour[rgba[s + 1]] * scale;
                    texel[k * 4 + 2] = colour[rgba[s + 2]] * scale; texel[k * 4 + 3] = use == TextureUse.Radiance ? 1 : a;
                }
                Average(texel, result.AsSpan((y * nw + x) * 4, 4));
            }
        }
        return result;
    }

    static float[] Halve(float[] source, int w, int h, int nw, int nh)
    {
        var result = new float[nw * nh * 4];
        Span<float> texel = stackalloc float[16];
        for (int y = 0; y < nh; y++)
        {
            int row0 = Math.Min(2 * y, h - 1) * w, row1 = Math.Min(2 * y + 1, h - 1) * w;
            for (int x = 0; x < nw; x++)
            {
                int x0 = Math.Min(2 * x, w - 1), x1 = Math.Min(2 * x + 1, w - 1);
                source.AsSpan((row0 + x0) * 4, 4).CopyTo(texel); source.AsSpan((row0 + x1) * 4, 4).CopyTo(texel[4..]);
                source.AsSpan((row1 + x0) * 4, 4).CopyTo(texel[8..]); source.AsSpan((row1 + x1) * 4, 4).CopyTo(texel[12..]);
                Average(texel, result.AsSpan((y * nw + x) * 4, 4));
            }
        }
        return result;
    }

    // A plain box: colour under zero alpha takes part like any other.
    static void Average(ReadOnlySpan<float> texel, Span<float> result)
    {
        for (int c = 0; c < 4; c++) result[c] = (texel[c] + texel[4 + c] + texel[8 + c] + texel[12 + c]) * .25f;
    }

    static byte[] Store(float[] light, int texels, TextureUse use)
    {
        var bytes = new byte[texels * 4];
        static byte Byte(float v) => (byte)Math.Clamp((int)(v * 255 + .5f), 0, 255);
        static float Display(float c) => c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f;
        for (int i = 0; i < texels * 4; i += 4)
        {
            float r = light[i], g = light[i + 1], b = light[i + 2];
            if (use == TextureUse.Colour) { bytes[i] = Byte(Display(r)); bytes[i + 1] = Byte(Display(g)); bytes[i + 2] = Byte(Display(b)); bytes[i + 3] = Byte(light[i + 3]); }
            else if (use == TextureUse.Data) { bytes[i] = Byte(r); bytes[i + 1] = Byte(g); bytes[i + 2] = Byte(b); bytes[i + 3] = Byte(light[i + 3]); }
            else
            {
                // The multiplier rounds up so the colour it scales stays within one.
                int m = Math.Clamp((int)MathF.Ceiling(MathF.Max(r, MathF.Max(g, b)) / 8 * 255), 1, 255);
                float inverse = 255f / (m * 8);
                bytes[i] = Byte(r * inverse); bytes[i + 1] = Byte(g * inverse); bytes[i + 2] = Byte(b * inverse); bytes[i + 3] = (byte)m;
            }
        }
        return bytes;
    }

    // The engine's .btex layout: width, height, block format (7 is BC7), level count, then each level's length and bytes.
    static PreparedTexture Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        int width = r.ReadInt32(), height = r.ReadInt32();
        if (r.ReadInt32() != 7) throw new InvalidDataException("Not a BC7 texture: " + path);
        var levels = new byte[r.ReadInt32()][];
        if (levels.Length != BitOperations.Log2((uint)Math.Max(width, height)) + 1) throw new InvalidDataException("Incomplete texture: " + path);
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = new byte[r.ReadInt32()];
            try { r.BaseStream.ReadExactly(levels[i]); }
            catch (EndOfStreamException) { throw new InvalidDataException("Incomplete texture: " + path); }
        }
        return new PreparedTexture { Width = width, Height = height, Format = SurfaceFormat.Bc7EXT, Levels = levels };
    }

    static void Write(string path, PreparedTexture texture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        // Workers of two processes may prepare the same image. Each writes its own file and moves it into place whole.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new BinaryWriter(File.Create(temporary)))
            {
                w.Write(texture.Width); w.Write(texture.Height); w.Write(7); w.Write(texture.Levels.Length);
                foreach (var level in texture.Levels) { w.Write(level.Length); w.Write(level); }
            }
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}

/// <summary>
/// A BC7 encoder for the two one-subset modes. Mode 6 fits one line through a 4×4 block's RGBA values, with 7-bit
/// endpoints and a 4-bit weight per texel. Mode 5 fits colour and alpha apart, each with a 2-bit weight, and wins
/// where the two vary independently: the rim of a cutout, or a mask kept in alpha. A block costs about a
/// microsecond; images whose blocks hold unrelated colour pairs would want the partitioned modes on top.
/// </summary>
static class Bc7
{
    static ReadOnlySpan<byte> Weights => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];
    static ReadOnlySpan<byte> CoarseWeights => [0, 21, 43, 64];

    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        int across = Math.Max(1, (width + 3) / 4), down = Math.Max(1, (height + 3) / 4);
        var output = new byte[across * down * 16];
        Span<float> block = stackalloc float[64];
        for (int by = 0; by < down; by++)
        for (int bx = 0; bx < across; bx++)
        {
            // Levels smaller than a block repeat their edge texels.
            for (int y = 0; y < 4; y++)
            {
                int row = Math.Min(by * 4 + y, height - 1) * width;
                for (int x = 0; x < 4; x++)
                {
                    int s = (row + Math.Min(bx * 4 + x, width - 1)) * 4, d = (y * 4 + x) * 4;
                    block[d] = rgba[s]; block[d + 1] = rgba[s + 1]; block[d + 2] = rgba[s + 2]; block[d + 3] = rgba[s + 3];
                }
            }
            EncodeBlock(block, output.AsSpan((by * across + bx) * 16, 16));
        }
        return output;
    }

    static void EncodeBlock(Span<float> p, Span<byte> output)
    {
        Span<float> mean = stackalloc float[4], axis = stackalloc float[4], next = stackalloc float[4];
        Span<float> first = stackalloc float[4], second = stackalloc float[4];
        Span<float> covariance = stackalloc float[16];
        for (int i = 0; i < 64; i += 4) { mean[0] += p[i]; mean[1] += p[i + 1]; mean[2] += p[i + 2]; mean[3] += p[i + 3]; }
        for (int c = 0; c < 4; c++) mean[c] *= 1f / 16;
        for (int i = 0; i < 64; i += 4)
            for (int a = 0; a < 4; a++)
            {
                float da = p[i + a] - mean[a];
                for (int b = a; b < 4; b++) covariance[a * 4 + b] += da * (p[i + b] - mean[b]);
            }
        for (int a = 1; a < 4; a++) for (int b = 0; b < a; b++) covariance[a * 4 + b] = covariance[b * 4 + a];

        Span<int> e0 = stackalloc int[4], e1 = stackalloc int[4], t0 = stackalloc int[4], t1 = stackalloc int[4];
        Span<int> index = stackalloc int[16], trial = stackalloc int[16];
        ulong lower = 0, upper = 0; int position = 0;
        int widest = 0;
        for (int c = 1; c < 4; c++) if (covariance[c * 5] > covariance[widest * 5]) widest = c;
        if (covariance[widest * 5] < .5f)
        {
            // One colour: both endpoints hold it and every weight is zero.
            Quantize(mean, e0, 4); e0.CopyTo(e1); index.Clear();
        }
        else
        {
            float error = Fit(p, mean, covariance, 4, Weights, e0, e1, index, t0, t1, trial, axis, next, first, second);
            if (covariance[15] >= .5f)
            {
                // Alpha varies, so try colour and alpha on separate lines and keep whichever leaves less error.
                Span<int> c0 = stackalloc int[4], c1 = stackalloc int[4], colourIndex = stackalloc int[16], alphaIndex = stackalloc int[16];
                float apart = covariance[0] + covariance[5] + covariance[10] < .5f ? Flat(p, mean, c0, c1, colourIndex)
                    : Fit(p, mean, covariance, 3, CoarseWeights, c0, c1, colourIndex, t0, t1, trial, axis, next, first, second);
                int a0 = 255, a1 = 0;
                for (int i = 3; i < 64; i += 4) { a0 = Math.Min(a0, (int)p[i]); a1 = Math.Max(a1, (int)p[i]); }
                for (int i = 0; i < 16; i++)
                {
                    int k = Math.Clamp((int)((p[i * 4 + 3] - a0) * 3 / (a1 - a0) + .5f), 0, 3), w = CoarseWeights[k];
                    float delta = ((a0 * (64 - w) + a1 * w + 32) >> 6) - p[i * 4 + 3];
                    alphaIndex[i] = k; apart += delta * delta;
                }
                if (apart < error)
                {
                    // Each set of weights keeps its first texel in the lower half, as below.
                    if (colourIndex[0] >= 2)
                    {
                        for (int c = 0; c < 3; c++) (c0[c], c1[c]) = (c1[c], c0[c]);
                        for (int i = 0; i < 16; i++) colourIndex[i] = 3 - colourIndex[i];
                    }
                    if (alphaIndex[0] >= 2)
                    {
                        (a0, a1) = (a1, a0);
                        for (int i = 0; i < 16; i++) alphaIndex[i] = 3 - alphaIndex[i];
                    }
                    Put(ref lower, ref upper, ref position, 0x20, 6);
                    Put(ref lower, ref upper, ref position, 0, 2);
                    for (int c = 0; c < 3; c++)
                    {
                        Put(ref lower, ref upper, ref position, (uint)(c0[c] >> 1), 7);
                        Put(ref lower, ref upper, ref position, (uint)(c1[c] >> 1), 7);
                    }
                    Put(ref lower, ref upper, ref position, (uint)a0, 8);
                    Put(ref lower, ref upper, ref position, (uint)a1, 8);
                    Put(ref lower, ref upper, ref position, (uint)colourIndex[0], 1);
                    for (int i = 1; i < 16; i++) Put(ref lower, ref upper, ref position, (uint)colourIndex[i], 2);
                    Put(ref lower, ref upper, ref position, (uint)alphaIndex[0], 1);
                    for (int i = 1; i < 16; i++) Put(ref lower, ref upper, ref position, (uint)alphaIndex[i], 2);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output, lower);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output[8..], upper);
                    return;
                }
            }
        }

        // The first texel's weight is stored without its top bit, so it must sit in the lower half.
        if (index[0] >= 8)
        {
            for (int c = 0; c < 4; c++) (e0[c], e1[c]) = (e1[c], e0[c]);
            for (int i = 0; i < 16; i++) index[i] = 15 - index[i];
        }
        Put(ref lower, ref upper, ref position, 0x40, 7);
        for (int c = 0; c < 4; c++)
        {
            Put(ref lower, ref upper, ref position, (uint)(e0[c] >> 1), 7);
            Put(ref lower, ref upper, ref position, (uint)(e1[c] >> 1), 7);
        }
        Put(ref lower, ref upper, ref position, (uint)(e0[0] & 1), 1);
        Put(ref lower, ref upper, ref position, (uint)(e1[0] & 1), 1);
        Put(ref lower, ref upper, ref position, (uint)index[0], 3);
        for (int i = 1; i < 16; i++) Put(ref lower, ref upper, ref position, (uint)index[i], 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output, lower);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(output[8..], upper);
    }

    // Fits one line through the block's first `channels` channels: endpoints along its longest direction, a weight
    // per texel, then one least-squares pass over the endpoints. Returns the squared error the decoder will leave.
    static float Fit(ReadOnlySpan<float> p, ReadOnlySpan<float> mean, ReadOnlySpan<float> covariance, int channels, ReadOnlySpan<byte> weights,
        Span<int> e0, Span<int> e1, Span<int> index, Span<int> t0, Span<int> t1, Span<int> trial,
        Span<float> axis, Span<float> next, Span<float> first, Span<float> second)
    {
        // The longest direction, by power iteration from the widest channel's row.
        int widest = 0;
        for (int c = 1; c < channels; c++) if (covariance[c * 5] > covariance[widest * 5]) widest = c;
        for (int c = 0; c < channels; c++) axis[c] = covariance[widest * 4 + c];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            float largest = 0;
            for (int a = 0; a < channels; a++)
            {
                float sum = 0;
                for (int b = 0; b < channels; b++) sum += covariance[a * 4 + b] * axis[b];
                next[a] = sum; largest = MathF.Max(largest, MathF.Abs(sum));
            }
            if (largest < 1e-12f) break;
            for (int a = 0; a < channels; a++) axis[a] = next[a] / largest;
        }
        float length = 0;
        for (int c = 0; c < channels; c++) length += axis[c] * axis[c];
        length = MathF.Sqrt(length);
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i < 64; i += 4)
        {
            float t = 0;
            for (int c = 0; c < channels; c++) t += (p[i + c] - mean[c]) * axis[c];
            t /= length; low = MathF.Min(low, t); high = MathF.Max(high, t);
        }
        for (int c = 0; c < channels; c++) { first[c] = mean[c] + axis[c] / length * low; second[c] = mean[c] + axis[c] / length * high; }
        Quantize(first, e0, channels); Quantize(second, e1, channels);
        float error = Assign(p, e0, e1, index, channels, weights);

        // With the weights chosen, the endpoints that best explain the block follow from least squares.
        float aa = 0, ab = 0, bb = 0;
        for (int c = 0; c < channels; c++) first[c] = second[c] = 0;
        for (int i = 0; i < 16; i++)
        {
            float w = weights[index[i]] * (1f / 64), u = 1 - w;
            aa += u * u; ab += u * w; bb += w * w;
            for (int c = 0; c < channels; c++) { first[c] += u * p[i * 4 + c]; second[c] += w * p[i * 4 + c]; }
        }
        float determinant = aa * bb - ab * ab;
        if (determinant > 1e-4f)
        {
            for (int c = 0; c < channels; c++)
            {
                float x = first[c], y = second[c];
                first[c] = (bb * x - ab * y) / determinant; second[c] = (aa * y - ab * x) / determinant;
            }
            Quantize(first, t0, channels); Quantize(second, t1, channels);
            float refined = Assign(p, t0, t1, trial, channels, weights);
            if (refined < error) { error = refined; t0[..channels].CopyTo(e0); t1[..channels].CopyTo(e1); trial.CopyTo(index); }
        }
        return error;
    }

    // A block of one colour under varying alpha.
    static float Flat(ReadOnlySpan<float> p, ReadOnlySpan<float> mean, Span<int> e0, Span<int> e1, Span<int> index)
    {
        Quantize(mean, e0, 3); e0[..3].CopyTo(e1);
        return Assign(p, e0, e1, index, 3, CoarseWeights);
    }

    // Four channels store seven bits each plus one low bit shared by the endpoint, and the better of the two bits
    // is taken. Three channels store seven bits and repeat the top one as the eighth.
    static void Quantize(ReadOnlySpan<float> colour, Span<int> stored, int channels)
    {
        if (channels == 3)
        {
            for (int c = 0; c < 3; c++)
            {
                int q = Math.Clamp((int)MathF.Round(colour[c] * (127f / 255)), 0, 127);
                stored[c] = q << 1 | q >> 6;
            }
            return;
        }
        float best = float.MaxValue; int shared = 0;
        for (int bit = 0; bit < 2; bit++)
        {
            float error = 0;
            for (int c = 0; c < 4; c++)
            {
                int value = Math.Clamp((int)MathF.Round((colour[c] - bit) * .5f), 0, 127) * 2 + bit;
                error += (value - colour[c]) * (value - colour[c]);
            }
            if (error < best) { best = error; shared = bit; }
        }
        for (int c = 0; c < 4; c++) stored[c] = Math.Clamp((int)MathF.Round((colour[c] - shared) * .5f), 0, 127) * 2 + shared;
    }

    // Nearest weight for each texel along the endpoint line, and the squared error the decoder's arithmetic leaves.
    static float Assign(ReadOnlySpan<float> p, ReadOnlySpan<int> e0, ReadOnlySpan<int> e1, Span<int> index, int channels, ReadOnlySpan<byte> weights)
    {
        float span = 0, error = 0;
        for (int c = 0; c < channels; c++) span += (e1[c] - e0[c]) * (float)(e1[c] - e0[c]);
        int last = weights.Length - 1;
        float scale = span > 0 ? last / span : 0;
        for (int i = 0; i < 16; i++)
        {
            int o = i * 4;
            float t = 0;
            for (int c = 0; c < channels; c++) t += (p[o + c] - e0[c]) * (e1[c] - e0[c]);
            int k = Math.Clamp((int)(t * scale + .5f), 0, last), w = weights[k];
            index[i] = k;
            for (int c = 0; c < channels; c++)
            {
                float delta = ((e0[c] * (64 - w) + e1[c] * w + 32) >> 6) - p[o + c];
                error += delta * delta;
            }
        }
        return error;
    }

    static void Put(ref ulong lower, ref ulong upper, ref int position, uint value, int bits)
    {
        if (position < 64)
        {
            lower |= (ulong)value << position;
            if (position + bits > 64) upper |= (ulong)value >> (64 - position);
        }
        else upper |= (ulong)value << (position - 64);
        position += bits;
    }
}

/// <summary>
/// Reads the PNGs content pipelines write (8 bits a channel, RGB or RGBA, not interlaced) into RGBA8. Unlike
/// FNA's decoder it leaves the colour under zero alpha alone: an atlas fills that area with colour spread from
/// the shapes beside it, and filtering reads it at every cutout's rim.
/// </summary>
static class Png
{
    public static bool TryRead(string path, out int width, out int height, out byte[] rgba)
    {
        width = height = 0; rgba = null;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] file = File.ReadAllBytes(path);
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (file.Length < 33 || !file.AsSpan(0, 8).SequenceEqual(signature)) return false;
        int channels = 0;
        using var packed = new MemoryStream();
        for (int at = 8; at + 12 <= file.Length;)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
            var type = file.AsSpan(at + 4, 4);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at + 8));
                height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at + 12));
                // Bit depth 8, colour type 2 (RGB) or 6 (RGBA), not interlaced. Anything else goes to FNA.
                if (file[at + 16] != 8 || file[at + 20] != 0) return false;
                channels = file[at + 17] switch { 2 => 3, 6 => 4, _ => 0 };
                if (channels == 0) return false;
            }
            // A colour key makes one RGB value transparent, which this reader does not apply.
            else if (type.SequenceEqual("tRNS"u8)) return false;
            else if (type.SequenceEqual("IDAT"u8)) packed.Write(file, at + 8, length);
            else if (type.SequenceEqual("IEND"u8)) break;
            at += 12 + length;
        }
        if (channels == 0) return false;
        int stride = width * channels;
        var lines = new byte[(stride + 1) * height];
        packed.Position = 0;
        using (var inflate = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionMode.Decompress))
            inflate.ReadExactly(lines);
        rgba = new byte[width * height * 4];
        var previous = new byte[stride]; var current = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = lines[y * (stride + 1)];
            lines.AsSpan(y * (stride + 1) + 1, stride).CopyTo(current);
            // Each byte was stored as its difference from a prediction made of the byte one pixel to the left (a),
            // the one above (b) and the one above-left (c).
            for (int i = 0; i < stride; i++)
            {
                int a = i >= channels ? current[i - channels] : 0, b = previous[i], c = i >= channels ? previous[i - channels] : 0;
                int predicted = filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1,
                    4 => Paeth(a, b, c),
                    _ => throw new InvalidDataException("Unknown PNG filter in " + path)
                };
                current[i] = (byte)(current[i] + predicted);
            }
            int o = y * width * 4;
            if (channels == 4) current.CopyTo(rgba.AsSpan(o));
            else
                for (int x = 0; x < width; x++)
                {
                    rgba[o + x * 4] = current[x * 3]; rgba[o + x * 4 + 1] = current[x * 3 + 1];
                    rgba[o + x * 4 + 2] = current[x * 3 + 2]; rgba[o + x * 4 + 3] = 255;
                }
            (previous, current) = (current, previous);
        }
        return true;
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, da = Math.Abs(p - a), db = Math.Abs(p - b), dc = Math.Abs(p - c);
        return da <= db && da <= dc ? a : db <= dc ? b : c;
    }
}
