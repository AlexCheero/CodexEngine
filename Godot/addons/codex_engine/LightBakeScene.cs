using System.Numerics;
using Foundry.Content;
using Image = Godot.Image;

namespace Foundry.Authoring;

// World-space triangles of the rendered meshes. Visibility rays and diffuse bounce rays
// both see UV-wrapped cutouts and the same material tint as the player.
internal sealed class LightBakeScene
{
    internal readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector4 Tangent, Vector2 Uv, uint Tint);
    internal readonly record struct Face(int A,int B,int C,int Material);
    internal sealed record Material(DrawSurface Surface, Albedo Image);
    internal sealed class Albedo
    {
        readonly byte[] pixels;
        readonly int width,height;
        internal Albedo(string path)
        {
            if (path == null) { pixels=[255,255,255,255]; width=height=1; return; }
            using var r=new BinaryReader(File.OpenRead(path));
            int w=r.ReadInt32(), h=r.ReadInt32(), format=r.ReadInt32();
            r.ReadInt32(); // mip count
            using var image=Image.CreateFromData(w,h,false,format switch {
                1=>Image.Format.Dxt1, 3=>Image.Format.Dxt3, 5=>Image.Format.Dxt5,
                _=>throw new InvalidDataException("Unknown baked albedo format: "+path)
            },r.ReadBytes(r.ReadInt32()));
            if (image.Decompress()!=Godot.Error.Ok) throw new InvalidDataException("Cannot decode baked albedo: "+path);
            image.Convert(Image.Format.Rgba8);
            width=w; height=h; pixels=image.GetData();
        }
        internal Vector4 Sample(Vector2 uv)
        {
            int x=Math.Min(width-1,(int)((uv.X-MathF.Floor(uv.X))*width));
            int y=Math.Min(height-1,(int)((uv.Y-MathF.Floor(uv.Y))*height));
            int i=4*(y*width+x);
            return new Vector4(pixels[i],pixels[i+1],pixels[i+2],pixels[i+3])/255;
        }
    }
    readonly record struct Triangle(Vector3 A,Vector3 E1,Vector3 E2,Vector3 Min,Vector3 Max,Face Face);
    readonly record struct Node(Vector3 Min,Vector3 Max,int First,int Count,int Right);
    internal readonly Vertex[] Vertices;
    internal readonly Face[] Faces;
    internal readonly Material[] Materials;
    readonly Triangle[] triangles;
    readonly Node[] nodes;
    internal Vector3 Min=>nodes[0].Min;
    internal Vector3 Max=>nodes[0].Max;

    internal LightBakeScene(string folder, IReadOnlyList<PlacedEntity> entities)
    {
        var vertices=new List<Vertex>(); var faces=new List<Face>(); var materials=new List<Material>();
        var textures=new Dictionary<string,Albedo>(StringComparer.Ordinal);
        var materialIds=new Dictionary<(string,float),int>();
        foreach (var e in entities)
        {
            if(e.Model==null)continue;
            var g=Geometry.Read(Path.Combine(folder,"Models",e.Model));
            var m=e.Matrix;
            var world=new Matrix4x4(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
            if(!Matrix4x4.Invert(world,out var inverse))throw new InvalidDataException("Singular light-bake transform: "+e.Name);
            var normals=Matrix4x4.Transpose(inverse);
            int first=vertices.Count;
            for(int i=0;i<g.Colors.Length;i++)
            {
                int k=i*3,t=i*4;
                Vector3 p=new(g.Positions[k],g.Positions[k+1],g.Positions[k+2]), n=new(g.Normals[k],g.Normals[k+1],g.Normals[k+2]);
                var tangent=Vector3.Normalize(Vector3.TransformNormal(new(g.Tangents[t],g.Tangents[t+1],g.Tangents[t+2]),world));
                vertices.Add(new(Vector3.Transform(p,world),Vector3.Normalize(Vector3.TransformNormal(n,normals)),
                    new(tangent,g.Tangents[t+3]),new(g.Uvs[i*2],g.Uvs[i*2+1]),g.Colors[i]));
            }
            foreach(var s in g.Surfaces)
            {
                if(!materialIds.TryGetValue((s.Texture,s.Cutoff),out int id))
                {
                    if(!textures.TryGetValue(s.Texture,out var texture))
                    {
                        string path=s.Texture.Length==0?null:Path.Combine(folder,"Textures",Path.GetFileNameWithoutExtension(s.Texture)+".btex");
                        textures.Add(s.Texture,texture=new Albedo(path));
                    }
                    id=materials.Count;materials.Add(new(s,texture));materialIds.Add((s.Texture,s.Cutoff),id);
                }
                for(int i=s.Start;i<s.Start+s.Count;i+=3)
                    faces.Add(new(first+g.Indices[i],first+g.Indices[i+1],first+g.Indices[i+2],id));
            }
        }
        Vertices=vertices.ToArray();Faces=faces.ToArray();Materials=materials.ToArray();
        triangles=new Triangle[Faces.Length];
        for(int i=0;i<Faces.Length;i++)
        {
            var f=Faces[i];var a=Vertices[f.A].Position;var b=Vertices[f.B].Position;var c=Vertices[f.C].Position;
            triangles[i]=new(a,b-a,c-a,Vector3.Min(a,Vector3.Min(b,c)),Vector3.Max(a,Vector3.Max(b,c)),f);
        }
        var tree=new List<Node>();Build(0,triangles.Length);nodes=tree.ToArray();
        int Build(int start,int count)
        {
            Vector3 min=new(float.MaxValue),max=new(float.MinValue);
            for(int i=start;i<start+count;i++){min=Vector3.Min(min,triangles[i].Min);max=Vector3.Max(max,triangles[i].Max);}
            int slot=tree.Count;tree.Add(new(min,max,start,count,0));
            if(count<=8)return slot;
            var size=max-min;
            int axis=size.X>=size.Y && size.X>=size.Z?0:size.Y>=size.Z?1:2;
            Array.Sort(triangles,start,count,Comparer<Triangle>.Create((a,b)=>(a.Min[axis]+a.Max[axis]).CompareTo(b.Min[axis]+b.Max[axis])));
            int left=Build(start,count/2),right=Build(start+count/2,count-count/2);
            tree[slot]=new(min,max,left,0,right);return slot;
        }
    }

    internal readonly record struct Hit(int Triangle, float T, float U, float V);
    internal bool Trace(Vector3 origin,Vector3 direction,bool any,out Hit hit)
    {
        hit=new(-1,float.MaxValue,0,0);
        Vector3 inv=new(Inv(direction.X),Inv(direction.Y),Inv(direction.Z));
        Span<int> pending=stackalloc int[64];pending[0]=0;int top=1;
        while(top!=0)
        {
            ref readonly var node=ref nodes[pending[--top]];
            var t0=(node.Min-origin)*inv;var t1=(node.Max-origin)*inv;
            var lo=Vector3.Min(t0,t1);var hi=Vector3.Max(t0,t1);
            if(MathF.Max(0,MathF.Max(lo.X,MathF.Max(lo.Y,lo.Z)))>MathF.Min(hit.T,MathF.Min(hi.X,MathF.Min(hi.Y,hi.Z))))continue;
            if(node.Count==0){pending[top++]=node.Right;pending[top++]=node.First;continue;}
            for(int i=node.First;i<node.First+node.Count;i++)
            {
                ref readonly var tri=ref triangles[i];
                var p=Vector3.Cross(direction,tri.E2);float det=Vector3.Dot(tri.E1,p);
                if(MathF.Abs(det)<1e-8f)continue;
                var offset=origin-tri.A;float u=Vector3.Dot(offset,p)/det;
                if(u<0 || u>1)continue;
                var q=Vector3.Cross(offset,tri.E1);float v=Vector3.Dot(direction,q)/det;
                if(v<0 || u+v>1)continue;
                float t=Vector3.Dot(tri.E2,q)/det;
                if(t<.001f || t>=hit.T)continue;
                var f=tri.Face;
                if(Materials[f.Material].Surface.Cutoff>0 && ColorAt(f,u,v).W<Materials[f.Material].Surface.Cutoff)continue;
                hit=new(i,t,u,v);if(any)return true;
            }
        }
        return hit.Triangle>=0;
        static float Inv(float d)=>MathF.Abs(d)>1e-12f?1/d:MathF.CopySign(1e12f,d);
    }
    Vector4 ColorAt(Face f,float u,float v)
    {
        var a=Vertices[f.A];var b=Vertices[f.B];var c=Vertices[f.C];float w=1-u-v;
        var tint=w*Unpack(a.Tint)+u*Unpack(b.Tint)+v*Unpack(c.Tint);
        return tint*Materials[f.Material].Image.Sample(w*a.Uv+u*b.Uv+v*c.Uv);
    }
    internal void Receiver(Hit hit,Vector3 direction,out Vector3 normal,out Vector3 albedo)
    {
        var tri=triangles[hit.Triangle];
        normal=Vector3.Normalize(Vector3.Cross(tri.E1,tri.E2));
        if(Vector3.Dot(normal,direction)>0)normal=-normal;
        var color=ColorAt(tri.Face,hit.U,hit.V);albedo=new(color.X,color.Y,color.Z);
    }
    internal bool BackFacing(Hit hit,Vector3 direction)
    {
        var f=triangles[hit.Triangle].Face;
        return Vector3.Dot(Vertices[f.A].Normal+Vertices[f.B].Normal+Vertices[f.C].Normal,direction)>0;
    }
    internal static Vector4 Unpack(uint c)=>new Vector4(c&255,(c>>8)&255,(c>>16)&255,c>>24)/255;
    internal static uint Pack(Vector4 c)
    {
        c=Vector4.Clamp(c,Vector4.Zero,Vector4.One)*255+new Vector4(.5f);
        return (uint)c.X | (uint)c.Y<<8 | (uint)c.Z<<16 | (uint)c.W<<24;
    }
}
