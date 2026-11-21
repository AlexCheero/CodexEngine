using CodexECS;
using Microsoft.Xna.Framework;

namespace CodexEngine.Rendering;

[Flags]
public enum VisibilityTraits : byte
{
    None = 0,
    /// <summary>May cast a shadow: it is tested against the frame's caster volume too.</summary>
    Caster = 1,
    /// <summary>Moves about inside its cell: it is seen and casts whenever the cell does, without a test of its own.</summary>
    Unbounded = 2,
    /// <summary>Left out of every list until the trait is cleared again.</summary>
    Hidden = 4
}

/// <summary>One thing that stands still in a level, as the grid needs to know it.</summary>
public struct VisibilityItem
{
    public BoundingSphere Bounds;
    /// <summary>Seen only within this distance of the eye. Zero for any distance.</summary>
    public float MaxDistance;
    /// <summary>
    /// Where the item changes to a coarser level of detail: its radius on screen, in pixels, below which the
    /// first, second and third coarser level takes over. Zero for a level it does not have.
    /// </summary>
    public Vector3 CoarserBelow;
    public VisibilityTraits Traits;
}

/// <summary>What one view asks of the grid.</summary>
public struct VisibilityView
{
    public BoundingFrustum Frustum;
    public Vector3 Eye;
    /// <summary>
    /// Pixels a sphere of radius one spans across its radius at distance one: half the view's height in pixels
    /// times the projection's vertical scale (its M22). Zero skips everything that goes by size on screen.
    /// </summary>
    public float PixelScale;
    /// <summary>An item whose radius on screen is under this many pixels is left out. Zero keeps every size.</summary>
    public float MinPixels;
    /// <summary>The frame's occluders, already built for this view, or null.</summary>
    public OcclusionBuffer Occlusion;
    /// <summary>Whether a sphere can cast a shadow the frame will show, or null for a view without shadow casters.</summary>
    public Func<BoundingSphere, bool> Casts;
}

/// <summary>
/// The things of a level that stand still, sorted into square cells over the ground, and for each view the lists
/// of what it sees and what casts shadows into it. <see cref="Cull"/> tests cells first and their items after, on
/// the job threads: against the view's frustum, by distance, by size on screen and against the frame's occluders.
/// A seen item also gets its level of detail from its size on screen. The lists are per cell and in the order the
/// items were given, on any number of threads.
/// </summary>
public sealed class VisibilityGrid
{
    sealed class Cell(BoundingSphere bounds, int[] items)
    {
        public readonly BoundingSphere Bounds = bounds;
        public readonly int[] Items = items, Seen = new int[items.Length], Casters = new int[items.Length];
        public readonly byte[] Levels = new byte[items.Length], CasterLevels = new byte[items.Length];
        public int SeenCount, CasterCount, TooSmall, Occluded;
    }

    readonly VisibilityItem[] items;
    readonly Cell[] cells;
    readonly Action<int, int> cull;
    VisibilityView view;

    public int Cells => cells.Length;
    public int Items => items.Length;
    /// <summary>What the last <see cref="Cull"/> saw, and what it left out for its size on screen or behind occluders.</summary>
    public int SeenCount { get; private set; }
    public int TooSmallCount { get; private set; }
    public int OccludedCount { get; private set; }

    /// <param name="cellSize">The side of a cell, in world units along X and Z.</param>
    public VisibilityGrid(VisibilityItem[] items, float cellSize = 40)
    {
        this.items = items;
        cells = Enumerable.Range(0, items.Length)
            .GroupBy(i => ((int)MathF.Floor(items[i].Bounds.Center.X / cellSize), (int)MathF.Floor(items[i].Bounds.Center.Z / cellSize)))
            .Select(group =>
            {
                var entries = group.ToArray();
                var bounds = items[entries[0]].Bounds;
                foreach (int index in entries) bounds = BoundingSphere.CreateMerged(bounds, items[index].Bounds);
                return new Cell(bounds, entries);
            }).ToArray();
        cull = CullCells;
    }

    public BoundingSphere CellBounds(int cell) => cells[cell].Bounds;
    /// <summary>Every item of a cell, seen or not.</summary>
    public ReadOnlySpan<int> ItemsIn(int cell) => cells[cell].Items;
    /// <summary>The items of a cell the last <see cref="Cull"/> saw.</summary>
    public ReadOnlySpan<int> SeenIn(int cell) => cells[cell].Seen.AsSpan(0, cells[cell].SeenCount);
    /// <summary>The level of detail of each item in <see cref="SeenIn"/>: 0 the finest.</summary>
    public ReadOnlySpan<byte> LevelsIn(int cell) => cells[cell].Levels.AsSpan(0, cells[cell].SeenCount);
    /// <summary>The items of a cell that may cast into the view of the last <see cref="Cull"/>.</summary>
    public ReadOnlySpan<int> CastersIn(int cell) => cells[cell].Casters.AsSpan(0, cells[cell].CasterCount);
    /// <summary>
    /// The level of detail of each item in <see cref="CastersIn"/>, by its size as the eye would see it: a caster
    /// the view also sees then casts from the same triangles it is drawn with.
    /// </summary>
    public ReadOnlySpan<byte> CasterLevelsIn(int cell) => cells[cell].CasterLevels.AsSpan(0, cells[cell].CasterCount);

