using System.Numerics;
using J = CodexEngine.Runtime;

namespace CodexEngine.Navigation;

/// <summary>A gate leaf's closed geometry: collision <see cref="Layers"/> hold it, bounds give its reach.</summary>
public readonly record struct NavigationGateVolume(int Group,int Layers,Vector3 Min,Vector3 Max);

/// <summary>Called for each baked column with its spans, lowest first, to set game flags. Runs on worker threads.</summary>
public delegate void LayeredColumnFlags(int column,Span<LayeredSpan> spans);

public sealed class LayeredNavigationBuildSettings
{
    public int Width,Length;
    public float CellSize;
    public Vector3 Origin;
    /// <summary>Body classes, at most eight. Bit k of span and edge masks belongs to profile k.</summary>
    public NavigationBody[] Profiles;
    /// <summary>Vertical extent of the scenery. Vertical rays cover it with a metre to spare.</summary>
    public float Bottom,Top;
    /// <summary>Collision layers of walkable and blocking scenery, with every gate open.</summary>
    public int WalkLayers=1;
    /// <summary>Closed gate leaves in groups 1 to 7. Each group can be opened at runtime.</summary>
    public IReadOnlyList<NavigationGateVolume> Gates=[];
    /// <summary>Closed colliders. A floor inside one is solid interior, not a walkable span.</summary>
    public IReadOnlySet<J.Collider> Watertight;
    public LayeredColumnFlags Flags;
    /// <summary>Clearance kept between a body and the scenery around it.</summary>
    public float Skin=.035f;
    /// <summary>Lowest up component of a floor normal; steeper faces are walls.</summary>
    public float MinNormal=.66f;
    /// <summary>Height differences above this are wall climbs and need an exposed face.</summary>
    public float MantleHeight=2;
    /// <summary>
    /// How far above a column's centre floor its footprint may find support and still stand on that floor.
    /// Higher support is taken for a neighbouring roof or boulder. Levels with many low detail steps
    /// (converted classic maps) raise it toward their step height, or cells beside every step drop out.
    /// </summary>
    public float SoleRise=.22f;
    /// <summary>
    /// The highest drop a body takes off a ledge onto the floor beside it, as a one-way link down. Zero bakes no
    /// drops: a body then only leaves a ledge where it could also come back up (a step or a climbable wall).
    /// </summary>
    public float DropHeight;
    public int MaxDegreeOfParallelism=Math.Min(12,Environment.ProcessorCount);
}

/// <summary>
/// Bakes a <see cref="LayeredNavigationGrid"/> from a collision world. Vertical rays find the floors of each
/// column, body sweeps test standing room and every link per body class. Keep a builder around to
/// <see cref="Rebuild"/> the area around scenery that is destroyed, moved or added at runtime.
/// Queries only read the world, so nothing may change it while a bake runs.
/// </summary>
public sealed class LayeredNavigationBuilder : IDisposable
{
    private sealed class Node(float floor,float ceiling,float slope,int profiles)
    {
        public readonly float Floor=floor,Ceiling=ceiling,Slope=slope;
        public readonly float[] Feet=new float[profiles];
        public byte Fits,Flags;
    }
    // Gates hold one blocked-profile byte per group, group g in byte g.
    private readonly record struct Link(int To,byte Mask,byte Climb,ulong Gates)
    {
        public byte Gate(int group)=>(byte)(Gates>>(group*8));
    }

    private readonly J.CollisionWorld world;
    private readonly LayeredNavigationBuildSettings settings;
    private readonly NavigationBody[] profiles;
    private readonly J.ShapeProbe[] sole,body;
    private readonly int[] gateLayers=new int[8];
    private readonly int gateGroups;
    private readonly ThreadLocal<J.RaycastHit[]> hits=new(()=>new J.RaycastHit[48]),ceilings=new(()=>new J.RaycastHit[48]);
    private readonly ParallelOptions parallel;
    private readonly Vector3 gateReachLow,gateReachHigh;

