using Brutal.JoltApi;
using Brutal.Numerics;

namespace CodexEngine.Runtime;

/// <summary>
/// A collision shape for sweeps that is not a body, so it never takes part in the simulation.
/// Use it to query a live world, as navigation does when it rebakes around changed scenery.
/// </summary>
public sealed class ShapeProbe : IDisposable
{
    private readonly CollisionWorld world;
    private readonly Shape shape;
    private bool disposed;

    internal ShapeProbe(CollisionWorld world,CollisionShape shape){this.world=world;this.shape=shape.Create();}

    /// <summary>Sweeps the shape, unrotated with its centre at <paramref name="at"/>, and reports the closest hit.</summary>
    public bool Cast(Vector3 at,Vector3 direction,float distance,int layerMask,out RaycastHit hit)=>
        world.Sweep(new CollisionWorld.Probe(shape,float4x4.CreateTranslation(CollisionWorld.Native(at))),direction,distance,layerMask,
            QueryTriggerInteraction.Ignore,out hit);

    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        shape.Dispose();
    }
}

public partial class CollisionWorld
{
    public ShapeProbe CreateProbe(CollisionShape shape)=>new(this,shape);
}
