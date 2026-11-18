using System.Security.Cryptography;
using Godot;

namespace Foundry.Authoring;

/// <summary>Resolve surface albedos for this bake, including textures embedded in imported model scenes.</summary>
public sealed class ModelTextures(string sourceLibrary, string output)
{
    private readonly Dictionary<ulong, string> keys = new();

    public string Key(Texture2D texture)
    {
        if (texture == null) return "";
        string path = texture.ResourcePath;
        // The game's named texture library is also read by the animated character renderer.
        if (path.GetBaseDir() == sourceLibrary && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return path.GetFile();

        ulong id = texture.GetInstanceId();
        if (keys.TryGetValue(id, out string key)) return key;
        using var image = texture.GetImage();
        if (image == null || image.IsEmpty()) throw new InvalidDataException($"No bakeable image in texture '{path}'.");
        if (image.IsCompressed() && image.Decompress() != Error.Ok)
            throw new InvalidDataException($"Could not decompress texture '{path}'.");
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgba8);
        string hash = Convert.ToHexString(SHA256.HashData(image.GetData()));
        key = $"entity-{image.GetWidth()}x{image.GetHeight()}-{hash}";
        string file = Path.Combine(output, key + ".btex");
        if (!File.Exists(file)) MeshBake.BakeTexture(image, file);
        keys.Add(id, key);
        return key;
    }
}
