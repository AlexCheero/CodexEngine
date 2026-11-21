using CodexECS;
using Microsoft.Xna.Framework;

namespace CodexEngine.Rendering;

/// <summary>The triangles of something solid enough to hide what stands behind it, in its own space.</summary>
public sealed class OccluderMesh(Vector3[] positions, int[] indices)
{
    public readonly Vector3[] Positions = positions;
    public readonly int[] Indices = indices;
    public int Triangles => Indices.Length / 3;
}

/// <summary>One placed occluder: its mesh, where it stands and the sphere around it.</summary>
public struct Occluder
{
    public OccluderMesh Mesh;
    public Matrix World;
    public BoundingSphere Bounds;
}

/// <summary>
/// A small depth image of the view's largest solid things, drawn on the job threads, that tells whether a bounding
/// sphere is hidden behind them. Each frame <see cref="Build"/> picks the occluders that cover most of the screen
/// for their triangles, up to <see cref="TriangleBudget"/>, and draws them; <see cref="Hidden"/> may then be asked
/// from any thread. An occluder must be opaque everywhere and stand still for the frame.
/// The image errs toward showing: a pixel holds the farthest depth its occluder reaches inside it, a sphere is
/// tested by its nearest point over one more pixel on every side than it covers, and a triangle that crosses the
/// near plane is left out.
/// </summary>
public sealed class OcclusionBuffer
{
    struct Triangle
    {
        // Screen positions in pixels and 1/depth at each corner, with the bounds of the pixels it may cover.
        public float X0, Y0, X1, Y1, X2, Y2, Q0, Q1, Q2;
        public int Left, Right, Top, Bottom;
    }

    readonly int width, height;
    // 1/depth of the nearest occluder at each pixel, zero where there is none. Larger is nearer.
    readonly float[] nearest;
    readonly List<(float Score, int Index)> order = new();
    readonly List<Occluder> chosen = new();
    readonly List<int> first = new();
    readonly Action<int, int> setUp, draw;
    Triangle[] triangles = new Triangle[4096];
    Matrix viewProjection;
    // The projection's scale on each screen axis, for the size of a sphere's outline.
    float scaleX, scaleY;
    int triangleCount;
    bool any;

    /// <summary>The most triangles one frame draws. Occluders are taken in order of screen cover per triangle.</summary>
    public int TriangleBudget { get; set; } = 4000;
    /// <summary>An occluder narrower than this many of the image's pixels is not worth its triangles.</summary>
    public float MinPixels { get; set; } = 12;
    /// <summary>Occluders and triangles the last <see cref="Build"/> drew.</summary>
    public int Occluders => chosen.Count;
    public int Triangles => triangleCount;

    public OcclusionBuffer(int width = 256, int height = 144)
    {
        this.width = width; this.height = height;
        nearest = new float[width * height];
        setUp = SetUp; draw = Draw;
    }

    /// <summary>
    /// Draws this frame's image from the occluders <paramref name="frustum"/> sees. <paramref name="eye"/> is the
    /// view's position and <paramref name="jobs"/> shares the work between its threads.
    /// </summary>
    public void Build(ReadOnlySpan<Occluder> candidates, Matrix view, Matrix projection, BoundingFrustum frustum, Vector3 eye, JobScheduler jobs = null)
    {
        viewProjection = view * projection;
        scaleX = MathF.Abs(projection.M11); scaleY = MathF.Abs(projection.M22);
        order.Clear(); chosen.Clear(); first.Clear();
        // How many image pixels a unit sphere at unit distance spans.
        float scale = MathF.Abs(projection.M22) * height * .5f;
        for (int i = 0; i < candidates.Length; i++)
        {
            ref readonly var c = ref candidates[i];
            if (c.Mesh.Triangles == 0 || !frustum.Intersects(c.Bounds)) continue;
            float distance = MathF.Max(Vector3.Distance(c.Bounds.Center, eye) - c.Bounds.Radius, .5f);
            float pixels = c.Bounds.Radius / distance * scale;
            if (pixels < MinPixels) continue;
            order.Add((pixels * pixels / c.Mesh.Triangles, i));
        }
        order.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        int total = 0;
        foreach (var (_, i) in order)
        {
            int count = candidates[i].Mesh.Triangles;
            if (total + count > TriangleBudget) continue;
            first.Add(total); chosen.Add(candidates[i]); total += count;
        }
        triangleCount = total;
        any = total > 0;
        Array.Clear(nearest);
        if (!any) return;
        if (triangles.Length < total) triangles = new Triangle[Math.Max(total, triangles.Length * 2)];
        // Triangles are projected an occluder at a time, then drawn a band of rows at a time: no two threads
        // write the same pixel.
        if (jobs != null) { jobs.Run(chosen.Count, 4, setUp); jobs.Run(height, 16, draw); }
        else { SetUp(0, chosen.Count); Draw(0, height); }
    }

