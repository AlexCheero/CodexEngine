using CodexECS;

namespace CodexEngine.Runtime;

/// <summary>Synchronous range dispatch for single-owner systems, on a job scheduler's threads. The caller runs
/// one of the slices. Workers inherit the constructing thread's execution context, including its collision world.
/// Ranges are disjoint. The caller can publish results in index order after Run returns.</summary>
public sealed class RangeWorkers : IDisposable
{
    private readonly bool owned;

    /// <summary>The scheduler the ranges run on. Give it to an EcsWorld so its jobs share these threads.</summary>
    public JobScheduler Scheduler { get; }

    public RangeWorkers(int maxWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWorkers, 1);
        Scheduler = new JobScheduler(maxWorkers);
        owned = true;
    }

    /// <summary>Dispatches on a scheduler that someone else owns and disposes.</summary>
    public RangeWorkers(JobScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        Scheduler = scheduler;
    }

    /// <summary>Runs processRange(start, end) over [0, count): one slice per thread, each at least minimumPerLane
    /// items, and returns when all have finished. A failure is rethrown here.</summary>
    public void Run(int count, int minimumPerLane, Action<int,int> processRange) =>
        Scheduler.Run(count, minimumPerLane, processRange);

    public void Dispose()
    {
        if (owned) Scheduler.Dispose();
    }
}
