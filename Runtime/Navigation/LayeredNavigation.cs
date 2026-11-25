using System.Numerics;
using RangeWorkers=CodexEngine.Runtime.RangeWorkers;

namespace CodexEngine.Navigation;

/// <summary>
/// Routes the body classes of one group of agents toward a shared target over a <see cref="LayeredNavigationGrid"/>.
/// <see cref="Rebuild"/> integrates one flow field per active class. Afterwards any number of agents read
/// <see cref="Next"/> and <see cref="RouteCost"/> in parallel without locks. Grids are shared; routers are not.
/// </summary>
public sealed class LayeredNavigation
{
    public LayeredNavigationGrid Grid{get;private set;}
    /// <summary>How far, in cells, a goal moves to find a span its body class fits on.</summary>
    public int GoalSearchCells{get;init;}=4;
    /// <summary>A moved goal stays within this height of the target's floor, so it never crosses a ceiling.</summary>
    public float GoalStoreyTolerance{get;init;}=1.5f;
    /// <summary>
    /// How far, in cells, a goal moves off a span agents cannot walk to (a ledge reached only by jumping) to the
    /// nearest one they can at least a metre below it. Agents then gather under the target instead of finding no
    /// route at all. 0 keeps the goal where it is.
    /// </summary>
    public int ApproachSearchCells{get;init;}=24;

    private LayeredNavigationGrid.Link[] links;
    private LayeredFlowAtlas[] atlases;
    private int gateState=-1;
    private readonly LayeredFlowField[] fields;
    private readonly int[] dirty,goals;
    private readonly Action<int,int> buildRange;
    private int cachedColumn=-2,cachedSupport=-2,cachedY,cachedMask;
    // Per body class: spans agents can walk (or drop) to from the level's main walkable mass with every gate open.
    // Gates stay out of it: a target behind a shut door keeps having no route, as before.
    private readonly bool[][] approachable;

    public LayeredNavigation(LayeredNavigationGrid grid)
    {
        Grid=grid;
        fields=new LayeredFlowField[grid.ProfileCount];
        dirty=new int[grid.ProfileCount];goals=new int[grid.ProfileCount];
        approachable=new bool[grid.ProfileCount][];
        buildRange=BuildRange;
        SetOpenGates(0);
    }

    /// <summary>Bit g open means gate group g lets every body class through. Published routes reset.</summary>
    public void SetOpenGates(int openGroups)
    {
        if(!Grid.HasGates)openGroups=0;
        if(gateState==openGroups)return;
        gateState=openGroups;
        var state=Grid.State(openGroups);
        links=state.Links;atlases=state.Atlases;
        Array.Clear(fields);
        cachedColumn=-2;
    }

    /// <summary>
    /// Whether agents of a class can walk to this span from the level's main walkable mass, every gate open: the
    /// largest set of spans that all reach each other, and everything it reaches by walking, climbing or dropping.
    /// </summary>
    public bool Approachable(int node,int profile)=>node>=0 && (approachable[profile]??=FindApproachable(profile))[node];