    public LayeredNavigationBuilder(J.CollisionWorld world,LayeredNavigationBuildSettings settings)
    {
        this.world=world;this.settings=settings;
        profiles=settings.Profiles;
        if(profiles is not {Length:>0 and <=LayeredNavigationGrid.MaxProfiles})
            throw new ArgumentException("Bake between one and eight navigation profiles.");
        foreach(var gate in settings.Gates)
        {
            if(gate.Group is <1 or >7)throw new ArgumentException("Gate groups run from 1 to 7.");
            gateLayers[gate.Group]|=gate.Layers;gateGroups|=1<<gate.Group;
        }
        sole=profiles.Select(p=>world.CreateProbe(J.CollisionShape.Cylinder(.08f,p.Radius))).ToArray();
        body=profiles.Select(p=>world.CreateProbe(J.CollisionShape.Cylinder(p.Height,p.Radius))).ToArray();
        parallel=new ParallelOptions{MaxDegreeOfParallelism=Math.Max(1,settings.MaxDegreeOfParallelism)};
        // A gate can stop the widest body anywhere it reaches up to the tallest one.
        float radius=profiles.Max(p=>p.Radius)+.1f,height=profiles.Max(p=>p.Height)+.2f;
        gateReachLow=new(radius,.1f,radius);gateReachHigh=new(radius,height,radius);
    }

    public void Dispose()
    {
        foreach(var probe in sole)probe.Dispose();
        foreach(var probe in body)probe.Dispose();
        hits.Dispose();ceilings.Dispose();
    }

    private int Width=>settings.Width;
    private static J.Vector3 Jv(Vector3 v)=>new(v.X,v.Y,v.Z);
    private Vector3 Center(int column,float y)=>new(settings.Origin.X+(column%Width+.5f)*settings.CellSize,y,
        settings.Origin.Z+(column/Width+.5f)*settings.CellSize);

    // Whole grid.

    public LayeredNavigationGrid Build()
    {
        int n=Width*settings.Length;
        var columns=new Node[n][];
        Parallel.For(0,n,parallel,c=>columns[c]=BakeColumn(c));
        return Assemble(columns,null,0,0,-1,-1);
    }

    /// <summary>
    /// Rebakes every column a body standing in the world-space box could touch, after the scenery there changed.
    /// The result matches a full bake of the current world; the previous grid stays valid for its routers until
    /// they switch with <see cref="LayeredNavigation.SetGrid"/>.
    /// </summary>
    public LayeredNavigationGrid Rebuild(LayeredNavigationGrid previous,Vector3 min,Vector3 max)
    {
        if(previous.Width!=Width || previous.Length!=settings.Length || previous.ProfileCount!=profiles.Length ||
           previous.CellSize!=settings.CellSize || previous.Origin!=settings.Origin)
            throw new ArgumentException("The grid was baked with other settings.");
        float reach=profiles.Max(p=>p.Radius)+settings.CellSize;
        int x0=Math.Max(0,(int)MathF.Floor((min.X-reach-settings.Origin.X)/settings.CellSize));
        int z0=Math.Max(0,(int)MathF.Floor((min.Z-reach-settings.Origin.Z)/settings.CellSize));
        int x1=Math.Min(Width-1,(int)MathF.Floor((max.X+reach-settings.Origin.X)/settings.CellSize));
        int z1=Math.Min(settings.Length-1,(int)MathF.Floor((max.Z+reach-settings.Origin.Z)/settings.CellSize));
        if(x0>x1 || z0>z1)return previous;
        // Links leaving the changed box need the standing heights of the spans on its outer two rings.
        // Those columns rebake to the spans they had, unless the change reached them: then grow the box.
        while(true)
        {
            int bx0=Math.Max(0,x0-2),bz0=Math.Max(0,z0-2),bx1=Math.Min(Width-1,x1+2),bz1=Math.Min(settings.Length-1,z1+2);
            int bw=bx1-bx0+1;
            var baked=new Node[bw*(bz1-bz0+1)][];
            Parallel.For(0,baked.Length,parallel,i=>baked[i]=BakeColumn((bz0+i/bw)*Width+bx0+i%bw));
            bool grow=false;
            for(int i=0;i<baked.Length && !grow;i++)
            {
                int x=bx0+i%bw,z=bz0+i/bw,c=z*Width+x;
                if(x>=x0 && x<=x1 && z>=z0 && z<=z1)continue;
                grow=!SameSpans(previous,c,baked[i]);
            }
            if(grow)
            {
                if(x0==0 && z0==0 && x1==Width-1 && z1==settings.Length-1)break;
                x0=Math.Max(0,x0-2);z0=Math.Max(0,z0-2);x1=Math.Min(Width-1,x1+2);z1=Math.Min(settings.Length-1,z1+2);
                continue;
            }
            var columns=new Node[Width*settings.Length][];
            for(int i=0;i<baked.Length;i++)columns[(bz0+i/bw)*Width+bx0+i%bw]=baked[i];
            return Assemble(columns,previous,x0,z0,x1,z1);
        }
        return Build();
    }

