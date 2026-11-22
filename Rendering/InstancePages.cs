using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace CodexEngine.Rendering;

/// <summary>
/// Instances that pile up over a session and then never move (settled shells, rubble, decals), kept in fixed GPU
/// pages. A page holds instances of one key (usually a mesh) in one cubic cell of space, so a pass draws one call per
/// visible page and culls them by cell. An instance is written to its page's vertex buffer once, when it is added;
/// nothing is copied or uploaded per frame. Pages never shrink: the set grows as long as the session does.
/// </summary>
public sealed class InstancePages<TKey,TInstance> : IDisposable where TKey : notnull where TInstance : unmanaged
{
    public sealed class Page
    {
        public readonly TKey Key;
        public readonly VertexBuffer Buffer;
        public int Count { get; internal set; }
        /// <summary>Holds every instance of the page.</summary>
        public BoundingSphere Bounds { get; internal set; }
        internal Page(TKey key,VertexBuffer buffer){Key=key;Buffer=buffer;}
    }

    private readonly GraphicsDevice gpu;
    private readonly VertexDeclaration declaration;
    private readonly float cellSize;
    private readonly int capacity;
    private readonly List<Page> pages=[];
    private readonly Dictionary<(int X,int Y,int Z,TKey Key),Page> open=[];
    private readonly TInstance[] one=new TInstance[1];

    /// <param name="declaration">The instance stream's layout; its stride is the size of <typeparamref name="TInstance"/>.</param>
    /// <param name="cellSize">Edge of the cubic cells pages are grouped by, in world units.</param>
    /// <param name="pageCapacity">Instances per page, so per draw.</param>
    public InstancePages(GraphicsDevice gpu,VertexDeclaration declaration,float cellSize,int pageCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCapacity,1);
        this.gpu=gpu;this.declaration=declaration;this.cellSize=cellSize;capacity=pageCapacity;
    }

    public IReadOnlyList<Page> Pages=>pages;
    /// <summary>Instances in every page.</summary>
    public int Count{get;private set;}

    /// <summary>Writes one instance into the open page of its key and cell, starting a page when that one is full.</summary>
    /// <param name="bounds">The instance's extent in the world; its centre chooses the cell.</param>
    public void Add(TKey key,in TInstance instance,BoundingSphere bounds)
    {
        var cell=((int)MathF.Floor(bounds.Center.X/cellSize),(int)MathF.Floor(bounds.Center.Y/cellSize),(int)MathF.Floor(bounds.Center.Z/cellSize),key);
        if(!open.TryGetValue(cell,out var page))
        {
            page=new Page(key,new VertexBuffer(gpu,declaration,capacity,BufferUsage.WriteOnly));
            pages.Add(page);open.Add(cell,page);
        }
        one[0]=instance;
        page.Buffer.SetData(page.Count*declaration.VertexStride,one,0,1,declaration.VertexStride);
        page.Bounds=page.Count==0?bounds:BoundingSphere.CreateMerged(page.Bounds,bounds);
        page.Count++;Count++;
        if(page.Count==capacity)open.Remove(cell);
    }

    /// <summary>The pages a view may see: inside the frustum, within <paramref name="maxDistance"/> of the eye when it
    /// is above zero, and not hidden behind the frame's occluders when an occlusion buffer is given.</summary>
    public void Select(List<Page> visible,BoundingFrustum frustum,Vector3 eye=default,float maxDistance=0,OcclusionBuffer occlusion=null)
    {
        visible.Clear();
        foreach(var page in pages)
        {
            var bounds=page.Bounds;
            if(maxDistance>0 && Vector3.Distance(bounds.Center,eye)-bounds.Radius>maxDistance)continue;
            if(!frustum.Intersects(bounds) || occlusion!=null && occlusion.Hidden(bounds))continue;
            visible.Add(page);
        }
    }

    public void Dispose()
    {
        foreach(var page in pages)page.Buffer.Dispose();
        pages.Clear();open.Clear();Count=0;
    }
}
