using Brutal.JoltApi;
using Brutal.Numerics;

namespace CodexEngine.Runtime;

/// Upright Jolt CharacterVirtual with its native contact manifold, stair solver and queryable inner body.
public sealed class CharacterMotor : IDisposable
{
    private readonly CollisionWorld world;
    private readonly CharacterVirtual character;
    private readonly TempAllocatorImpl memory;
    private readonly BroadPhaseLayerFilter broad;
    private readonly ObjectLayerFilter layers;
    private readonly BodyFilter bodies;
    private readonly ShapeFilter shapes;
    private readonly float halfHeight,step;
    private bool disposed;
    public Collider Collider {get;}
    public bool Supported=>character.GetGroundState()==CharacterBase.GroundState.OnGround;
    public Vector3 Feet=>CollisionWorld.Managed(character.GetPosition())-Vector3.up*halfHeight;
    public Vector3 WallNormal {get;private set;}
    public bool Ceiling {get;private set;}

    internal CharacterMotor(CollisionWorld world,PhysicsSystem system,ObjectLayerFilter layers,Vector3 feet,float height,
        float radius,float slopeDegrees,float step,float padding,ushort layer)
    {
        this.world=world;this.layers=layers;this.step=step;halfHeight=height*.5f;
        broad=BroadPhaseLayerFilter.Create();bodies=BodyFilter.Create();shapes=ShapeFilter.Create();
        memory=TempAllocatorImpl.Create(2*1024*1024);
        using var capsule=CollisionWorld.Capsule(height,radius);
        using var settings=CharacterVirtualSettings.Create();
        settings.SetShape(capsule);settings.SetInnerBodyShape(capsule);settings.SetInnerBodyLayer(layer);
        settings.SetCharacterPadding(padding);settings.SetMaxSlopeAngle(slopeDegrees*(MathF.PI/180));
        settings.SetSupportingVolume(Plane.Create(new float3(0,1,0),halfHeight-radius));
        character=CharacterVirtual.Create(settings,CollisionWorld.Native(feet+Vector3.up*halfHeight),CollisionWorld.Native(Quaternion.identity),system);
        var node=new CollisionNode(world,"Character"){layer=layer};
        Collider=new Collider(world,node,character.GetInnerBodyID());
        Teleport(feet);
    }
    public void Teleport(Vector3 feet)
    {
        var center=feet+Vector3.up*halfHeight;
        character.SetPosition(CollisionWorld.Native(center));character.SetLinearVelocity(float3.Zero);
        character.RefreshContacts(broad,layers,bodies,shapes,memory);Collider.transform.SetSimulatedPose(center,Quaternion.identity);
        ReadContacts();
    }
    public Vector3 Move(Vector3 velocity,float dt)
    {
        var stair=new CharacterVirtual.ExtendedUpdateSettings {
            WalkStairsStepUp=new float3(0,step,0),WalkStairsMinStepForward=.02f,WalkStairsStepForwardTest=.15f,
            WalkStairsCosAngleForwardContact=.258819f,StickToFloorStepDown=velocity.y<=0?new float3(0,-step,0):float3.Zero
        };
        character.SetLinearVelocity(CollisionWorld.Native(velocity));
        // Gameplay supplies vertical velocity. Jolt uses gravity here for impulses to supporting bodies.
        character.ExtendedUpdate(dt,new float3(0,-9.81f,0),in stair,broad,layers,bodies,shapes,memory);
        Collider.transform.SetSimulatedPose(CollisionWorld.Managed(character.GetPosition()),Quaternion.identity);
        ReadContacts();return Feet;
    }
    private void ReadContacts()
    {
        WallNormal=default;Ceiling=false;
        foreach(ref readonly var contact in character.GetActiveContacts())
        {
            if(!contact.HadCollision || contact.WasDiscarded || contact.IsSensorB)continue;
            var normal=CollisionWorld.Managed(contact.SurfaceNormal);
            if(normal.y<-.5f)Ceiling=true;
            else if(MathF.Abs(normal.y)<.5f)WallNormal=normal;
        }
    }
    public void Dispose()
    {
        if(disposed)return;
        disposed=true;world.ForgetCharacter(Collider);
        character.Dispose();memory.Dispose();shapes.Dispose();bodies.Dispose();broad.Dispose();
    }
}

public partial class CollisionWorld
{
    public CharacterMotor CreateCharacterMotor(Vector3 feet,float height,float radius,float slopeDegrees,float stepHeight,
        float padding,ushort layer,int contactMask)=>new(this,system,Filter(contactMask,QueryTriggerInteraction.Ignore),
            feet,height,radius,slopeDegrees,stepHeight,padding,layer);
    internal void ForgetCharacter(Collider collider)=>nodes.TryRemove(collider.Id,out _);
}
