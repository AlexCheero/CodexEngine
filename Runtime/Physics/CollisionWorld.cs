using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace CodexEngine.Runtime;

/// <summary>A transform for one physics attachment or group of attachments. Gameplay ownership stays in ECS.</summary>
public sealed class CollisionNode : IDisposable
{
    private readonly List<CollisionNode> children = new();
    private readonly List<Collider> colliders = new();
    private CollisionNode parent;
    private Vector3 offset;
    private Quaternion orientation = Quaternion.identity;
    private bool disposed;
    public readonly string name;
    public int layer;
    public bool activeSelf { get; private set; } = true;
    public bool IsActiveInHierarchy => !disposed && activeSelf && (parent == null || parent.IsActiveInHierarchy);

    private readonly CollisionWorld world;
    public CollisionNode(string name) : this(CollisionWorld.Current, name) { }
    internal CollisionNode(CollisionWorld world, string name) { this.world = world; this.name = name; }
    internal void SetSimulatedPose(Vector3 at, Quaternion q)
    {
        offset = parent == null ? at : Quaternion.Inverse(parent.rotation) * (at - parent.position);
        orientation = parent == null ? q : Quaternion.Inverse(parent.rotation) * q;
    }
    internal void Attach(Collider collider) => colliders.Add(collider);
    public Vector3 localPosition { get => offset; set { offset = value; Synchronize(); } }
    public Vector3 position
    {
        get => parent == null ? offset : parent.position + parent.rotation * offset;
        set { offset = parent == null ? value : Quaternion.Inverse(parent.rotation) * (value - parent.position); Synchronize(); }
    }
    public Quaternion rotation
    {
        get => parent == null ? orientation : parent.rotation * orientation;
        set { orientation = parent == null ? value : Quaternion.Inverse(parent.rotation) * value; Synchronize(); }
    }
    public Vector3 forward => rotation * Vector3.forward;
    public void SetPositionAndRotation(Vector3 at, Quaternion q)
    {
        offset = parent == null ? at : Quaternion.Inverse(parent.rotation) * (at - parent.position);
        orientation = parent == null ? q : Quaternion.Inverse(parent.rotation) * q;
        Synchronize();
    }
    public void SetParent(CollisionNode target, bool worldPositionStays)
    {
        Vector3 at = position;
        Quaternion q = rotation;
        parent?.children.Remove(this);
        parent = target;
        parent?.children.Add(this);
        if (worldPositionStays) SetPositionAndRotation(at,q); else Synchronize();
    }
    public bool IsChildOf(CollisionNode other)
    {
        for (CollisionNode n = this; n != null; n = n.parent) if (n == other) return true;
        return false;
    }
    public void SetActive(bool active) { if (activeSelf == active) return; activeSelf = active; Synchronize(); }
    private void Synchronize()
    {
        foreach (Collider collider in colliders) collider.Synchronize();
        foreach (CollisionNode child in children) child.Synchronize();
    }
    public Collider AddBox(Vector3 size, bool trigger = false)
    {
        using Shape shape = CollisionWorld.Box(size * .5f);
        return Add(shape, trigger);
    }
    public Collider AddCapsule(float height, float radius, bool trigger = false)
    {
        using Shape shape = CollisionWorld.Capsule(height,radius);
        return Add(shape, trigger);
    }
    public Collider AddSphere(float radius, bool trigger = false)
    {
        using Shape shape = SphereShape.Create(radius);
        return Add(shape, trigger);
    }
    /// <summary>Add indexed static geometry directly from a content bake, in node-local space.</summary>
    public Collider AddMesh(ReadOnlySpan<float3> vertices, ReadOnlySpan<IndexedTriangle> triangles, bool trigger = false)
    {
        using var settings = MeshShapeSettings.Create(vertices, triangles, ReadOnlySpan<PhysicsMaterial>.Empty);
        using Shape shape = MeshShape.Create(settings);
        return Add(shape, trigger);
    }
    internal Collider Add(Shape shape, bool trigger)
    {
        var collider = new Collider(world, this, shape, trigger);
        colliders.Add(collider);
        return collider;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        while (children.Count > 0) children[^1].Dispose();
        foreach (Collider c in colliders) c.Dispose();
        parent?.children.Remove(this);
        parent = null;
    }
}

