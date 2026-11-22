using System.Numerics;

namespace CodexEngine.Navigation;

// Regions are connected components within 12x12 X/Z tiles, separated by body class
// and storey. A portal groups all legal crossings from one region to its neighbor.
// Regions join only over two-way links. A one-way link (a drop off a ledge) is always
// a portal or a link inside one region, and integration follows it against its direction.
// Moving the target a cell only reintegrates its own region. Leaving that region
// also updates the much smaller portal graph. Each local portal flow is immutable.
internal sealed class LayeredFlowAtlas
{
    private const int Tile=12;
    internal readonly LayeredNavigationGrid Grid;
    internal readonly LayeredNavigationGrid.Link[] Links;
    internal readonly int Mask;
    internal readonly int[] Region,Local;
    // One-way links into each span for this class: InFrom[InStart[n]..InStart[n+1]) with their costs.
    private readonly int[] inStart,inFrom;
    private readonly int[] inCost;
    internal readonly Sector[] Sectors;
    internal readonly int Largest;
    internal sealed class Sector(int[] nodes)
    {
        internal readonly int[] Nodes=nodes;
        internal Portal[] Exits;
        // Portals of neighbouring regions that lead here, in the order of this region's own exits back to them,
        // then those with no way back.
        internal Portal[] Entrances;
        internal Vector3 Center;
    }
    internal sealed class Portal(int region,int size)
    {
        internal readonly int To=region;
        internal int From;
        internal readonly int[] Next=new int[size];
        internal readonly int[] Distance=new int[size];
        internal int TransitCost;
        internal Portal Reverse;
    }

    internal LayeredFlowAtlas(LayeredNavigationGrid grid,LayeredNavigationGrid.Link[] links,int profile)
    {
        Grid=grid;Links=links;Mask=1<<profile;
        int width=grid.Width;float cellSize=grid.CellSize;
        int count=grid.NodeCount;
        Region=new int[count];Array.Fill(Region,-1);
        Local=new int[count];
        inStart=new int[count+1];
        for(int node=0;node<count;node++)
        for(int e=grid.Edges[node];e<grid.Edges[node+1];e++)
            if((links[e].Profiles&~links[e].Back&Mask)!=0)inStart[links[e].To+1]++;
        for(int node=0;node<count;node++)inStart[node+1]+=inStart[node];
        inFrom=new int[inStart[count]];inCost=new int[inStart[count]];
        var fill=inStart[..count];
        for(int node=0;node<count;node++)
        for(int e=grid.Edges[node];e<grid.Edges[node+1];e++)
            if((links[e].Profiles&~links[e].Back&Mask)!=0){int at=fill[links[e].To]++;inFrom[at]=node;inCost[at]=links[e].ForwardCost;}
        var queue=new int[count];List<Sector> sectors=[];
        for(int seed=0;seed<count;seed++)
        {
            if(Region[seed]>=0 || (grid.Occupancy[seed]&Mask)==0)continue;
            int tileX=grid.Column[seed]%width/Tile,tileZ=grid.Column[seed]/width/Tile;
            int head=0,tail=1,region=sectors.Count;
            Region[seed]=region;queue[0]=seed;
            Vector3 center=default;
            while(head<tail)
            {
                int node=queue[head];Local[node]=head++;
                int column=grid.Column[node];
                center+=new Vector3(column%width,grid.Floors[node]/cellSize,column/width);
                for(int e=grid.Edges[node];e<grid.Edges[node+1];e++)
                {
                    ref readonly var edge=ref links[e];
                    int to=edge.To,c=grid.Column[to];
                    if((edge.Profiles&edge.Back&Mask)==0 || Region[to]>=0 || c%width/Tile!=tileX || c/width/Tile!=tileZ)continue;
                    Region[to]=region;queue[tail++]=to;
                }
            }
            sectors.Add(new(queue.AsSpan(0,tail).ToArray()){Center=center/tail});
            Largest=Math.Max(Largest,tail);
        }
        Sectors=sectors.ToArray();
        Dictionary<int,Portal> exits=[];
        var frontier=new PriorityQueue<int,int>();
        for(int region=0;region<Sectors.Length;region++)
        {
            var sector=Sectors[region];exits.Clear();
            foreach(int node in sector.Nodes)
            for(int e=grid.Edges[node];e<grid.Edges[node+1];e++)
            {
                ref readonly var edge=ref links[e];
                if((edge.Profiles&Mask)==0)continue;
                int other=Region[edge.To];
                if(other==region)continue;
                if(!exits.TryGetValue(other,out var portal))
                {
                    exits.Add(other,portal=new(other,sector.Nodes.Length){From=region});
                    Array.Fill(portal.Distance,int.MaxValue);
                }
                // Pick a downhill crossing before a climb when both reach this neighbor.
                int local=Local[node];
                int crossing=edge.ForwardCost;
                if(crossing<portal.Distance[local]){portal.Distance[local]=crossing;portal.Next[local]=edge.To;}
            }
            sector.Exits=exits.Values.ToArray();
            foreach(var portal in sector.Exits)
            {
                int cheapest=int.MaxValue;
                for(int i=0;i<sector.Nodes.Length;i++){
                    int distance=portal.Distance[i];if(distance==int.MaxValue)continue;
                    frontier.Enqueue(sector.Nodes[i],distance);cheapest=Math.Min(cheapest,distance);
                }
                portal.TransitCost=cheapest+(int)(10*Vector3.Distance(sector.Center,Sectors[portal.To].Center));
                Integrate(region,portal.Next,portal.Distance,frontier);
            }
        }
        for(int r=0;r<Sectors.Length;r++)
        foreach(var portal in Sectors[r].Exits)
        foreach(var reverse in Sectors[portal.To].Exits)
            if(reverse.To==r){portal.Reverse=reverse;break;}
        var entrances=new List<Portal>[Sectors.Length];
        for(int r=0;r<Sectors.Length;r++)
        {
            var list=entrances[r]=[];
            foreach(var exit in Sectors[r].Exits)if(exit.Reverse!=null)list.Add(exit.Reverse);
        }
        for(int r=0;r<Sectors.Length;r++)
        foreach(var portal in Sectors[r].Exits)
            if(portal.Reverse==null)entrances[portal.To].Add(portal);
        for(int r=0;r<Sectors.Length;r++)Sectors[r].Entrances=entrances[r].ToArray();
    }

