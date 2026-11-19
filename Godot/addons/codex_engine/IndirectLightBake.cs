using System.Numerics;
using System.Security.Cryptography;
using Foundry.Content;
using Vertex = Foundry.Authoring.LightBakeScene.Vertex;

namespace Foundry.Authoring;

/// <summary>Offline diffuse light transport for static surfaces and moving-object probes.</summary>
public static class IndirectLightBake
{
    const float VertexSpacing=.92f, DistantSpacing=4f, ProbeSpacing=2f;
    const int SurfaceRays=64, ProbeRays=128;
    const float Bias=.014f;

    public static void Write(string folder,string map,IReadOnlyList<PlacedEntity> entities,
        Vector3 sunDirection,Vector3 sunColor,Vector3 skyColor,Vector3 horizonColor,Vector2 detailMin,Vector2 detailMax)
    {
        string path=Path.Combine(folder,map+".brgi");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("diffuse-bake-3-linear"u8);
        AddVector(new(detailMin,VertexSpacing)); AddVector(new(detailMax,DistantSpacing));
        AddVector(new(ProbeSpacing,Bias,0)); AddVector(new(SurfaceRays,ProbeRays,0));
        AddVector(sunDirection);AddVector(sunColor);AddVector(skyColor);AddVector(horizonColor);
        var textureNames=new SortedSet<string>(StringComparer.Ordinal);
        foreach(var e in entities)
        {
            if(e.Model==null)continue;
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(e.Matrix.AsSpan()));
            string model=Path.Combine(folder,"Models",e.Model);
            hash.AppendData(File.ReadAllBytes(model));
            foreach(var s in Geometry.Read(model).Surfaces)
                if(s.Texture.Length>0)textureNames.Add(s.Texture);
        }
        foreach(string name in textureNames)
            hash.AppendData(File.ReadAllBytes(Path.Combine(folder,"Textures",Path.GetFileNameWithoutExtension(name)+".btex")));
        var inputHash=hash.GetHashAndReset();
        if(File.Exists(path))
        {
            using var r=new BinaryReader(File.OpenRead(path));
            if(BakedSceneLight.Matches(r,inputHash))return;
        }
        var scene=new LightBakeScene(folder,entities);
        var solver=new Solver(scene,Vector3.Normalize(sunDirection),sunColor,skyColor,horizonColor);
        var mesh=Refine(scene,detailMin,detailMax);
        var surface=new uint[mesh.Colors.Length];
        var parallel=new ParallelOptions{MaxDegreeOfParallelism=Math.Clamp(Environment.ProcessorCount-2,1,12)};
        Parallel.For(0,surface.Length,parallel,i=>{
            var p=At(mesh.Positions,i);var n=At(mesh.Normals,i);
            surface[i]=LightBakeScene.PackLight(solver.Surface(p,n));
        });
        // Dynamic objects sample the playable area. Distant static scenery has its own surface bake.
        var origin=new Vector3(detailMin.X,scene.Min.Y+ProbeSpacing*.25f,detailMin.Y);
        var extent=(new Vector3(detailMax.X,scene.Max.Y,detailMax.Y)-origin)/ProbeSpacing;
        int width=2+(int)MathF.Ceiling(extent.X),height=2+(int)MathF.Ceiling(extent.Y),depth=2+(int)MathF.Ceiling(extent.Z);
        var probes=new uint[checked(width*height*depth*6)];var valid=new bool[probes.Length/6];
        Parallel.For(0,valid.Length,parallel,i=>{
            int x=i%width,y=i/width%height,z=i/(width*height);
            var p=origin+new Vector3(x,y,z)*ProbeSpacing;
            valid[i]=solver.Probe(p,probes.AsSpan(i*6,6));
        });
        ExtendInterior(probes,valid,width,height,depth);
        new BakedSceneLight(mesh,surface,origin,ProbeSpacing,width,height,depth,probes).Write(path,inputHash);
        Godot.GD.Print($"Baked {map} indirect light: {surface.Length} surface samples, {width} x {height} x {depth} directional probes.");

