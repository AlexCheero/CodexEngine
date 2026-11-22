using System.IO.Compression;
using System.Numerics;

namespace CodexEngine.Navigation;

/// <summary>One walkable floor in a grid column. <see cref="Profiles"/> has a bit for each body class that stands there.</summary>
public record struct LayeredSpan(float Floor,float Ceiling,byte Profiles,byte Flags);

/// <summary>
/// A directed walk link between spans of neighbouring columns, per body class. Most links come in pairs; a class
/// whose link has no partner back only travels it forward, as when it drops off a ledge.
/// </summary>
public readonly record struct LayeredEdge(int To,byte Profiles,byte Climb);

/// <summary>Body classes that cannot cross <see cref="Edge"/> while gate <see cref="Group"/> is closed.</summary>
public readonly record struct LayeredGate(int Edge,byte Group,byte Blocked);

/// <summary>
/// Multi-storey navigation on a regular X/Z grid. Each column holds the walkable floors stacked above that cell,
/// lowest first, so bridges, decks, tunnels and basements route separately. Edges link spans of the eight
/// neighbouring columns. The grid is immutable and shared: create a <see cref="LayeredNavigation"/> per user.
/// An alternative to <see cref="Runtime.NavigationMesh"/> for crowds that need flow fields across storeys.
/// </summary>
public sealed class LayeredNavigationGrid
{
    public const uint Magic=0x314E4C43; // "CLN1"
    public const int MaxProfiles=8;

    public readonly int Width,Length,ProfileCount;
    public readonly float CellSize;
    public readonly Vector3 Origin;
    /// <summary>A foot can sit this far below the floor sampled at a cell centre, as on a sloped tread.</summary>
    public readonly float SupportTolerance;
    public readonly float LowestFloor;

    internal readonly int[] Columns,Column,Edges;
    internal readonly float[] Floors,Ceilings;
    internal readonly byte[] Occupancy,SpanFlags;
    internal readonly Link[] Links;
    internal readonly LayeredGate[] Gates;
    private readonly Dictionary<int,GateState> states=[];

    // Back holds the classes that can also walk the reverse way. A class in Profiles but not in Back only goes
    // forward: a drop off a ledge. ReverseCost is the cost of that reverse walk where it exists.
    internal readonly record struct Link(int To,byte Profiles,byte Climb,ushort ReverseCost,ushort ForwardCost,byte Back);
    internal sealed class GateState(Link[] links,LayeredFlowAtlas[] atlases)
    {
        internal readonly Link[] Links=links;
        internal readonly LayeredFlowAtlas[] Atlases=atlases;
    }

    public int ColumnCount=>Width*Length;
    public int NodeCount=>Floors.Length;
    public int EdgeCount=>Links.Length;
    public bool HasGates=>Gates.Length>0;