    /// <summary>Hides an item from every list, or shows it again: for scenery that can be destroyed.</summary>
    public void Hide(int item, bool hidden)
    {
        if (hidden) items[item].Traits |= VisibilityTraits.Hidden;
        else items[item].Traits &= ~VisibilityTraits.Hidden;
    }

    /// <summary>
    /// Fills the lists for one view. <paramref name="jobs"/> shares the cells between its threads once there are
    /// <paramref name="minimumPerThread"/> cells for each.
    /// </summary>
    public void Cull(in VisibilityView view, JobScheduler jobs = null, int minimumPerThread = 32)
    {
        this.view = view;
        if (jobs != null) jobs.Run(cells.Length, minimumPerThread, cull);
        else CullCells(0, cells.Length);
        int seen = 0, small = 0, occluded = 0;
        foreach (var cell in cells) { seen += cell.SeenCount; small += cell.TooSmall; occluded += cell.Occluded; }
        SeenCount = seen; TooSmallCount = small; OccludedCount = occluded;
    }

    void CullCells(int start, int end)
    {
        var frustum = view.Frustum; var occlusion = view.Occlusion; var casts = view.Casts;
        var eye = view.Eye;
        float scale = view.PixelScale, minPixels = scale > 0 ? view.MinPixels : 0;
        for (int c = start; c < end; c++)
        {
            var cell = cells[c];
            cell.SeenCount = cell.CasterCount = cell.TooSmall = cell.Occluded = 0;
            bool inView = frustum.Intersects(cell.Bounds), inCast = casts != null && casts(cell.Bounds);
            if (inView && occlusion != null && occlusion.Hidden(cell.Bounds)) { inView = false; cell.Occluded += cell.Items.Length; }
            if (!inView && !inCast) continue;
            foreach (int index in cell.Items)
            {
                ref readonly var item = ref items[index];
                if ((item.Traits & VisibilityTraits.Hidden) != 0) continue;
                bool unbounded = (item.Traits & VisibilityTraits.Unbounded) != 0;
                if (inView && Sees(in item, unbounded, frustum, occlusion, eye, scale, minPixels, cell, out byte level))
                {
                    cell.Levels[cell.SeenCount] = level;
                    cell.Seen[cell.SeenCount++] = index;
                }
                // Something drawn only near the eye casts only there too.
                if (inCast && (item.Traits & VisibilityTraits.Caster) != 0 && (unbounded || casts(item.Bounds))
                    && (unbounded || item.MaxDistance <= 0 || Vector3.DistanceSquared(item.Bounds.Center, eye) <= item.MaxDistance * item.MaxDistance))
                {
                    cell.CasterLevels[cell.CasterCount] = unbounded || scale <= 0 ? (byte)0 : Level(in item, Pixels(in item, eye, scale));
                    cell.Casters[cell.CasterCount++] = index;
                }
            }
        }
    }

    // An item's radius on screen, in pixels. Something the eye is inside of is as large as the screen.
    static float Pixels(in VisibilityItem item, Vector3 eye, float scale)
    {
        float distance = Vector3.Distance(item.Bounds.Center, eye);
        return distance > item.Bounds.Radius ? item.Bounds.Radius / distance * scale : float.MaxValue;
    }

    static byte Level(in VisibilityItem item, float pixels)
    {
        var coarser = item.CoarserBelow;
        return pixels >= coarser.X ? (byte)0 : pixels < coarser.Z ? (byte)3 : pixels < coarser.Y ? (byte)2 : (byte)1;
    }

    static bool Sees(in VisibilityItem item, bool unbounded, BoundingFrustum frustum, OcclusionBuffer occlusion, Vector3 eye,
        float scale, float minPixels, Cell cell, out byte level)
    {
        level = 0;
        if (unbounded) return true;
        float distance = 0;
        if (item.MaxDistance > 0 || scale > 0)
        {
            distance = Vector3.Distance(item.Bounds.Center, eye);
            if (item.MaxDistance > 0 && distance > item.MaxDistance) return false;
        }
        if (!frustum.Intersects(item.Bounds)) return false;
        if (scale > 0)
        {
            float pixels = distance > item.Bounds.Radius ? item.Bounds.Radius / distance * scale : float.MaxValue;
            if (pixels < minPixels) { cell.TooSmall++; return false; }
            level = Level(in item, pixels);
        }
        if (occlusion != null && occlusion.Hidden(item.Bounds)) { cell.Occluded++; return false; }
        return true;
    }
}
