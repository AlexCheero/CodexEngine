using System.Numerics;
using N = System.Numerics;
using System.Runtime.InteropServices;

namespace CodexEngine.Runtime;

/// <summary>
/// Bounding spheres stored a field to an array, so that a test takes as many of them at a time as the processor's
/// vectors hold. The arrays are padded to whole blocks with spheres of negative infinite radius, which every
/// plane test culls; a result beyond <see cref="Count"/> means nothing.
/// </summary>
public sealed class SphereSet
{
    /// <summary>The arrays are a whole number of these long, so a vector of any width stays inside them.</summary>
    public const int Block = 16;

    public readonly float[] X, Y, Z, Radius;
    public readonly int Count;

    /// <summary>The arrays' length, and the least length of a span a test writes its results to.</summary>
    public int Padded => X.Length;

    public static int PaddedLength(int count) => (count + Block - 1) / Block * Block;

    public SphereSet(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Count = count;
        int padded = PaddedLength(count);
        X = new float[padded]; Y = new float[padded]; Z = new float[padded]; Radius = new float[padded];
        Radius.AsSpan(count).Fill(float.NegativeInfinity);
    }

    public void Set(int index, float x, float y, float z, float radius)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
        X[index] = x; Y[index] = y; Z[index] = z; Radius[index] = radius;
    }
}

/// <summary>
/// Tests over a whole <see cref="SphereSet"/> at once. Each is the arithmetic of the one-sphere test it replaces,
/// operation for operation and in the same order, so its answers are the same to the last bit on every processor
/// and vector width; where vectors are not available the plain loop below it runs instead.
/// A "cull" test ORs -1 into the entry of every sphere it rules out and leaves the others as they were, so
/// several tests can fill one span.
/// </summary>
public static class SphereTests
{
    /// <summary>Runs the plain loops even where vectors are available, to check one against the other.</summary>
    public static bool ForceScalar { get; set; }

    static bool Wide => Vector.IsHardwareAccelerated && !ForceScalar;

    /// <summary>What a worker thread needs to cull one set: grown as needed and kept for the thread.</summary>
    public sealed class Scratch
    {
        [ThreadStatic] static Scratch local;
        public int[] Culled = [], Other = [];
        public float[] Distance = [];

        public static Scratch For(int padded)
        {
            var scratch = local ??= new Scratch();
            if (scratch.Culled.Length < padded)
            {
                int size = Math.Max(padded, scratch.Culled.Length * 2);
                scratch.Culled = new int[size]; scratch.Other = new int[size]; scratch.Distance = new float[size];
            }
            return scratch;
        }
    }

    /// <summary>
    /// Culls every sphere that lies wholly in front of one of the planes. A plane is its normal in x, y and z
    /// and its distance in w: a frustum's six, with normals pointing outward, cull what it cannot see.
    /// </summary>
    public static void CullByPlanes(SphereSet spheres, ReadOnlySpan<N.Vector4> planes, Span<int> culled)
    {
        int padded = spheres.Padded;
        ArgumentOutOfRangeException.ThrowIfLessThan(culled.Length, padded);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(planes.Length, 8);
        if (!Wide)
        {
            for (int i = 0; i < spheres.Count; i++)
                foreach (var plane in planes)
                    if (spheres.X[i] * plane.X + spheres.Y[i] * plane.Y + spheres.Z[i] * plane.Z + plane.W > spheres.Radius[i])
                    { culled[i] = -1; break; }
            return;
        }
        Span<Vector<float>> wide = stackalloc Vector<float>[planes.Length * 4];
        for (int p = 0; p < planes.Length; p++)
        {
            wide[p * 4] = new Vector<float>(planes[p].X); wide[p * 4 + 1] = new Vector<float>(planes[p].Y);
            wide[p * 4 + 2] = new Vector<float>(planes[p].Z); wide[p * 4 + 3] = new Vector<float>(planes[p].W);
        }
        ref float xs = ref MemoryMarshal.GetArrayDataReference(spheres.X), ys = ref MemoryMarshal.GetArrayDataReference(spheres.Y);
        ref float zs = ref MemoryMarshal.GetArrayDataReference(spheres.Z), rs = ref MemoryMarshal.GetArrayDataReference(spheres.Radius);
        ref int result = ref MemoryMarshal.GetReference(culled);
        for (int i = 0; i < padded; i += Vector<float>.Count)
        {
            var x = Vector.LoadUnsafe(ref xs, (nuint)i); var y = Vector.LoadUnsafe(ref ys, (nuint)i);
            var z = Vector.LoadUnsafe(ref zs, (nuint)i); var r = Vector.LoadUnsafe(ref rs, (nuint)i);
            var outside = Vector.LoadUnsafe(ref result, (nuint)i);
            for (int p = 0; p < wide.Length; p += 4)
                outside |= Vector.GreaterThan(x * wide[p] + y * wide[p + 1] + z * wide[p + 2] + wide[p + 3], r);
            outside.StoreUnsafe(ref result, (nuint)i);
        }
    }