        void AddVector(Vector3 v){hash.AppendData(BitConverter.GetBytes(v.X));hash.AppendData(BitConverter.GetBytes(v.Y));hash.AppendData(BitConverter.GetBytes(v.Z));}
        static Vector3 At(float[] data,int i)=>new(data[3*i],data[3*i+1],data[3*i+2]);
    }

    static Geometry Refine(LightBakeScene scene,Vector2 detailMin,Vector2 detailMax)
    {
        var vertices=new List<Vertex>(scene.Vertices);
        var midpoints=new Dictionary<(int,int),int>();
        var indices=new List<int>();var surfaces=new List<DrawSurface>();
        foreach(var group in scene.Faces.GroupBy(f=>f.Material))
        {
            int start=indices.Count;
            foreach(var face in group)Split(face.A,face.B,face.C);
            var m=scene.Materials[group.Key].Surface;
            surfaces.Add(new(start,indices.Count-start,m.Texture,m.Cutoff));
        }
        int count=vertices.Count;
        var p=new float[count*3];var n=new float[count*3];var t=new float[count*4];var uv=new float[count*2];var tint=new uint[count];
        for(int i=0;i<count;i++)
        {
            var v=vertices[i];p[i*3]=v.Position.X;p[i*3+1]=v.Position.Y;p[i*3+2]=v.Position.Z;
            n[i*3]=v.Normal.X;n[i*3+1]=v.Normal.Y;n[i*3+2]=v.Normal.Z;
            t[i*4]=v.Tangent.X;t[i*4+1]=v.Tangent.Y;t[i*4+2]=v.Tangent.Z;t[i*4+3]=v.Tangent.W;
            uv[i*2]=v.Uv.X;uv[i*2+1]=v.Uv.Y;tint[i]=v.Tint;
        }
        return new(p,n,t,uv,tint,indices.ToArray(),surfaces.ToArray());

        void Split(int a,int b,int c)
        {
            var va=vertices[a];var vb=vertices[b];var vc=vertices[c];
            float ab=Vector3.DistanceSquared(va.Position,vb.Position),bc=Vector3.DistanceSquared(vb.Position,vc.Position),ca=Vector3.DistanceSquared(vc.Position,va.Position);
            var min=Vector3.Min(va.Position,Vector3.Min(vb.Position,vc.Position));
            var max=Vector3.Max(va.Position,Vector3.Max(vb.Position,vc.Position));
            float distance=MathF.Max(MathF.Max(detailMin.X-max.X,min.X-detailMax.X),MathF.Max(detailMin.Y-max.Z,min.Z-detailMax.Y));
            float spacing=Math.Clamp(VertexSpacing+distance*.25f,VertexSpacing,DistantSpacing);
            if(Math.Max(ab,Math.Max(bc,ca))<=spacing*spacing){indices.Add(a);indices.Add(b);indices.Add(c);return;}
            if(bc>ab && bc>=ca)(a,b,c)=(b,c,a);else if(ca>ab && ca>bc)(a,b,c)=(c,a,b);
            var edge=(Math.Min(a,b),Math.Max(a,b));
            if(!midpoints.TryGetValue(edge,out int mid))
            {
                va=vertices[a];vb=vertices[b];mid=vertices.Count;midpoints.Add(edge,mid);
                var blendedNormal=va.Normal+vb.Normal;
                vertices.Add(new((va.Position+vb.Position)*.5f,blendedNormal.LengthSquared()>1e-8f?Vector3.Normalize(blendedNormal):va.Normal,
                    (va.Tangent+vb.Tangent)*.5f,(va.Uv+vb.Uv)*.5f,LightBakeScene.Pack((LightBakeScene.Unpack(va.Tint)+LightBakeScene.Unpack(vb.Tint))*.5f)));
            }
            Split(a,mid,c);Split(mid,b,c);
        }
    }

    // Light is linear radiance. The sky's colour runs from the horizon to the zenith, like Unity's three-colour ambient.
    sealed class Solver(LightBakeScene scene,Vector3 sun,Vector3 sunColor,Vector3 sky,Vector3 horizon)
    {
        static readonly Vector3[] Hemisphere=CosineDirections(SurfaceRays);
        static readonly Vector3[] SkyRays=CosineDirections(131);
        static readonly Vector3[] Sphere=Enumerable.Range(0,ProbeRays).Select(i=>{
            float y=1-2*(i+.5f)/ProbeRays, r=MathF.Sqrt(1-y*y), a=i*2.39996323f;
            return new Vector3(r*MathF.Cos(a),y,r*MathF.Sin(a));
        }).ToArray();
        static Vector3[] CosineDirections(int count)=>Enumerable.Range(0,count).Select(i=>{
            float h=(i+.5f)/count,r=MathF.Sqrt(h),a=i*2.39996323f;
            return new Vector3(r*MathF.Cos(a),r*MathF.Sin(a),MathF.Sqrt(1-h));
        }).ToArray();
        static void Basis(Vector3 n,out Vector3 u,out Vector3 v)
        {
            u=Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y)<.95f?Vector3.UnitY:Vector3.UnitX,n));v=Vector3.Cross(n,u);
        }
        Vector3 Sky(float up)=>Vector3.Lerp(horizon,sky,up);
        Vector3 Incident(Vector3 origin,Vector3 dir,int ray,out bool inside)
        {
            inside=false;
            if(!scene.Trace(origin,dir,false,out var hit))return dir.Y>0?Sky(dir.Y):Vector3.Zero;
            inside=scene.BackFacing(hit,dir);
            scene.Receiver(hit,dir,out var n,out var albedo);
            var at=origin+dir*hit.T+n*Bias;
            float nl=Math.Max(0,Vector3.Dot(n,sun));
            var incoming=nl>0 && !scene.Trace(at,sun,true,out _)?sunColor*nl:Vector3.Zero;
            // One cosine-weighted sky visibility sample at the reflecting surface. A second hit ends the path.
            Basis(n,out var u,out var v);
            var sample=SkyRays[ray*47%SkyRays.Length];var toSky=u*sample.X+v*sample.Y+n*sample.Z;
            if(toSky.Y>0 && !scene.Trace(at,toSky,true,out _))incoming+=Sky(toSky.Y);
            return albedo*incoming;
        }
        internal Vector3 Surface(Vector3 p,Vector3 n)
        {
            Basis(n,out var u,out var v);var sum=Vector3.Zero;p+=n*Bias;
            for(int i=0;i<Hemisphere.Length;i++)
            {
                var s=Hemisphere[i];sum+=Incident(p,u*s.X+v*s.Y+n*s.Z,i,out _);
            }
            return sum/Hemisphere.Length;
        }
        internal bool Probe(Vector3 p,Span<uint> packed)
        {
            Span<Vector3> sum=stackalloc Vector3[6];Span<float> weight=stackalloc float[6];
            sum.Clear();weight.Clear();int backs=0;
            for(int i=0;i<Sphere.Length;i++)
            {
                var d=Sphere[i];var radiance=Incident(p,d,i,out bool inside);if(inside)backs++;
                for(int axis=0;axis<3;axis++)
                {
                    int face=axis*2+(d[axis]<0?1:0);float w=MathF.Abs(d[axis]);sum[face]+=radiance*w;weight[face]+=w;
                }
            }
            for(int f=0;f<6;f++)packed[f]=LightBakeScene.PackLight(sum[f]/weight[f]);
            return backs<Sphere.Length*.48f;
        }
    }
    static void ExtendInterior(uint[] light,bool[] valid,int w,int h,int d)
    {
        var queue=new Queue<int>();
        for(int i=0;i<valid.Length;i++)if(valid[i])queue.Enqueue(i);
        if(queue.Count==0)throw new InvalidDataException("No exterior irradiance probes in scene.");
        while(queue.TryDequeue(out int i))
        {
            int x=i%w,y=i/w%h,z=i/(w*h);
            if(x>0)Visit(i-1);if(x+1<w)Visit(i+1);if(y>0)Visit(i-w);if(y+1<h)Visit(i+w);if(z>0)Visit(i-w*h);if(z+1<d)Visit(i+w*h);
            void Visit(int j)
            {
                if(valid[j])return;
                valid[j]=true;Array.Copy(light,i*6,light,j*6,6);queue.Enqueue(j);
            }
        }
    }
}
