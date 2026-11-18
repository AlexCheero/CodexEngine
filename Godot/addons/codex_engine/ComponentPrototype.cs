using Godot;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public abstract partial class ComponentPrototype : Resource
{
    public abstract Foundry.Content.Parts Part { get; } public abstract void Bake(EntityAuthoring source, EntityDraft target);
}
