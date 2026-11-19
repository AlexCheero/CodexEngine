using Godot;

namespace Foundry.Authoring;

[Tool]
public partial class EntityWorkbenchPalette : RefCounted
{
    public const string Folder = "res://Entities";

    public static IEnumerable<string> ScenePaths() =>
        Directory.EnumerateFiles(ProjectSettings.GlobalizePath(Folder), "*.tscn", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(ProjectSettings.LocalizePath);

    public string[] Presets() => ScenePaths().ToArray();
    public virtual string[] Components() => ["Mesh", "BoxBody", "SpawnPoint"];
    public virtual int CoreComponentCount() => 3;

    public string ComponentKey(Node node) => node switch
    {
        EntityMesh => "Mesh", EntityBox => "BoxBody", EntitySpawn => "SpawnPoint",
        EntityState state => state.TypeKey, _ => ""
    };

    public virtual string ComponentIcon(string key) => key switch
    {
        "Mesh" => "MeshInstance3D",
        "BoxBody" => "CollisionShape3D",
        "SpawnPoint" => "Marker3D",
        _ => "Object"
    };

    public Node NewComponent(string key)
    {
        Node node = CreateComponent(key);
        node.Name = key.Capitalize();
        return node;
    }

    protected virtual Node CreateComponent(string key) => key switch
    {
        "Mesh" => new EntityMesh { Geometry = new BoxMesh() },
        "BoxBody" => new EntityBox { Mesh = new BoxMesh(), Visible = false },
        "SpawnPoint" => new EntitySpawn(),
        _ => throw new ArgumentException($"Unknown entity component '{key}'.", nameof(key))
    };

    public string NameError(string name)
    {
        if (name.Length == 0) return "Enter a name.";
        if (name.Length > 100 || name != name.ValidateNodeName() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || System.Text.RegularExpressions.Regex.IsMatch(name,
                @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "Choose a valid scene name.";
        return ScenePaths().Any(path => string.Equals(Path.GetFileNameWithoutExtension(path), name,
            StringComparison.OrdinalIgnoreCase)) ? "A preset with this name already exists." : "";
    }

    public string CreatePreset(string name)
    {
        string error = NameError(name);
        if (error.Length > 0) return error;
        var entity = new EntityAuthoring { Name = name };
        // Publish only a complete scene and never replace an existing preset.
        string temp = $"res://.godot/new-entity-{Guid.NewGuid():N}.tscn";
        string staging = ProjectSettings.GlobalizePath(temp);
        try
        {
            using var packed = new PackedScene();
            var result = packed.Pack(entity);
            if (result == Error.Ok) result = ResourceSaver.Save(packed, temp);
            if (result != Error.Ok) return $"Could not save the scene ({result}).";
            File.Move(staging, ProjectSettings.GlobalizePath(PresetPath(name)));
            return "";
        }
        catch (IOException ex) { return ex.Message; }
        finally { entity.Free(); File.Delete(staging); }
    }

    public string PresetPath(string name) => $"{Folder}/{name}.tscn";
}
