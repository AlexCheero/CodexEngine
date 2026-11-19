using Foundry.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

public sealed class IrradianceProbes : IDisposable
{
    readonly Texture3D atlas;
    readonly Vector3 origin, size;
    readonly float inverseStep;

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
    public void Bind(Effect material)
    {
        material.Parameters["ProbeAtlas"].SetValue(atlas);
        material.Parameters["ProbeOriginStep"].SetValue(new Vector4(origin,inverseStep));
        material.Parameters["ProbeSize"].SetValue(size);
    }
    public void Dispose() => atlas.Dispose();
}
