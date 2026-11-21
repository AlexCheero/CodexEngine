using Foundry.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// A grid of light probes for what a lightmap cannot cover: moving objects and unbaked surfaces. Each probe holds
/// irradiance on the six axes. Bind it and sample <c>ProbeIrradiance</c> from Shaders/IrradianceProbes.hlsl.
/// </summary>
public sealed class IrradianceProbes : IDisposable
{
    readonly Texture3D atlas;
    readonly Vector3 origin, size;
    readonly float inverseStep, scale = 1;

    public IrradianceProbes(GraphicsDevice gpu, BakedSceneLight data)
    {
        origin = new(data.Origin.X, data.Origin.Y, data.Origin.Z);
        size = new(data.Width, data.Height, data.Depth);
        inverseStep = 1 / data.Step;
        var packed = new uint[data.ProbeLight.Length];
        // Each lobe occupies one band along Y, with X contiguous for the GPU.
        for (int z = 0; z < data.Depth; z++)
        for (int face = 0; face < 6; face++)
        for (int y = 0; y < data.Height; y++)
        {
            int source = 6 * (z * data.Height + y) * data.Width + face;
            int destination = ((z * 6 + face) * data.Height + y) * data.Width;
            for (int x = 0; x < data.Width; x++)
                packed[destination + x] = data.ProbeLight[source + 6 * x];
        }
        atlas = new Texture3D(gpu, data.Width, 6 * data.Height, data.Depth, false, SurfaceFormat.Color);
        try { atlas.SetData(packed); } catch { atlas.Dispose(); throw; }
    }