public sealed class Collider : IDisposable
{
    private readonly CollisionWorld world;
    internal readonly uint Id;
    public readonly CollisionNode transform;
    /// <summary>Authored surface identifier, or -1 on a runtime attachment.</summary>
    public int Surface { get; internal set; } = -1;
    internal Collider(CollisionWorld world, CollisionNode node, Shape shape, bool trigger)
    {
        this.world = world;
        transform = node;
        using var body = BodyCreationSettings.Create(shape, CollisionWorld.Native(node.position), CollisionWorld.Native(node.rotation),
            MotionType.Static, (ushort)(node.layer + (trigger ? 32 : 0)));
        body.SetIsSensor(trigger);
        Id = world.Bodies.CreateAndAddBody(body,Activation.DontActivate);
        world.Register(this);
        if (!node.IsActiveInHierarchy) world.Bodies.RemoveBody(Id);
    }
    internal Collider(CollisionWorld world, CollisionNode node, uint bodyId)
    {
        this.world = world;
        transform = node;
        Id = bodyId;
        Dynamic = true;
        node.Attach(this);
        world.Register(this);
    }
    internal bool Dynamic { get; }
    public void SetCapsule(float height,float radius)
    {
        using Shape shape = CollisionWorld.Capsule(height,radius);
        world.Bodies.SetShape(Id,shape,false,Activation.DontActivate);
    }
    internal void Synchronize()
    {
        bool added = world.Bodies.IsAdded(Id);
        if (!transform.IsActiveInHierarchy) { if (added) world.Bodies.RemoveBody(Id); return; }
        world.Bodies.SetPositionAndRotationWhenChanged(Id,CollisionWorld.Native(transform.position),CollisionWorld.Native(transform.rotation),Activation.DontActivate);
        if (!added) world.Bodies.AddBody(Id,Activation.DontActivate);
    }
    public void Dispose() => world.Remove(Id);
}

public partial class CollisionWorld : IDisposable
{
    // Queries select their world through the execution context, including child worker tasks.
    private static readonly AsyncLocal<CollisionWorld> active = new();
    public static CollisionWorld Current => active.Value ?? throw new InvalidOperationException("No collision world is active in this execution context.");
    private static readonly object factoryLock = new();
    private static int factoryUsers;
    private readonly BroadPhaseLayerInterfaceTable layers;
    private readonly ObjectLayerPairFilterTable pairs;
    private readonly ObjectVsBroadPhaseLayerFilterTable broad;
    private readonly PhysicsSystem system;
    internal readonly BodyInterface Bodies;
    private readonly NarrowPhaseQuery query;
    private readonly ConcurrentDictionary<uint,Collider> nodes = new();
    private readonly Dictionary<string,CollisionNode> groups = new(StringComparer.Ordinal);
    private readonly CollisionNode scene;
    private readonly CollisionWorld previous;
    private bool disposed;

