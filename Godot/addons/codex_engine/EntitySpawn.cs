using Godot;

namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntitySpawn : Node
{
    [Export] public bool AllocateTogether { get; set; } = true;
}