    /// <param name="columnStarts">First span of each column, plus the span count at the end.</param>
    /// <param name="edgeStarts">First edge of each span, plus the edge count at the end.</param>
    public LayeredNavigationGrid(int width,int length,float cellSize,Vector3 origin,int profileCount,
        ReadOnlySpan<int> columnStarts,ReadOnlySpan<LayeredSpan> spans,ReadOnlySpan<int> edgeStarts,
        ReadOnlySpan<LayeredEdge> edges,ReadOnlySpan<LayeredGate> gates)
    {
        if(width<=0 || length<=0 || !(cellSize>0))throw new ArgumentException("Grid size and cell size must be positive.");
        if(profileCount is <1 or >MaxProfiles)throw new ArgumentOutOfRangeException(nameof(profileCount));
        if(columnStarts.Length!=width*length+1 || columnStarts[^1]!=spans.Length)
            throw new ArgumentException("Column starts do not match the grid and span count.");
        if(edgeStarts.Length!=spans.Length+1 || edgeStarts[^1]!=edges.Length)
            throw new ArgumentException("Edge starts do not match the span and edge counts.");
        Width=width;Length=length;CellSize=cellSize;Origin=origin;ProfileCount=profileCount;
        SupportTolerance=Math.Min(.8f,cellSize*.55f);
        int nodes=spans.Length;
        Columns=columnStarts.ToArray();Edges=edgeStarts.ToArray();Column=new int[nodes];
        for(int c=0;c<Columns.Length-1;c++)Array.Fill(Column,c,Columns[c],Columns[c+1]-Columns[c]);
        Floors=new float[nodes];Ceilings=new float[nodes];Occupancy=new byte[nodes];SpanFlags=new byte[nodes];
        LowestFloor=float.PositiveInfinity;
        for(int i=0;i<nodes;i++)
        {
            Floors[i]=spans[i].Floor;Ceilings[i]=spans[i].Ceiling;Occupancy[i]=spans[i].Profiles;SpanFlags[i]=spans[i].Flags;
            LowestFloor=Math.Min(LowestFloor,Floors[i]);
        }
        Links=new Link[edges.Length];
        for(int node=0;node<nodes;node++)
        for(int i=Edges[node];i<Edges[node+1];i++)
        {
            var e=edges[i];
            int columnDelta=Math.Abs(Column[e.To]-Column[node]);
            int length2=columnDelta==1 || columnDelta==width?10:14;
            // Reverse integration visits predecessors. Uphill travel is more expensive.
            int cost=length2+(int)MathF.Round(40*Math.Max(0,Floors[node]-Floors[e.To])/cellSize);
            int forward=length2+(int)MathF.Round(40*Math.Max(0,Floors[e.To]-Floors[node])/cellSize);
            byte back=0;
            for(int j=Edges[e.To];j<Edges[e.To+1];j++)if(edges[j].To==node)back|=edges[j].Profiles;
            Links[i]=new(e.To,e.Profiles,e.Climb,checked((ushort)cost),checked((ushort)forward),back);
        }
        Gates=gates.ToArray();
        foreach(var gate in Gates)
            if((uint)gate.Edge>=(uint)Links.Length || gate.Group>30)throw new ArgumentException("Gate edge or group out of range.");
    }

    // Spatial queries.

    /// <summary>The column under a point, or -1 outside the grid.</summary>
    public int Cell(Vector3 p)
    {
        int x=(int)MathF.Floor((p.X-Origin.X)/CellSize),z=(int)MathF.Floor((p.Z-Origin.Z)/CellSize);
        return (uint)x<Width && (uint)z<Length?z*Width+x:-1;
    }
    public Vector3 CenterNode(int node)
    {
        int c=Column[node];
        return new(Origin.X+(c%Width+.5f)*CellSize,Floors[node],Origin.Z+(c/Width+.5f)*CellSize);
    }
    /// <summary>The highest span in a column that a foot at height <paramref name="y"/> stands on, or -1.</summary>
    public int Support(int column,float y)
    {
        if(column<0)return -1;
        for(int i=Columns[column+1]-1;i>=Columns[column];i--)
            if(Floors[i]<=y+SupportTolerance)return i;
        return -1;
    }
    public int Support(Vector3 p)=>Support(Cell(p),p.Y);
    public int FirstNode(int column)=>Columns[column];
    public int EndNode(int column)=>Columns[column+1];
    public int ColumnOf(int node)=>Column[node];
    public float Floor(int node)=>Floors[node];
    public float Ceiling(int node)=>Ceilings[node];
    /// <summary>Bit mask of the body classes that stand on a span.</summary>
    public int Profiles(int node)=>Occupancy[node];
    /// <summary>Game-defined bits baked with each span.</summary>
    public byte Flags(int node)=>SpanFlags[node];
    public int NeighborCount(int node)=>Edges[node+1]-Edges[node];
    /// <summary>An edge as baked, with every gate open.</summary>
    public LayeredEdge Edge(int node,int ordinal)
    {
        ref readonly var link=ref Links[Edges[node]+ordinal];
        return new(link.To,link.Profiles,link.Climb);
    }
    public bool RequiresClimb(int from,int to,int profile)
    {
        for(int i=Edges[from];i<Edges[from+1];i++)
            if(Links[i].To==to)return (Links[i].Climb&(1<<profile))!=0;
        return false;
    }
    public ReadOnlySpan<LayeredGate> GateEdges=>Gates;

