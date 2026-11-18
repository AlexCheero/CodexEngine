using Godot;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class ModelPrototype : ComponentPrototype
{
    [Export] public PackedScene ModelAsset { get; set; }
    public override Foundry.Content.Parts Part => Foundry.Content.Parts.Model;
    public override void Bake(EntityAuthoring source, EntityDraft target)
    {
        if (ModelAsset == null) { target.Model = MeshBake.Read(source); return; }
        var root = ModelAsset.Instantiate<Node3D>();
        try { target.Model = MeshBake.Read(root); } finally { root.Free(); }
    }
}
