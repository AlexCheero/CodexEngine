using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Foundry.Content;
namespace Foundry.Authoring;

public sealed class EntityDraft
{
    public float[] Box;
    public Geometry Model;
    public bool Spawn;
}

public static class MeshBake
{
    public const string ScriptPath = "res://addons/codex_engine/MeshBake.cs";

    public static void BakeTexture(string source,string destination)
    {
        if(File.Exists(destination) && File.GetLastWriteTimeUtc(destination)>File.GetLastWriteTimeUtc(source))return;
        using var img=Image.LoadFromFile(source);
        if(img.DetectAlpha()==Image.AlphaMode.None)img.Convert(Image.Format.Rgb8);
        if(img.GenerateMipmaps()!=Error.Ok || img.Compress(Image.CompressMode.S3Tc,Image.CompressSource.Generic)!=Error.Ok)
            throw new InvalidDataException("Cannot encode texture "+source);
        var format=img.GetFormat();
        int block=format switch{Image.Format.Dxt1=>1,Image.Format.Dxt5=>5,Image.Format.Dxt3=>3,_=>throw new InvalidDataException("Unexpected block format "+format)};
        byte[] bytes=img.GetData();int levels=img.GetMipmapCount()+1;
        var temp=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            using(var writer=new BinaryWriter(File.Create(temp))){
                writer.Write(img.GetWidth());writer.Write(img.GetHeight());writer.Write(block);writer.Write(levels);
                for(int i=0;i<levels;i++){
                    int first=checked((int)img.GetMipmapOffset(i)),end=i+1==levels?bytes.Length:checked((int)img.GetMipmapOffset(i+1));
                    writer.Write(end-first);writer.Write(bytes,first,end-first);
                }
            }
            File.Move(temp,destination,true);
        }finally{File.Delete(temp);}
    }
    public static float[] Matrix(Transform3D t) =>
        [t.Basis.X.X,t.Basis.X.Y,t.Basis.X.Z,0, t.Basis.Y.X,t.Basis.Y.Y,t.Basis.Y.Z,0,
         t.Basis.Z.X,t.Basis.Z.Y,t.Basis.Z.Z,0, t.Origin.X,t.Origin.Y,t.Origin.Z,1];

    public static Geometry Read(Node3D root)
    {
        var positions = new List<float>();
        var normals = new List<float>();
        var colors = new List<uint>(); var tangents=new List<float>(); var uvs=new List<float>();
        var indices = new List<int>();
        var surfaces = new List<DrawSurface>();
        Visit(root, Transform3D.Identity);
        if (indices.Count == 0) throw new InvalidDataException($"Empty model {root.Name}");
        return new(positions.ToArray(), normals.ToArray(), tangents.ToArray(), uvs.ToArray(), colors.ToArray(), indices.ToArray(),surfaces.ToArray());

        void Visit(Node n, Transform3D t)
        {
            if (n is MeshInstance3D mesh)
            {
                var normalBasis = t.Basis.Inverse().Transposed();
                for (int s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
                {
                    if (mesh.Mesh is ArrayMesh imported && imported.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles)
                        throw new InvalidDataException($"{n.Name}: triangle surfaces required.");
                    var arrays = mesh.Mesh.SurfaceGetArrays(s);
                    var verts = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    var norms = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                    var st=arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array(); var uv=arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                    var faces = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                    var material = mesh.GetActiveMaterial(s) as BaseMaterial3D;
                    var tint = material?.AlbedoColor ?? Colors.LightGray;
                    int first = colors.Count;
                    int start = indices.Count;
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var p = t * verts[i];
                        var u = (normalBasis * norms[i]).Normalized();
                        positions.AddRange([p.X,p.Y,p.Z]);
                        normals.AddRange([u.X,u.Y,u.Z]);
                        var tangent=(t.Basis * new Vector3(st[i*4],st[i*4+1],st[i*4+2])).Normalized(); tangents.AddRange([tangent.X,tangent.Y,tangent.Z,st[i*4+3]*MathF.Sign(t.Basis.Determinant())]); uvs.AddRange([uv[i].X,uv[i].Y]); colors.Add(tint.ToAbgr32());
                    }
                    if (faces.Length == 0)
                        for (int i = 0; i < verts.Length; i++) indices.Add(first + i);
                    else foreach (int index in faces) indices.Add(first + index);
                    string path=material?.AlbedoTexture?.ResourcePath ?? "";
                    surfaces.Add(new(start,indices.Count-start,Path.GetFileName(path),material?.Transparency == BaseMaterial3D.TransparencyEnum.AlphaScissor ? material.AlphaScissorThreshold : 0));
                }
            }
            foreach (var child in n.GetChildren())
                if (child is Node3D spatial && spatial.Visible && child is not EntityAuthoring)
                    Visit(spatial, t * spatial.Transform);
        }
    }
}
