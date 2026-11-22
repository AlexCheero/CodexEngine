using System.Numerics;

namespace Foundry.Content;

/// <summary>One cell of a fractured mesh.</summary>
/// <param name="Mesh">The cell's surface with its vertices relative to <paramref name="Center"/>. It keeps the source's
/// draw surfaces in order (some may be empty) and adds one interior surface for the cut faces.</param>
/// <param name="Center">Where the cell's origin lies in the source mesh's space: the centroid of its volume.</param>
/// <param name="Hull">The cell's corners relative to <paramref name="Center"/>, for a convex collision shape.</param>
/// <param name="Volume">Enclosed volume in the source mesh's units, cubed.</param>
public sealed record FracturePiece(Geometry Mesh,Vector3 Center,Vector3[] Hull,float Volume);

/// <summary>
/// Splits a mesh into the Voronoi cells of a set of seed points. Every cell is the mesh clipped by the planes halfway
/// between its seed and each other seed; each cut is closed with a flat interior face. Every vertex attribute is
/// interpolated along the cut edges. The caps are exact for convex meshes and for cuts whose cross-section is convex;
/// elsewhere a cap spans the cross-section's convex outline. Meshes need not be closed.
/// </summary>
public static class MeshFracture
{
    private struct Vertex
    {
        public Vector3 Position,Normal;
        public Vector4 Tangent,Color;
        public Vector2 Uv;
        public static Vertex Lerp(in Vertex a,in Vertex b,float t)=>new()
        {
            Position=Vector3.Lerp(a.Position,b.Position,t),Normal=Vector3.Lerp(a.Normal,b.Normal,t),
            Tangent=Vector4.Lerp(a.Tangent,b.Tangent,t),Color=Vector4.Lerp(a.Color,b.Color,t),Uv=Vector2.Lerp(a.Uv,b.Uv,t)
        };
    }
    private readonly record struct Triangle(Vertex A,Vertex B,Vertex C,int Surface);

    /// <param name="capTexture">Texture of the interior faces.</param>
    /// <param name="capUvScale">Interior texture repeats per unit of the source mesh's space.</param>
    public static FracturePiece[] Fracture(Geometry mesh,ReadOnlySpan<Vector3> seeds,string capTexture,float capUvScale=1)
    {
        var source=Read(mesh);
        var points=seeds.ToArray();
        var pieces=new FracturePiece[points.Length];
        Parallel.For(0,points.Length,i=>pieces[i]=Cell(mesh,source,points,i,capTexture,capUvScale));
        return pieces.Where(p=>p!=null).ToArray();
    }

    /// <summary>Seeds spread through a box, stratified along <paramref name="axis"/> (0, 1 or 2) so that tall shapes
    /// break into stacked chunks, pulled inside an ellipse across the other two axes.</summary>
    public static Vector3[] StratifiedSeeds(Vector3 min,Vector3 max,int count,int axis,int seed)
    {
        var random=new Random(seed);
        var result=new Vector3[count];
        var size=max-min;
        for(int i=0;i<count;i++)
        {
            float along=(i+.15f+.7f*random.NextSingle())/count;
            float angle=random.NextSingle()*MathF.Tau,radius=MathF.Sqrt(random.NextSingle())*.42f;
            float u=.5f+MathF.Cos(angle)*radius,v=.5f+MathF.Sin(angle)*radius;
            var unit=axis switch{0=>new Vector3(along,u,v),1=>new Vector3(u,along,v),_=>new Vector3(u,v,along)};
            result[i]=min+unit*size;
        }
        return result;
    }

    private static Triangle[] Read(Geometry mesh)
    {
        int count=mesh.Colors.Length;
        var vertices=new Vertex[count];
        for(int i=0;i<count;i++)
        {
            uint c=mesh.Colors[i];
            vertices[i]=new Vertex
            {
                Position=new(mesh.Positions[i*3],mesh.Positions[i*3+1],mesh.Positions[i*3+2]),
                Normal=new(mesh.Normals[i*3],mesh.Normals[i*3+1],mesh.Normals[i*3+2]),
                Tangent=new(mesh.Tangents[i*4],mesh.Tangents[i*4+1],mesh.Tangents[i*4+2],mesh.Tangents[i*4+3]),
                Uv=new(mesh.Uvs[i*2],mesh.Uvs[i*2+1]),
                Color=new Vector4(c&255,c>>8&255,c>>16&255,c>>24)/255f
            };
        }
        var triangles=new List<Triangle>(mesh.Indices.Length/3);
        for(int s=0;s<mesh.Surfaces.Length;s++)
        {
            var part=mesh.Surfaces[s];
            for(int k=part.Start;k+2<part.Start+part.Count;k+=3)
                triangles.Add(new(vertices[mesh.Indices[k]],vertices[mesh.Indices[k+1]],vertices[mesh.Indices[k+2]],s));
        }
        return triangles.ToArray();
    }

