using CodexECS;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Presentation copy of a replicated component: the value interpolated between the two received
    /// snapshots around the render time. Lives on the same entity as the authoritative <typeparamref name="T"/>,
    /// is never simulation state (so rollback leaves it alone on living entities, and it is never
    /// sent) and is written by <see cref="StateSyncClient{TInput}.UpdateInterpolation"/>. Views read
    /// this, never <typeparamref name="T"/> directly.
    /// </summary>
    public struct Interpolated<T> : IComponent where T : unmanaged, IComponent
    {
        public T Value;
        /// <summary>Server ticks of the two samples the value was interpolated between (equal when snapped).</summary>
        public int FromTick;
        public int ToTick;
    }

    /// <summary>Blends two received values. Presentation only, so float is fine here.</summary>
    public interface IInterpolator<T> where T : unmanaged
    {
        T Interpolate(in T from, in T to, float alpha);
    }

    /// <summary>
    /// Interpolator bound per component type. Null means no interpolation: the copy snaps to the
    /// sample nearest to the render time.
    /// </summary>
    public static class NetInterpolator<T> where T : unmanaged
    {
        public static IInterpolator<T> Instance;
    }
}
