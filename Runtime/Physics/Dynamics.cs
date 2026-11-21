using System;
using System.Collections.Generic;
using Brutal.JoltApi;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace CodexEngine.Runtime;

public readonly record struct CollisionLayerPair(ushort First, ushort Second);
/// <summary>Jolt capacities: bodies, simultaneous broad-phase pairs and contact constraints, and the solver's per-step scratch memory in bytes.</summary>
public readonly record struct CollisionLimits(int Bodies, int BodyPairs, int ContactConstraints, int SolverMemory)
{
    public static readonly CollisionLimits Default = new(32768, 8192, 8192, 8 * 1024 * 1024);
}
public readonly record struct DynamicBodyMotion(bool LockRotation,float GravityFactor,float Friction,float LinearDamping,float AngularDamping=.05f);
public readonly record struct CompoundPart(CollisionShape Shape,Vector3 Position,Quaternion Rotation);

/// Identifies members of one articulated object and the pairs of members that may collide.
public sealed class BodyCollisionGroup : IDisposable
{
    private readonly GroupFilterTable filter;
    private readonly uint id;

    internal BodyCollisionGroup(uint id,int bodyCount)
    {
        this.id=id;
        filter=GroupFilterTable.Create((uint)bodyCount);
    }
    public void DisablePair(int first,int second)=>filter.DisableCollision((uint)first,(uint)second);
    internal void Apply(BodyCreationSettings settings,int member)
    {
        using var group=CollisionGroup.Create(filter,id,(uint)member);
        settings.SetCollisionGroup(group);
    }
    public void Dispose()=>filter.Dispose();
}

/// <summary>Box dimensions are half extents. Capsule height includes both caps.</summary>
public readonly struct CollisionShape
{
    private enum Kind { Box, Sphere, Capsule, Cylinder }
    private readonly Kind kind;
    private readonly Vector3 dimensions;
    private CollisionShape(Kind kind, Vector3 dimensions) { this.kind = kind; this.dimensions = dimensions; }
    public static CollisionShape Box(Vector3 halfExtents) => new(Kind.Box, halfExtents);
    public static CollisionShape Sphere(float radius) => new(Kind.Sphere, new(radius, 0, 0));
    public static CollisionShape Capsule(float height, float radius) => new(Kind.Capsule, new(radius, height, 0));
    /// Upright cylinder with full height and rounded edges, suitable for stackable bodies.
    public static CollisionShape Cylinder(float height,float radius) => new(Kind.Cylinder,new(radius,height,0));
    internal Shape Create() => kind switch
    {
        Kind.Box => CollisionWorld.Box(dimensions),
        Kind.Sphere => SphereShape.Create(dimensions.x),
        Kind.Cylinder => CylinderShape.Create(dimensions.y*.5f,dimensions.x,Math.Min(.05f,Math.Min(dimensions.y*.25f,dimensions.x*.25f))),
        _ => CollisionWorld.Capsule(dimensions.y, dimensions.x)
    };
}

