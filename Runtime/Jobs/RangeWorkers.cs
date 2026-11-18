using System.Runtime.ExceptionServices;

namespace CodexEngine.Runtime;

/// <summary>Synchronous, reusable range dispatch for single-owner systems. The caller runs lane zero.
/// Workers inherit the constructing thread's execution context, including its collision world.
/// Ranges are disjoint. The caller can publish results in index order after Run returns.</summary>
public sealed class RangeWorkers : IDisposable
{
    private readonly AutoResetEvent[] starts;
    private readonly Thread[] threads;
    private readonly CountdownEvent finished = new(0);
    private readonly int owner = Environment.CurrentManagedThreadId;
    private Action<int,int> work;
    private ExceptionDispatchInfo failure;
    private int count, lanes;
    private bool busy, stopping;

    public RangeWorkers(int maxWorkers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWorkers, 1);
        int helpers = Math.Min(maxWorkers, Environment.ProcessorCount) - 1;
        starts = new AutoResetEvent[helpers];
        threads = new Thread[helpers];
        for (int i = 0; i < helpers; i++)
        {
            int lane = i + 1;
            starts[i] = new(false);
            threads[i] = new Thread(() => Worker(lane)) { IsBackground = true, Name = $"Codex range {lane}" };
            threads[i].Start();
        }
    }

    public void Run(int count, int minimumPerLane, Action<int,int> processRange)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(stopping, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumPerLane, 1);
        ArgumentNullException.ThrowIfNull(processRange);
        if (count == 0) return;
        busy = true;
        try
        {
            lanes = Math.Clamp(count / minimumPerLane, 1, threads.Length + 1);
            if (lanes == 1)
            {
                processRange(0,count);
                return;
            }
            this.count = count;
            work = processRange;
            failure = null;
            finished.Reset(lanes - 1);
            for (int i = 0; i < lanes - 1; i++) starts[i].Set();
            Execute(0);
            finished.Wait();
            failure?.Throw();
        }
        finally { work = null; busy = false; }
    }

    private void Worker(int lane)
    {
        while (true)
        {
            starts[lane - 1].WaitOne();
            if (stopping) return;
            Execute(lane);
            finished.Signal();
        }
    }

    private void Execute(int lane)
    {
        try { work((int)((long)count * lane / lanes), (int)((long)count * (lane + 1) / lanes)); }
        catch (Exception e) { Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(e), null); }
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != owner || busy)
            throw new InvalidOperationException("Dispatch and dispose range workers from the idle owning thread.");
    }

    public void Dispose()
    {
        CheckOwner();
        if (stopping) return;
        stopping = true;
        foreach (var start in starts) start.Set();
        foreach (var thread in threads) thread.Join();
        foreach (var start in starts) start.Dispose();
        finished.Dispose();
    }
}