    internal void Integrate(int region,int[] next,int[] distances,PriorityQueue<int,int> frontier)
    {
        while(frontier.TryDequeue(out int node,out int cost))
        {
            if(distances[Local[node]]!=cost)continue;
            for(int e=Grid.Edges[node];e<Grid.Edges[node+1];e++)
            {
                ref readonly var edge=ref Links[e];int to=edge.To;
                if((edge.Profiles&edge.Back&Mask)==0 || Region[to]!=region)continue;
                int at=Local[to],d=cost+edge.ReverseCost;
                if(d>=distances[at])continue;
                distances[at]=d;next[at]=node;frontier.Enqueue(to,d);
            }
            // Spans that only drop onto this one.
            for(int i=inStart[node];i<inStart[node+1];i++)
            {
                int from=inFrom[i];
                if(Region[from]!=region)continue;
                int at=Local[from],d=cost+inCost[i];
                if(d>=distances[at])continue;
                distances[at]=d;next[at]=node;frontier.Enqueue(from,d);
            }
        }
    }
}

// The route of one body class toward one goal span: a local flow inside the goal's region and
// precomputed portal flows everywhere else.
internal sealed class LayeredFlowField(LayeredFlowAtlas atlas)
{
    internal int Goal=-2;
    private int region=-2;
    private readonly int[] remaining=new int[atlas.Sectors.Length];
    private readonly LayeredFlowAtlas.Portal[] exits=new LayeredFlowAtlas.Portal[atlas.Sectors.Length];
    private readonly int[] localNext=new int[atlas.Largest],localCost=new int[atlas.Largest];
    private readonly PriorityQueue<int,int> queue=new();

    internal bool ChangesRegion(int goal)=>goal>=0 && region!=atlas.Region[goal];

    internal void Build(int goal)
    {
        Goal=goal;
        if(goal<0){region=-1;return;}
        int target=atlas.Region[goal];
        if(region!=target)
        {
            region=target;Array.Fill(remaining,int.MaxValue);
            remaining[region]=0;queue.Enqueue(region,0);
            while(queue.TryDequeue(out int sector,out int cost))
            {
                if(cost!=remaining[sector])continue;
                foreach(var incoming in atlas.Sectors[sector].Entrances)
                {
                    int neighbor=incoming.From;
                    int d=cost+incoming.TransitCost;
                    if(d>=remaining[neighbor])continue;
                    remaining[neighbor]=d;exits[neighbor]=incoming;queue.Enqueue(neighbor,d);
                }
            }
        }
        Array.Fill(localCost,int.MaxValue,0,atlas.Sectors[region].Nodes.Length);
        int local=atlas.Local[goal];
        localCost[local]=0;localNext[local]=goal;queue.Enqueue(goal,0);
        atlas.Integrate(region,localNext,localCost,queue);
    }
    internal int Next(int node)
    {
        int r=atlas.Region[node];
        if(r<0 || region<0 || remaining[r]==int.MaxValue)return -1;
        return r==region?localNext[atlas.Local[node]]:exits[r].Next[atlas.Local[node]];
    }
    internal int Cost(int node)
    {
        int r=atlas.Region[node];
        if(r<0 || region<0 || remaining[r]==int.MaxValue)return int.MaxValue;
        return r==region?localCost[atlas.Local[node]]:remaining[r]*1024+exits[r].Distance[atlas.Local[node]];
    }
}
