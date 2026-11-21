using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// A point or spot light fixed in the level. <paramref name="Range"/> is where its light ends, the spot angles are
/// full cone angles in degrees, and <paramref name="ShadowChannel"/> names the shadow-mask channel that holds its
/// baked shadow (negative for none). A <paramref name="Baked"/> light is already in the lightmaps and probes, so
/// only surfaces without either receive it here.
/// </summary>
public readonly record struct LocalLight(Vector3 Position, Vector3 Color, float Range, bool Spot, Vector3 Direction,
    float SpotAngle, float InnerSpotAngle, bool Baked, int ShadowChannel);

/// <summary>
/// The level's local lights for Shaders/LitSurface.hlsl, in a grid over the ground plane. Each cell lists the eight
/// lights that matter most there, once for everything and once without the baked ones, so a pixel reads its
/// cell and loops over at most eight lights however many the level has. Built once: the lights do not move.
/// </summary>
public sealed class LightGrid : IDisposable
{
    readonly Texture2D grid, lights;
    readonly Vector4 gridInfo;
    readonly int count;
    readonly float cellSize;

    public LightGrid(GraphicsDevice gpu, IReadOnlyList<LocalLight> source, float cellSize = 12)
    {
        this.cellSize = cellSize;
        count = source.Count;
        if (count > 254) throw new InvalidDataException("Bake a wider light index format for this scene.");
        Vector2 origin = Vector2.Zero, limit = Vector2.Zero;
        if (count > 0)
        {
            origin = new(float.MaxValue); limit = new(float.MinValue);
            foreach (var light in source)
            {
                Vector2 center = new(light.Position.X, light.Position.Z);
                origin = Vector2.Min(origin, center - new Vector2(light.Range));
                limit = Vector2.Max(limit, center + new Vector2(light.Range));
            }
        }
        int width = Math.Max(1, (int)MathF.Ceiling((limit.X - origin.X) / cellSize));
        int height = Math.Max(1, (int)MathF.Ceiling((limit.Y - origin.Y) / cellSize));
        gridInfo = new(origin.X, origin.Y, 1f / (2 * width), 1f / (2 * height));
        var cells = new Color[width * height * 4];
        Parallel.For(0, height, z =>
        {
            Span<float> rank = stackalloc float[8]; Span<byte> indices = stackalloc byte[8];
            for (int x = 0; x < width; x++)
            for (int baked = 0; baked <= 1; baked++)
            {
                rank.Fill(float.PositiveInfinity); indices.Clear();
                Vector2 point = origin + new Vector2(x + .5f, z + .5f) * cellSize;
                for (int i = 0; i < count; i++)
                {
                    var l = source[i]; if (baked == 1 && l.Baked) continue;
                    var center = new Vector2(l.Position.X, l.Position.Z);
                    float dist = Vector2.DistanceSquared(point, center);
                    if (dist > MathF.Pow(l.Range + cellSize * .71f, 2)) continue;
                    float score = dist / (l.Range * l.Range);
                    int slot = 7; if (score >= rank[slot]) continue;
                    while (slot > 0 && score < rank[slot - 1]) { rank[slot] = rank[slot - 1]; indices[slot] = indices[slot - 1]; slot--; }
                    rank[slot] = score; indices[slot] = (byte)(i + 1);
                }
                int offset = ((z + baked * height) * width + x) * 2;
                cells[offset] = new Color(indices[0], indices[1], indices[2], indices[3]);
                cells[offset + 1] = new Color(indices[4], indices[5], indices[6], indices[7]);
            }
        });
        grid = new(gpu, width * 2, height * 2, false, SurfaceFormat.Color); grid.SetData(cells);
        var data = new Vector4[4 * (count + 1)];
        for (int i = 0; i < count; i++)
        {
            var l = source[i]; int start = (i + 1) * 4;
            data[start] = new(l.Position, 1 / (l.Range * l.Range));
            data[start + 1] = new(l.Color, l.ShadowChannel);
            float outer = MathF.Cos(MathHelper.ToRadians(l.SpotAngle * .5f));
            float inner = MathF.Cos(MathHelper.ToRadians(l.InnerSpotAngle * .5f));
            data[start + 2] = new(l.Direction, l.Spot ? outer : -2);
            data[start + 3] = new(l.Spot ? 1 / MathF.Max(.001f, inner - outer) : 1, 0, 0, 0);
        }
        lights = new(gpu, 4, count + 1, false, SurfaceFormat.Vector4); lights.SetData(data);
    }

    public void Bind(Effect effect)
    {
        effect.Parameters["LightGrid"].SetValue(grid); effect.Parameters["Lights"].SetValue(lights);
        effect.Parameters["GridInfo"].SetValue(gridInfo);
        effect.Parameters["LightInfo"].SetValue(new Vector3(1f / cellSize, 1f / (count + 1), count));
    }

    public void Dispose() { grid.Dispose(); lights.Dispose(); }
}