    private static bool SameSpans(LayeredNavigationGrid grid,int column,Node[] spans)
    {
        int first=grid.FirstNode(column);
        if(grid.EndNode(column)-first!=spans.Length)return false;
        for(int i=0;i<spans.Length;i++)
        {
            int node=first+i;
            if(grid.Floors[node]!=spans[i].Floor || grid.Ceilings[node]!=spans[i].Ceiling ||
               grid.Occupancy[node]!=spans[i].Fits || grid.SpanFlags[node]!=spans[i].Flags)return false;
        }
        return true;
    }

    // Columns.

    private int VerticalHits(float x,float z,bool down)
    {
        var local=down?hits:ceilings;
        var buffer=local.Value;
        var origin=new J.Vector3(x,down?settings.Top+1:settings.Bottom-1,z);
        int count;
        while((count=world.RaycastNonAlloc(origin,down?J.Vector3.down:J.Vector3.up,buffer,settings.Top-settings.Bottom+2,
            settings.WalkLayers,J.QueryTriggerInteraction.Ignore))==buffer.Length)
            Array.Resize(ref buffer,buffer.Length*2);
        local.Value=buffer;return count;
    }

    // A thin flat cylinder finds the highest contact under the real footprint (edges of sloping floors
    // support a flat sole above its centre ray). Sweeping that sole upwards tests the entire standing volume.
    private Node[] BakeColumn(int col)
    {
        var p=Center(col,0);
        int count=VerticalHits(p.X,p.Z,true),roofCount=VerticalHits(p.X,p.Z,false);
        var buffer=hits.Value;var roofs=ceilings.Value;
        float skin=settings.Skin,minNormal=settings.MinNormal;int walk=settings.WalkLayers;
        var watertight=settings.Watertight;
        bool InsideSolid(float floor)
        {
            if(watertight==null)return false;
            float y=floor+.045f;
            for(int i=0;i<count;i++)
            {
                ref var exit=ref buffer[i];
                if(exit.point.y<=y || !watertight.Contains(exit.collider))continue;
                bool entered=false;
                for(int j=0;j<roofCount;j++)
                {
                    ref var entry=ref roofs[j];
                    if(entry.collider==exit.collider && entry.point.y>y && entry.point.y<=exit.point.y+.015f)
                    {
                        entered=true;break;
                    }
                }
                if(!entered)return true;
            }
            return false;
        }
        var floors=new List<(float Y,float Slope)>();
        for(int i=0;i<count;i++)
        {
            var hit=buffer[i];
            if(hit.normal.y<minNormal)continue;
            floors.Add((hit.point.y,MathF.Sqrt(Math.Max(0,1-hit.normal.y*hit.normal.y))/hit.normal.y));
        }
        floors.Sort((a,b)=>a.Y.CompareTo(b.Y));
        var column=new List<Node>();
        for(int i=0;i<floors.Count;i++)
        {
            var (floor,slope)=floors[i];
            if(i+1<floors.Count && floors[i+1].Y<=floor+.06f || InsideSolid(floor))continue;
            var from=Jv(new Vector3(p.X,floor+.05f,p.Z));
            float ceiling=world.Raycast(from,J.Vector3.up,out var roof,settings.Top-floor+1,walk,J.QueryTriggerInteraction.Ignore)
                ? roof.point.y : float.PositiveInfinity;
            if(i+1<floors.Count)ceiling=Math.Min(ceiling,floors[i+1].Y);
            var node=new Node(floor,ceiling,slope,profiles.Length);
            for(int k=0;k<profiles.Length;k++)
            {
                var size=profiles[k];
                if(ceiling-floor<size.Height+skin)continue;
                float lift=Math.Min(ceiling-floor-.1f,size.Radius*1.18f+.12f);
                var at=Jv(new Vector3(p.X,floor+lift+.04f,p.Z));
                if(!sole[k].Cast(at,J.Vector3.down,lift+.15f,walk,out var ground) || ground.normal.y<minNormal)continue;
                float feet=floor+lift-ground.distance;
                // A horizontal neighbouring roof or boulder is not the centre's support.
                if(feet-floor>size.Radius*slope+settings.SoleRise || ceiling-feet<size.Height+skin)continue;
                at.y=feet+.04f+skin;
                if(sole[k].Cast(at,J.Vector3.up,size.Height-.08f,walk,out _))continue;
                node.Fits|=(byte)(1<<k);node.Feet[k]=feet;
            }
            if(node.Fits!=0)column.Add(node);
        }
        if(settings.Flags!=null && column.Count>0)
        {
            var spans=new LayeredSpan[column.Count];
            for(int i=0;i<spans.Length;i++)spans[i]=new(column[i].Floor,column[i].Ceiling,column[i].Fits,0);
            settings.Flags(col,spans);
            for(int i=0;i<spans.Length;i++)column[i].Flags=spans[i].Flags;
        }
        return column.ToArray();
    }