public readonly struct RigidBody : IDisposable
{
    internal readonly CollisionWorld World;
    public readonly Collider Collider;
    internal uint Id => Collider.Id;

    internal RigidBody(CollisionWorld world, Collider collider) { World = world; Collider = collider; }
    public bool Cast(Vector3 startOffset,Vector3 direction,float distance,int layerMask,out RaycastHit hit)=>
        World.CastBody(this,startOffset,direction,distance,layerMask,out hit);
    public bool HasSupport(Vector3 up,float tolerance,float minimumDot,int layerMask)=>
        World.HasSupport(this,up,tolerance,minimumDot,layerMask);
    public int OverlapNonAlloc(int layerMask,Collider[] results)=>World.OverlapBodyNonAlloc(this,layerMask,results);
    public void SetKinematic(bool value)=>World.Bodies.SetMotionType(Id,value?MotionType.Kinematic:MotionType.Dynamic,Activation.Activate);
    public void AddImpulse(Vector3 impulse,Vector3 point)=>World.Bodies.AddImpulse(Id,CollisionWorld.Native(impulse),CollisionWorld.Native(point));
    public void MoveKinematic(Vector3 position,Quaternion rotation,float dt)=>
        World.Bodies.MoveKinematic(Id,CollisionWorld.Native(position),CollisionWorld.Native(rotation),dt);
    public void AddImpulse(Vector3 impulse) => World.Bodies.AddImpulse(Id, CollisionWorld.Native(impulse));
    public void SetAngularVelocity(Vector3 velocity) => World.Bodies.SetAngularVelocity(Id, CollisionWorld.Native(velocity));
    public void SetLinearVelocity(Vector3 velocity) => World.Bodies.SetLinearVelocity(Id, CollisionWorld.Native(velocity));
    public Vector3 LinearVelocity => CollisionWorld.Managed(World.Bodies.GetLinearVelocity(Id));
    public bool IsSleeping => !World.Bodies.IsActive(Id);
    public void SetFriction(float value) => World.Bodies.SetFriction(Id,value);
    public void SetGravityFactor(float factor) => World.Bodies.SetGravityFactor(Id,factor);
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
    private int nextCollisionGroup;

    public BodyCollisionGroup CreateBodyCollisionGroup(int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size,1);
        return new((uint)Interlocked.Increment(ref nextCollisionGroup),size);
    }

    public RigidBody CreateDynamicBody(CollisionShape shape, Vector3 position, Quaternion rotation, float mass, ushort layer, Vector3 velocity,
        DynamicBodyMotion? motion=null,BodyCollisionGroup group=null,int member=0)
    {
        using var nativeShape = shape.Create();
        return CreateDynamicBody(nativeShape,position,rotation,mass,layer,velocity,motion,group,member);
    }

    public RigidBody CreateDynamicCompound(ReadOnlySpan<CompoundPart> parts, Vector3 position, Quaternion rotation, float mass,
        ushort layer, Vector3 velocity, DynamicBodyMotion motion)
    {
        using var shape=CreateCompoundShape(parts);
        return CreateDynamicBody(shape,position,rotation,mass,layer,velocity,motion);
    }

    internal static Shape CreateCompoundShape(ReadOnlySpan<CompoundPart> parts)
    {
        using var settings=StaticCompoundShapeSettings.Create();
        foreach(ref readonly var part in parts)
        {
            using var child=part.Shape.Create();
            settings.AddShape(Native(part.Position),Native(part.Rotation),child,0);
        }
        return settings.CreateShape();
    }

    private RigidBody CreateDynamicBody(Shape nativeShape,Vector3 position,Quaternion rotation,float mass,ushort layer,
        Vector3 velocity,DynamicBodyMotion? motion,BodyCollisionGroup group=null,int member=0)
    {
        using var settings = BodyCreationSettings.Create(nativeShape, Native(position), Native(rotation), MotionType.Dynamic, layer);
        group?.Apply(settings,member);
        if(motion is {} m)
        {
            if(m.LockRotation)settings.SetAllowedDOFs(AllowedDOFs.TranslationX|AllowedDOFs.TranslationY|AllowedDOFs.TranslationZ);
            settings.SetGravityFactor(m.GravityFactor);
            settings.SetFriction(m.Friction);
            settings.SetLinearDamping(m.LinearDamping);
            settings.SetAngularDamping(m.AngularDamping);
        }
        settings.SetOverrideMassProperties(OverrideMassProperties.CalculateInertia);
        using var properties = MassProperties.Create();
        properties.SetMass(mass);
        settings.SetMassPropertiesOverride(properties);
        var body=TrackMovingBody(settings,position,rotation,layer);
        body.SetLinearVelocity(velocity);
        return body;
    }

    public RigidBody CreateKinematicBody(CollisionShape shape,Vector3 position,Quaternion rotation,ushort layer,
        BodyCollisionGroup group=null,int member=0)
    {
        using var native=shape.Create();
        using var settings=BodyCreationSettings.Create(native,Native(position),Native(rotation),MotionType.Kinematic,layer);
        group?.Apply(settings,member);
        return TrackMovingBody(settings,position,rotation,layer);
    }

    private RigidBody TrackMovingBody(BodyCreationSettings settings,Vector3 position,Quaternion rotation,ushort layer)
    {
        uint id=Bodies.CreateAndAddBody(settings,Activation.Activate);
        var node=new CollisionNode(this,"Rigid body"){layer=layer};
        node.SetSimulatedPose(position,rotation);
        var collider=new Collider(this,node,id){DynamicSlot=dynamicColliders.Count};
        dynamicColliders.Add(collider);
        return new RigidBody(this,collider);
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

    public void Step(float dt,int subdivisions=1)
    {
        if (!solverInitialized)
        {
            temporaryMemory = TempAllocatorImpl.Create((uint)limits.SolverMemory);
            solverJobs = JobSystemThreadPool.Create(2048, 8, Math.Clamp(Environment.ProcessorCount / 2, 1, 4));
            solverInitialized = true;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(subdivisions,1);
        float interval=dt/subdivisions;
        for(int i=0;i<subdivisions;i++)system.Update(interval,1,temporaryMemory,solverJobs);
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
