using System.Numerics;
using System.Runtime.InteropServices;

namespace Foundry.Content;

public sealed record BakedSceneLight(Geometry Mesh, uint[] SurfaceLight, Vector3 Origin, float Step,
    int Width, int Height, int Depth, uint[] ProbeLight)
{
    // Probe records store the six cosine-weighted lobes in +X,-X,+Y,-Y,+Z,-Z order.
    const int Signature = 0x31494742, HashBytes = 32;
    public static bool Matches(BinaryReader reader,byte[] inputHash)
    {
        var stream=reader.BaseStream;
        return stream.Length>=sizeof(int)+sizeof(long)+HashBytes && reader.ReadInt32()==Signature &&
            reader.ReadInt64()==stream.Length && reader.ReadBytes(HashBytes).AsSpan().SequenceEqual(inputHash);
    }

    public void Write(string path, byte[] inputHash)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(Signature); w.Write(0L); w.Write(inputHash);
                Mesh.Write(w);
                w.BaseStream.Write(MemoryMarshal.AsBytes(SurfaceLight.AsSpan()));
                w.Write(Origin.X); w.Write(Origin.Y); w.Write(Origin.Z); w.Write(Step);
                w.Write(Width); w.Write(Height); w.Write(Depth);
                w.BaseStream.Write(MemoryMarshal.AsBytes(ProbeLight.AsSpan()));
                long length=w.BaseStream.Position;
                w.BaseStream.Position=sizeof(int);
                w.Write(length);
            }
            File.Move(temp,path,true);
        }
        finally { File.Delete(temp); }
    }

    public static BakedSceneLight Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadInt32()!=Signature || r.ReadInt64()!=r.BaseStream.Length) throw new InvalidDataException("Rebake indirect lighting for " + path);
        r.BaseStream.Seek(HashBytes,SeekOrigin.Current);
        var mesh = Geometry.Read(r);
        var surface = ReadColors(mesh.Colors.Length);
        Vector3 origin = new(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
        float step = r.ReadSingle();
        int w=r.ReadInt32(), h=r.ReadInt32(), d=r.ReadInt32();
        if (!(step>0) || w<2 || h<2 || d<2 || r.BaseStream.Length-r.BaseStream.Position != checked(24L*w*h*d))
            throw new InvalidDataException("Invalid probe grid. Rebake " + path);
        return new(mesh,surface,origin,step,w,h,d,ReadColors(checked(w*h*d*6)));

        uint[] ReadColors(int count)
        {
            var values = new uint[count];
            r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
            return values;
        }
    }
}