    // Links.

    private bool Blocked(int k,Vector3 a,Vector3 b,int layers)
    {
        Vector3 delta=b-a;
        float length=delta.Length();
        return length>.0001f && body[k].Cast(Jv(a+Vector3.UnitY*(settings.Skin+profiles[k].Height*.5f)),
            Jv(delta/length),length,layers,out _);
    }

    private bool ClearPath(int k,Vector3 a,Vector3 b,bool climb,int layers,out bool lifted)
    {
        lifted=false;
        if(!climb && !Blocked(k,a,b,layers) && !Blocked(k,b,a,layers))return true;
        // Try the same path a stepping motor takes: lift on the low side, clear the lip, settle.
        // The vertical sweeps reject climbing onto the deck from directly underneath it.
        lifted=true;
        float top=Math.Max(a.Y,b.Y)+.065f;
        Vector3 highA=new(a.X,top,a.Z),highB=new(b.X,top,b.Z);
        return !Blocked(k,a,highA,layers) && !Blocked(k,b,highB,layers) &&
               !Blocked(k,highA,highB,layers) && !Blocked(k,highB,highA,layers);
    }

    // Look for intermediate supports on the candidate layer; a pair of nearby end heights alone would
    // bridge a void or allow crossing from a tunnel floor to the roof. Determine risers separately from ramps.
    private bool GroundBetween(Vector3 a,Vector3 b,out float riser)
    {
        float previous=a.Y;riser=0;
        for(int j=1;j<=4;j++)
        {
            float t=j*.25f;
            var p=Vector3.Lerp(a,b,t);
            float y=b.Y;
            if(j<4)
            {
                int count=VerticalHits(p.X,p.Z,true);
                float error=float.PositiveInfinity;
                for(int i=0;i<count;i++)
                {
                    var hit=hits.Value[i];
                    float diff=Math.Abs(hit.point.y-p.Y);
                    if(hit.normal.y>=settings.MinNormal && diff<error){error=diff;y=hit.point.y;}
                }
                // A discrete stair/ledge can be flat until the endpoint; a hole has no nearby support.
                if(error>Math.Max(.24f,Math.Abs(b.Y-a.Y))+.06f)return false;
            }
            riser=Math.Max(riser,Math.Abs(y-previous));previous=y;
        }
        return true;
    }