    /// <summary>Builds the routing tables for a gate state ahead of use, for example on a worker after a rebake.</summary>
    public void Prepare(int openGroups)=>State(openGroups);

    // Gate states share their edge masks and region atlases between every router on this grid.
    internal GateState State(int openGroups)
    {
        if(Gates.Length==0)openGroups=0;
        lock(states)
        {
            if(states.TryGetValue(openGroups,out var state))return state;
            var edges=Gates.Length==0?Links:(Link[])Links.Clone();
            foreach(var gate in Gates)
            {
                if((openGroups&(1<<gate.Group))!=0)continue;
                ref var link=ref edges[gate.Edge];
                link=link with {Profiles=(byte)(link.Profiles&~gate.Blocked)};
            }
            var atlases=new LayeredFlowAtlas[ProfileCount];
            Parallel.For(0,atlases.Length,new ParallelOptions{MaxDegreeOfParallelism=3},
                p=>atlases[p]=new(this,edges,p));
            states.Add(openGroups,state=new(edges,atlases));
            return state;
        }
    }

    // Storage: little-endian, optionally gzip-compressed (.gz).

    public static LayeredNavigationGrid Load(string path)
    {
        using var file=File.OpenRead(path);
        if(!path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase))return Read(file);
        using var data=new GZipStream(file,CompressionMode.Decompress);
        return Read(data);
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using(var file=File.Create(path+".tmp"))
        {
            if(path.EndsWith(".gz",StringComparison.OrdinalIgnoreCase))
            {
                using var data=new GZipStream(file,CompressionLevel.SmallestSize);
                Write(data);
            }
            else Write(file);
        }
        File.Move(path+".tmp",path,true);
    }

    public static LayeredNavigationGrid Read(Stream stream)
    {
        using var r=new BinaryReader(stream,System.Text.Encoding.UTF8,true);
        if(r.ReadUInt32()!=Magic)throw new InvalidDataException("Not a layered navigation grid. Rebake it.");
        int width=r.ReadInt32(),length=r.ReadInt32();
        float cell=r.ReadSingle();
        var origin=new Vector3(r.ReadSingle(),r.ReadSingle(),r.ReadSingle());
        int profiles=r.ReadInt32(),nodes=r.ReadInt32(),edgeCount=r.ReadInt32();
        var columns=new int[width*length+1];
        for(int c=0;c<columns.Length;c++)columns[c]=r.ReadInt32();
        var spans=new LayeredSpan[nodes];
        for(int i=0;i<nodes;i++)spans[i]=new(r.ReadSingle(),r.ReadSingle(),r.ReadByte(),r.ReadByte());
        var starts=new int[nodes+1];
        for(int i=0;i<starts.Length;i++)starts[i]=r.ReadInt32();
        var edges=new LayeredEdge[edgeCount];
        for(int i=0;i<edges.Length;i++)edges[i]=new(r.ReadInt32(),r.ReadByte(),r.ReadByte());
        var gates=new LayeredGate[r.ReadInt32()];
        for(int i=0;i<gates.Length;i++)gates[i]=new(r.ReadInt32(),r.ReadByte(),r.ReadByte());
        return new(width,length,cell,origin,profiles,columns,spans,starts,edges,gates);
    }
    public void Write(Stream stream)
    {
        using var w=new BinaryWriter(stream,System.Text.Encoding.UTF8,true);
        w.Write(Magic);w.Write(Width);w.Write(Length);w.Write(CellSize);
        w.Write(Origin.X);w.Write(Origin.Y);w.Write(Origin.Z);
        w.Write(ProfileCount);w.Write(NodeCount);w.Write(EdgeCount);
        foreach(int start in Columns)w.Write(start);
        for(int i=0;i<NodeCount;i++){w.Write(Floors[i]);w.Write(Ceilings[i]);w.Write(Occupancy[i]);w.Write(SpanFlags[i]);}
        foreach(int start in Edges)w.Write(start);
        foreach(var link in Links){w.Write(link.To);w.Write(link.Profiles);w.Write(link.Climb);}
        w.Write(Gates.Length);
        foreach(var gate in Gates){w.Write(gate.Edge);w.Write(gate.Group);w.Write(gate.Blocked);}
    }
}