    private bool[] FindApproachable(int profile)
    {
        int count=Grid.NodeCount,mask=1<<profile;
        var links=Grid.Links;
        // Iterative Tarjan: strongly connected components of the class's links.
        var index=new int[count];Array.Fill(index,-1);
        var low=new int[count];var component=new int[count];var onStack=new bool[count];
        var stack=new int[count];var call=new (int Node,int Next)[count];
        int counter=0,top=0,components=0,largest=-1,largestSize=0;
        for(int root=0;root<count;root++)
        {
            if(index[root]>=0 || (Grid.Occupancy[root]&mask)==0)continue;
            int depth=0;call[depth++]=(root,Grid.Edges[root]);
            index[root]=low[root]=counter++;stack[top++]=root;onStack[root]=true;
            while(depth>0)
            {
                ref var frame=ref call[depth-1];
                int v=frame.Node;
                if(frame.Next<Grid.Edges[v+1])
                {
                    ref readonly var e=ref links[frame.Next++];
                    if((e.Profiles&mask)==0)continue;
                    int w=e.To;
                    if(index[w]<0){index[w]=low[w]=counter++;stack[top++]=w;onStack[w]=true;call[depth++]=(w,Grid.Edges[w]);}
                    else if(onStack[w])low[v]=Math.Min(low[v],index[w]);
                    continue;
                }
                if(low[v]==index[v])
                {
                    int size=0,w;
                    do{w=stack[--top];onStack[w]=false;component[w]=components;size++;}while(w!=v);
                    if(size>largestSize){largestSize=size;largest=components;}
                    components++;
                }
                depth--;
                if(depth>0){int parent=call[depth-1].Node;low[parent]=Math.Min(low[parent],low[v]);}
            }
        }
        var reach=new bool[count];
        if(largest<0)return reach;
        int head=0,tail=0;
        for(int n=0;n<count;n++)if(index[n]>=0 && component[n]==largest){reach[n]=true;stack[tail++]=n;}
        while(head<tail)
        {
            int n=stack[head++];
            for(int i=Grid.Edges[n];i<Grid.Edges[n+1];i++)
            {
                ref readonly var e=ref links[i];
                if((e.Profiles&mask)!=0 && !reach[e.To]){reach[e.To]=true;stack[tail++]=e.To;}
            }
        }
        return reach;
    }

    // For a goal agents cannot walk to, the nearest span they can at least a metre below it: they gather under a
    // ledge the target jumped onto. A goal cut off at its own height (a sealed room, a split floor) keeps no route.
    private int Approach(int goal,Vector3 target,int profile)
    {
        if(ApproachSearchCells<=0 || Approachable(goal,profile))return goal;
        var reach=approachable[profile];
        int mask=1<<profile,width=Grid.Width,c=Grid.Column[goal],cx=c%width,cz=c/width,r=ApproachSearchCells,best=-1;
        float floor=Grid.Floors[goal]-1,bestDistance=float.PositiveInfinity;
        for(int z=Math.Max(0,cz-r);z<=Math.Min(Grid.Length-1,cz+r);z++)
        for(int x=Math.Max(0,cx-r);x<=Math.Min(width-1,cx+r);x++)
        {
            int n=Grid.Support(z*width+x,floor);
            if(n<0 || Grid.Floors[n]>floor || !reach[n] || (Grid.Occupancy[n]&mask)==0)continue;
            var at=Grid.CenterNode(n);
            float d=(at.X-target.X)*(at.X-target.X)+(at.Z-target.Z)*(at.Z-target.Z);
            if(d<bestDistance){bestDistance=d;best=n;}
        }
        return best>=0?best:goal;
    }
    public int OpenGates=>gateState;

    /// <summary>
    /// Switches to a rebaked grid, such as one from <see cref="LayeredNavigationBuilder.Rebuild"/>, keeping the open gates.
    /// Span indices of the old grid mean nothing on the new one: resolve agents again. Published routes reset.
    /// </summary>
    public void SetGrid(LayeredNavigationGrid grid)
    {
        if(grid==Grid)return;
        if(grid.ProfileCount!=Grid.ProfileCount)throw new ArgumentException("The grid has other body classes.");
        Array.Clear(approachable);
        int open=gateState;
        Grid=grid;gateState=-1;
        SetOpenGates(open);
    }

    /// <summary>The span a body class stands on at these feet, or -1 if it does not fit there.</summary>
    public int Resolve(Vector3 feet,int profile)
    {
        int node=Grid.Support(feet);
        return node>=0 && (Grid.Occupancy[node]&(1<<profile))!=0?node:-1;
    }
    /// <summary>The floor under a point on its own storey, or +infinity.</summary>
    public float Height(Vector3 p)
    {
        int node=Grid.Support(p);
        return node>=0?Grid.Floors[node]:float.PositiveInfinity;
    }
    /// <summary>A neighbour reachable from a span with the current gates, or -1.</summary>
    public int Neighbor(int node,int ordinal,int profile)
    {
        ref readonly var edge=ref links[Grid.Edges[node]+ordinal];
        return (edge.Profiles&(1<<profile))!=0?edge.To:-1;
    }
    /// <summary>The goal span of the last rebuild for this class, or a negative value.</summary>
    public int Goal(int profile)=>fields[profile]?.Goal??-2;
    public int RouteCost(int node,int profile)=>fields[profile]?.Cost(node)??int.MaxValue;
    /// <summary>The next span toward the goal; the goal returns itself, and -1 means no route.</summary>
    public int Next(int node,int profile)=>fields[profile]?.Next(node)??-1;

