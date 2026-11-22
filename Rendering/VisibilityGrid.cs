using CodexECS;
using SphereSet = CodexEngine.Runtime.SphereSet;
using SphereTests = CodexEngine.Runtime.SphereTests;
using Microsoft.Xna.Framework;
using N = System.Numerics;

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
    /// <summary>
    /// The same question as a volume: a sphere can cast when it touches this frustum. Where the casters' reach is
    /// one, give it here in place of <see cref="Casts"/> and a cell's items are tested together.
    /// </summary>
    public BoundingFrustum CasterVolume;
}

/// <summary>
/// The things of a level that stand still, sorted into square cells over the ground, and for each view the lists
/// of what it sees and what casts shadows into it. <see cref="Cull"/> tests cells first and their items after, on
/// the job threads: against the view's frustum, by distance, by size on screen and against the frame's occluders.
/// A seen item also gets its level of detail from its size on screen. The lists are per cell and in the order the
/// items were given, on any number of threads.
/// A cell keeps its items' spheres a field to an array (<see cref="SpheresIn"/>), and the frustum and distance
/// tests run over a whole cell at a time. A pass that tests the items again, a shadow cascade or a light's tile,
/// can do the same with the set.
/// </summary>
public sealed class VisibilityGrid
{
    sealed class Cell(BoundingSphere bounds, int[] items)
    {
        public readonly BoundingSphere Bounds = bounds;
        public readonly int[] Items = items, Seen = new int[items.Length], Casters = new int[items.Length];
        // Where in Items each caster of the last cull sits: the entry of its sphere in Spheres.
        public readonly int[] CasterSlots = new int[items.Length];
        public readonly byte[] Levels = new byte[items.Length], CasterLevels = new byte[items.Length];
        public int SeenCount, CasterCount, TooSmall, Occluded;
        // The items' bounds in the order of Items, and the distance each is seen within (zero for any).
        public SphereSet Spheres;
        public float[] MaxDistance;
        public bool Limited;
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
                var cell = new Cell(bounds, entries) { Spheres = new SphereSet(entries.Length) };
                cell.MaxDistance = new float[cell.Spheres.Padded];
                for (int slot = 0; slot < entries.Length; slot++)
                {
                    ref readonly var item = ref items[entries[slot]];
                    cell.Spheres.Set(slot, item.Bounds.Center.X, item.Bounds.Center.Y, item.Bounds.Center.Z, item.Bounds.Radius);
                    cell.MaxDistance[slot] = item.MaxDistance;
                    cell.Limited |= item.MaxDistance > 0;
                }
                return cell;
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
    /// <summary>The bounds of a cell's items in the order of <see cref="ItemsIn"/>, for testing them together.</summary>
    public SphereSet SpheresIn(int cell) => cells[cell].Spheres;
    /// <summary>Where each item of <see cref="CastersIn"/> sits in <see cref="ItemsIn"/> and <see cref="SpheresIn"/>.</summary>
    public ReadOnlySpan<int> CasterSlotsIn(int cell) => cells[cell].CasterSlots.AsSpan(0, cells[cell].CasterCount);

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
        var frustum = view.Frustum; var occlusion = view.Occlusion; var casts = view.Casts; var volume = view.CasterVolume;
        var eye = new N.Vector3(view.Eye.X, view.Eye.Y, view.Eye.Z);
        float scale = view.PixelScale, minPixels = scale > 0 ? view.MinPixels : 0;
        Span<N.Vector4> planes = stackalloc N.Vector4[6], casterPlanes = stackalloc N.Vector4[6];
        SphereCulling.Planes(frustum, planes);
        if (volume != null) SphereCulling.Planes(volume, casterPlanes);
        for (int c = start; c < end; c++)
        {
            var cell = cells[c];
            cell.SeenCount = cell.CasterCount = cell.TooSmall = cell.Occluded = 0;
            bool inView = frustum.Intersects(cell.Bounds);
            bool inCast = volume != null ? volume.Intersects(cell.Bounds) : casts != null && casts(cell.Bounds);
            if (inView && occlusion != null && occlusion.Hidden(cell.Bounds)) { inView = false; cell.Occluded += cell.Items.Length; }
            if (!inView && !inCast) continue;

            // The tests that are the same arithmetic for every item run over the whole cell first: how far each
            // is from the eye, which the view's frustum or their own distance limit rules out, which cannot cast.
            var spheres = cell.Spheres;
            var scratch = SphereTests.Scratch.For(spheres.Padded);
            Span<int> unseen = scratch.Culled.AsSpan(0, spheres.Padded), castless = scratch.Other.AsSpan(0, spheres.Padded);
            Span<float> distances = scratch.Distance.AsSpan(0, spheres.Padded);
            if (scale > 0 || cell.Limited) SphereTests.Distances(spheres, eye, distances);
            if (inView)
            {
                unseen.Clear();
                if (cell.Limited) SphereTests.CullByLimit(distances, cell.MaxDistance, unseen);
                SphereTests.CullByPlanes(spheres, planes, unseen);
            }
            if (inCast && volume != null)
            {
                castless.Clear();
                SphereTests.CullByPlanes(spheres, casterPlanes, castless);
            }

            var entries = cell.Items;
            for (int slot = 0; slot < entries.Length; slot++)
            {
                int index = entries[slot];
                ref readonly var item = ref items[index];
                if ((item.Traits & VisibilityTraits.Hidden) != 0) continue;
                bool unbounded = (item.Traits & VisibilityTraits.Unbounded) != 0;
                if (inView && (unbounded || unseen[slot] == 0))
                {
                    byte level = 0;
                    bool sees = true;
                    if (!unbounded)
                    {
                        if (scale > 0)
                        {
                            float pixels = Pixels(in item, distances[slot], scale);
                            if (pixels < minPixels) { cell.TooSmall++; sees = false; }
                            else level = Level(in item, pixels);
                        }
                        if (sees && occlusion != null && occlusion.Hidden(item.Bounds)) { cell.Occluded++; sees = false; }
                    }
                    if (sees)
                    {
                        cell.Levels[cell.SeenCount] = level;
                        cell.Seen[cell.SeenCount++] = index;
                    }
                }
                // Something drawn only near the eye casts only there too.
                if (inCast && (item.Traits & VisibilityTraits.Caster) != 0 &&
                    (unbounded || (volume != null ? castless[slot] == 0 : casts(item.Bounds)))
                    && (unbounded || item.MaxDistance <= 0 || Vector3.DistanceSquared(item.Bounds.Center, view.Eye) <= item.MaxDistance * item.MaxDistance))
                {
                    cell.CasterLevels[cell.CasterCount] = unbounded || scale <= 0 ? (byte)0 : Level(in item, Pixels(in item, distances[slot], scale));
                    cell.CasterSlots[cell.CasterCount] = slot;
                    cell.Casters[cell.CasterCount++] = index;
                }
            }
        }
    }

    // An item's radius on screen, in pixels, from its distance. Something the eye is inside of is as large as the screen.
    static float Pixels(in VisibilityItem item, float distance, float scale) =>
        distance > item.Bounds.Radius ? item.Bounds.Radius / distance * scale : float.MaxValue;

    static byte Level(in VisibilityItem item, float pixels)
    {
        var coarser = item.CoarserBelow;
        return pixels >= coarser.X ? (byte)0 : pixels < coarser.Z ? (byte)3 : pixels < coarser.Y ? (byte)2 : (byte)1;
    }
}
