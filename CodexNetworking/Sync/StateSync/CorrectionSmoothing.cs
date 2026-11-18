using System;
using CodexECS;

namespace CodexNetworking.Sync.StateSync
{
    /// <summary>
    /// Presentation component of <see cref="CorrectionSmoother{T}"/>: what is left of past
    /// corrections of <typeparamref name="T"/> on this entity. Views show the authoritative value
    /// plus <see cref="Offset"/>. Never simulation state, so rollbacks leave it alone and it is
    /// never sent. The game adds it to the entities it predicts.
    /// </summary>
    public struct CorrectionOffset<T> : IComponent where T : unmanaged, IComponent
    {
        public T Offset;
        /// <summary>The predicted value right before the correction in progress.</summary>
        public T Captured;
        public bool HasCapture;
    }

    /// <summary>
    /// The arithmetic a smoothed component needs. Presentation only, so float is fine.
    /// </summary>
    public interface IOffsetMath<T> where T : unmanaged
    {
        /// <summary>The offset that makes <paramref name="after"/> look like <paramref name="before"/>: before minus after.</summary>
        T Difference(in T before, in T after);

        T Add(in T value, in T offset);

        T Scale(in T offset, float factor);

        /// <summary>Size of an offset in the game's units, compared with the snap and rest thresholds.</summary>
        float Magnitude(in T offset);
    }

    /// <summary>
    /// Hides prediction corrections from the eye. When a snapshot disagrees with what the client
    /// predicted, the simulation jumps to the corrected value at once (it has to: the next inputs
    /// build on it), but the view keeps showing where the entity was and slides to the truth over
    /// a few frames. Hook <see cref="Capture"/> and <see cref="Measure"/> to
    /// <see cref="StateSyncClient{TInput}.BeforeCorrection"/> and
    /// <see cref="StateSyncClient{TInput}.AfterCorrection"/> (or call <see cref="Attach{TInput}"/>),
    /// call <see cref="Decay"/> once per rendered frame and read <see cref="Smoothed"/> from views.
    ///
    /// Works on every entity that owns both <typeparamref name="T"/> and
    /// <see cref="CorrectionOffset{T}"/>. A correction that changes nothing (the usual case: the
    /// prediction was right) adds nothing. An error above <see cref="SnapMagnitude"/> is a teleport
    /// or a respawn and is shown at once. Allocation free.
    /// </summary>
    public sealed class CorrectionSmoother<T> where T : unmanaged, IComponent
    {
        private readonly EcsWorld _world;
        private readonly IOffsetMath<T> _math;
        private readonly EcsFilter _filter;
        private double _halfLife = 0.05;

        public CorrectionSmoother(EcsWorld world, IOffsetMath<T> math)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _math = math ?? throw new ArgumentNullException(nameof(math));
            _filter = world.Filter().With<T>().With<CorrectionOffset<T>>().Build(gateSystem: false);
        }

        /// <summary>Seconds in which a visible offset halves. Default 0.05.</summary>
        public double HalfLife
        {
            get => _halfLife;
            set => _halfLife = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Offsets larger than this are dropped instead of eased: the entity really moved.</summary>
        public float SnapMagnitude = float.PositiveInfinity;

        /// <summary>Offsets smaller than this become exactly zero.</summary>
        public float RestMagnitude = 1e-4f;

        /// <summary>Corrections that actually changed a value.</summary>
        public long Corrections { get; private set; }

        /// <summary>Corrections too large to ease.</summary>
        public long Snaps { get; private set; }

        /// <summary>Largest single correction seen, in <see cref="IOffsetMath{T}.Magnitude"/> units.</summary>
        public float LargestError { get; private set; }

        /// <summary>Subscribes to the client's correction hooks.</summary>
        public void Attach<TInput>(StateSyncClient<TInput> client) where TInput : unmanaged
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            client.BeforeCorrection += OnBefore;
            client.AfterCorrection += OnAfter;
        }

        public void Detach<TInput>(StateSyncClient<TInput> client) where TInput : unmanaged
        {
            if (client == null)
                return;
            client.BeforeCorrection -= OnBefore;
            client.AfterCorrection -= OnAfter;
        }

        private void OnBefore(int tick) => Capture();

        private void OnAfter(int tick) => Measure();

        /// <summary>Remembers the predicted values. Call right before a correction.</summary>
        public void Capture()
        {
            foreach (int id in _filter)
            {
                ref CorrectionOffset<T> offset = ref _world.Get<CorrectionOffset<T>>(id);
                offset.Captured = _world.Get<T>(id);
                offset.HasCapture = true;
            }
        }

        /// <summary>Adds what the correction changed to the visible offsets. Call right after a correction.</summary>
        public void Measure()
        {
            foreach (int id in _filter)
            {
                ref CorrectionOffset<T> offset = ref _world.Get<CorrectionOffset<T>>(id);
                if (!offset.HasCapture)
                    continue;
                offset.HasCapture = false;
                T error = _math.Difference(offset.Captured, _world.Get<T>(id));
                float size = _math.Magnitude(error);
                if (!(size > 0f))
                    continue;
                Corrections++;
                if (size > LargestError)
                    LargestError = size;
                T total = _math.Add(offset.Offset, error);
                if (_math.Magnitude(total) > SnapMagnitude)
                {
                    offset.Offset = default;
                    Snaps++;
                }
                else
                {
                    offset.Offset = total;
                }
            }
        }

        /// <summary>Shrinks every offset by the frame's share of the half-life. Call once per rendered frame.</summary>
        public void Decay(double deltaSeconds)
        {
            if (!(deltaSeconds > 0))
                return;
            float factor = (float)Math.Pow(0.5, deltaSeconds / _halfLife);
            foreach (int id in _filter)
            {
                ref CorrectionOffset<T> offset = ref _world.Get<CorrectionOffset<T>>(id);
                T scaled = _math.Scale(offset.Offset, factor);
                offset.Offset = _math.Magnitude(scaled) < RestMagnitude ? default : scaled;
            }
        }

        /// <summary>The value a view should show: authoritative plus the remaining offset.</summary>
        public T Smoothed(int entityId)
        {
            T value = _world.Get<T>(entityId);
            return _world.Have<CorrectionOffset<T>>(entityId) ? _math.Add(value, _world.Get<CorrectionOffset<T>>(entityId).Offset) : value;
        }

        /// <summary>Drops an entity's offset, for example after a respawn the game knows about.</summary>
        public void Reset(int entityId)
        {
            if (_world.IsIdValid(entityId) && _world.Have<CorrectionOffset<T>>(entityId))
                _world.Get<CorrectionOffset<T>>(entityId) = default;
        }
    }
}
