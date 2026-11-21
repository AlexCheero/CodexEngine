using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace CodexECS
{
    /// <summary>Work given to a JobScheduler. The default handle is already complete.</summary>
    public readonly struct JobHandle : IEquatable<JobHandle>
    {
        internal readonly JobScheduler.Job Job;

        internal JobHandle(JobScheduler.Job job) => Job = job;

        public bool IsCompleted => Job == null || Volatile.Read(ref Job.State) == JobScheduler.Done;

        /// <summary>Runs or waits for the job and everything it depends on, then rethrows its failure.</summary>
        public void Complete()
        {
            if (Job != null)
                Job.Owner.Complete(this);
        }

        public bool Equals(JobHandle other) => ReferenceEquals(Job, other.Job);
        public override bool Equals(object obj) => obj is JobHandle other && Equals(other);
        public override int GetHashCode() => Job == null ? 0 : Job.GetHashCode();
    }

    /// <summary>
    /// Worker threads that run jobs in dependency order. A job is one call, or a range cut into batches that
    /// several threads share. Scheduling returns at once; a thread that completes a handle runs queued batches
    /// itself while it waits, so the scheduling thread is one of the workers.
    /// Batches of a range are fixed slices in index order, whatever thread runs them: a job that writes only
    /// what its own slice owns gives the same result on any number of threads.
    /// Workers inherit the constructing thread's execution context.
    /// </summary>
    public sealed class JobScheduler : IDisposable
    {
        internal const int Waiting = 0, Ready = 1, Done = 2;

        internal sealed class Job
        {
            internal JobScheduler Owner;
            internal Action Single;
            internal Action<int, int> Range;
            internal Action<EcsFilter.Batch> Entities;
            internal EcsFilter Filter;
            internal int Count, Batches, PagesPerBatch;
            // Claimed and finished batches; threads advance both without the gate.
            internal int NextBatch, Finished;
            // Unfinished dependencies and the jobs waiting on this one; both guarded by the gate.
            internal int Pending;
            internal List<Job> Dependents;
            internal int State;
            internal ExceptionDispatchInfo Error;

            internal void Execute(int batch)
            {
                if (Range != null)
                    Range((int)((long)Count * batch / Batches), (int)((long)Count * (batch + 1) / Batches));
                else if (Entities != null)
                {
                    int first = batch * PagesPerBatch;
                    Entities(new EcsFilter.Batch(Filter, first, Math.Min(Count, first + PagesPerBatch), batch));
                }
                else
                    Single();
            }
        }

        [ThreadStatic] private static int _threadIndex;

        private readonly object _gate = new object();
        private readonly List<Job> _ready = new List<Job>(16);
        private readonly Thread[] _threads;
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
        private int _idle, _waiters, _outstanding;
        private ExceptionDispatchInfo _unobserved;
        private volatile bool _stopping;

        /// <summary>Threads that can run batches, counting the one that schedules and completes.</summary>
        public int ThreadCount => _threads.Length + 1;

        /// <summary>Zero on a thread that is not a worker, otherwise the worker's index below ThreadCount.</summary>
        public static int CurrentThreadIndex => _threadIndex;

        /// <summary>True while any scheduled job has not finished.</summary>
        public bool Busy => Volatile.Read(ref _outstanding) != 0;

        public JobScheduler(int maxThreads)
        {
            if (maxThreads < 1)
                throw new ArgumentOutOfRangeException(nameof(maxThreads));
            int helpers = Math.Min(maxThreads, Environment.ProcessorCount) - 1;
            _threads = new Thread[helpers];
            for (int i = 0; i < helpers; i++)
            {
                int index = i + 1;
                _threads[i] = new Thread(() => Work(index)) { IsBackground = true, Name = "Codex job " + index };
                _threads[i].Start();
            }
        }

        /// <summary>Schedules one call after dependsOn.</summary>
        public JobHandle Schedule(Action body, JobHandle dependsOn = default)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            return Submit(new Job { Owner = this, Single = body, Batches = 1 }, dependsOn, false);
        }

        /// <summary>
        /// Schedules body(start, end) over [0, count) in at most maxBatches slices of at least minimumPerBatch
        /// items. More batches than threads let threads that finish early take what is left.
        /// </summary>
        public JobHandle ScheduleRange(int count, int minimumPerBatch, Action<int, int> body, JobHandle dependsOn = default,
            int maxBatches = 0)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (minimumPerBatch < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumPerBatch));
            if (count == 0)
                return dependsOn;
            if (maxBatches <= 0)
                maxBatches = ThreadCount * 4;
            int batches = Math.Max(1, Math.Min(count / minimumPerBatch, maxBatches));
            return Submit(new Job { Owner = this, Range = body, Count = count, Batches = batches }, dependsOn, false);
        }

        internal JobHandle ScheduleEntities(EcsFilter filter, Action<EcsFilter.Batch> body, int pagesPerBatch, JobHandle dependsOn)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (pagesPerBatch < 1)
                throw new ArgumentOutOfRangeException(nameof(pagesPerBatch));
            int pages = filter.World.PageCount;
            if (pages == 0)
                return dependsOn;
            var job = new Job
            {
                Owner = this, Entities = body, Filter = filter, Count = pages, PagesPerBatch = pagesPerBatch,
                Batches = (pages + pagesPerBatch - 1) / pagesPerBatch
            };
            return Submit(job, dependsOn, false);
        }

        /// <summary>
        /// Runs body(start, end) over [0, count) on up to ThreadCount threads and returns when every slice has
        /// finished: one slice per thread, each at least minimumPerLane items. A failure is rethrown here.
        /// </summary>
        public void Run(int count, int minimumPerLane, Action<int, int> body)
        {
            if (body == null)
                throw new ArgumentNullException(nameof(body));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (minimumPerLane < 1)
                throw new ArgumentOutOfRangeException(nameof(minimumPerLane));
            if (count == 0)
                return;
            int lanes = Math.Max(1, Math.Min(count / minimumPerLane, ThreadCount));
            if (lanes == 1)
            {
                body(0, count);
                return;
            }
            Complete(Submit(new Job { Owner = this, Range = body, Count = count, Batches = lanes }, default, true));
        }

        /// <summary>A handle that completes when both do.</summary>
        public JobHandle Combine(JobHandle first, JobHandle second)
        {
            if (first.IsCompleted)
                return second;
            if (second.IsCompleted || first.Equals(second))
                return first;
            var job = new Job { Owner = this };
            lock (_gate)
            {
                Depend(job, first.Job);
                Depend(job, second.Job);
                _outstanding++;
                if (job.Pending == 0)
                    MakeReady(job, false);
            }
            return new JobHandle(job);
        }

        /// <summary>A handle that completes when all of them do.</summary>
        public JobHandle Combine(List<JobHandle> handles)
        {
            Job job = null;
            JobHandle only = default;
            int unfinished = 0;
            for (int i = 0; i < handles.Count; i++)
            {
                if (handles[i].IsCompleted || unfinished == 1 && handles[i].Equals(only))
                    continue;
                if (unfinished++ == 0)
                    only = handles[i];
            }
            if (unfinished <= 1)
                return only;
            job = new Job { Owner = this };
            lock (_gate)
            {
                for (int i = 0; i < handles.Count; i++)
                    if (handles[i].Job != null)
                        Depend(job, handles[i].Job);
                _outstanding++;
                if (job.Pending == 0)
                    MakeReady(job, false);
            }
            return new JobHandle(job);
        }

        // The gate is held.
        private void Depend(Job job, Job on)
        {
            if (on.State == Done)
                return;
            if (on.Owner != this)
                throw new InvalidOperationException("A job cannot depend on another scheduler's job.");
            (on.Dependents ??= new List<Job>(2)).Add(job);
            job.Pending++;
        }

        private JobHandle Submit(Job job, JobHandle dependsOn, bool callerRuns)
        {
            lock (_gate)
            {
                if (_stopping)
                    throw new ObjectDisposedException(nameof(JobScheduler));
                _outstanding++;
                if (dependsOn.Job != null)
                    Depend(job, dependsOn.Job);
                if (job.Pending == 0)
                    MakeReady(job, callerRuns);
            }
            return new JobHandle(job);
        }

        // The gate is held.
        private void MakeReady(Job job, bool callerRuns)
        {
            if (job.Batches == 0)
            {
                Finish(job);
                return;
            }
            Volatile.Write(ref job.State, Ready);
            _ready.Add(job);
            int wake = Math.Min(callerRuns ? job.Batches - 1 : job.Batches, Volatile.Read(ref _idle));
            if (wake > 0)
                _wake.Release(wake);
            if (_waiters > 0)
                Monitor.PulseAll(_gate);
        }

        private void Finish(Job job)
        {
            lock (_gate)
            {
                Volatile.Write(ref job.State, Done);
                _outstanding--;
                if (job.Error != null && _unobserved == null)
                    _unobserved = job.Error;
                var dependents = job.Dependents;
                job.Dependents = null;
                if (dependents != null)
                    for (int i = 0; i < dependents.Count; i++)
                        if (--dependents[i].Pending == 0)
                            MakeReady(dependents[i], false);
                if (_waiters > 0)
                    Monitor.PulseAll(_gate);
            }
        }

        // A job with batches nobody has claimed, or null. Exhausted jobs leave the queue here.
        private Job Take()
        {
            lock (_gate)
                return TakeLocked();
        }

        private Job TakeLocked()
        {
            for (int i = 0; i < _ready.Count; i++)
            {
                var job = _ready[i];
                if (Volatile.Read(ref job.NextBatch) < job.Batches)
                    return job;
                _ready.RemoveAt(i--);
            }
            return null;
        }

        private void RunBatches(Job job)
        {
            while (true)
            {
                if (Volatile.Read(ref job.NextBatch) >= job.Batches)
                    return;
                int batch = Interlocked.Increment(ref job.NextBatch) - 1;
                if (batch >= job.Batches)
                    return;
                try
                {
                    job.Execute(batch);
                }
                catch (Exception e)
                {
                    Interlocked.CompareExchange(ref job.Error, ExceptionDispatchInfo.Capture(e), null);
                }
                if (Interlocked.Increment(ref job.Finished) == job.Batches)
                    Finish(job);
            }
        }

        private void Work(int index)
        {
            _threadIndex = index;
            while (true)
            {
                var job = Take();
                if (job != null)
                {
                    RunBatches(job);
                    continue;
                }
                if (_stopping)
                    return;
                // Announce idleness, then look again: a job queued in between is found here and one queued
                // afterwards releases the semaphore.
                Interlocked.Increment(ref _idle);
                job = Take();
                if (job == null && !_stopping)
                    _wake.Wait();
                Interlocked.Decrement(ref _idle);
                if (job != null)
                    RunBatches(job);
            }
        }

        internal void Complete(JobHandle handle)
        {
            var job = handle.Job;
            while (Volatile.Read(ref job.State) != Done)
            {
                // The awaited job's own batches first, then whatever is queued: its dependencies may be there.
                if (Volatile.Read(ref job.State) == Ready && Volatile.Read(ref job.NextBatch) < job.Batches)
                {
                    RunBatches(job);
                    continue;
                }
                Job other;
                lock (_gate)
                {
                    other = TakeLocked();
                    if (other == null && job.State != Done)
                    {
                        _waiters++;
                        Monitor.Wait(_gate);
                        _waiters--;
                    }
                }
                if (other != null)
                    RunBatches(other);
            }
            var error = job.Error;
            if (error == null)
                return;
            lock (_gate)
                if (_unobserved == error)
                    _unobserved = null;
            job.Error = null;
            error.Throw();
        }

        /// <summary>Runs or waits for every scheduled job, then rethrows the first failure nobody has seen.</summary>
        public void CompleteAll()
        {
            while (Volatile.Read(ref _outstanding) != 0)
            {
                Job job;
                lock (_gate)
                {
                    job = TakeLocked();
                    if (job == null && _outstanding != 0)
                    {
                        _waiters++;
                        Monitor.Wait(_gate);
                        _waiters--;
                    }
                }
                if (job != null)
                    RunBatches(job);
            }
            if (_unobserved == null)
                return;
            ExceptionDispatchInfo error;
            lock (_gate)
            {
                error = _unobserved;
                _unobserved = null;
            }
            error?.Throw();
        }

        public void Dispose()
        {
            if (_stopping)
                return;
            try
            {
                CompleteAll();
            }
            finally
            {
                lock (_gate)
                    _stopping = true;
                if (_threads.Length > 0)
                    _wake.Release(_threads.Length);
                foreach (var thread in _threads)
                    thread.Join();
                _wake.Dispose();
            }
        }
    }
}
