using System.IO;
using Godot;
using Foundry.Content;
namespace Foundry.Authoring;

[Tool, GlobalClass]
public partial class EntityPreset : Resource
{
    [Export] public Godot.Collections.Array<ComponentPrototype> Components { get; set; } = new();
    [Export(PropertyHint.Flags,"Pose,Model,StaticBox,Spawn")]
    public int GroupComponents { get; set; } = (int)Parts.Pose;

    public PresetSpec Describe()
    {
        Parts mask = Parts.Pose;
        foreach (var c in Components)
        {
            if ((mask & c.Part) != 0) throw new InvalidDataException($"{ResourcePath}: duplicate component {c.Part}");
            mask |= c.Part;
        }
        var group=(Parts)GroupComponents;
        if ((group & mask)!=group) throw new InvalidDataException($"{ResourcePath}: group must be a subset of components.");
        return new(ResourcePath,mask,group);
    }
}
