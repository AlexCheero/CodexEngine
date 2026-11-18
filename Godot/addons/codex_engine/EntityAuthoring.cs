using Godot;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntityAuthoring : Node3D
{
    [Export] public EntityPreset Preset { get; set; }
    [Export] public string SourceName { get; set; } = "";
}
