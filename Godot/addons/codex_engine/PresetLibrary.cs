using System;
using System.IO;
using Godot;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace Foundry.Authoring;

[Tool,GlobalClass]
public partial class PresetLibrary : Resource
{
    [Export] public Godot.Collections.Array<SimulationPreset> Presets {get;set;}=new();
    public void Bake(string file)
    {
        var library=new JsonObject();
        foreach(var preset in Presets){
            var components=JsonNode.Parse(Json.Stringify(preset.Components));
            foreach(string member in preset.AllocationGroup)
                if(components[member] is null)throw new InvalidDataException($"{preset.Role} groups an absent component: {member}");
            library.Add(preset.Role,new JsonObject{["components"]=components,["group"]=JsonSerializer.SerializeToNode(preset.AllocationGroup)});
        }
        var bytes=JsonSerializer.SerializeToUtf8Bytes(library,new JsonSerializerOptions{WriteIndented=true});
        if(File.Exists(file)&&File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes))return;
        string temp=file+".tmp";File.WriteAllBytes(temp,bytes);File.Move(temp,file,true);
    }
}

