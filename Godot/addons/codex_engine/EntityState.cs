using Godot;
using System.Text.Json;

namespace Foundry.Authoring;

[Tool]
public abstract partial class EntityState : Node
{
    [Export] public bool AllocateTogether { get; set; } = true;
    public abstract string TypeKey { get; }
    public abstract JsonElement BakeDefaults();
}