    /// <summary>
    /// A grid from linear irradiance: six lobes (+X,-X,+Y,-Y,+Z,-Z) per probe, probes ordered x, then y, then z.
    /// The texture stores them display-encoded against the brightest value, in half floats: a probe beside a lamp
    /// can be a thousand times brighter than one in shadow, and eight bits would band the dark ones.
    /// </summary>
    public IrradianceProbes(GraphicsDevice gpu, Vector3 origin, float step, int width, int height, int depth, Vector3[] lobes)
    {
        this.origin = origin;
        size = new(width, height, depth);
        inverseStep = 1 / step;
        float brightest = 1e-4f;
        foreach (var lobe in lobes) brightest = MathF.Max(brightest, MathF.Max(lobe.X, MathF.Max(lobe.Y, lobe.Z)));
        scale = brightest;
        static float Encode(float c) => c <= .0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - .055f;
        var packed = new Microsoft.Xna.Framework.Graphics.PackedVector.HalfVector4[lobes.Length];
        for (int z = 0; z < depth; z++)
        for (int face = 0; face < 6; face++)
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var lobe = lobes[((z * height + y) * width + x) * 6 + face] / brightest;
            packed[((z * 6 + face) * height + y) * width + x] = new(Encode(lobe.X), Encode(lobe.Y), Encode(lobe.Z), 1);
        }
        atlas = new Texture3D(gpu, width, 6 * height, depth, false, SurfaceFormat.HalfVector4);
        try { atlas.SetData(packed); } catch { atlas.Dispose(); throw; }
    }

    /// <summary>
    /// Reads light probes placed at arbitrary points (a Unity scene's baked probes, as Tools/UnityFidelity writes
    /// its .lightprobes file) and resamples them onto a grid whose longest side has <paramref name="cells"/> probes.
    /// A grid costs the shader three filtered samples wherever the object is, where the source's tetrahedra would
    /// need a search per object.
    /// </summary>
    public static IrradianceProbes Read(GraphicsDevice gpu, string path, int cells = 48)
    {
        Vector3[] points; Vector3[] light;
        using (var r = new BinaryReader(File.OpenRead(path)))
        {
            if (r.ReadInt32() != 0x42504C43) throw new InvalidDataException("Not a light probe file: " + path);
            int count = r.ReadInt32();
            points = new Vector3[count]; light = new Vector3[count * 6];
            for (int i = 0; i < count; i++)
            {
                points[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                for (int face = 0; face < 6; face++) light[i * 6 + face] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            }
        }
        var low = new Vector3(float.MaxValue); var high = new Vector3(float.MinValue);
        foreach (var p in points) { low = Vector3.Min(low, p); high = Vector3.Max(high, p); }
        var extent = high - low;
        float step = MathF.Max(MathF.Max(extent.X, extent.Y), MathF.Max(extent.Z, .01f)) / (cells - 1);
        int width = Math.Max(2, (int)MathF.Ceiling(extent.X / step) + 1), height = Math.Max(2, (int)MathF.Ceiling(extent.Y / step) + 1),
            depth = Math.Max(2, (int)MathF.Ceiling(extent.Z / step) + 1);
        var lobes = new Vector3[width * height * depth * 6];
        // Each cell blends the source probes by inverse distance to the fourth power, softened at half a cell:
        // near a source probe the cell takes its value, and between probes it moves smoothly from one to the next.
        float soft = step * step * .25f;
        // A probe six cells away weighs under a thousandth of one beside the cell, so each cell reads only the
        // probes in the columns around it and skips those past that distance. The sums use SIMD vectors: with
        // plain ones this loop took over half a second of the level load.
        float reach = step * 6, reachSquared = reach * reach;
        int across = Math.Max(1, (int)MathF.Ceiling(extent.X / reach)), along = Math.Max(1, (int)MathF.Ceiling(extent.Z / reach));
        var columns = new List<int>[across * along];
        var at = new System.Numerics.Vector3[points.Length]; var radiance = new System.Numerics.Vector3[light.Length];
        for (int i = 0; i < points.Length; i++)
        {
            int column = Math.Clamp((int)((points[i].Z - low.Z) / reach), 0, along - 1) * across + Math.Clamp((int)((points[i].X - low.X) / reach), 0, across - 1);
            (columns[column] ??= new()).Add(i);
            at[i] = new(points[i].X, points[i].Y, points[i].Z);
            for (int face = 0; face < 6; face++) radiance[i * 6 + face] = new(light[i * 6 + face].X, light[i * 6 + face].Y, light[i * 6 + face].Z);
        }
        Parallel.For(0, depth, z =>
        {
            Span<System.Numerics.Vector3> sum = stackalloc System.Numerics.Vector3[6];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var cell = new System.Numerics.Vector3(low.X + x * step, low.Y + y * step, low.Z + z * step);
                sum.Clear(); float total = 0;
                int cx = Math.Clamp((int)(x * step / reach), 0, across - 1), cz = Math.Clamp((int)(z * step / reach), 0, along - 1);
                // An empty stretch of the volume widens the search, without the distance limit, until it meets probes.
                for (int ring = 1; ; ring *= 2)
                {
                    for (int dz = Math.Max(0, cz - ring); dz <= Math.Min(along - 1, cz + ring); dz++)
                    for (int dx = Math.Max(0, cx - ring); dx <= Math.Min(across - 1, cx + ring); dx++)
                    {
                        var near = columns[dz * across + dx];
                        if (near == null) continue;
                        foreach (int i in near)
                        {
                            float d = System.Numerics.Vector3.DistanceSquared(at[i], cell);
                            if (ring == 1 && d > reachSquared) continue;
                            d += soft;
                            float w = 1 / (d * d);
                            total += w;
                            int o = i * 6;
                            sum[0] += radiance[o] * w; sum[1] += radiance[o + 1] * w; sum[2] += radiance[o + 2] * w;
                            sum[3] += radiance[o + 3] * w; sum[4] += radiance[o + 4] * w; sum[5] += radiance[o + 5] * w;
                        }
                    }
                    if (total > 0 || ring >= Math.Max(across, along)) break;
                    sum.Clear();
                }
                int first = ((z * height + y) * width + x) * 6;
                for (int face = 0; face < 6; face++)
                {
                    var lobe = System.Numerics.Vector3.Max(sum[face] / total, System.Numerics.Vector3.Zero);
                    lobes[first + face] = new(lobe.X, lobe.Y, lobe.Z);
                }
            }
        });
        return new IrradianceProbes(gpu, low, step, width, height, depth, lobes);
    }

    public void Bind(Effect material)
    {
        material.Parameters["ProbeAtlas"].SetValue(atlas);
        material.Parameters["ProbeOriginStep"].SetValue(new Vector4(origin,inverseStep));
        material.Parameters["ProbeSize"].SetValue(size);
        material.Parameters["ProbeScale"].SetValue(scale);
    }
    public void Dispose() => atlas.Dispose();
}
