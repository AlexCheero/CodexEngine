using System.Runtime.InteropServices;
using System.Text.Json;
namespace Foundry.Content;

public sealed record DrawSurface(int Start, int Count, string Texture, float Cutoff);
public sealed record Geometry(float[] Positions, float[] Normals, float[] Tangents, float[] Uvs, uint[] Colors, int[] Indices, DrawSurface[] Surfaces)
{
    const int Signature = 0x324D5242;
    public void Write(string path)
    {
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{WriteData(temp);File.Move(temp,path,true);}finally{File.Delete(temp);}
    }
    void WriteData(string path)
    {
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(Signature);
        w.Write(Colors.Length);
        w.Write(Indices.Length);
        w.Write(Surfaces.Length);
        foreach (var s in Surfaces) { w.Write(s.Start); w.Write(s.Count); w.Write(s.Texture); w.Write(s.Cutoff); }
        WriteArray(Positions); WriteArray(Normals); WriteArray(Tangents); WriteArray(Uvs); WriteArray(Colors); WriteArray(Indices);
        void WriteArray<T>(T[] values) where T : unmanaged => stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }
    public static Geometry Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var r = new BinaryReader(stream);
        if (r.ReadInt32() != Signature) throw new InvalidDataException("Rebake this model in Godot: " + path);
        int count = r.ReadInt32(), indices = r.ReadInt32(), surfaces = r.ReadInt32();
        var parts = new DrawSurface[surfaces];
        for(int i=0;i<parts.Length;i++) parts[i]=new(r.ReadInt32(),r.ReadInt32(),r.ReadString(),r.ReadSingle());
        return new(ReadArray<float>(count*3), ReadArray<float>(count*3), ReadArray<float>(count*4),
            ReadArray<float>(count*2), ReadArray<uint>(count), ReadArray<int>(indices), parts);
        T[] ReadArray<T>(int n) where T : unmanaged { var a = new T[n]; stream.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan())); return a; }
    }
}
public sealed record PlacedEntity(string Name, string Preset, float[] Matrix, string Model, float[] Box, bool Spawn,string SourceName=null);
public sealed record SceneFile(int Version, PlacedEntity[] Entities, PresetSpec[] Presets)
{
    public const int CurrentVersion = 3;
    public static SceneFile Load(string path)
    {
        using var stream=File.OpenRead(path);
        var scene=JsonSerializer.Deserialize<SceneFile>(stream);
        if (scene?.Version != CurrentVersion) throw new InvalidDataException("Rebake the scene in Godot.");
        return scene;
    }
}
public sealed record CatalogEntry(string kind,int id,string name,string path);

