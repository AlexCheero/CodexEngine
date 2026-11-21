using CodexECS;
using N = System.Numerics;

// A namespace of its own: CodexEngine.Runtime has a Vector3, and a renderer that imports the lights also imports its
// framework's vectors.
namespace CodexEngine.Lighting;

[Flags]
public enum LightTraits : byte
{
    None = 0,
    /// <summary>A cone along <see cref="LocalLight.Direction"/> instead of a sphere.</summary>
    Spot = 1,
    /// <summary>
    /// Already in the level's baked lighting. Surfaces lit by a lightmap or by light probes skip it; everything
    /// else still receives it.
    /// </summary>
    Baked = 2,
    /// <summary>May be given a real-time shadow map when the renderer has one to spare.</summary>
    CastsShadows = 4
}

/// <summary>
/// A point or spot light: a lamp, a muzzle flash, an explosion. Any entity that carries one is lit from by every
/// renderer that collects the world, so a light is made, moved and removed like any other component.
/// Positions and directions are in the space the renderer draws in.
/// </summary>
public struct LocalLight : IComponent
{
    public N.Vector3 Position;
    /// <summary>Where the light ends. It falls off by the inverse square and eases to zero here.</summary>
    public float Range;
    /// <summary>
    /// The size of the source. Nothing nearer than this is lit more brightly than a surface at this distance, so
    /// what holds the light or stands inside it (a weapon and its muzzle flash, smoke in an explosion) does not
    /// burn out. Zero for a point. Kept to steps of 1/64 up to 16 units.
    /// </summary>
    public float Radius;
    /// <summary>Linear radiance at one unit of distance.</summary>
    public N.Vector3 Color;
    /// <summary>Unit axis of a spot's cone, pointing away from the light.</summary>
    public N.Vector3 Direction;
    /// <summary>Full cone angles of a spot in degrees: light is whole inside the inner one and gone at the outer.</summary>
    public float OuterAngle, InnerAngle;
    /// <summary>The channel of the surface's baked shadow mask that holds this light's shadow, or negative for none.</summary>
    public sbyte ShadowChannel;
    public LightTraits Traits;

    public static LocalLight Point(N.Vector3 position, N.Vector3 color, float range) =>
        new() { Position = position, Color = color, Range = range, ShadowChannel = -1 };

    public static LocalLight Spot(N.Vector3 position, N.Vector3 direction, N.Vector3 color, float range,
        float outerAngle, float innerAngle) =>
        new()
        {
            Position = position, Color = color, Range = range, Direction = N.Vector3.Normalize(direction),
            OuterAngle = outerAngle, InnerAngle = innerAngle, ShadowChannel = -1, Traits = LightTraits.Spot
        };
}

/// <summary>
/// Beside a <see cref="LocalLight"/>: the light lasts from <see cref="Start"/> for <see cref="Duration"/> seconds
/// on the <see cref="LightClock"/>, and <see cref="LightFlashSystem"/> then deletes its entity.
/// </summary>
public struct LightFlash : IComponent
{
    public float Start, Duration;
    /// <summary>
    /// How the light dims: it keeps the share of time left raised to this power. 0 stays whole until the end,
    /// 1 fades evenly, 2 drops fast and lingers.
    /// </summary>
    public float Fade;

    /// <summary>The share of the light's colour left at <paramref name="now"/>; zero before the start and after the end.</summary>
    public readonly float Strength(float now)
    {
        float t = (now - Start) / Duration;
        if (!(t >= 0 && t < 1)) return 0;
        return Fade <= 0 ? 1 : Fade == 1 ? 1 - t : Fade == 2 ? (1 - t) * (1 - t) : MathF.Pow(1 - t, Fade);
    }
}

/// <summary>The time flashes are measured against. Whoever owns the world sets <see cref="Now"/> before it ticks.</summary>
public sealed class LightClock
{
    public float Now;
}

/// <summary>Deletes the entities of flashes that have ended. Needs the world's <see cref="LightClock"/> service.</summary>
[System(ESystemCategory.Update)]
public sealed class LightFlashSystem : EcsSystem
{
    private readonly LightClock clock;
    private readonly FilterRows<LightFlash> flashes;

    public LightFlashSystem(EcsWorld world)
    {
        clock = world.Service<LightClock>();
        flashes = world.Filter().With<LightFlash>().Build().Rows<LightFlash>();
        Declare().Write<LightFlash>().Write<LocalLight>();
    }

    public override void Tick(EcsWorld world)
    {
        CompleteDependency();
        float now = clock.Now;
        EntityCommandBuffer commands = null;
        foreach (var row in flashes)
        {
            ref readonly var flash = ref row.Item1;
            if (now - flash.Start < flash.Duration && now >= flash.Start) continue;
            commands ??= world.CreateCommandBuffer();
            commands.Delete(row.Id);
        }
    }

    /// <summary>
    /// The same sweep for a world that runs no pipeline, such as a renderer's own world of presentation lights.
    /// </summary>
    public static void Sweep(EcsWorld world, FilterRows<LightFlash> flashes, float now)
    {
        foreach (var row in flashes)
        {
            ref readonly var flash = ref row.Item1;
            if (now - flash.Start >= flash.Duration || now < flash.Start) world.Delete(row.Id);
        }
    }
}

/// <summary>Makes light entities.</summary>
public static class LightSpawning
{
    public static int Spawn(this EcsWorld world, in LocalLight light)
    {
        int id = world.Create();
        world.Add(id, light);
        return id;
    }

    /// <summary>
    /// A light that lasts <paramref name="duration"/> seconds from <paramref name="now"/>, dimming by
    /// <see cref="LightFlash.Fade"/>.
    /// </summary>
    public static int SpawnFlash(this EcsWorld world, in LocalLight light, float now, float duration, float fade)
    {
        int id = world.Spawn(light);
        world.Add(id, new LightFlash { Start = now, Duration = duration, Fade = fade });
        return id;
    }

    /// <summary>The same, whole until the end or fading by the square of the time left.</summary>
    public static int SpawnFlash(this EcsWorld world, in LocalLight light, float now, float duration, bool fades) =>
        world.SpawnFlash(in light, now, duration, fades ? 2f : 0f);
}
