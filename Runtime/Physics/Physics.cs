using System;
using System.Collections.Concurrent;
using System.Threading;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace CodexEngine.Runtime;

public enum QueryTriggerInteraction { Ignore, Collide }
public struct RaycastHit
{
    public Collider collider;
    public Vector3 point, normal;
    public float distance;
}

public partial class CollisionWorld
{
    public bool Raycast(Vector3 origin,Vector3 direction,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction triggers)=>
        Ray(origin,direction,distance,mask,triggers,out hit);
    public int RaycastNonAlloc(Vector3 origin,Vector3 direction,RaycastHit[] hits,float distance,int mask,QueryTriggerInteraction triggers)=>
        Rays(origin,direction,distance,mask,triggers,hits);
    public int OverlapSphereNonAlloc(Vector3 origin,float radius,Collider[] hits,int mask,QueryTriggerInteraction triggers)=>
        Overlaps(SphereProbe(origin,radius),mask,triggers,hits);
    public int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float radius,Collider[] hits,int mask,QueryTriggerInteraction triggers)=>
        Overlaps(CapsuleProbe(a,b,radius),mask,triggers,hits);
    public bool SphereCast(Vector3 at,float radius,Vector3 direction,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction triggers)=>
        Sweep(SphereProbe(at,radius),direction,distance,mask,triggers,out hit);
    public bool CapsuleCast(Vector3 a,Vector3 b,float radius,Vector3 direction,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction triggers)=>
        Sweep(CapsuleProbe(a,b,radius),direction,distance,mask,triggers,out hit);
    public bool CheckCapsule(Vector3 a,Vector3 b,float radius,int mask,QueryTriggerInteraction triggers)=>
        Overlap(CapsuleProbe(a,b,radius),mask,triggers);
    private sealed class LayerFilter(int mask, bool triggers) : ManagedObjectLayerFilter
    {
        protected override bool ShouldCollide(ushort layer) =>
            (triggers || layer < 32) && (mask & (1 << (layer & 31))) != 0;
    }
    private sealed class Scratch : IDisposable
    {
        internal readonly OmitBody Exclusion=new();
        public RayCastResult[] Rays = new RayCastResult[32];
        public ShapeCastResult[] Sweeps = new ShapeCastResult[16];
        public CollideShapeResult[] Overlaps = new CollideShapeResult[8];
        // World queries use a small number of radii and stance/vehicle sizes. Parkour probes
        // can vary continuously, so a bounded per-worker cache is used for their native shapes.
        private readonly (int Kind,Vector3 Size,Shape Shape)[] shapes = new (int,Vector3,Shape)[32];
        private int next;
        public Shape Get(int kind,Vector3 size)
        {
            for (int i=0;i<shapes.Length;i++)
                if (shapes[i].Kind == kind && shapes[i].Size.Equals(size)) return shapes[i].Shape;
            ref var slot = ref shapes[next++ & (shapes.Length-1)];
            slot.Shape.Dispose();
            Shape shape = kind == 1 ? (Shape)SphereShape.Create(size.x) : kind == 2 ? Capsule(size.y,size.x) : Box(size);
            slot = (kind,size,shape);
            return shape;
        }
        public void Dispose() { Exclusion.Dispose();foreach (var slot in shapes) slot.Shape.Dispose(); }
    }
    private sealed class OmitBody : ManagedBodyFilter
    {
        internal uint Id;
        protected override bool ShouldCollide(uint bodyId)=>bodyId!=Id;
        protected override bool ShouldCollideLocked(Body body)=>true;
    }
    public bool RaycastExcept(Collider excluded,Vector3 at,Vector3 direction,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction triggers)
    {
        var delta=direction.normalized*distance;
        var omit=scratch.Value.Exclusion;omit.Id=excluded.Id;
        if(query.CastRayClosest(new RRayCast{Origin=Native(at),Direction=Native(delta)},raySettings,out var result,
            objectLayerFilter:Filter(mask,triggers),bodyFilter:omit))
        {hit=RayHit(result,at,delta,distance);return true;}
        hit=default;return false;
    }
    private readonly ThreadLocal<Scratch> scratch = new(() => new Scratch(),true);
    private readonly ConcurrentDictionary<(int,QueryTriggerInteraction),LayerFilter> filters = new();
    private RayCastSettings raySettings;
    private CollideShapeSettings overlapSettings;
    private ShapeCastSettings sweepSettings;

    private void CreateQuerySettings()
    {
        raySettings = RayCastSettings.Create();
        raySettings.SetTreatConvexAsSolid(false);
        overlapSettings = CollideShapeSettings.Create();
        sweepSettings = ShapeCastSettings.Create();
    }
    private void DisposeQuerySettings()
    {
        foreach (var s in scratch.Values) s.Dispose();
        scratch.Dispose();
        foreach (var f in filters.Values) f.Dispose();
        raySettings.Dispose(); overlapSettings.Dispose(); sweepSettings.Dispose();
    }
    private LayerFilter Filter(int mask,QueryTriggerInteraction triggers)
    {
        if (filters.TryGetValue((mask,triggers),out var filter)) return filter;
        lock(filters) {
            if (!filters.TryGetValue((mask,triggers),out filter))
                filters[(mask,triggers)] = filter = new LayerFilter(mask,triggers == QueryTriggerInteraction.Collide);
            return filter;
        }
    }
    internal bool Line(Vector3 at,Vector3 delta,int mask,QueryTriggerInteraction triggers) =>
        query.CastRayClosest(new RRayCast { Origin=Native(at), Direction=Native(delta) },raySettings,out _,objectLayerFilter:Filter(mask,triggers));

    private RaycastHit RayHit(in RayCastResult result,Vector3 origin,Vector3 displacement,float distance)
    {
        Vector3 point = origin+displacement*result.Fraction;
        using var body = BodyLockRead.Create(system.GetBodyLockInterface(),result.BodyID);
        return new RaycastHit { point = point, distance = distance*result.Fraction, collider = nodes[result.BodyID],
            normal = Managed(body.GetBody().GetWorldSpaceSurfaceNormal(result.SubShapeID2,Native(point))) };
    }
    internal bool Ray(Vector3 at,Vector3 direction,float distance,int mask,QueryTriggerInteraction triggers,out RaycastHit hit)
    {
        var delta = direction.normalized*distance;
        if (query.CastRayClosest(new RRayCast { Origin=Native(at), Direction=Native(delta) },raySettings,out var result,objectLayerFilter:Filter(mask,triggers)))
        {
            hit = RayHit(result,at,delta,distance);
            return true;
        }
        hit = default;
        return false;
    }
    internal int Rays(Vector3 at,Vector3 direction,float distance,int mask,QueryTriggerInteraction triggers,RaycastHit[] hits)
    {
        var delta = direction.normalized*distance;
        ref var results = ref scratch.Value.Rays;
        if (results.Length < hits.Length) Array.Resize(ref results,hits.Length);
        int count = (int)query.CastRay(new RRayCast {Origin=Native(at),Direction=Native(delta)}, raySettings,results.AsSpan(0,hits.Length),out _,objectLayerFilter:Filter(mask,triggers));
        for (int i=0;i<count;i++) hits[i]=RayHit(results[i],at,delta,distance);
        return count;
    }
    internal readonly struct Probe
    {
        public readonly Shape Shape;
        public readonly float4x4 Pose;
        public Probe(Shape shape,Vector3 at,Quaternion q) {
            Shape = shape;
            Pose = float4x4.CreateFromQuaternion(Native(q))*float4x4.CreateTranslation(Native(at));
        }
        public RShapeCast Cast(Vector3 delta) => new() {Shape=Shape, Scale=float3.One, CenterOfMassStart=Pose,
            Direction=Native(delta), ShapeWorldBounds=Shape.GetWorldSpaceBounds(Pose,float3.One)};
    }
    internal Probe CapsuleProbe(Vector3 a,Vector3 b,float radius)
    {
        Vector3 axis = b-a;
        float length = axis.magnitude;
        if (length < .00001f) return SphereProbe(a,radius);
        Quaternion rot = Quaternion.identity;
        if (MathF.Abs(axis.x)+MathF.Abs(axis.z) > .00001f)
        {
            float angle = MathF.Acos(Math.Clamp(axis.y/length,-1f,1f))*Mathf.Rad2Deg;
            rot = Quaternion.AngleAxis(angle,Vector3.Cross(Vector3.up,axis).normalized);
        }
        // Quantizing the cylinder length removes world-origin rounding differences between identical stances.
        float height = MathF.Round(length*10000f)*.0001f+2*radius;
        return new(scratch.Value.Get(2,new Vector3(radius,height,0)),(a+b)*.5f,rot);
    }
    internal Probe BoxProbe(Vector3 at,Vector3 half,Quaternion rot) => new(scratch.Value.Get(3,half),at,rot);
    internal Probe SphereProbe(Vector3 at,float radius) => new(scratch.Value.Get(1,new Vector3(radius,0,0)),at,Quaternion.identity);
    private RaycastHit SweepHit(in ShapeCastResult result,float distance) => new()
    {
        point=Managed(result.ContactPointOn2), distance=distance*result.Fraction,
        normal=-Managed(result.PenetrationAxis).normalized, collider=nodes[result.BodyID2]
    };
    internal bool Sweep(in Probe probe,Vector3 direction,float distance,int mask,QueryTriggerInteraction triggers,out RaycastHit hit)
    {
        if (query.CastShapeClosest(probe.Cast(direction.normalized*distance),sweepSettings,float3.Zero,out var result,objectLayerFilter:Filter(mask,triggers)))
        {
            hit=SweepHit(result,distance);
            return true;
        }
        hit=default;
        return false;
    }
    internal int Sweeps(in Probe probe,Vector3 direction,float distance,int mask,QueryTriggerInteraction triggers,RaycastHit[] hits)
    {
        ref var results=ref scratch.Value.Sweeps;
        if(results.Length<hits.Length)Array.Resize(ref results,hits.Length);
        int count=(int)query.CastShape(probe.Cast(direction.normalized*distance),sweepSettings,float3.Zero,results.AsSpan(0,hits.Length),out _,objectLayerFilter:Filter(mask,triggers));
        for(int i=0;i<count;i++) hits[i]=SweepHit(results[i],distance);
        return count;
    }
    internal bool Overlap(in Probe probe,int mask,QueryTriggerInteraction triggers) =>
        query.CollideShapeClosest(probe.Shape,float3.One,probe.Pose,overlapSettings,float3.Zero,out _,objectLayerFilter:Filter(mask,triggers));
    internal bool Penetration(in Probe probe,int mask,out Vector3 separation)
    {
        if(query.CollideShapeClosest(probe.Shape,float3.One,probe.Pose,overlapSettings,float3.Zero,out var result,
            objectLayerFilter:Filter(mask,QueryTriggerInteraction.Ignore)) && result.PenetrationDepth>.0001f)
        {
            separation=-Managed(result.PenetrationAxis).normalized*(result.PenetrationDepth+.001f);
            return true;
        }
        separation=default;
        return false;
    }
    internal int Overlaps(in Probe probe,int mask,QueryTriggerInteraction triggers,Collider[] hits)
    {
        ref var results=ref scratch.Value.Overlaps;
        if(results.Length<hits.Length)Array.Resize(ref results,hits.Length);
        int count=(int)query.CollideShape(probe.Shape,float3.One,probe.Pose,overlapSettings,float3.Zero,results.AsSpan(0,hits.Length),out _,objectLayerFilter:Filter(mask,triggers));
        for(int i=0;i<count;i++) hits[i]=nodes[results[i].BodyID2];
        return count;
    }
}

