using CodexECS;
namespace CodexEngine.Runtime.Entities;

public struct PureEntity : IComponent { }

public interface IComponentDefault
{
    int TypeId { get; }
    void Apply(EcsWorld world, int id);
}
public sealed class ComponentDefault<T>(T value = default) : IComponentDefault where T : struct, IComponent
{
    public int TypeId => ComponentMeta<T>.Id;
    public void Apply(EcsWorld world, int id)
    {
        if (ComponentMeta<T>.IsTag) return;
        ref var destination=ref world.Get<T>(id);
        ComponentMeta<T>.Copy(in value, ref destination);
    }
}

// A preset contains component defaults and an allocation group. No scene or presentation object
// participates in spawning. The caller owns any native resources associated with its components.
public sealed class EntityPreset
{
    readonly BitMask mask, group;
    readonly IComponentDefault[] defaults;
    public EntityPreset(IComponentDefault[] defaults, int[] groupIds)
    {
        this.defaults=defaults;
        var all=new BitMask().SetTypeId<PureEntity>();
        var grouped=new BitMask().SetTypeId<PureEntity>();
        foreach(var prototype in defaults) all.Set(prototype.TypeId);
        foreach(int type in groupIds) grouped.Set(type);
        mask=all; group=grouped;
    }
    public int CreatePureEntity(EcsWorld world)
    {
        int id=world.CreateWithComponents(in mask,in group);
        foreach(var prototype in defaults) prototype.Apply(world,id);
        return id;
    }
}
