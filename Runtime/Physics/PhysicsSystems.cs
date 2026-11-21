using CodexECS;
using N = System.Numerics;

namespace CodexEngine.Runtime;

// The collision world as pipeline systems. A game asks for a step with a request component, lists
// PhysicsStepSystem and BodySampleSystem in its pipeline, and reads BodySample afterwards.
// Both need the world's CollisionWorld service and a job scheduler.

/// <summary>Asks PhysicsStepSystem to advance the collision world once. The system removes it.</summary>
public struct PhysicsStepRequest : IComponent
{
    public float Dt;
    public int Substeps;
}

/// <summary>Stands for the bodies of the collision world in a system's declared access. No entity owns it.</summary>
public struct PhysicsBodies : IComponent { }

/// <summary>
/// A dynamic body an entity owns, read back after every solver step: where it is, how it moves and whether
/// something holds it up.
/// </summary>
public struct BodySample : IComponent
{
    public RigidBody Body;
    /// <summary>Layers that count as support. Zero skips the probe.</summary>
    public int SupportMask;
    public float SupportTolerance, SupportMinimumDot;
    public N.Vector3 Position, Velocity;
    public bool Supported;
}

/// <summary>
/// Advances the solver for every PhysicsStepRequest, then starts reading body poses back into their transforms
/// as a job. Systems that touch bodies afterwards are sync points or declare PhysicsBodies, so they wait for it.
/// </summary>
[System(ESystemCategory.FixedUpdate)]
public sealed class PhysicsStepSystem : EcsSystem
{
    private readonly CollisionWorld physics;
    private readonly EcsFilter requests;

    public PhysicsStepSystem(EcsWorld world)
    {
        physics = world.Service<CollisionWorld>();
        requests = world.Filter().With<PhysicsStepRequest>().Build();
        Declare().Write<PhysicsBodies>().Write<PhysicsStepRequest>();
    }

    public override void Tick(EcsWorld world)
    {
        // The solver reads and moves every body: nothing else may be touching them.
        CompleteDependency();
        bool stepped = false;
        foreach (int id in requests)
        {
            ref readonly var request = ref world.Get<PhysicsStepRequest>(id);
            physics.Advance(request.Dt, Math.Max(1, request.Substeps));
            stepped = true;
        }
        if (!stepped) return;
        var commands = world.CreateCommandBuffer();
        foreach (int id in requests) commands.Remove<PhysicsStepRequest>(id);
        if (physics.Jobs == null) physics.SyncPoses();
        else Dependency = physics.SchedulePoseSync();
    }
}

/// <summary>
/// After a solver step, reads every BodySample's body in parallel. It leaves the job running: the next system
/// that reads BodySample, or the next sync point, waits for it.
/// </summary>
[System(ESystemCategory.FixedUpdate)]
public sealed class BodySampleSystem : EcsSystem
{
    private readonly CollisionWorld physics;
    private readonly EcsFilter bodies;
    private readonly ComponentsPool<BodySample> samples;
    private readonly Action<EcsFilter.Batch> sample;
    private long sampled = -1;

    public BodySampleSystem(EcsWorld world)
    {
        physics = world.Service<CollisionWorld>();
        bodies = world.Filter().With<BodySample>().Build();
        samples = world.GetComponentsPool<BodySample>();
        sample = Sample;
        Declare().Read<PhysicsBodies>().Write<BodySample>();
    }

    public override void Tick(EcsWorld world)
    {
        if (physics.Steps == sampled) return;
        sampled = physics.Steps;
        if (world.Jobs == null) Sample(bodies.All);
        else Dependency = bodies.ScheduleParallel(sample, 2, Dependency);
    }

    /// <summary>Reads every body now and returns when done, for a caller that steps the collision world itself.</summary>
    public void SampleNow(EcsWorld world)
    {
        sampled = physics.Steps;
        if (world.Jobs == null) Sample(bodies.All);
        else bodies.ScheduleParallel(sample, 2).Complete();
    }

    private void Sample(EcsFilter.Batch batch)
    {
        foreach (int id in batch)
        {
            ref var s = ref samples[id];
            var body = s.Body;
            s.Position = body.Collider.transform.position.ToNumerics();
            s.Velocity = body.LinearVelocity.ToNumerics();
            if (s.SupportMask != 0) s.Supported = body.HasSupport(Vector3.up, s.SupportTolerance, s.SupportMinimumDot, s.SupportMask);
        }
    }
}
