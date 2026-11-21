using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// How a level's fog departs from one flat colour and density, for effects that include Shaders/Atmosphere.hlsl.
/// A level without an entry keeps its authored fog exactly.
/// </summary>
public sealed record Atmosphere
{
    /// <summary>How fast the fog thins with height, per world unit. 0.05 halves it every 14 units; 0 keeps it even.</summary>
    public float HeightFalloff { get; init; }
    /// <summary>The height at which the fog has its authored density.</summary>
    public float BaseHeight { get; init; }
    /// <summary>Share of the fog colour taken from the sky in the direction looked, 0 to 1. Distant shapes then fade into the sky behind them.</summary>
    public float SkyShare { get; init; }
    /// <summary>Strength of the glow the fog takes on toward the sun.</summary>
    public float SunScatter { get; init; }
    /// <summary>The glow's colour, display-encoded.</summary>
    public float[] SunColour { get; init; } = [1, 1, 1];
    /// <summary>How tight the glow is around the sun. Higher is tighter.</summary>
    public float SunExponent { get; init; } = 8;
    /// <summary>Strength of light shafts from the sun (<see cref="PostProcessing.Shafts"/>), as a scale on the sky's radiance. 0 for none.</summary>
    public float ShaftStrength { get; init; }
    /// <summary>How far toward the sun a shaft gathers its light, as a share of the distance on screen.</summary>
    public float ShaftReach { get; init; } = .7f;
    /// <summary>Sky dimmer than this (linear radiance) feeds no shaft, so only the sun and what it lights streak.</summary>
    public float ShaftThreshold { get; init; } = .2f;

    public static readonly Atmosphere None = new();

    /// <summary>Entries by level name. A missing file leaves every level's fog as authored.</summary>
    public static Dictionary<string, Atmosphere> Load(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, Atmosphere>>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
            : [];

    /// <summary>
    /// Binds the settings. <paramref name="sky"/> is the sky's colour along the six axes
    /// (<see cref="EnvironmentCube.Axes"/>), which <see cref="SkyShare"/> draws on; without it that share stays black.
    /// </summary>
    public void Bind(Effect material, Vector3[] sky = null)
    {
        static float Linear(float c) => c <= .04045f ? c / 12.92f : MathF.Pow((c + .055f) / 1.055f, 2.4f);
        material.Parameters["FogShape"].SetValue(new Vector4(HeightFalloff, BaseHeight, SkyShare, SunScatter));
        material.Parameters["FogSun"].SetValue(new Vector4(Linear(SunColour[0]), Linear(SunColour[1]), Linear(SunColour[2]), SunExponent));
        if (sky != null) material.Parameters["FogSky"].SetValue(sky);
    }
}
