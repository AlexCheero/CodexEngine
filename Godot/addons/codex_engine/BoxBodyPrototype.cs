using System.IO;
using Godot;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class BoxBodyPrototype : ComponentPrototype
{
    [Export] public NodePath BoxMesh { get; set; } = "Solid";
    public override Foundry.Content.Parts Part => Foundry.Content.Parts.StaticBox;
    public override void Bake(EntityAuthoring source, EntityDraft target)
    {
        var mesh = source.GetNode<MeshInstance3D>(BoxMesh);
        if (mesh.GetParent() != source || mesh.Transform != Transform3D.Identity || mesh.Mesh is not BoxMesh box)
            throw new InvalidDataException($"{source.Name}: the box body requires a direct, untransformed BoxMesh child. Move its entity or edit BoxMesh.Size.");
        target.Box = [box.Size.X, box.Size.Y, box.Size.Z];
    }
}
