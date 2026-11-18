using Godot;
namespace Foundry.Authoring;

[Tool,GlobalClass]
public partial class SimulationPreset : Resource
{
    [Export] public string Role {get;set;}
    [Export] public Godot.Collections.Dictionary Components {get;set;}=new();
    [Export] public string[] AllocationGroup {get;set;}=[];
}

