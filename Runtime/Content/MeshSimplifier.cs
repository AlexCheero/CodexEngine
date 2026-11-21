using System.Numerics;

namespace Foundry.Content;

/// <summary>
/// Coarser versions of a mesh that draw from its own vertices: only the index list changes, so a level of detail
/// costs no vertex memory and keeps every attribute a vertex carries (texture and lightmap coordinates, normals,
/// skin weights) exactly as authored.
/// Triangles are removed by moving one end of an edge onto the other, cheapest first by how far the surface
/// moves (the squared distance to the planes of the triangles it replaces). An edge may only collapse where every
/// vertex at the moved end has a partner at the other: along a seam or inside a smooth patch, never across the
/// border of a texture chart or a hard edge, and open borders are held in place. A mesh whose every face has its
/// own vertices therefore does not simplify at all, and is returned as it is.
/// </summary>
public static class MeshSimplifier
{
    /// <summary>
    /// The index list of a coarser mesh, its triangles still grouped by <paramref name="surfaces"/> in their order,
    /// with each group's new start and count in <paramref name="ranges"/>. Simplification stops when the next
    /// collapse would move the surface by more than <paramref name="maxError"/> world units, or when fewer than
    /// <paramref name="minShare"/> of the triangles are left. <paramref name="error"/> is the largest move made.
    /// Safe on any thread.
    /// </summary>
    public static int[] Simplify(ReadOnlySpan<float> positions, ReadOnlySpan<int> indices, ReadOnlySpan<(int Start, int Count)> surfaces,
        float maxError, float minShare, out (int Start, int Count)[] ranges, out float error)
    {
        int vertexCount = positions.Length / 3, triangleCount = indices.Length / 3;
        var at = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++) at[i] = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);

        // Vertices at one point are one place: collapses move places, and each vertex follows its own partner.
        var place = new int[vertexCount];
        var places = new Dictionary<Vector3, int>(vertexCount);
        var point = new List<Vector3>(vertexCount);
        for (int i = 0; i < vertexCount; i++)
        {
            if (!places.TryGetValue(at[i], out int id)) { id = point.Count; places.Add(at[i], id); point.Add(at[i]); }
            place[i] = id;
        }
        int placeCount = point.Count;

        var corner = indices.ToArray();
        var alive = new bool[triangleCount];
        var surfaceOf = new int[triangleCount];
        for (int s = 0; s < surfaces.Length; s++)
            for (int t = surfaces[s].Start / 3; t < (surfaces[s].Start + surfaces[s].Count) / 3; t++) { surfaceOf[t] = s; alive[t] = true; }
        var around = new List<int>[placeCount];
        for (int i = 0; i < placeCount; i++) around[i] = new List<int>(6);
        var quadric = new double[placeCount * 10];
        int live = 0;
        for (int t = 0; t < triangleCount; t++)
        {
            if (!alive[t]) continue;
            int a = place[corner[t * 3]], b = place[corner[t * 3 + 1]], c = place[corner[t * 3 + 2]];
            // A triangle with two corners at one place has no area and no plane.
            if (a == b || b == c || a == c) { alive[t] = false; continue; }
            live++;
            around[a].Add(t); around[b].Add(t); around[c].Add(t);
            var normal = Vector3.Cross(point[b] - point[a], point[c] - point[a]);
            float length = normal.Length();
            if (length < 1e-12f) continue;
            normal /= length;
            double d = -Vector3.Dot(normal, point[a]);
            AddPlane(quadric, a, normal, d, 1); AddPlane(quadric, b, normal, d, 1); AddPlane(quadric, c, normal, d, 1);
        }
        int started = live;

        // An edge that only one triangle uses, or whose two sides do not share its vertices, is a border or a seam.
        // A plane through it, square to its triangle, keeps both its places from drifting off it.
        var edges = new Dictionary<(int, int), (int Count, int V0, int V1, bool Seam)>();
        for (int t = 0; t < triangleCount; t++)
        {
            if (!alive[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                int v0 = corner[t * 3 + k], v1 = corner[t * 3 + (k + 1) % 3];
                int p0 = place[v0], p1 = place[v1];
                var key = p0 < p1 ? (p0, p1) : (p1, p0);
                if (p0 > p1) (v0, v1) = (v1, v0);
                if (edges.TryGetValue(key, out var e)) edges[key] = (e.Count + 1, e.V0, e.V1, e.Seam || e.V0 != v0 || e.V1 != v1);
                else edges.Add(key, (1, v0, v1, false));
            }
        }
        for (int t = 0; t < triangleCount; t++)
        {
            if (!alive[t]) continue;
            int a = place[corner[t * 3]], b = place[corner[t * 3 + 1]], c = place[corner[t * 3 + 2]];
            var normal = Vector3.Cross(point[b] - point[a], point[c] - point[a]);
            if (normal.LengthSquared() < 1e-24f) continue;
            normal = Vector3.Normalize(normal);
            for (int k = 0; k < 3; k++)
            {
                int p0 = place[corner[t * 3 + k]], p1 = place[corner[t * 3 + (k + 1) % 3]];
                var e = edges[p0 < p1 ? (p0, p1) : (p1, p0)];
                if (e.Count == 2 && !e.Seam) continue;
                var across = Vector3.Cross(point[p1] - point[p0], normal);
                if (across.LengthSquared() < 1e-24f) continue;
                across = Vector3.Normalize(across);
                double d = -Vector3.Dot(across, point[p0]);
                AddPlane(quadric, p0, across, d, 8); AddPlane(quadric, p1, across, d, 8);
            }
        }

        var version = new int[placeCount];
        var heap = new PriorityQueue<(int From, int To, int FromVersion, int ToVersion), double>();
        foreach (var (p0, p1) in edges.Keys) { Offer(heap, quadric, point, version, p0, p1); Offer(heap, quadric, point, version, p1, p0); }

        double limit = (double)maxError * maxError, worst = 0;
        int floor = (int)(started * minShare);
        var partner = new Dictionary<int, int>();
        var neighbours = new HashSet<int>();
        while (live > floor && heap.TryDequeue(out var move, out double cost))
        {
            if (cost > limit) break;
            int from = move.From, to = move.To;
            if (version[from] != move.FromVersion || version[to] != move.ToVersion) continue;

            // Each vertex at the moved place needs a partner at the other: the vertex it shares a vanishing triangle with.
            partner.Clear();
            foreach (int t in around[from])
            {
                if (!alive[t]) continue;
                int own = -1, other = -1;
                for (int k = 0; k < 3; k++)
                {
                    int v = corner[t * 3 + k];
                    if (place[v] == from) own = v; else if (place[v] == to) other = v;
                }
                if (other >= 0) partner[own] = other;
            }
            bool allowed = partner.Count > 0;
            foreach (int t in around[from])
            {
                if (!allowed) break;
                if (!alive[t]) continue;
                int own = -1, k0 = -1; bool vanishes = false;
                for (int k = 0; k < 3; k++)
                {
                    int v = corner[t * 3 + k];
                    if (place[v] == from) { own = v; k0 = k; } else if (place[v] == to) vanishes = true;
                }
                if (vanishes) continue;
                if (!partner.ContainsKey(own)) { allowed = false; break; }
                // The triangles that stay must not turn over or lose their area.
                var p1 = point[place[corner[t * 3 + (k0 + 1) % 3]]]; var p2 = point[place[corner[t * 3 + (k0 + 2) % 3]]];
                var before = Vector3.Cross(p1 - point[from], p2 - point[from]);
                var after = Vector3.Cross(p1 - point[to], p2 - point[to]);
                if (Vector3.Dot(before, after) < .2f * before.Length() * after.Length() || after.LengthSquared() < 1e-20f) allowed = false;
            }
            if (!allowed) continue;

            neighbours.Clear();
            foreach (int t in around[from])
            {
                if (!alive[t]) continue;
                bool vanishes = false;
                for (int k = 0; k < 3; k++) vanishes |= place[corner[t * 3 + k]] == to;
                if (vanishes) { alive[t] = false; live--; continue; }
                for (int k = 0; k < 3; k++)
                {
                    int v = corner[t * 3 + k];
                    if (place[v] == from) corner[t * 3 + k] = partner[v];
                }
                around[to].Add(t);
            }
            around[from].Clear();
            for (int k = 0; k < 10; k++) quadric[to * 10 + k] += quadric[from * 10 + k];
            version[from] = -1; version[to]++;
            worst = Math.Max(worst, cost);
            around[to].RemoveAll(t => !alive[t]);
            foreach (int t in around[to])
                for (int k = 0; k < 3; k++) { int p = place[corner[t * 3 + k]]; if (p != to) neighbours.Add(p); }
            foreach (int p in neighbours) { Offer(heap, quadric, point, version, to, p); Offer(heap, quadric, point, version, p, to); }
        }

        error = (float)Math.Sqrt(worst);
        ranges = new (int, int)[surfaces.Length];
        var result = new int[live * 3];
        int written = 0;
        for (int s = 0; s < surfaces.Length; s++)
        {
            int start = written;
            for (int t = surfaces[s].Start / 3; t < (surfaces[s].Start + surfaces[s].Count) / 3; t++)
            {
                if (!alive[t]) continue;
                result[written++] = corner[t * 3]; result[written++] = corner[t * 3 + 1]; result[written++] = corner[t * 3 + 2];
            }
            ranges[s] = (start, written - start);
        }
        return written == result.Length ? result : result.AsSpan(0, written).ToArray();
    }

    // A plane's squared distance as a symmetric 4×4 matrix, kept as its ten distinct terms.
    static void AddPlane(double[] q, int place, Vector3 n, double d, double weight)
    {
        int o = place * 10; double a = n.X, b = n.Y, c = n.Z;
        q[o] += weight * a * a; q[o + 1] += weight * a * b; q[o + 2] += weight * a * c; q[o + 3] += weight * a * d;
        q[o + 4] += weight * b * b; q[o + 5] += weight * b * c; q[o + 6] += weight * b * d;
        q[o + 7] += weight * c * c; q[o + 8] += weight * c * d; q[o + 9] += weight * d * d;
    }

    // The sum of squared distances from `to` to every plane gathered at both places: what moving `from` onto it costs.
    static void Offer(PriorityQueue<(int, int, int, int), double> heap, double[] q, List<Vector3> point, int[] version, int from, int to)
    {
        if (version[from] < 0 || version[to] < 0) return;
        double x = point[to].X, y = point[to].Y, z = point[to].Z, cost = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            int o = (pass == 0 ? from : to) * 10;
            cost += q[o] * x * x + 2 * q[o + 1] * x * y + 2 * q[o + 2] * x * z + 2 * q[o + 3] * x
                + q[o + 4] * y * y + 2 * q[o + 5] * y * z + 2 * q[o + 6] * y + q[o + 7] * z * z + 2 * q[o + 8] * z + q[o + 9];
        }
        heap.Enqueue((from, to, version[from], version[to]), Math.Max(cost, 0));
    }
}
