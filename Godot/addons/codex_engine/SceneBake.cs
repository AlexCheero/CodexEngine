using Foundry.Content;
using Godot;

namespace Foundry.Authoring;

public static class SceneBake
{
    public static void Collect(Node root, string sceneName, string modelDirectory,
        List<PlacedEntity> entities, Dictionary<string, PresetSpec> presets, ModelTextures textures)
    {
        if (root is EntityAuthoring entity)
        {
            var parts = Parts.Pose;
            var group = entity.AllocateTransformTogether ? Parts.Pose : 0;
            string model = null;
            float[] box = null;
            foreach (var child in entity.GetChildren())
            {
                switch (child)
                {
                    case EntityMesh mesh:
                        Include(Parts.Model, mesh.AllocateTogether);
                        model = $"{sceneName}-{entities.Count}.brmodel";
                        mesh.Bake(textures).Write(Path.Combine(modelDirectory, model));
                        break;
                    case EntityBox body:
                        Include(Parts.StaticBox, body.AllocateTogether);
                        box = body.Bake();
                        break;
                    case EntitySpawn spawn:
                        Include(Parts.Spawn, spawn.AllocateTogether);
                        break;
                }
            }

            var gameplay = entity.DescribeGameplay();
            // Plain scenery shares masks. Instances with gameplay overrides retain their own defaults.
            string key = gameplay.Components.Count == 0 ? $"{(int)parts}/{(int)group}" : entity.GetPath().ToString();
            presets.TryAdd(key, new(key, parts, group, gameplay));
            entities.Add(new(entity.Name, key, MeshBake.Matrix(entity.GlobalTransform), model, box,
                parts.HasFlag(Parts.Spawn), entity.SourceName));

            void Include(Parts part, bool allocateTogether)
            {
                if ((parts & part) != 0) throw new InvalidDataException($"{entity.GetPath()}: duplicate {part} component.");
                parts |= part;
                if (allocateTogether) group |= part;
            }
        }
        foreach (var child in root.GetChildren())
            if (child is not (EntityMesh or EntityBox or EntityState or EntitySpawn))
                Collect(child, sceneName, modelDirectory, entities, presets, textures);
    }
}