public static class Physics
{
    private static CollisionWorld World => CollisionWorld.Current;
    // Jolt immediately updates the broad phase on SetPositionAndRotationWhenChanged.
    public static void SyncTransforms() { }
    public static bool Linecast(Vector3 a,Vector3 b,int mask,QueryTriggerInteraction t) => World.Line(a,b-a,mask,t);
    public static bool Raycast(Vector3 a,Vector3 d,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction t) => World.Ray(a,d,distance,mask,t,out hit);
    public static int RaycastNonAlloc(Vector3 a,Vector3 d,RaycastHit[] hits,float distance,int mask,QueryTriggerInteraction t) => World.Rays(a,d,distance,mask,t,hits);
    public static bool CapsuleCast(Vector3 a,Vector3 b,float r,Vector3 d,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction t) =>
        World.Sweep(World.CapsuleProbe(a,b,r),d,distance,mask,t,out hit);
    public static bool SphereCast(Vector3 a,float r,Vector3 d,out RaycastHit hit,float distance,int mask,QueryTriggerInteraction t) =>
        World.Sweep(World.SphereProbe(a,r),d,distance,mask,t,out hit);
    public static int SphereCastNonAlloc(Vector3 a,float r,Vector3 d,RaycastHit[] hits,float distance,int mask,QueryTriggerInteraction t) =>
        World.Sweeps(World.SphereProbe(a,r),d,distance,mask,t,hits);
    public static int BoxCastNonAlloc(Vector3 a,Vector3 half,Vector3 d,RaycastHit[] hits,Quaternion q,float distance,int mask,QueryTriggerInteraction t) =>
        World.Sweeps(World.BoxProbe(a,half,q),d,distance,mask,t,hits);
    public static bool ComputeCapsulePenetration(Vector3 a, Vector3 b, float radius, int mask, out Vector3 separation) =>
        World.Penetration(World.CapsuleProbe(a, b, radius), mask, out separation);
    public static bool CheckSphere(Vector3 a,float r,int mask,QueryTriggerInteraction t) => World.Overlap(World.SphereProbe(a,r),mask,t);
    public static bool CheckCapsule(Vector3 a,Vector3 b,float r,int mask,QueryTriggerInteraction t) => World.Overlap(World.CapsuleProbe(a,b,r),mask,t);
    public static int OverlapCapsuleNonAlloc(Vector3 a,Vector3 b,float r,Collider[] hits,int mask,QueryTriggerInteraction t) =>
        World.Overlaps(World.CapsuleProbe(a,b,r),mask,t,hits);
}