    // Climbers can scale high faces. Require a real, continuously exposed wall
    // next to the lower centre: a suspended deck across a void is never a climbable wall.
    private bool HasClimbFace(Vector3 a,Vector3 b)
    {
        if(a.Y>b.Y)(a,b)=(b,a);
        var horizontal=new Vector3(b.X-a.X,0,b.Z-a.Z);
        float length=horizontal.Length(),height=b.Y-a.Y;
        var toward=Jv(horizontal/length);
        int samples=(int)MathF.Ceiling(height/.30f);
        float previousDistance=-1;
        for(int i=0;i<samples;i++)
        {
            var at=Jv(a+Vector3.UnitY*(height*(i+.5f)/samples));
            if(!world.Raycast(at,toward,out var hit,length+.02f,settings.WalkLayers,J.QueryTriggerInteraction.Ignore) ||
               hit.normal.y>settings.MinNormal || J.Vector3.Dot(hit.normal,toward)>-.4f)return false;
            if(previousDistance>=0 && Math.Abs(hit.distance-previousDistance)>.35f)return false;
            previousDistance=hit.distance;
        }
        return true;
    }

    private int GateMask(Vector3 a,Vector3 b)
    {
        var max=Vector3.Max(a,b)+gateReachHigh;
        var min=Vector3.Min(a,b)-gateReachLow;
        int mask=0;
        foreach(var g in settings.Gates)if(max.X>=g.Min.X && min.X<=g.Max.X && max.Y>=g.Min.Y &&
            min.Y<=g.Max.Y && max.Z>=g.Min.Z && min.Z<=g.Max.Z)mask|=1<<g.Group;
        return mask;
    }

    private Link Connect(Node[][] columns,int col,int col2,int fromIndex,int toIndex,int to)
    {
        var n=columns[col][fromIndex];var m=columns[col2][toIndex];
        int eligible=n.Fits&m.Fits;
        var a=Center(col,n.Floor);var b=Center(col2,m.Floor);
        if(eligible==0)return default;
        bool wall=Math.Abs(a.Y-b.Y)>settings.MantleHeight;
        if(wall && !HasClimbFace(a,b) || !GroundBetween(a,b,out float rise))return default;
        float quarterLength=Vector2.Distance(new(a.X,a.Z),new(b.X,b.Z))*.25f;
        int near=GateMask(a,b);
        byte mask=0,climbs=0;ulong gates=0;
        for(int k=0;k<profiles.Length;k++)
        {
            byte bit=(byte)(1<<k);if((eligible&bit)==0)continue;
            bool climb=wall || rise>Math.Max(profiles[k].Step,quarterLength*1.16f)+.045f;
            a.Y=n.Feet[k];b.Y=m.Feet[k];
            if(!ClearPath(k,a,b,climb,settings.WalkLayers,out bool lifted))continue;
            mask|=bit;if(climb)climbs|=bit;
            for(int g=1;g<=7;g++)
            {
                if((near&(1<<g))==0)continue;
                int layers=gateLayers[g];
                // A closed leaf can also surround an endpoint. Sweep across the complete height
                // there, since the graph itself contains the open-state geometry.
                bool closed=sole[k].Cast(Jv(a+Vector3.UnitY*(settings.Skin+.04f)),J.Vector3.up,profiles[k].Height-.08f,layers,out _) ||
                    sole[k].Cast(Jv(b+Vector3.UnitY*(settings.Skin+.04f)),J.Vector3.up,profiles[k].Height-.08f,layers,out _);
                if(!closed)
                {
                    if(lifted)closed=!ClearPath(k,a,b,true,layers,out _);
                    else closed=Blocked(k,a,b,layers)||Blocked(k,b,a,layers);
                }
                if(closed)gates|=(ulong)bit<<(g*8);
            }
        }
        return new(to,mask,climbs,gates);
    }