    private int Adjacent(int node,int column,int profile,float y,out bool climb)
    {
        int best=-1;float difference=float.PositiveInfinity;climb=false;
        for(int i=Grid.Edges[node];i<Grid.Edges[node+1];i++)
        {
            ref readonly var e=ref links[i];
            if(Grid.Column[e.To]!=column || (e.Profiles&(1<<profile))==0)continue;
            float d=Math.Abs(Grid.Floors[e.To]-y);
            if(d<difference){difference=d;best=e.To;climb=(e.Climb&(1<<profile))!=0;}
        }
        return best;
    }
    /// <summary>Whether a straight walk crosses only linked spans along its own storey, with no climb on the way.</summary>
    public bool ClearWalkSegment(Vector3 from,Vector3 to,int profile)
    {
        int start=Resolve(from,profile),end=Resolve(to,profile);
        return start>=0 && end>=0 && ClearWalkSegment(from,to,profile,start,end);
    }
    /// <summary>As <see cref="ClearWalkSegment(Vector3,Vector3,int)"/> with known start and end spans.</summary>
    public bool ClearWalkSegment(Vector3 from,Vector3 to,int profile,int node,int end)
    {
        int width=Grid.Width;float cellSize=Grid.CellSize;var origin=Grid.Origin;
        int first=node,c=Grid.Column[node],last=Grid.Column[end];
        int x=c%width,z=c/width,endX=last%width,endZ=last/width;
        float dx=to.X-from.X,dz=to.Z-from.Z;
        int sx=Math.Sign(dx),sz=Math.Sign(dz);
        float incX=sx==0?float.PositiveInfinity:cellSize/Math.Abs(dx);
        float incZ=sz==0?float.PositiveInfinity:cellSize/Math.Abs(dz);
        float tx=sx==0?float.PositiveInfinity:(origin.X+(x+(sx>0?1:0))*cellSize-from.X)/dx;
        float tz=sz==0?float.PositiveInfinity:(origin.Z+(z+(sz>0?1:0))*cellSize-from.Z)/dz;
        while(x!=endX || z!=endZ)
        {
            float t=Math.Min(tx,tz);
            if(x!=endX && z!=endZ && Math.Abs(tx-tz)<1e-5f){x+=sx;z+=sz;tx+=incX;tz+=incZ;}
            else if(z==endZ || x!=endX && tx<tz){x+=sx;tx+=incX;}
            else{z+=sz;tz+=incZ;}
            int previous=node;
            node=Adjacent(previous,z*width+x,profile,float.Lerp(from.Y,to.Y,t),out bool climb);
            if(node<0 || climb && (previous!=first || node!=end))return false;
        }
        return node==end;
    }

