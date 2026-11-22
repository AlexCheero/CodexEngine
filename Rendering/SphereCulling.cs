using SphereSet = CodexEngine.Runtime.SphereSet;
using SphereTests = CodexEngine.Runtime.SphereTests;
using Microsoft.Xna.Framework;
using N = System.Numerics;

namespace CodexEngine.Rendering;

/// <summary>
/// The framework's volumes for the engine's whole-set sphere tests (<see cref="SphereTests"/>). A set culled here
/// loses exactly the spheres the volume's own Intersects would refuse one at a time.
/// </summary>
public static class SphereCulling
{
    /// <summary>A frustum's six planes as the tests take them: the normal in x, y and z, the distance in w.</summary>
    public static void Planes(BoundingFrustum frustum, Span<N.Vector4> planes)
    {
        planes[0] = Of(frustum.Near); planes[1] = Of(frustum.Far); planes[2] = Of(frustum.Left);
        planes[3] = Of(frustum.Right); planes[4] = Of(frustum.Top); planes[5] = Of(frustum.Bottom);
    }

    static N.Vector4 Of(Plane plane) => new(plane.Normal.X, plane.Normal.Y, plane.Normal.Z, plane.D);

    /// <summary>Culls the spheres <see cref="BoundingFrustum.Intersects(BoundingSphere)"/> is false for.</summary>
    public static void CullByFrustum(SphereSet spheres, BoundingFrustum frustum, Span<int> culled)
    {
        Span<N.Vector4> planes = stackalloc N.Vector4[6];
        Planes(frustum, planes);
        SphereTests.CullByPlanes(spheres, planes, culled);
    }

    /// <summary>Culls the spheres <see cref="BoundingSphere.Intersects(BoundingSphere)"/> is false for.</summary>
    public static void CullBySphere(SphereSet spheres, BoundingSphere volume, Span<int> culled) =>
        SphereTests.CullBySphere(spheres, new N.Vector3(volume.Center.X, volume.Center.Y, volume.Center.Z), volume.Radius, culled);

    /// <summary>A set holding the given spheres in their order.</summary>
    public static SphereSet SetOf(ReadOnlySpan<BoundingSphere> bounds)
    {
        var set = new SphereSet(bounds.Length);
        for (int i = 0; i < bounds.Length; i++)
            set.Set(i, bounds[i].Center.X, bounds[i].Center.Y, bounds[i].Center.Z, bounds[i].Radius);
        return set;
    }
}
