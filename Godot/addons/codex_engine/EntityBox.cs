using Godot;

namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntityBox : MeshInstance3D
{
    [Export] public bool AllocateTogether { get; set; } = true;

    public float[] Bake()
    {
        if (Mesh is not BoxMesh box || Transform != Transform3D.Identity)
            throw new InvalidDataException($"{GetPath()}: edit the BoxMesh size and move its entity.");
        return [box.Size.X, box.Size.Y, box.Size.Z];
    }
}
