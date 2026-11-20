// These routines take System.Numerics vectors (the engine's own Vector3 is a different type).
using Vec3 = System.Numerics.Vector3;

namespace CodexEngine.Runtime;

/// <summary>A capsule between two sphere centres, for <see cref="CapsuleSweep"/>.</summary>
public readonly record struct SweptCapsule(Vec3 P0, Vec3 P1, float Radius)
{
    /// <summary>An upright capsule standing on <paramref name="feet"/>.</summary>
    public static SweptCapsule Upright(Vec3 feet, float height, float radius) =>
        new(feet + Vec3.UnitY * radius, feet + Vec3.UnitY * (height - radius), radius);
}

/// <summary>
/// Moves a character capsule against capsule obstacles outside the physics engine: cast, stop just before the first hit,
/// slide the remainder, then push out of residual overlaps (scaled by a depenetration multiplier). This is the
/// character-versus-crowd step of games that keep their mobs in a custom collision world beside the engine's character
/// controller, so a crowd can block and pin the player without being rigid bodies.
/// </summary>
public static class CapsuleSweep
{
    /// <summary>Returns the position delta to apply this step for a requested <paramref name="velocity"/> delta.</summary>
    public static Vec3 Move(in SweptCapsule mover, Vec3 velocity, ReadOnlySpan<SweptCapsule> obstacles,
        float depenetrationMultiplier = 2, int iterations = 3)
    {
        var delta = velocity;
        foreach (ref readonly var other in obstacles)
        {
            if (!Cast(mover, delta, other, out float t, out var normal)) continue;
            // Each hit restarts from the requested motion: stop at contact, nudge off the surface, slide the rest.
            delta = velocity * t + normal * .001f;
            var remaining = velocity * (1 - t);
            remaining -= normal * Vec3.Dot(remaining, normal);
            delta += remaining;
        }
        return delta + Depenetrate(mover, obstacles, iterations) * depenetrationMultiplier;
    }

    /// <summary>Total correction that separates <paramref name="mover"/> from overlapping obstacles.</summary>
    public static Vec3 Depenetrate(in SweptCapsule mover, ReadOnlySpan<SweptCapsule> obstacles, int iterations = 3)
    {
        Vec3 p0 = mover.P0, p1 = mover.P1, total = default;
        for (int i = 0; i < iterations; i++)
        {
            bool any = false;
            foreach (ref readonly var other in obstacles)
            {
                float distance = SegmentDistance(p0, p1, other.P0, other.P1, out var a, out var b);
                float combined = mover.Radius + other.Radius;
                if (distance >= combined) continue;
                var normal = distance > 1e-6f ? Vec3.Normalize(a - b) : Vec3.UnitY;
                var correction = normal * (combined - distance);
                p0 += correction; p1 += correction; total += correction; any = true;
            }
            if (!any) break;
        }
        return total;
    }

    // Sweeps the moving capsule's two end points as rays against the obstacle grown by the mover's radius.
    private static bool Cast(in SweptCapsule mover, Vec3 velocity, in SweptCapsule other, out float t, out Vec3 normal)
    {
        t = float.MaxValue; normal = default;
        float distance = velocity.Length();
        if (distance < 1e-6f) return false;
        var dir = velocity / distance;
        float radius = other.Radius + mover.Radius, best = float.MaxValue;
        if (RayCapsule(mover.P0, dir, other.P0, other.P1, radius, out float d0, out var n0) && d0 <= distance) { best = d0; normal = n0; }
        if (RayCapsule(mover.P1, dir, other.P0, other.P1, radius, out float d1, out var n1) && d1 <= distance && d1 < best) { best = d1; normal = n1; }
        if (best == float.MaxValue) return false;
        t = best / distance;
        return true;
    }

    private static bool RayCapsule(Vec3 origin, Vec3 dir, Vec3 p0, Vec3 p1, float radius, out float distance, out Vec3 normal)
    {
        var ab = p1 - p0; var ao = origin - p0;
        float abLen2 = Vec3.Dot(ab, ab), abDir = Vec3.Dot(ab, dir), abAo = Vec3.Dot(ab, ao);
        float a = abLen2 - abDir * abDir, b = abLen2 * Vec3.Dot(ao, dir) - abAo * abDir;
        float c = abLen2 * Vec3.Dot(ao, ao) - abAo * abAo - radius * radius * abLen2;
        distance = float.MaxValue; normal = default;
        if (MathF.Abs(a) > 1e-6f)
        {
            float disc = b * b - a * c;
            if (disc >= 0)
            {
                float root = MathF.Sqrt(disc), t = (-b - root) / a;
                if (t < 0) t = (-b + root) / a;
                if (t >= 0)
                {
                    float projection = abAo + t * abDir;
                    if (projection >= 0 && projection <= abLen2)
                    {
                        distance = t;
                        normal = Vec3.Normalize(origin + dir * t - (p0 + ab * (projection / abLen2)));
                    }
                }
            }
        }
        if (RaySphere(origin, dir, p0, radius, out float s0, out var m0) && s0 < distance) { distance = s0; normal = m0; }
        if (RaySphere(origin, dir, p1, radius, out float s1, out var m1) && s1 < distance) { distance = s1; normal = m1; }
        return distance < float.MaxValue;
    }

    // Like Unity's sweep helpers, a ray starting inside the sphere reports its exit point.
    private static bool RaySphere(Vec3 origin, Vec3 dir, Vec3 center, float radius, out float distance, out Vec3 normal)
    {
        var oc = origin - center;
        float b = Vec3.Dot(oc, dir), c = Vec3.Dot(oc, oc) - radius * radius, disc = b * b - c;
        distance = 0; normal = default;
        if (disc < 0) return false;
        float root = MathF.Sqrt(disc), t = -b - root;
        if (t < 0) t = -b + root;
        if (t < 0) return false;
        distance = t; normal = Vec3.Normalize(origin + dir * t - center);
        return true;
    }

    private static float SegmentDistance(Vec3 a, Vec3 b, Vec3 c, Vec3 d, out Vec3 p1, out Vec3 p2)
    {
        var ab = b - a; var cd = d - c; var ac = c - a;
        float abLen2 = Vec3.Dot(ab, ab), cdLen2 = Vec3.Dot(cd, cd), acAb = Vec3.Dot(ac, ab), acCd = Vec3.Dot(ac, cd), abCd = Vec3.Dot(ab, cd);
        float denom = abLen2 * cdLen2 - abCd * abCd, s, t;
        if (denom < 1e-6f) { s = 0; t = Math.Clamp(acCd / cdLen2, 0, 1); }
        else
        {
            s = Math.Clamp((acAb * cdLen2 - acCd * abCd) / denom, 0, 1);
            t = Math.Clamp((acCd + s * abCd) / cdLen2, 0, 1);
            s = Math.Clamp((t * abCd + acAb) / abLen2, 0, 1);
        }
        p1 = a + ab * s; p2 = c + cd * t;
        return Vec3.Distance(p1, p2);
    }
}