    public CollisionWorld(params ReadOnlySpan<CollisionLayerPair> collisions)
    {
        lock (factoryLock)
        {
            if (factoryUsers++ == 0) { Jolt.RegisterDefaultAllocator(); Factory.Instance = Factory.Create(); Jolt.RegisterTypes(); }
        }
        layers = BroadPhaseLayerInterfaceTable.Create(64,1);
        pairs = ObjectLayerPairFilterTable.Create(64);
        for (ushort i = 0; i < 64; i++) layers.MapObjectToBroadPhaseLayer(i,0);
        foreach (var pair in collisions) pairs.EnableCollision(pair.First, pair.Second);
        broad = ObjectVsBroadPhaseLayerFilterTable.Create(layers,1,pairs,64);
        system = PhysicsSystem.Create();
        system.Init(32768,0,8192,8192,layers,broad,pairs);
        Bodies = system.GetBodyInterface();
        query = system.GetNarrowPhaseQuery();
        CreateQuerySettings();
        scene = new CollisionNode(this, "Level collision");
        previous = active.Value;
        active.Value = this;
    }
    internal static float3 Native(Vector3 v) => new(v.x,v.y,v.z);
    internal static floatQuat Native(Quaternion q) => new(q.x,q.y,q.z,q.w);
    internal static Vector3 Managed(float3 v) => new(v.X,v.Y,v.Z);
    internal static Shape Capsule(float height,float radius) => height <= 2*radius ? (Shape)SphereShape.Create(radius) : CapsuleShape.Create(height*.5f-radius,radius);
    internal static Shape Box(Vector3 half) => BoxShape.Create(Native(half),Math.Min(.01f,Math.Min(half.x,Math.Min(half.y,half.z))*.25f));
    internal void Register(Collider c) => nodes[c.Id] = c;
    internal void Remove(uint id)
    {
        if (!nodes.TryRemove(id,out var collider)) return;
        if (collider.Dynamic)
        {
            for (int i = constraints.Count - 1; i >= 0; i--)
                if (constraints[i].Connects(id)) constraints[i].Dispose();
            dynamicColliders.Remove(collider);
        }
        if (Bodies.IsAdded(id)) Bodies.RemoveBody(id);
        Bodies.DestroyBody(id);
    }
    public static Vector3 ReadPoint(JsonElement a) => new(a[0].GetSingle(),a[1].GetSingle(),a[2].GetSingle());
    public void LoadCollision(string collisionPath)
    {
        using var input = File.OpenRead(collisionPath);
        using var data = JsonDocument.Parse(input);
        foreach (var item in data.RootElement.GetProperty("colliders"u8).EnumerateArray())
        {
            var node = new CollisionNode(this, item.GetProperty("name"u8).GetString()) { layer = item.GetProperty("layer"u8).GetInt32() };
            var group = item.GetProperty("group"u8).GetString();
            var root = scene;
            if (!string.IsNullOrEmpty(group) && !groups.TryGetValue(group,out root))
            {
                root = groups[group] = new CollisionNode(this, group);
                root.SetParent(scene,false);
            }
            node.SetParent(root,false);
            var rot = item.GetProperty("rotation"u8);
            node.SetPositionAndRotation(ReadPoint(item.GetProperty("position"u8)),new Quaternion(rot[0].GetSingle(),rot[1].GetSingle(),rot[2].GetSingle(),rot[3].GetSingle()));
            bool trigger = item.GetProperty("trigger"u8).GetBoolean();
            Collider collider;
            switch (item.GetProperty("kind"u8).GetString())
            {
                case "box": collider=node.AddBox(ReadPoint(item.GetProperty("size"u8)),trigger); break;
                case "capsule": collider=node.AddCapsule(item.GetProperty("height"u8).GetSingle(),item.GetProperty("radius"u8).GetSingle(),trigger); break;
                case "sphere": collider=node.AddSphere(item.GetProperty("radius"u8).GetSingle(),trigger); break;
                case "mesh":
                    var vert = item.GetProperty("vertices"u8);
                    var index = item.GetProperty("indices"u8);
                    var vertices = new float3[vert.GetArrayLength()];
                    var triangles = new IndexedTriangle[index.GetArrayLength()/3];
                    int n = 0;
                    foreach (var p in vert.EnumerateArray()) vertices[n++] = Native(ReadPoint(p));
                    n = 0;
                    var iter = index.EnumerateArray();
                    while (iter.MoveNext())
                    {
                        uint a = iter.Current.GetUInt32(); iter.MoveNext(); uint b = iter.Current.GetUInt32(); iter.MoveNext();
                        triangles[n++] = IndexedTriangle.Create(a,b,iter.Current.GetUInt32(),0);
                    }
                    collider = node.AddMesh(vertices, triangles, trigger);
                    break;
                default: throw new InvalidDataException("Unknown collider kind in "+collisionPath);
            }
            collider.Surface=item.GetProperty("surface"u8).GetInt32();
        }
        OptimizeBroadPhase();
    }
    /// <summary>Finish bulk insertion before starting concurrent scene queries.</summary>
    public void OptimizeBroadPhase() => system.OptimizeBroadPhase();
    public CollisionNode GetGroup(string name) => groups[name];
    public bool TryGetGroup(string name, out CollisionNode group) => groups.TryGetValue(name, out group);
    public void SetGroupActive(string group,bool enabled) => groups[group].SetActive(enabled);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        while (constraints.Count > 0) constraints[^1].Dispose();
        scene.Dispose();
        foreach (uint id in nodes.Keys) Remove(id);
        DisposeQuerySettings();
        ReleaseDynamics();
        system.Dispose(); broad.Dispose(); pairs.Dispose(); layers.Dispose();
        if (active.Value == this) active.Value = previous;
        lock (factoryLock)
        {
            if (--factoryUsers == 0) { Jolt.UnregisterTypes(); Factory.DestroyInstance(); }
        }
    }
}