    // One-way links from a ledge down to the floor beside it, for the classes that have no other link there.
    private void Drops(Node[][] columns,int[] starts,List<Link>[] links,Func<int,bool> inBox)
    {
        int width=Width,length=settings.Length;
        (int X,int Z)[] directions=[(1,0),(-1,0),(0,1),(0,-1)];
        var added=new List<(int From,Link Link)>[width*length];
        Parallel.For(0,width*length,parallel,col=>{
            int x=col%width,z=col/width;
            List<(int,Link)> found=null;
            foreach(var d in directions)
            {
                int xx=x+d.X,zz=z+d.Z;
                if((uint)xx>=width || (uint)zz>=length)continue;
                int col2=zz*width+xx;
                if(!inBox(col) && !inBox(col2))continue;
                for(int a=starts[col];a<starts[col+1];a++)
                {
                    var n=columns[col][a-starts[col]];
                    // The floor a body falls onto: the highest one in the next column below this ledge.
                    int b=-1;
                    for(int i=starts[col2+1]-1;i>=starts[col2];i--)
                    {
                        float below=n.Floor-columns[col2][i-starts[col2]].Floor;
                        if(below<=.05f)continue;
                        if(below<=settings.DropHeight)b=i;
                        break;
                    }
                    if(b<0)continue;
                    var m=columns[col2][b-starts[col2]];
                    byte mask=0;
                    for(int k=0;k<profiles.Length;k++)
                    {
                        byte bit=(byte)(1<<k);
                        if((n.Fits&m.Fits&bit)==0 || n.Floor-m.Floor<=profiles[k].Step)continue;
                        bool linked=false;
                        foreach(var link in links[a])if(link.To==b && (link.Mask&bit)!=0){linked=true;break;}
                        if(linked)continue;
                        // Walk off the ledge at its own height, then fall straight down onto the floor below.
                        var top=Center(col,n.Feet[k]);var bottom=Center(col2,m.Feet[k]);
                        var over=new Vector3(bottom.X,top.Y+.065f,bottom.Z);
                        if(!Blocked(k,top+Vector3.UnitY*.065f,over,settings.WalkLayers) && !Blocked(k,over,bottom,settings.WalkLayers))mask|=bit;
                    }
                    if(mask!=0)(found??=[]).Add((a,new(b,mask,0,0)));
                }
            }
            added[col]=found;
        });
        // A pair other classes already link keeps one link: the dropping classes join its mask.
        foreach(var list in added)
            if(list!=null)foreach(var (a,link) in list)
            {
                int same=links[a].FindIndex(l=>l.To==link.To);
                if(same<0)links[a].Add(link);
                else links[a][same]=links[a][same] with{Mask=(byte)(links[a][same].Mask|link.Mask)};
            }
    }

