namespace Claustrum.Core.Process;

// The job's timeout as a clock a live session can stop (#124): the time a role spends waiting for its human
// does not count toward --timeout. Without a live session nothing calls Pause, and this is the plain
// CancelAfter ProcessRunner always had. Active is the running time so far, which the 85% warning reads.
public sealed class PausableDeadline : IDisposable
{
    private readonly TimeProvider time;
    private readonly CancellationTokenSource source;
    private readonly Lock gate = new();
    private TimeSpan accrued = TimeSpan.Zero;
    private long runningSince;
    private bool running = true;

    public PausableDeadline(TimeSpan? limit, TimeProvider? timeProvider = null)
    {
        time = timeProvider ?? TimeProvider.System;
        Limit = limit;
        source = new CancellationTokenSource(limit ?? Timeout.InfiniteTimeSpan, time);
        runningSince = time.GetTimestamp();
    }

    public TimeSpan? Limit { get; }

    public CancellationToken Token => source.Token;

    public TimeSpan Active
    {
        get
        {
            lock (gate)
                return running ? accrued + time.GetElapsedTime(runningSince) : accrued;
        }
    }

    public void Pause()
    {
        lock (gate)
        {
            if (!running)
                return;

            accrued += time.GetElapsedTime(runningSince);
            running = false;
            Reschedule(Timeout.InfiniteTimeSpan);
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            if (running)
                return;

            running = true;
            runningSince = time.GetTimestamp();
            if (Limit is { } limit)
                Reschedule(TimeSpan.FromTicks(Math.Max(0, (limit - accrued).Ticks)));
        }
    }

    public void Dispose() => source.Dispose();

    // A session that outlives the deadline's owner by a tick is a no-op, not a crash on the pump thread.
    private void Reschedule(TimeSpan delay)
    {
        try
        {
            source.CancelAfter(delay);
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
