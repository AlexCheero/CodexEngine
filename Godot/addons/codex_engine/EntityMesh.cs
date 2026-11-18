using Foundry.Content;
using Godot;

namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntityMesh : Node3D
{
    private Resource geometry;
    private Node3D visual;

    [Export]
    public Resource Geometry
    {
        get => geometry;
        set
        {
            geometry = value;
            if (IsNodeReady()) Rebuild();
        }
    }

    [Export] public bool AllocateTogether { get; set; } = true;

    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsString() != nameof(Geometry)) return;
        property["hint"] = (int)PropertyHint.ResourceType;
        property["hint_string"] = "Mesh,PackedScene";
    }

    public override void _Ready() => Rebuild();

    private void Rebuild()
    {
        if (visual != null)
        {
            RemoveChild(visual);
            visual.QueueFree();
        }
        visual = geometry switch
        {
            Mesh mesh => new MeshInstance3D { Mesh = mesh },
            PackedScene scene => InstantiateModel(scene),
            _ => null
        };
        // Ownerless display geometry stays out of the saved scene. The baker reads this same tree.
        if (visual != null) AddChild(visual);
        UpdateConfigurationWarnings();
    }

    private static Node3D InstantiateModel(PackedScene scene)
    {
        var root = scene.Instantiate();
        // Entity scenes are placed as entities. Using one as its own mesh would recurse in the editor.
        if (root is Node3D model && model is not EntityAuthoring) return model;
        root.Free();
        return null;
    }

    public override string[] _GetConfigurationWarnings() =>
        visual != null ? [] : ["Assign a mesh or a model-only 3D scene to Geometry."];

    public Geometry Bake(ModelTextures textures)
    {
        // Catalog models can be baked directly from a PackedScene outside the running scene tree.
        if (!IsNodeReady()) Rebuild();
        if (visual == null) throw new InvalidDataException($"{Name}: assign a mesh or a 3D scene to Geometry.");
        return MeshBake.Read(this, Transform, textures);
    }
}
