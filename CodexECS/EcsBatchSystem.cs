using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace CodexECS
{
    /// <summary>
    /// A system that works through its entities a batch at a time, on whichever threads the world's scheduler has.
    /// Neighbouring batch systems of a pipeline run as one group: the entity range is cut into batches once, and
    /// a thread takes each batch through every system of the group, in their order, before it starts the next.
    /// A system so finds what the ones before it wrote for the same entities, and the group starts its threads
    /// and waits for them once instead of once for each system.
    /// A batch may read and write the components of its own entities and read what no batch writes. Whatever
    /// reaches further is left for End or recorded in the command buffer: another entity's components, a list the
    /// batches share, the creation or removal of anything. Then nothing depends on which thread ran which batch.
    /// </summary>
    public abstract class EcsBatchSystem : EcsSystem
    {
        private BatchGroup _alone;
        private List<EcsBatchSystem> _self;

        /// <summary>The entities the system works through.</summary>
        public abstract EcsFilter Entities { get; }

        /// <summary>Pages of 64 entity slots to a batch. The first system of a group decides for the group.</summary>
        public virtual int PagesPerBatch => 4;

        /// <summary>
        /// False starts a group of its own after the batch system before this one: for a system whose batches
        /// need what an earlier one does in End or records as commands.
        /// </summary>
        public virtual bool JoinsGroup => true;

        /// <summary>Once a tick on the owning thread, before any batch. False sits this tick out.</summary>
        public virtual bool Begin(EcsWorld world) => true;

        /// <summary>One batch, on any thread. Commands take the entity's id as their sort key.</summary>
        public abstract void Run(EcsFilter.Batch batch, EntityCommandBuffer commands);

        /// <summary>
        /// Once a tick on the owning thread, after every batch of the group and the playback of their commands:
        /// for what must happen in a fixed order.
        /// </summary>
        public virtual void End(EcsWorld world) { }

        /// <summary>Outside a pipeline, or with no batch system beside it, the system is a group of one.</summary>
        public sealed override void Tick(EcsWorld world)
        {
            _alone ??= new BatchGroup();
            _self ??= new List<EcsBatchSystem> { this };
            _alone.Run(world, _self, null);
        }
    }

    /// <summary>Runs batch systems as one group: Begin for each, the batches on the threads, the commands, End for each.</summary>
    internal sealed class BatchGroup
    {
        private EcsBatchSystem[] _running = new EcsBatchSystem[4];
        private long[] _spent = new long[4];
        private int[] _slot = new int[4];
        private readonly Action<int, int> _runBatches;
        private EntityCommandBuffer _commands;
        private int _count, _pages, _pagesPerBatch;
        private bool _timed;

        public BatchGroup() => _runBatches = RunBatches;

        /// <summary>
        /// The systems must be ready to run: jobs finished, commands played back. With `ticks` given, each
        /// system's share of the time is added at its index: its own Begin and End, and of the time the batches
        /// took the share of processor time it had in them.
        /// </summary>
        public void Run(EcsWorld world, List<EcsBatchSystem> systems, long[] ticks)
        {
            if (_running.Length < systems.Count)
            {
                _running = new EcsBatchSystem[systems.Count];
                _spent = new long[systems.Count];
                _slot = new int[systems.Count];
            }
            _timed = ticks != null;
            _count = 0;
            world.Lock();
            try
            {
                for (int i = 0; i < systems.Count; i++)
                {
                    long started = _timed ? Stopwatch.GetTimestamp() : 0;
                    bool runs = systems[i].Begin(world);
                    if (_timed) ticks[i] += Stopwatch.GetTimestamp() - started;
                    if (!runs) continue;
                    _slot[_count] = i;
                    _spent[_count] = 0;
                    _running[_count++] = systems[i];
                }
            }
            finally { world.Unlock(); }
            if (_count == 0)
                return;

            _pages = world.PageCount;
            _pagesPerBatch = Math.Max(1, _running[0].PagesPerBatch);
            int batches = (_pages + _pagesPerBatch - 1) / _pagesPerBatch;
            _commands = world.CreateCommandBuffer();
            long began = _timed ? Stopwatch.GetTimestamp() : 0;
            world.Lock();
            try
            {
                // More batches than threads: a thread that finishes early takes what is left.
                if (world.Jobs == null || batches <= 1) RunBatches(0, batches);
                else world.Jobs.ScheduleRange(batches, 1, _runBatches, default, batches).Complete();
            }
            finally { world.Unlock(); }
            world.Sync();
            if (_timed)
            {
                long wall = Stopwatch.GetTimestamp() - began, all = 0;
                for (int i = 0; i < _count; i++) all += _spent[i];
                for (int i = 0; i < _count; i++)
                    ticks[_slot[i]] += all > 0 ? (long)((double)wall * _spent[i] / all) : wall / _count;
            }

            world.Lock();
            try
            {
                for (int i = 0; i < _count; i++)
                {
                    long started = _timed ? Stopwatch.GetTimestamp() : 0;
                    _running[i].End(world);
                    if (_timed) ticks[_slot[i]] += Stopwatch.GetTimestamp() - started;
                }
            }
            finally
            {
                world.Unlock();
                Array.Clear(_running, 0, _count);
                _commands = null;
            }
        }

        private void RunBatches(int first, int end)
        {
            for (int batch = first; batch < end; batch++)
            {
                int firstPage = batch * _pagesPerBatch, endPage = Math.Min(_pages, firstPage + _pagesPerBatch);
                for (int i = 0; i < _count; i++)
                {
                    var system = _running[i];
                    long started = _timed ? Stopwatch.GetTimestamp() : 0;
                    system.Run(new EcsFilter.Batch(system.Entities, firstPage, endPage, batch), _commands);
                    if (_timed) Interlocked.Add(ref _spent[i], Stopwatch.GetTimestamp() - started);
                }
            }
        }
    }
}