    void SetUp(int start, int end)
    {
        Span<Vector4> clip = stackalloc Vector4[3];
        for (int o = start; o < end; o++)
        {
            var occluder = chosen[o];
            var transform = occluder.World * viewProjection;
            var positions = occluder.Mesh.Positions; var indices = occluder.Mesh.Indices;
            int written = first[o];
            for (int i = 0; i < indices.Length; i += 3)
            {
                clip[0] = Vector4.Transform(positions[indices[i]], transform);
                clip[1] = Vector4.Transform(positions[indices[i + 1]], transform);
                clip[2] = Vector4.Transform(positions[indices[i + 2]], transform);
                ref var t = ref triangles[written];
                t.Left = 1; t.Right = 0;
                written++;
                // One that reaches behind the eye would need clipping. Leaving it out only shows more.
                if (clip[0].W < .05f || clip[1].W < .05f || clip[2].W < .05f) continue;
                t.Q0 = 1 / clip[0].W; t.Q1 = 1 / clip[1].W; t.Q2 = 1 / clip[2].W;
                t.X0 = (clip[0].X * t.Q0 * .5f + .5f) * width; t.Y0 = (.5f - clip[0].Y * t.Q0 * .5f) * height;
                t.X1 = (clip[1].X * t.Q1 * .5f + .5f) * width; t.Y1 = (.5f - clip[1].Y * t.Q1 * .5f) * height;
                t.X2 = (clip[2].X * t.Q2 * .5f + .5f) * width; t.Y2 = (.5f - clip[2].Y * t.Q2 * .5f) * height;
                float area = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0);
                if (MathF.Abs(area) < 1e-3f) continue;
                // Either winding is drawn: put the corners in the order the edge tests below expect.
                if (area < 0) { (t.X1, t.X2) = (t.X2, t.X1); (t.Y1, t.Y2) = (t.Y2, t.Y1); (t.Q1, t.Q2) = (t.Q2, t.Q1); }
                int left = (int)MathF.Floor(MathF.Min(t.X0, MathF.Min(t.X1, t.X2))), right = (int)MathF.Ceiling(MathF.Max(t.X0, MathF.Max(t.X1, t.X2)));
                int top = (int)MathF.Floor(MathF.Min(t.Y0, MathF.Min(t.Y1, t.Y2))), bottom = (int)MathF.Ceiling(MathF.Max(t.Y0, MathF.Max(t.Y1, t.Y2)));
                if (right < 0 || left >= width || bottom < 0 || top >= height) continue;
                t.Left = Math.Max(left, 0); t.Right = Math.Min(right, width - 1);
                t.Top = Math.Max(top, 0); t.Bottom = Math.Min(bottom, height - 1);
            }
        }
    }

    void Draw(int firstRow, int endRow)
    {
        for (int i = 0; i < triangleCount; i++)
        {
            ref readonly var t = ref triangles[i];
            if (t.Left > t.Right || t.Bottom < firstRow || t.Top >= endRow) continue;
            float area = (t.X1 - t.X0) * (t.Y2 - t.Y0) - (t.X2 - t.X0) * (t.Y1 - t.Y0), inverse = 1 / area;
            // 1/depth is a plane over the screen. A pixel takes the smallest value it holds anywhere inside itself:
            // the value at its centre, less half its change across the pixel each way.
            float qx = ((t.Q1 - t.Q0) * (t.Y2 - t.Y0) - (t.Q2 - t.Q0) * (t.Y1 - t.Y0)) * inverse;
            float qy = ((t.Q2 - t.Q0) * (t.X1 - t.X0) - (t.Q1 - t.Q0) * (t.X2 - t.X0)) * inverse;
            float slack = .5f * (MathF.Abs(qx) + MathF.Abs(qy));
            int top = Math.Max(t.Top, firstRow), bottom = Math.Min(t.Bottom, endRow - 1);
            // Each edge is a line a*x + b*y + c >= 0 on the inside. Along a row that bounds x from one side, so a
            // row's pixels are one run between the tightest bounds, found without testing every pixel.
            float a0 = t.Y0 - t.Y1, b0 = t.X1 - t.X0, c0 = -(a0 * t.X0 + b0 * t.Y0);
            float a1 = t.Y1 - t.Y2, b1 = t.X2 - t.X1, c1 = -(a1 * t.X1 + b1 * t.Y1);
            float a2 = t.Y2 - t.Y0, b2 = t.X0 - t.X2, c2 = -(a2 * t.X2 + b2 * t.Y2);
            for (int y = top; y <= bottom; y++)
            {
                float py = y + .5f, from = t.Left + .5f, to = t.Right + .5f;
                if (!Bound(a0, b0 * py + c0, ref from, ref to) || !Bound(a1, b1 * py + c1, ref from, ref to) ||
                    !Bound(a2, b2 * py + c2, ref from, ref to)) continue;
                int first = (int)MathF.Ceiling(from - .5f), last = (int)MathF.Floor(to - .5f);
                if (first > last) continue;
                float q = t.Q0 + qx * (first + .5f - t.X0) + qy * (py - t.Y0) - slack;
                var row = nearest.AsSpan(y * width + first, last - first + 1);
                for (int x = 0; x < row.Length; x++, q += qx)
                    if (q > row[x]) row[x] = q;
            }
        }
    }

    // Narrows [from, to] to where a*x + rest >= 0. False when nothing is left.
    static bool Bound(float a, float rest, ref float from, ref float to)
    {
        if (a > 1e-12f) from = MathF.Max(from, -rest / a);
        else if (a < -1e-12f) to = MathF.Min(to, -rest / a);
        else if (rest < 0) return false;
        return from <= to;
    }

    /// <summary>Whether everything inside the sphere lies behind the frame's occluders. Safe from any thread after <see cref="Build"/>.</summary>
    public bool Hidden(in BoundingSphere bounds)
    {
        if (!any) return false;
        var c = Vector4.Transform(bounds.Center, viewProjection);
        float front = c.W - bounds.Radius;
        // A sphere the eye is inside of, or nearly, covers the screen.
        if (front < .1f) return false;
        // The sphere's outline, taken a little wide: its radius at its nearest depth.
        float q = 1 / c.W, reach = bounds.Radius / front;
        float halfX = reach * scaleX * width * .5f, halfY = reach * scaleY * height * .5f;
        float x = (c.X * q * .5f + .5f) * width, y = (.5f - c.Y * q * .5f) * height;
        int left = (int)MathF.Floor(x - halfX) - 1, right = (int)MathF.Floor(x + halfX) + 1;
        int top = (int)MathF.Floor(y - halfY) - 1, bottom = (int)MathF.Floor(y + halfY) + 1;
        // What reaches past the image's edge cannot be told hidden from the image.
        if (left < 0 || top < 0 || right >= width || bottom >= height) return false;
        float nearestPoint = 1 / front;
        for (int row = top; row <= bottom; row++)
        {
            int at = row * width;
            for (int column = left; column <= right; column++)
                if (nearest[at + column] <= nearestPoint) return false;
        }
        return true;
    }
}
