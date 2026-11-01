using System.Threading;

namespace Ntilde.Tests.AgentOutput;

/// <summary>One-shot timers driven explicitly, with callbacks outside the clock lock.</summary>
internal sealed class ManualRefreshTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _now; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(by.Ticks);
        long target;
        lock (_gate) target = _now + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due is long due && due <= target)
                    .OrderBy(timer => timer.Due).FirstOrDefault();
                if (next == null)
                {
                    _now = Math.Max(_now, target);
                    return;
                }
                _now = Math.Max(_now, next.Due.GetValueOrDefault());
                next.Due = null;
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualRefreshTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;
        public long? Due { get; set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new ArgumentException("Only one-shot timers are needed.", nameof(period));
            lock (owner._gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + Math.Max(0, dueTime.Ticks);
                return true;
            }
        }
        public void Fire() => callback(state);
        public void Dispose()
        {
            lock (owner._gate)
            {
                _disposed = true;
                Due = null;
                owner._timers.Remove(this);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