    /// <summary>Culls every sphere that does not touch the given one.</summary>
    public static void CullBySphere(SphereSet spheres, N.Vector3 center, float radius, Span<int> culled)
    {
        int padded = spheres.Padded;
        ArgumentOutOfRangeException.ThrowIfLessThan(culled.Length, padded);
        if (!Wide)
        {
            for (int i = 0; i < spheres.Count; i++)
            {
                float dx = spheres.X[i] - center.X, dy = spheres.Y[i] - center.Y, dz = spheres.Z[i] - center.Z;
                float reach = spheres.Radius[i] + radius;
                if (dx * dx + dy * dy + dz * dz > reach * reach) culled[i] = -1;
            }
            return;
        }
        var cx = new Vector<float>(center.X); var cy = new Vector<float>(center.Y); var cz = new Vector<float>(center.Z);
        var other = new Vector<float>(radius);
        ref float xs = ref MemoryMarshal.GetArrayDataReference(spheres.X), ys = ref MemoryMarshal.GetArrayDataReference(spheres.Y);
        ref float zs = ref MemoryMarshal.GetArrayDataReference(spheres.Z), rs = ref MemoryMarshal.GetArrayDataReference(spheres.Radius);
        ref int result = ref MemoryMarshal.GetReference(culled);
        for (int i = 0; i < padded; i += Vector<float>.Count)
        {
            var dx = Vector.LoadUnsafe(ref xs, (nuint)i) - cx; var dy = Vector.LoadUnsafe(ref ys, (nuint)i) - cy;
            var dz = Vector.LoadUnsafe(ref zs, (nuint)i) - cz;
            var reach = Vector.LoadUnsafe(ref rs, (nuint)i) + other;
            (Vector.LoadUnsafe(ref result, (nuint)i) | Vector.GreaterThan(dx * dx + dy * dy + dz * dz, reach * reach)).StoreUnsafe(ref result, (nuint)i);
        }
    }

    /// <summary>Writes the distance from a point to each sphere's centre.</summary>
    public static void Distances(SphereSet spheres, N.Vector3 point, Span<float> distances)
    {
        int padded = spheres.Padded;
        ArgumentOutOfRangeException.ThrowIfLessThan(distances.Length, padded);
        if (!Wide)
        {
            for (int i = 0; i < spheres.Count; i++)
            {
                float dx = spheres.X[i] - point.X, dy = spheres.Y[i] - point.Y, dz = spheres.Z[i] - point.Z;
                distances[i] = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            }
            return;
        }
        var px = new Vector<float>(point.X); var py = new Vector<float>(point.Y); var pz = new Vector<float>(point.Z);
        ref float xs = ref MemoryMarshal.GetArrayDataReference(spheres.X), ys = ref MemoryMarshal.GetArrayDataReference(spheres.Y);
        ref float zs = ref MemoryMarshal.GetArrayDataReference(spheres.Z);
        ref float result = ref MemoryMarshal.GetReference(distances);
        for (int i = 0; i < padded; i += Vector<float>.Count)
        {
            var dx = Vector.LoadUnsafe(ref xs, (nuint)i) - px; var dy = Vector.LoadUnsafe(ref ys, (nuint)i) - py;
            var dz = Vector.LoadUnsafe(ref zs, (nuint)i) - pz;
            Vector.SquareRoot(dx * dx + dy * dy + dz * dz).StoreUnsafe(ref result, (nuint)i);
        }
    }

    /// <summary>Culls every entry whose limit is above zero and below its distance. The spans are as long as a set's padding.</summary>
    public static void CullByLimit(ReadOnlySpan<float> distances, ReadOnlySpan<float> limits, Span<int> culled)
    {
        int padded = limits.Length;
        ArgumentOutOfRangeException.ThrowIfLessThan(distances.Length, padded);
        ArgumentOutOfRangeException.ThrowIfLessThan(culled.Length, padded);
        if (!Wide || padded % Vector<float>.Count != 0)
        {
            for (int i = 0; i < padded; i++)
                if (limits[i] > 0 && distances[i] > limits[i]) culled[i] = -1;
            return;
        }
        ref float ds = ref MemoryMarshal.GetReference(distances), ls = ref MemoryMarshal.GetReference(limits);
        ref int result = ref MemoryMarshal.GetReference(culled);
        for (int i = 0; i < padded; i += Vector<float>.Count)
        {
            var limit = Vector.LoadUnsafe(ref ls, (nuint)i);
            var beyond = Vector.GreaterThan(limit, Vector<float>.Zero) & Vector.GreaterThan(Vector.LoadUnsafe(ref ds, (nuint)i), limit);
            (Vector.LoadUnsafe(ref result, (nuint)i) | beyond).StoreUnsafe(ref result, (nuint)i);
        }
    }
}