    private static FracturePiece Cell(Geometry mesh,Triangle[] source,Vector3[] seeds,int index,string capTexture,float capUvScale)
    {
        int cap=mesh.Surfaces.Length;
        var triangles=new List<Triangle>(source);
        var next=new List<Triangle>(source.Length);
        var cut=new List<Vector3>();
        var own=seeds[index];
        // Nearer neighbours first: their planes remove the most, so later clips have less to test.
        foreach(int other in Enumerable.Range(0,seeds.Length).Where(j=>j!=index).OrderBy(j=>Vector3.DistanceSquared(seeds[j],own)))
        {
            var normal=seeds[other]-own;
            float length=normal.Length();
            if(length<1e-6f)continue;
            normal/=length;
            float offset=Vector3.Dot(normal,(own+seeds[other])*.5f);
            next.Clear();cut.Clear();
            bool clipped=false;
            foreach(var triangle in triangles)
                clipped|=Clip(triangle,normal,offset,next,cut);
            if(!clipped)continue;
            (triangles,next)=(next,triangles);
            if(triangles.Count==0)return null;
            AddCap(triangles,cut,normal,offset,cap,capUvScale);
        }
        if(triangles.Count==0)return null;
        return Build(mesh,triangles,capTexture);
    }

    // Keeps the part of the triangle on the plane's negative side. True when anything was removed.
    private static bool Clip(in Triangle triangle,Vector3 normal,float offset,List<Triangle> kept,List<Vector3> cut)
    {
        const float epsilon=1e-5f;
        Span<Vertex> corners=[triangle.A,triangle.B,triangle.C];
        Span<float> distance=stackalloc float[3];
        int outside=0;
        for(int i=0;i<3;i++){distance[i]=Vector3.Dot(normal,corners[i].Position)-offset;if(distance[i]>epsilon)outside++;}
        if(outside==0)
        {
            kept.Add(triangle);
            for(int i=0;i<3;i++)if(distance[i]>-epsilon)cut.Add(corners[i].Position);
            return false;
        }
        if(outside==3)return true;
        Span<Vertex> polygon=stackalloc Vertex[4];
        int count=0;
        for(int i=0;i<3;i++)
        {
            int j=(i+1)%3;
            bool inside=distance[i]<=epsilon,nextInside=distance[j]<=epsilon;
            if(inside)polygon[count++]=corners[i];
            if(inside!=nextInside)
            {
                float t=distance[i]/(distance[i]-distance[j]);
                var crossing=Vertex.Lerp(corners[i],corners[j],t);
                polygon[count++]=crossing;cut.Add(crossing.Position);
            }
        }
        for(int i=1;i+1<count;i++)kept.Add(new(polygon[0],polygon[i],polygon[i+1],triangle.Surface));
        return true;
    }

    // Closes a cut with the convex outline of its crossing points, facing out along the plane normal.
    private static void AddCap(List<Triangle> triangles,List<Vector3> points,Vector3 normal,float offset,int surface,float uvScale)
    {
        if(points.Count<3)return;
        var u=Vector3.Normalize(Vector3.Cross(MathF.Abs(normal.Y)<.9f?Vector3.UnitY:Vector3.UnitX,normal));
        var v=Vector3.Cross(normal,u);
        var flat=points.Select(p=>new Vector2(Vector3.Dot(p,u),Vector3.Dot(p,v))).ToList();
        var hull=ConvexHull(flat);
        if(hull.Count<3)return;
        var origin=normal*offset;
        Vertex At(Vector2 q)
        {
            var p=origin+u*q.X+v*q.Y;
            return new Vertex{Position=p,Normal=normal,Tangent=new(u,1),Color=Vector4.One,Uv=q*uvScale};
        }
        // The hull runs counter-clockwise in (u,v); u x v is the normal, so this winding faces outward.
        var first=At(hull[0]);
        for(int i=1;i+1<hull.Count;i++)triangles.Add(new(first,At(hull[i]),At(hull[i+1]),surface));
    }

