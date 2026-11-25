using System;
using System.Collections.Generic;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace CodexEngine.Runtime;

/// <summary>
/// A collision shape built ahead of time, on any thread: building a mesh shape's tree is the slow part of adding level
/// geometry, so a streamed cell builds its shapes on a worker and only adds the bodies on the game thread with
/// <see cref="CollisionNode.Add(PreparedShape,bool)"/>. Dispose it once added (the body keeps its own reference)
/// or when the cell is dropped before activation.
/// </summary>
public sealed class PreparedShape : IDisposable
{
    internal Shape Shape;
    bool disposed;
    PreparedShape(Shape shape) => Shape = shape;

    /// <summary>A static triangle mesh in node-local space. Degenerate triangles are left out, which Jolt otherwise
    /// turns into NaN contacts.</summary>
    public static PreparedShape Mesh(ReadOnlySpan<float3> vertices, ReadOnlySpan<IndexedTriangle> triangles)
    {
        using var settings = MeshShapeSettings.Create(vertices, triangles, ReadOnlySpan<PhysicsMaterial>.Empty);
        return new(MeshShape.Create(settings));
    }

    /// <summary>A static mesh from flat position (xyz) and index arrays, skipping triangles with less than
    /// <paramref name="minimumArea"/> square metres.</summary>
    public static PreparedShape Mesh(ReadOnlySpan<float> positions, ReadOnlySpan<int> indices, float minimumArea = 1e-4f)
    {
        var vertices = new float3[positions.Length / 3];
        for (int i = 0; i < vertices.Length; i++) vertices[i] = new(positions[3 * i], positions[3 * i + 1], positions[3 * i + 2]);
        var faces = new List<IndexedTriangle>(indices.Length / 3);
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a == b || b == c || a == c) continue;
            var cross = float3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
            if (float3.Dot(cross, cross) < 4 * minimumArea * minimumArea) continue;
            faces.Add(IndexedTriangle.Create((uint)a, (uint)b, (uint)c, 0));
        }
        if (faces.Count == 0) return null;
        return Mesh(vertices, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(faces));
    }

    /// <summary>One static compound of primitive parts.</summary>
    public static PreparedShape Compound(ReadOnlySpan<CompoundPart> parts) => new(CollisionWorld.CreateCompoundShape(parts));

    public void Dispose() { if (disposed) return; disposed = true; Shape.Dispose(); }
    internal Shape Live => disposed ? throw new ObjectDisposedException(nameof(PreparedShape)) : Shape;
}

public static class PreparedShapeExtensions
{
    /// <summary>Adds a static body with a shape prepared off the game thread.</summary>
    public static Collider Add(this CollisionNode node, PreparedShape shape, bool trigger = false) =>
        node.Add(shape.Live, trigger);
}
