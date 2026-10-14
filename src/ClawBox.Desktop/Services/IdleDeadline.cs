namespace ClawBox.Desktop.Services;

// Only real model/tool activity renews this clock; UI timers and transport pings do not.
public sealed class IdleDeadline : IDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly TimeSpan limit;
    private readonly ITimer timer;
    private long last;
    private bool disposed;
    public CancellationTokenSource Source { get; } = new();
    public IdleDeadline(TimeSpan limit, TimeProvider? clock = null)
    {
        this.limit = limit; this.clock = clock ?? TimeProvider.System;
        last = this.clock.GetTimestamp();
        timer = this.clock.CreateTimer(_ => Check(), null, limit, Timeout.InfiniteTimeSpan);
    }
    public void Signal()
    {
        lock (gate)
        {
            if (disposed || Source.IsCancellationRequested) return;
            last = clock.GetTimestamp(); timer.Change(limit, Timeout.InfiniteTimeSpan);
        }
    }
    public void Check()
    {
        lock (gate)
        {
            if (disposed || Source.IsCancellationRequested) return;
            TimeSpan remaining = limit - clock.GetElapsedTime(last);
            if (remaining <= TimeSpan.Zero) Source.Cancel();
            else timer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; timer.Dispose(); Source.Dispose(); }
    }
}