    private static List<Vector2> ConvexHull(List<Vector2> points)
    {
        points.Sort((a,b)=>a.X!=b.X?a.X.CompareTo(b.X):a.Y.CompareTo(b.Y));
        var unique=new List<Vector2>(points.Count);
        foreach(var p in points)if(unique.Count==0 || Vector2.DistanceSquared(unique[^1],p)>1e-10f)unique.Add(p);
        if(unique.Count<3)return unique;
        var hull=new List<Vector2>(unique.Count+1);
        static float Turn(Vector2 o,Vector2 a,Vector2 b)=>(a.X-o.X)*(b.Y-o.Y)-(a.Y-o.Y)*(b.X-o.X);
        foreach(var p in unique){while(hull.Count>=2 && Turn(hull[^2],hull[^1],p)<=1e-9f)hull.RemoveAt(hull.Count-1);hull.Add(p);}
        int lower=hull.Count+1;
        for(int i=unique.Count-2;i>=0;i--)
        {
            var p=unique[i];
            while(hull.Count>=lower && Turn(hull[^2],hull[^1],p)<=1e-9f)hull.RemoveAt(hull.Count-1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count-1);
        return hull;
    }

    private static FracturePiece Build(Geometry mesh,List<Triangle> triangles,string capTexture)
    {
        // Volume and centroid from the signed tetrahedra of the closed cell.
        double volume=0;Vector3 weighted=default;
        foreach(var t in triangles)
        {
            float six=Vector3.Dot(t.A.Position,Vector3.Cross(t.B.Position,t.C.Position));
            volume+=six/6;weighted+=(t.A.Position+t.B.Position+t.C.Position)*(six/24);
        }
        Vector3 center;
        if(Math.Abs(volume)>1e-9)center=weighted/(float)volume;
        else{center=default;foreach(var t in triangles)center+=t.A.Position+t.B.Position+t.C.Position;center/=triangles.Count*3;}
        int surfaces=mesh.Surfaces.Length+1;
        var ordered=triangles.OrderBy(t=>t.Surface).ToArray();
        int n=ordered.Length*3;
        var positions=new float[n*3];var normals=new float[n*3];var tangents=new float[n*4];var uvs=new float[n*2];
        var colors=new uint[n];var indices=new int[n];
        var parts=new DrawSurface[surfaces];
        int write=0;
        var corners=new HashSet<Vector3>();
        for(int s=0;s<surfaces;s++)
        {
            int start=write;
            foreach(var t in ordered)
            {
                if(t.Surface!=s)continue;
                foreach(var vertex in (ReadOnlySpan<Vertex>)[t.A,t.B,t.C])
                {
                    var p=vertex.Position-center;var nrm=vertex.Normal.LengthSquared()>0?Vector3.Normalize(vertex.Normal):Vector3.UnitY;
                    positions[write*3]=p.X;positions[write*3+1]=p.Y;positions[write*3+2]=p.Z;
                    normals[write*3]=nrm.X;normals[write*3+1]=nrm.Y;normals[write*3+2]=nrm.Z;
                    tangents[write*4]=vertex.Tangent.X;tangents[write*4+1]=vertex.Tangent.Y;tangents[write*4+2]=vertex.Tangent.Z;tangents[write*4+3]=vertex.Tangent.W;
                    uvs[write*2]=vertex.Uv.X;uvs[write*2+1]=vertex.Uv.Y;
                    var c=Vector4.Clamp(vertex.Color,Vector4.Zero,Vector4.One)*255+new Vector4(.5f);
                    colors[write]=(uint)c.X|(uint)c.Y<<8|(uint)c.Z<<16|(uint)c.W<<24;
                    indices[write]=write;write++;
                    corners.Add(new(MathF.Round(p.X,4),MathF.Round(p.Y,4),MathF.Round(p.Z,4)));
                }
            }
            parts[s]=s<mesh.Surfaces.Length?mesh.Surfaces[s] with {Start=start,Count=write-start}:new DrawSurface(start,write-start,capTexture,0);
        }
        return new FracturePiece(new Geometry(positions,normals,tangents,uvs,colors,indices,parts),center,corners.ToArray(),(float)Math.Abs(volume));
    }
}
