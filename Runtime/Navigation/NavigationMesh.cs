using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CodexEngine.Runtime;

/// <summary>Walkable triangle mesh with tiled boundaries and bidirectional off-mesh links.</summary>
public sealed class NavigationMesh
{
    public readonly Vector3[] Vertices;
    public readonly int[] Indices;
    private readonly Vector3[] centres;
    private readonly int[] neighbours;
    private readonly List<Seam>[] seams;
    private readonly Dictionary<int,List<Link>> links = new();
    private readonly Dictionary<(int,int),List<int>> cells = new();
    private const float CellSize=5f;
    private record struct Link(int Target,Vector3 From,Vector3 To);
    private record struct Seam(int Target,Vector3 A,Vector3 B);
    public NavigationMesh(Vector3[] vertices,int[] indices)
    {
        Vertices=vertices;Indices=indices;
        centres=new Vector3[indices.Length/3];neighbours=new int[indices.Length];Array.Fill(neighbours,-1);
        seams=new List<Seam>[indices.Length];
        var vertexIds=new Dictionary<(int,int,int),int>();
        int[] ids=new int[vertices.Length];
        for(int i=0;i<vertices.Length;i++)
        {
            var v=vertices[i];
            var key=((int)MathF.Round(v.x*1000),(int)MathF.Round(v.y*1000),(int)MathF.Round(v.z*1000));
            if(!vertexIds.TryGetValue(key,out ids[i]))vertexIds.Add(key,ids[i]=vertexIds.Count);
        }
        var edges=new Dictionary<(int,int),int>();
        for(int t=0;t<centres.Length;t++)
        {
            var a=Vertex(t,0);var b=Vertex(t,1);var c=Vertex(t,2);
            centres[t]=(a+b+c)/3;
            int x0=Cell(Math.Min(a.x,Math.Min(b.x,c.x))),x1=Cell(Math.Max(a.x,Math.Max(b.x,c.x)));
            int z0=Cell(Math.Min(a.z,Math.Min(b.z,c.z))),z1=Cell(Math.Max(a.z,Math.Max(b.z,c.z)));
            for(int x=x0;x<=x1;x++)for(int z=z0;z<=z1;z++)
            {
                if(!cells.TryGetValue((x,z),out var triangles))cells[(x,z)]=triangles=new();
                triangles.Add(t);
            }
            for(int side=0;side<3;side++)
            {
                int edge=t*3+side, v1=ids[indices[edge]],v2=ids[indices[t*3+(side+1)%3]];
                var key=v1<v2?(v1,v2):(v2,v1);
                if(edges.TryGetValue(key,out int other)){neighbours[edge]=other/3;neighbours[other]=t;}
                else edges.Add(key,edge);
            }
        }
        StitchTileBoundaries();
    }
    // Recast clips tiles independently. A long boundary in one tile can meet several
    // shorter collinear boundaries on its neighbour; those triangles share only part of an edge.
    private void StitchTileBoundaries()
    {
        var planes=new Dictionary<(int,int),List<int>>();
        for(int e=0;e<neighbours.Length;e++)
        {
            if(neighbours[e]>=0)continue;
            int t=e/3,side=e%3;
            var a=Vertex(t,side);var b=Vertex(t,(side+1)%3);
            int axis=Math.Abs(a.x-b.x)<.001f?0:Math.Abs(a.z-b.z)<.001f?2:-1;
            if(axis<0)continue;
            var key=(axis,(int)MathF.Round((axis==0?a.x:a.z)*1000));
            if(!planes.TryGetValue(key,out var candidates))planes[key]=candidates=new();
            float Across(Vector3 p)=>axis==0?p.x:p.z;
            float Along(Vector3 p)=>axis==0?p.z:p.x;
            Vector3 At(Vector3 one,Vector3 two,float at)=>Vector3.Lerp(one,two,(at-Along(one))/(Along(two)-Along(one)));
            foreach(int other in candidates)
            {
                int q=other/3;
                if((Across(centres[q])-Across(a))*(Across(centres[t])-Across(a))>=0)continue;
                var c=Vertex(q,other%3);var d=Vertex(q,(other%3+1)%3);
                float low=Math.Max(Math.Min(Along(a),Along(b)),Math.Min(Along(c),Along(d)));
                float high=Math.Min(Math.Max(Along(a),Along(b)),Math.Max(Along(c),Along(d)));
                if(high-low<.002f)continue;
                Vector3 entry=At(a,b,low),exit=At(a,b,high);
                if(Math.Abs(entry.y-At(c,d,low).y)>.03f || Math.Abs(exit.y-At(c,d,high).y)>.03f)continue;
                (seams[e]??=new()).Add(new Seam(q,entry,exit));
                (seams[other]??=new()).Add(new Seam(t,entry,exit));
            }
            candidates.Add(e);
        }
    }
    private int AcrossEdge(int edge,Vector3 crossing)
    {
        if(neighbours[edge]>=0 || seams[edge]==null)return neighbours[edge];
        foreach(var seam in seams[edge])
        {
            Vector3 along=seam.B-seam.A;
            float u=(crossing.x-seam.A.x)*along.x+(crossing.z-seam.A.z)*along.z;
            if(u>=-.0002f && u<=along.x*along.x+along.z*along.z+.0002f)return seam.Target;
        }
        return -1;
    }
    public static NavigationMesh Load(string path)
    {
        using var data=JsonDocument.Parse(File.ReadAllBytes(path));
        var verts=data.RootElement.GetProperty("vertices"u8);var idx=data.RootElement.GetProperty("indices"u8);
        var v=new Vector3[verts.GetArrayLength()];var indices=new int[idx.GetArrayLength()];
        int i=0;foreach(var p in verts.EnumerateArray())v[i++]=CollisionWorld.ReadPoint(p);
        i=0;foreach(var n in idx.EnumerateArray())indices[i++]=n.GetInt32();
        return new NavigationMesh(v,indices);
    }
    private static int Cell(float x)=>(int)MathF.Floor(x/CellSize);
    private Vector3 Vertex(int t,int edge)=>Vertices[Indices[t*3+edge]];
    private static Vector3 Nearest(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
    {
        // Closest point in the triangle's vertex, edge or face Voronoi region.
        Vector3 ab=b-a,ac=c-a,ap=p-a;
        float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
        if(d1<=0 && d2<=0)return a;
        Vector3 bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);
        if(d3>=0 && d4<=d3)return b;
        float vc=d1*d4-d3*d2;
        if(vc<=0 && d1>=0 && d3<=0)return a+ab*(d1/(d1-d3));
        Vector3 cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);
        if(d6>=0 && d5<=d6)return c;
        float vb=d5*d2-d1*d6;
        if(vb<=0 && d2>=0 && d6<=0)return a+ac*(d2/(d2-d6));
        float va=d3*d6-d5*d4;
        if(va<=0 && d4-d3>=0 && d5-d6>=0)return b+(c-b)*((d4-d3)/(d4-d3+d5-d6));
        float denom=1/(va+vb+vc);
        return a+ab*(vb*denom)+ac*(vc*denom);
    }
    public int Sample(Vector3 p,float range,out Vector3 point)
    {
        float best=range*range; int found=-1; point=p;
        for(int x=Cell(p.x-range);x<=Cell(p.x+range);x++)
        for(int z=Cell(p.z-range);z<=Cell(p.z+range);z++)
        {
            if(!cells.TryGetValue((x,z),out var tris))continue;
            foreach(int t in tris)
            {
                var q=Nearest(p,Vertex(t,0),Vertex(t,1),Vertex(t,2));float dist=(p-q).sqrMagnitude;
                if(dist>best || (dist==best && t>=found && found>=0))continue;
                found=t;best=dist;point=q;
            }
        }
        return found;
    }
    public void AddOffMeshLink(Vector3 a,Vector3 b)
    {
        int start=Sample(a,2f,out var p),end=Sample(b,2f,out var q);
        if(start<0 || end<0 || start==end)return;
        Add(start,new Link(end,p,q));Add(end,new Link(start,q,p));
        void Add(int t,Link link)
        {
            if(!links.TryGetValue(t,out var edges))links[t]=edges=new();
            edges.Add(link);
        }
    }
    private static float CrossXZ(Vector3 a,Vector3 b)=>a.x*b.z-a.z*b.x;
    public float Trace(int triangle,Vector3 start,Vector3 end,out bool reached)
    {
        Vector3 ray=end-start;
        reached=false;
        float fraction=0;
        for(int steps=0;steps<=centres.Length;steps++)
        {
            Vector3 a=Vertex(triangle,0),b=Vertex(triangle,1),c=Vertex(triangle,2);
            float winding=CrossXZ(b-a,c-a)>=0?1:-1;
            int exit=-1;float first=1;
            for(int i=0;i<3;i++)
            {
                Vector3 p=Vertex(triangle,i),edge=Vertex(triangle,(i+1)%3)-p;
                float atStart=winding*CrossXZ(edge,start-p),change=winding*CrossXZ(edge,ray);
                if(change>=-.000001f || atStart+change>=-.0001f)continue;
                float hit=-atStart/change;
                if(hit<fraction-.0001f || hit>first+.00001f)continue;
                if(exit>=0 && first<=hit+.00001f && AcrossEdge(triangle*3+exit,start+ray*first)>=0)continue;
                exit=i;first=Math.Max(fraction,hit);
            }
            if(exit<0)
            {
                var n=Vector3.Cross(b-a,c-a);
                float y=Math.Abs(n.y)>.000001f?a.y-(n.x*(end.x-a.x)+n.z*(end.z-a.z))/n.y:a.y;
                reached=Math.Abs(y-end.y)<1f;return 1;
            }
            fraction=first;
            triangle=AcrossEdge(triangle*3+exit,start+ray*fraction);
            if(triangle<0)return Math.Clamp(fraction,0,1);
        }
        return fraction;
    }
    public sealed class Route
    {
        internal readonly PriorityQueue<int,float> Open=new();
        internal readonly List<(Vector3 Point,bool Fixed)> Reverse=new();
        internal readonly int[] Seen,Closed,Parent;
        internal readonly float[] Cost;
        internal readonly (Vector3 In,Vector3 Out,bool OffMesh)[] Portal;
        internal int Generation;
        internal Route(int n){ Seen=new int[n];Closed=new int[n];Parent=new int[n];Cost=new float[n];Portal=new (Vector3,Vector3,bool)[n]; }
    }
    public Route NewRoute()=>new(centres.Length);
    public bool Path(Route r,Vector3 from,Vector3 to,List<Vector3> result,out bool complete)
    {
        result.Clear();complete=false;
        int start=Sample(from,3,out var a),goal=Sample(to,3,out var b);
        if(start<0 || goal<0)return false;
        int generation=++r.Generation;
        r.Open.Clear();r.Open.Enqueue(start,0);r.Seen[start]=generation;r.Cost[start]=0;r.Parent[start]=-1;
        int closest=start;float best=(centres[start]-b).sqrMagnitude;
        while(r.Open.TryDequeue(out int t,out _))
        {
            if(r.Closed[t]==generation)continue;
            r.Closed[t]=generation;
            float d=(centres[t]-b).sqrMagnitude;
            if(d<best){closest=t;best=d;}
            if(t==goal){closest=t;complete=true;break;}
            for(int i=0;i<3;i++)
            {
                int e=t*3+i,other=neighbours[e];
                if(other>=0)
                {
                    Vector3 mid=(Vertex(t,i)+Vertex(t,(i+1)%3))*.5f;
                    Relax(t,other,mid,mid,false);
                }
                else if(seams[e]!=null)foreach(var seam in seams[e])
                {
                    Vector3 mid=(seam.A+seam.B)*.5f;
                    Relax(t,seam.Target,mid,mid,false);
                }
            }
            if(links.TryGetValue(t,out var list))foreach(var link in list)Relax(t,link.Target,link.From,link.To,true);
        }
        void Relax(int t,int other,Vector3 p,Vector3 q,bool offMesh)
        {
            float cost=r.Cost[t]+Vector3.Distance(centres[t],p)+Vector3.Distance(centres[other],q)+2*Vector3.Distance(p,q);
            if(r.Seen[other]==generation && cost>=r.Cost[other])return;
            r.Seen[other]=generation;r.Parent[other]=t;r.Cost[other]=cost;r.Portal[other]=(p,q,offMesh);
            r.Open.Enqueue(other,cost+Vector3.Distance(centres[other],b));
        }
        if(!complete)b=Nearest(b,Vertex(closest,0),Vertex(closest,1),Vertex(closest,2));
        r.Reverse.Clear();r.Reverse.Add((b,false));
        for(int t=closest;t!=start;t=r.Parent[t])
        {
            var portal=r.Portal[t];
            if(portal.OffMesh)r.Reverse.Add((portal.Out,true));
            r.Reverse.Add((portal.In,portal.OffMesh));
        }
        result.Add(a);int index=r.Reverse.Count-1;
        while(index>=0)
        {
            int pick=index;
            if(!r.Reverse[index].Fixed)
            {
                int triangle=Sample(result[^1],.1f,out var on);
                for(int i=index-1;i>=0 && triangle>=0;i--)
                {
                    Trace(triangle,on,r.Reverse[i].Point,out bool clear);
                    if(!clear)break;
                    pick=i;
                    if(r.Reverse[i].Fixed)break;
                }
            }
            var corner=r.Reverse[pick].Point;
            if(result[^1]!=corner)result.Add(corner);
            index=pick-1;
        }
        return result.Count>=2;
    }
}

