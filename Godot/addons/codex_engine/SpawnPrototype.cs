using Godot;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class SpawnPrototype : ComponentPrototype
{
    public override Foundry.Content.Parts Part => Foundry.Content.Parts.Spawn;
    public override void Bake(EntityAuthoring source, EntityDraft target) => target.Spawn = true;
}
