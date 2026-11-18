using System;
using CodexECS;

namespace CodexNetworking.History
{
    /// <summary>
    /// One fixed tick of the simulation. Run only simulation systems here; presentation runs outside
    /// the loop. <paramref name="resimulating"/> is true while a rollback replays ticks, so one-shot
    /// side effects that reach presentation (sounds, effects, UI events) must be suppressed or
    /// deduplicated when it is set.
    /// </summary>
    public interface ISimulationStep
    {
        void Step(EcsWorld world, int tick, bool resimulating);
    }

    /// <summary>Runs one group of an <see cref="EcsPipeline{TGroup}"/> as the simulation step.</summary>
    public sealed class PipelineStep<TGroup> : ISimulationStep where TGroup : struct, Enum
    {
        private readonly EcsPipeline<TGroup> _pipeline;
        private readonly TGroup _group;

        public PipelineStep(EcsPipeline<TGroup> pipeline, TGroup group)
        {
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _group = group;
        }

        public void Step(EcsWorld world, int tick, bool resimulating) => _pipeline.Tick(_group);
    }

    /// <summary>
    /// Drives a deterministic simulation tick by tick, snapshots the world after every tick and can
    /// roll back to any tick still in the history and resimulate to the present. Both sync modes are
    /// built on this: input sync rolls back when a late input differs from its prediction, state sync
    /// rolls back to the last acknowledged server tick before replaying local inputs.
    /// Convention: the snapshot stored under tick T is the world at the start of tick T, that is after
    /// T ticks have run.
    /// </summary>
    public sealed class SimulationLoop
    {
        private readonly EcsWorld _world;
        private readonly ISimulationStep _step;
        private readonly StateHistory _history;

        public SimulationLoop(EcsWorld world, ISimulationStep step, SimulationRegistry registry, int historyCapacity)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _step = step ?? throw new ArgumentNullException(nameof(step));
            _history = new StateHistory(historyCapacity, registry);
        }

        public EcsWorld World => _world;

        public StateHistory History => _history;

        /// <summary>Ticks completed; also the next tick to simulate.</summary>
        public int Tick { get; private set; }

        public bool IsResimulating { get; private set; }

        public int RollbackCount { get; private set; }

        public long ResimulatedTicks { get; private set; }

        /// <summary>Snapshots the current world as the state at <paramref name="tick"/> and starts counting from it.</summary>
        public void Start(int tick = 0)
        {
            Tick = tick;
            _history.Clear();
            _history.Save(_world, tick);
        }

        /// <summary>Simulates one tick and snapshots the result.</summary>
        public void Advance()
        {
            _step.Step(_world, Tick, false);
            Tick++;
            _history.Save(_world, Tick);
        }

        public void Advance(int ticks)
        {
            for (int i = 0; i < ticks; i++)
                Advance();
        }

        /// <summary>
        /// Restores the world to <paramref name="toTick"/> and resimulates up to the current tick. The
        /// caller changes inputs between restore and replay through <paramref name="beforeReplay"/>.
        /// False when the tick is no longer in the history.
        /// </summary>
        public bool Rollback(int toTick, Action beforeReplay = null)
        {
            if (toTick > Tick || !_history.TryRestore(_world, toTick))
                return false;
            int target = Tick;
            Tick = toTick;
            if (beforeReplay != null)
            {
                // The caller changed the restored state (for example applied a server snapshot):
                // the stored snapshot for this tick must reflect that.
                beforeReplay();
                _history.Save(_world, toTick);
            }
            IsResimulating = true;
            try
            {
                while (Tick < target)
                {
                    _step.Step(_world, Tick, true);
                    Tick++;
                    _history.Save(_world, Tick);
                }
            }
            finally
            {
                IsResimulating = false;
            }
            RollbackCount++;
            ResimulatedTicks += target - toTick;
            return true;
        }

        /// <summary>Hash of the snapshot stored for the current tick.</summary>
        public ulong HashCurrent()
        {
            if (!_history.TryGet(Tick, out WorldState state))
                throw new InvalidOperationException("no snapshot for the current tick; call Start first");
            return WorldHash.Compute(state, _history.Registry);
        }

        public bool TryHash(int tick, out ulong hash)
        {
            if (_history.TryGet(tick, out WorldState state))
            {
                hash = WorldHash.Compute(state, _history.Registry);
                return true;
            }
            hash = 0;
            return false;
        }
    }
}
