namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The clock a remote <see cref="MuxConnectionHost"/>'s liveness ping and reconnect loop run on (Phase 4
/// spec §7.2, §7.3): one timer per scheduled callback, never a polling loop or a <c>Task.Delay</c> chain on
/// the thread pool. Tests swap in a clock they move by hand.
/// </summary>
internal interface IMuxTimerScheduler
{
    /// <summary>
    /// Runs <paramref name="callback"/> once, <paramref name="due"/> from now, on a thread of the scheduler's -
    /// never synchronously on the caller's, so a caller may schedule while holding its own lock. Disposing the
    /// result before then cancels it; a callback already running is not waited for. Keep the result while the
    /// callback is pending. The callback must do bounded work and should not throw.
    /// </summary>
    IDisposable Schedule(TimeSpan due, Action callback);

    /// <summary>Milliseconds on a monotonic clock; the reconnect budget is measured on it.</summary>
    long NowMs { get; }
}

/// <summary>
/// The app's <see cref="IMuxTimerScheduler"/>: one <see cref="Timer"/> per <see cref="Schedule"/> call, which
/// fires once on the pool and is disposed after firing (or when cancelled). The clock is
/// <see cref="Environment.TickCount64"/>.
/// </summary>
internal sealed class SystemMuxTimerScheduler : IMuxTimerScheduler
{
    private SystemMuxTimerScheduler()
    {
    }

    public static SystemMuxTimerScheduler Instance { get; } = new();

    public long NowMs => Environment.TickCount64;

    public IDisposable Schedule(TimeSpan due, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var entry = new Entry(callback);
        entry.Arm(due < TimeSpan.Zero ? TimeSpan.Zero : due);
        return entry;
    }

    /// <summary>One scheduled callback and its timer. It roots the timer: the caller keeps the entry.</summary>
    private sealed class Entry(Action callback) : IDisposable
    {
        private readonly object _gate = new();
        private Action? _callback = callback; // guarded by _gate; null once run or cancelled
        private Timer? _timer;                // guarded by _gate

        public void Arm(TimeSpan due)
        {
            lock (_gate)
            {
                // Created disarmed and armed under the lock, so a Fire that comes at once (a due of zero)
                // waits for the assignment, and a Dispose cannot slip between the two.
                _timer = new Timer(static state => ((Entry)state!).Fire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _callback = null;
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void Fire()
        {
            Action? run;
            lock (_gate)
            {
                run = _callback;
                _callback = null;
                _timer?.Dispose();
                _timer = null;
            }

            if (run is null) return;
            try
            {
                run();
            }
            catch (Exception ex)
            {
                // A throw on a timer thread would end the process.
                AppLogger.Log($"[Mux] a timer callback threw: {ex}");
            }
        }
    }
}