    /// <summary>
    /// Naive local steering toward a point, without a flow field: the straight heading when a walk along it stays on
    /// linked spans for up to <paramref name="lookAhead"/> metres, else the clear heading nearest to it in 30 degree
    /// steps to either side (the <paramref name="side"/> sign is tried first), up to square to it. Zero when the body
    /// stands on no span or every heading is blocked. Short-sighted by design and costs a few segment walks: meant
    /// for secondary goals close to a body (fighting a neighbour), while far goals share the flow field.
    /// </summary>
    public Vector3 Seek(Vector3 from,Vector3 to,int profile,float lookAhead,int side=1)
    {
        int node=Resolve(from,profile);
        if(node<0)return Vector3.Zero;
        var delta=new Vector3(to.X-from.X,0,to.Z-from.Z);
        float distance=delta.Length();
        if(distance<1e-4f)return Vector3.Zero;
        var forward=delta/distance;
        float reach=Math.Min(distance,lookAhead);
        side=side<0?-1:1;
        for(int i=0;i<7;i++)
        {
            float angle=(i+1)/2*((i&1)==1?side:-side)*(MathF.PI/6);
            float cos=MathF.Cos(angle),sin=MathF.Sin(angle);
            var heading=new Vector3(forward.X*cos-forward.Z*sin,0,forward.X*sin+forward.Z*cos);
            // Straight on, a short walk that already reaches the goal's storey counts too (the goal may stand in a
            // span too tight for this body); a turned heading must keep its footing for the whole look-ahead.
            for(float length=reach;length>=reach*.4f;length*=.5f)
            {
                var probe=from+heading*length;
                probe.Y=i==0?float.Lerp(from.Y,to.Y,length/distance):from.Y;
                int end=Resolve(probe,profile);
                if(end>=0 && ClearWalkSegment(from,probe,profile,node,end))return heading;
                if(i!=0)break;
            }
        }
        return Vector3.Zero;
    }

    private int GoalNear(Vector3 target,int profile,int c,int support)
    {
        if(c<0)return -1;
        int mask=1<<profile;
        if(support>=0 && (Grid.Occupancy[support]&mask)!=0 && Grid.NeighborCount(support)>0)return support;
        // A target can stand in an aperture too small for this class. Approach its entrance
        // on the same storey. Never bind the goal to an unrelated floor across a ceiling.
        float reference=support>=0?Grid.Floors[support]:target.Y;
        int width=Grid.Width,cx=c%width,cz=c/width,closest=-1,reach=GoalSearchCells;float best=float.PositiveInfinity;
        for(int z=Math.Max(0,cz-reach);z<=Math.Min(Grid.Length-1,cz+reach);z++)
        for(int x=Math.Max(0,cx-reach);x<=Math.Min(width-1,cx+reach);x++)
        {
            int n=Grid.Support(z*width+x,reference);
            if(n<0 || (Grid.Occupancy[n]&mask)==0 || Math.Abs(Grid.Floors[n]-reference)>GoalStoreyTolerance)continue;
            float d=Vector3.DistanceSquared(Grid.CenterNode(n),new(target.X,reference,target.Z));
            if(d<best){best=d;closest=n;}
        }
        return closest;
    }

    /// <summary>
    /// Points the flow field of every class in <paramref name="activeProfileMask"/> at the target. A goal that stays
    /// on the same span costs nothing; a goal inside its region only reintegrates that region.
    /// </summary>
    public void Rebuild(Vector3 target,int activeProfileMask,RangeWorkers workers)
    {
        int column=Grid.Cell(target),support=Grid.Support(column,target.Y);
        int elevation=support<0?(int)MathF.Floor(target.Y):0;
        if(cachedColumn!=column || cachedSupport!=support || cachedY!=elevation)
        {
            cachedColumn=column;cachedSupport=support;cachedY=elevation;cachedMask=0;
        }
        int count=0,changedRegions=0;
        for(int p=0;p<fields.Length;p++)
        {
            if((activeProfileMask&(1<<p))==0)continue;
            var field=fields[p]??=new(atlases[p]);
            if((cachedMask&(1<<p))==0)
            {
                int goal=GoalNear(target,p,column,support);
                goals[p]=goal>=0?Approach(goal,target,p):goal;cachedMask|=1<<p;
            }
            if(field.Goal==goals[p])continue;
            dirty[count++]=p;
            if(field.ChangesRegion(goals[p]))changedRegions++;
        }
        // A few small local tiles cost less to integrate than waking a worker pool.
        if(count==0)return;
        if(changedRegions>=2 || count>=4)workers.Run(count,1,buildRange);
        else BuildRange(0,count);
    }
    private void BuildRange(int start,int end)
    {
        for(int i=start;i<end;i++){int p=dirty[i];fields[p].Build(goals[p]);}
    }
}