    // Numbers spans, keeps the previous grid's links away from the changed box [x0,x1]x[z0,z1] and
    // bakes the links that touch it. Without a previous grid every link is baked.
    private LayeredNavigationGrid Assemble(Node[][] columns,LayeredNavigationGrid previous,int x0,int z0,int x1,int z1)
    {
        int width=Width,length=settings.Length,n=width*length;
        bool InBox(int c)=>previous==null || c%width>=x0 && c%width<=x1 && c/width>=z0 && c/width<=z1;
        var starts=new int[n+1];
        for(int c=0;c<n;c++)
        {
            if(columns[c]==null)
            {
                // Untouched column: the previous grid's spans, which keep their standing heights out of any new link.
                int count=previous.EndNode(c)-previous.FirstNode(c);
                var kept=new Node[count];
                for(int i=0;i<count;i++)
                {
                    int node=previous.FirstNode(c)+i;
                    kept[i]=new Node(previous.Floors[node],previous.Ceilings[node],0,profiles.Length)
                        {Fits=previous.Occupancy[node],Flags=previous.SpanFlags[node]};
                }
                columns[c]=kept;
            }
            starts[c+1]=starts[c]+columns[c].Length;
        }
        int total=starts[n];
        var links=new List<Link>[total];
        for(int i=0;i<total;i++)links[i]=new(8);
        if(previous!=null)
        {
            var gates=new ulong[previous.EdgeCount];
            foreach(var gate in previous.Gates)gates[gate.Edge]|=(ulong)gate.Blocked<<(gate.Group*8);
            // Keep orthogonal links with neither column in the box and diagonals whose 2x2 block avoids it.
            for(int node=0;node<previous.NodeCount;node++)
            {
                int c=previous.Column[node],local=node-previous.FirstNode(c);
                for(int e=previous.Edges[node];e<previous.Edges[node+1];e++)
                {
                    var link=previous.Links[e];
                    int c2=previous.Column[link.To];
                    if(InBox(c) || InBox(c2) || InBox(c%width+c2/width*width) || InBox(c2%width+c/width*width))continue;
                    int to=starts[c2]+link.To-previous.FirstNode(c2);
                    links[starts[c]+local].Add(new(to,link.Profiles,link.Climb,gates[e]));
                }
            }
        }
        void Pass((int X,int Z)[] directions,bool diagonal)
        {
            // Each work item owns its forward edges. Transpose only after workers have joined.
            var added=new List<(int From,Link Link)>[n];
            Parallel.For(0,n,parallel,col=>{
                int x=col%width,z=col/width;
                List<(int,Link)> edges=null;
                foreach(var d in directions)
                {
                    int xx=x+d.X,zz=z+d.Z;
                    if((uint)xx>=width || (uint)zz>=length)continue;
                    int col2=zz*width+xx,side1=z*width+xx,side2=zz*width+x;
                    if(!(InBox(col) || InBox(col2) || diagonal && (InBox(side1) || InBox(side2))))continue;
                    for(int a=starts[col];a<starts[col+1];a++)
                    for(int b=starts[col2];b<starts[col2+1];b++)
                    {
                        var link=Connect(columns,col,col2,a-starts[col],b-starts[col2],b);
                        if(diagonal && link.Mask!=0)
                        {
                            // Each side of a corner must reach *this particular* destination span.
                            // Looking up only the adjacent column would cut across stacked bridges.
                            byte Via(int middle,int group)
                            {
                                byte reachable=0;
                                foreach(var ab in links[a])
                                {
                                    if(ab.To<starts[middle] || ab.To>=starts[middle+1])continue;
                                    foreach(var bc in links[ab.To])if(bc.To==b)
                                    {
                                        int blocked=group==0?0:ab.Gate(group)|bc.Gate(group);
                                        reachable|=(byte)(ab.Mask&bc.Mask&~blocked);
                                    }
                                }
                                return reachable;
                            }
                            byte mask=(byte)(link.Mask&Via(side1,0)&Via(side2,0));
                            ulong gates=0;
                            for(int g=1;g<=7;g++)
                            {
                                byte blocked=link.Gate(g);
                                if((gateGroups&(1<<g))!=0)blocked|=(byte)~(Via(side1,g)&Via(side2,g));
                                gates|=(ulong)(byte)(mask&blocked)<<(g*8);
                            }
                            link=new(b,mask,(byte)(mask&link.Climb),gates);
                        }
                        if(link.Mask!=0)(edges??=[]).Add((a,link));
                    }
                }
                added[col]=edges;
            });
            foreach(var list in added)
                if(list!=null)
                    foreach(var (a,link) in list){links[a].Add(link);links[link.To].Add(link with{To=a});}
        }
        Pass([(1,0),(0,1)],false);Pass([(1,1),(-1,1)],true);
        if(settings.DropHeight>0)Drops(columns,starts,links,InBox);

        var spans=new LayeredSpan[total];
        int index=0;
        foreach(var column in columns)foreach(var node in column)spans[index++]=new(node.Floor,node.Ceiling,node.Fits,node.Flags);
        var edgeStarts=new int[total+1];
        var edgeList=new List<LayeredEdge>(links.Sum(l=>l.Count));
        var guards=new List<LayeredGate>();
        for(int node=0;node<total;node++)
        {
            edgeStarts[node]=edgeList.Count;
            foreach(var edge in links[node].OrderBy(e=>e.To))
            {
                for(int g=1;g<=7;g++)
                    if(edge.Gate(g)!=0)guards.Add(new(edgeList.Count,(byte)g,edge.Gate(g)));
                edgeList.Add(new(edge.To,edge.Mask,edge.Climb));
            }
        }
        edgeStarts[total]=edgeList.Count;
        return new(width,length,settings.CellSize,settings.Origin,profiles.Length,starts,spans,edgeStarts,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edgeList),System.Runtime.InteropServices.CollectionsMarshal.AsSpan(guards));
    }
}
