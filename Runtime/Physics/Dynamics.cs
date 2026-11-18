using System;
using System.Collections.Generic;
using Brutal.JoltApi;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace CodexEngine.Runtime;

public readonly record struct CollisionLayerPair(ushort First, ushort Second);

/// <summary>Box dimensions are half extents. Capsule height includes both caps.</summary>
public readonly struct CollisionShape
{
    private enum Kind { Box, Sphere, Capsule }
    private readonly Kind kind;
    private readonly Vector3 dimensions;
    private CollisionShape(Kind kind, Vector3 dimensions) { this.kind = kind; this.dimensions = dimensions; }
    public static CollisionShape Box(Vector3 halfExtents) => new(Kind.Box, halfExtents);
    public static CollisionShape Sphere(float radius) => new(Kind.Sphere, new(radius, 0, 0));
    public static CollisionShape Capsule(float height, float radius) => new(Kind.Capsule, new(radius, height, 0));
    internal Shape Create() => kind switch
    {
        Kind.Box => CollisionWorld.Box(dimensions),
        Kind.Sphere => SphereShape.Create(dimensions.x),
        _ => CollisionWorld.Capsule(dimensions.y, dimensions.x)
    };
}

public readonly struct RigidBody : IDisposable
{
    internal readonly CollisionWorld World;
    public readonly Collider Collider;
    internal uint Id => Collider.Id;

    internal RigidBody(CollisionWorld world, Collider collider) { World = world; Collider = collider; }
    public void AddImpulse(Vector3 impulse) => World.Bodies.AddImpulse(Id, CollisionWorld.Native(impulse));
    public void SetAngularVelocity(Vector3 velocity) => World.Bodies.SetAngularVelocity(Id, CollisionWorld.Native(velocity));
    public void SetLinearVelocity(Vector3 velocity) => World.Bodies.SetLinearVelocity(Id, CollisionWorld.Native(velocity));
    public Matrix4x4 Pose => Matrix4x4.CreateFromQuaternion(Collider.transform.rotation.ToNumerics())
                          * Matrix4x4.CreateTranslation(Collider.transform.position.ToNumerics());
    public void Translate(Vector3 offset) => Collider.transform.position += offset;
    public void Dispose() => Collider.Dispose();
}

public sealed class PhysicsConstraint : IDisposable
{
    private readonly CollisionWorld world;
    internal readonly TwoBodyConstraint Joint;
    private readonly uint first, second;
    private bool disposed;

    internal PhysicsConstraint(CollisionWorld world, TwoBodyConstraint joint, uint first, uint second)
    {
        this.world = world;
        Joint = joint;
        this.first = first;
        this.second = second;
    }
    internal bool Connects(uint id) => id == first || id == second;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        world.RemoveConstraint(this);
    }
}

public partial class CollisionWorld
{
    private readonly List<Collider> dynamicColliders = new();
    private readonly List<PhysicsConstraint> constraints = new();
    private TempAllocatorImpl temporaryMemory;
    private JobSystemThreadPool solverJobs;
    private bool solverInitialized;

    public RigidBody CreateDynamicBody(CollisionShape shape, Vector3 position, Quaternion rotation, float mass, ushort layer, Vector3 velocity)
    {
        using var nativeShape = shape.Create();
        using var settings = BodyCreationSettings.Create(nativeShape, Native(position), Native(rotation), MotionType.Dynamic, layer);
        settings.SetOverrideMassProperties(OverrideMassProperties.CalculateInertia);
        using var properties = MassProperties.Create();
        properties.SetMass(mass);
        settings.SetMassPropertiesOverride(properties);
        uint id = Bodies.CreateAndAddBody(settings, Activation.Activate);
        var node = new CollisionNode(this, "Rigid body") { layer = layer };
        node.SetSimulatedPose(position, rotation);
        var collider = new Collider(this, node, id);
        dynamicColliders.Add(collider);
        var body = new RigidBody(this, collider);
        body.SetLinearVelocity(velocity);
        return body;
    }

    /// <summary>Shared world-space anchor and normalized orthogonal joint axes. Limits are in degrees.</summary>
    public PhysicsConstraint CreateSwingTwistJoint(RigidBody first, RigidBody second, Vector3 position, Vector3 twistAxis,
        Vector3 planeAxis, float twistMin, float twistMax, float normalHalfCone, float planeHalfCone)
    {
        if (first.World != this || second.World != this) throw new ArgumentException("Both bodies must belong to this collision world.");
        using var settings = SwingTwistConstraintSettings.Create();
        settings.SetSpace(ConstraintSpace.WorldSpace);
        var anchor = Native(position);
        settings.SetPosition1(anchor);
        settings.SetPosition2(anchor);
        var twist = Native(twistAxis);
        settings.SetTwistAxis1(twist);
        settings.SetTwistAxis2(twist);
        var plane = Native(planeAxis);
        settings.SetPlaneAxis1(plane);
        settings.SetPlaneAxis2(plane);
        settings.SetTwistMinAngle(twistMin * Mathf.Deg2Rad);
        settings.SetTwistMaxAngle(twistMax * Mathf.Deg2Rad);
        settings.SetNormalHalfConeAngle(normalHalfCone * Mathf.Deg2Rad);
        settings.SetPlaneHalfConeAngle(planeHalfCone * Mathf.Deg2Rad);
        var bodies = system.GetBodyLockInterfaceNoLock();
        var joint = settings.Create(bodies.TryGetBody(first.Id), bodies.TryGetBody(second.Id));
        var result = new PhysicsConstraint(this, joint, first.Id, second.Id);
        system.AddConstraint(joint);
        constraints.Add(result);
        return result;
    }

    internal void RemoveConstraint(PhysicsConstraint constraint)
    {
        constraints.Remove(constraint);
        system.RemoveConstraint(constraint.Joint);
        constraint.Joint.Dispose();
    }

    public void Step(float dt)
    {
        if (!solverInitialized)
        {
            temporaryMemory = TempAllocatorImpl.Create(8 * 1024 * 1024);
            solverJobs = JobSystemThreadPool.Create(2048, 8, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            solverInitialized = true;
        }
        system.Update(dt, 1, temporaryMemory, solverJobs);
        foreach (var collider in dynamicColliders)
        {
            Bodies.GetPositionAndRotation(collider.Id, out var position, out var rotation);
            collider.transform.SetSimulatedPose(Managed(position), new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W));
        }
    }
    private void ReleaseDynamics()
    {
        if (!solverInitialized) return;
        solverJobs.Dispose();
        temporaryMemory.Dispose();
    }
}
