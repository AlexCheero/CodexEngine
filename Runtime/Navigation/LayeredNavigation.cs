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

    private LayeredNavigationGrid.Link[] links;
    private LayeredFlowAtlas[] atlases;
    private int gateState=-1;
    private readonly LayeredFlowField[] fields;
    private readonly int[] dirty,goals;
    private readonly Action<int,int> buildRange;
    private int cachedColumn=-2,cachedSupport=-2,cachedY,cachedMask;

    public LayeredNavigation(LayeredNavigationGrid grid)
    {
        Grid=grid;
        fields=new LayeredFlowField[grid.ProfileCount];
        dirty=new int[grid.ProfileCount];goals=new int[grid.ProfileCount];
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
    public int OpenGates=>gateState;

    /// <summary>
    /// Switches to a rebaked grid, such as one from <see cref="LayeredNavigationBuilder.Rebuild"/>, keeping the open gates.
    /// Span indices of the old grid mean nothing on the new one: resolve agents again. Published routes reset.
    /// </summary>
    public void SetGrid(LayeredNavigationGrid grid)
    {
        if(grid==Grid)return;
        if(grid.ProfileCount!=Grid.ProfileCount)throw new ArgumentException("The grid has other body classes.");
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
                goals[p]=GoalNear(target,p,column,support);cachedMask|=1<<p;
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
