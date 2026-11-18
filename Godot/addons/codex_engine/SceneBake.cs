using System.Collections.Generic;
using System.IO;
using Foundry.Content;
using Godot;

namespace Foundry.Authoring;

public static class SceneBake
{
    public static void Collect(Node root, string sceneName, string modelDirectory,
        List<PlacedEntity> entities, Dictionary<string, PresetSpec> presets)
    {
        if (root is EntityAuthoring entity)
        {
            var preset = entity.Preset;
            var spec = preset.Describe();
            presets.TryAdd(spec.Name, spec);

            var draft = new EntityDraft();
            foreach (var component in preset.Components)
                component.Bake(entity, draft);

            string model = null;
            if (draft.Model != null)
            {
                model = $"{sceneName}-{entities.Count}.brmodel";
                draft.Model.Write(Path.Combine(modelDirectory, model));
            }
            entities.Add(new PlacedEntity(entity.Name, preset.ResourcePath, MeshBake.Matrix(entity.GlobalTransform),
                model, draft.Box, draft.Spawn, entity.SourceName));
        }
        foreach (var child in root.GetChildren())
            Collect(child, sceneName, modelDirectory, entities, presets);
    }
}
