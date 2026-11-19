using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace CodexEngine.Rendering;

/// <summary>What an authored material adds to a surface's base texture and tint: relief, reflectance and glow.</summary>
public sealed class SurfaceMaterial
{
    /// <summary>Texture names for <see cref="StaticMeshRenderer.ReadTexture"/>.</summary>
    public string Normal { get; init; } = StaticMeshRenderer.FlatNormal;
    public string Mask { get; init; } = StaticMeshRenderer.PlainMask;
    public string Glow { get; init; } = StaticMeshRenderer.NoGlow;
    /// <summary>
    /// Scales for the mask's metalness and smoothness, the strength of the normal map, and which specular
    /// light the surface takes: two if it mirrors its surroundings, plus one if it shows lights' highlights.
    /// Unity's Simple Lit has no reflections and a highlight only when switched on.
    /// </summary>
    public Vector4 Factors { get; init; } = new(0, 0, 1, Specular(true, true));
    public static float Specular(bool reflections, bool highlights) => (reflections ? 2 : 0) + (highlights ? 1 : 0);
    public Vector3 Emission { get; init; }
    /// <summary>Drawn over the opaque scene by its alpha, without writing depth.</summary>
    public bool Blend { get; init; }

    /// <summary>A matte dielectric for surfaces no authored material describes. It shades as the base texture alone did.</summary>
    public static readonly SurfaceMaterial Plain = new();
}

/// <summary>
/// Finds an authored material from what a baked surface keeps of it: its base texture and tint.
/// Model and skin files therefore need no material slot of their own.
/// </summary>
public sealed class SurfaceMaterials
{
    sealed record Entry(string name, string texture, float[] tint, string normal, float bump, string mask,
        float metallic, float smoothness, string glow, float[] emission, bool blend, string[] models,
        bool? reflections, bool? highlights);
    sealed record Library(Entry[] materials);
    sealed record Candidate(Vector4 Tint, SurfaceMaterial Material, HashSet<string> Models, int Maps, float Show);

    readonly Dictionary<string, List<Candidate>> byTexture = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A missing file leaves every surface plain.</summary>
    static float Decode(float c) => MathF.Pow(Math.Max(c, 0), 2.2f);

    public static SurfaceMaterials Load(string path)
    {
        var materials = new SurfaceMaterials();
        if (!File.Exists(path)) return materials;
        var library = JsonSerializer.Deserialize<Library>(File.ReadAllBytes(path));
        foreach (var e in library.materials)
        {
            // The library keeps emission in display space. Surfaces add it as linear light.
            var emission = new Vector3(Decode(e.emission[0]), Decode(e.emission[1]), Decode(e.emission[2]));
            var material = new SurfaceMaterial
            {
                Normal = e.normal.Length > 0 ? e.normal : StaticMeshRenderer.FlatNormal,
                Mask = e.mask.Length > 0 ? e.mask : StaticMeshRenderer.PlainMask,
                // A glow colour without a map lights the whole surface.
                Glow = e.glow.Length > 0 ? e.glow : emission == Vector3.Zero ? StaticMeshRenderer.NoGlow : "",
                Factors = new Vector4(e.metallic, e.smoothness, e.bump, SurfaceMaterial.Specular(e.reflections != false, e.highlights != false)),
                Emission = emission,
                Blend = e.blend
            };
            string key = Path.GetFileNameWithoutExtension(e.texture);
            if (!materials.byTexture.TryGetValue(key, out var list)) materials.byTexture.Add(key, list = new());
            int maps = (e.normal.Length > 0 ? 2 : 0) + (e.mask.Length > 0 ? 1 : 0) + (e.glow.Length > 0 ? 1 : 0);
            float show = emission.X + emission.Y + emission.Z + e.metallic + e.smoothness + (e.blend ? 1 : 0);
            list.Add(new(new Vector4(e.tint[0], e.tint[1], e.tint[2], e.tint[3]), material,
                new HashSet<string>(e.models ?? [], StringComparer.OrdinalIgnoreCase), maps, show));
        }
        return materials;
    }

    /// <summary>
    /// The material sharing this base texture whose tint is nearest. <paramref name="model"/> names the
    /// asset the surface belongs to, as kind-id, and settles a tie between materials of different assets.
    /// Safe to call from asset workers.
    /// </summary>
    public SurfaceMaterial Find(string texture, Vector4 tint, string model = null)
    {
        string key = Path.GetFileNameWithoutExtension(texture ?? "");
        if (!byTexture.TryGetValue(key, out var list)) return SurfaceMaterial.Plain;
        float nearest = float.MaxValue;
        foreach (var candidate in list) nearest = Math.Min(nearest, Vector4.DistanceSquared(candidate.Tint, tint));
        // Materials sharing a texture share its maps. Untextured ones have only their colour in common,
        // so an untextured surface of some other colour belongs to none of them.
        if (key.Length == 0 && nearest >= .0015f) return SurfaceMaterial.Plain;
        Candidate best = null;
        bool bestOwned = false;
        foreach (var candidate in list)
        {
            if (Vector4.DistanceSquared(candidate.Tint, tint) > nearest + .0004f) continue;
            bool owned = model != null && candidate.Models.Contains(model);
            bool better;
            if (best == null) better = true;
            else if (owned != bestOwned) better = owned;
            // Among a model's own materials, or those of one texture, the fullest set of maps describes most of it.
            else if (owned || key.Length > 0) better = candidate.Maps > best.Maps;
            // With no owner known, an untextured surface takes the plainest reading: it may be anyone's white paint.
            else better = candidate.Show < best.Show;
            if (better) { best = candidate; bestOwned = owned; }
        }
        return best.Material;
    }
}
