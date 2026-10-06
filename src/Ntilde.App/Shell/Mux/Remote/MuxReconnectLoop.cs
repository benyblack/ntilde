namespace Ntilde.Shell.Mux.Remote;

/// <summary>Why a remote host's connection ended (Phase 4 spec §7.3), as the host's classifier tells.</summary>
internal enum MuxDisconnectKind
{
    /// <summary>
    /// The link went away - a ping timeout, ssh's exit 255, the channel's EOF, a native disconnect - and the
    /// daemon, with its sessions, presumably did not. The host reconnects on its own.
    /// </summary>
    LinkLost,

    /// <summary>The proxy exited 3: the daemon's process is gone, and its sessions with it. No automatic retry.</summary>
    DaemonStopped,
}

/// <summary>
/// A remote host's reconnect loop (Phase 4 spec §7.3): after a lost link it calls the host's attempt, backing
/// off 1, 2, 4, 8, 16 then 30 s, each wait jittered by 20% either way under a 30 s ceiling, until an attempt connects or
/// <see cref="Budget"/> has passed since <see cref="Start"/>; then it raises the abandoned callback.
/// </summary>
/// <remarks>
/// It runs on one <see cref="IMuxTimerScheduler"/> timer at a time, rescheduled after each attempt - never a
/// polling loop, never on a client's delivery thread. A timer callback only starts the attempt (the host's
/// connect runs on the pool) and returns; the attempt's completion schedules the next wait. The attempt and
/// the abandoned callback are called outside this loop's lock, so they may call back into it.
/// </remarks>
internal sealed class MuxReconnectLoop : IDisposable
{
    /// <summary>How long after the loss the loop keeps trying (spec §7.3: a constant, not a setting).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    private readonly IMuxTimerScheduler _scheduler;
    private readonly Func<Task<bool>> _attempt;
    private readonly Action _abandoned;
    private readonly Random _jitter; // guarded by _gate
    private readonly object _gate = new();
    private bool _running;           // guarded by _gate
    private bool _disposed;          // guarded by _gate
    private bool _inFlight;          // guarded by _gate: an attempt of the current run has not completed
    private int _run;                // guarded by _gate: bumped by each start and stop; an older run's completion is stale
    private int _nextBackoff;        // guarded by _gate: the Delay index of the next wait
    private long _startedAtMs;       // guarded by _gate
    private IDisposable? _pending;    // guarded by _gate: the wait in progress
    private object? _pendingToken;   // guarded by _gate: identifies that wait's callback, so a cancelled one that fires anyway is ignored

    /// <param name="scheduler">The clock: <see cref="SystemMuxTimerScheduler.Instance"/> in the app.</param>
    /// <param name="attempt">One connect attempt: true once connected, which ends the loop. A throw or a fault is a failure.</param>
    /// <param name="abandoned">Called once when <see cref="Budget"/> ran out with no attempt connected.</param>
    /// <param name="jitter">The jitter's source; <see cref="Random.Shared"/> when null.</param>
    public MuxReconnectLoop(IMuxTimerScheduler scheduler, Func<Task<bool>> attempt, Action abandoned, Random? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(abandoned);
        _scheduler = scheduler;
        _attempt = attempt;
        _abandoned = abandoned;
        _jitter = jitter ?? Random.Shared;
    }

    /// <summary>The wait before attempt number <paramref name="attempt"/> (from 0): 1, 2, 4, 8, 16, then 30 s, times U(0.8, 1.2), at most 30 s.</summary>
    public static TimeSpan Delay(int attempt, Random jitter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attempt);
        ArgumentNullException.ThrowIfNull(jitter);
        double seconds = attempt < 5 ? 1 << attempt : Ceiling.TotalSeconds;
        TimeSpan delay = TimeSpan.FromSeconds(seconds * (0.8 + (0.4 * jitter.NextDouble())));
        return delay < Ceiling ? delay : Ceiling;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    /// <summary>Starts a run: the budget is counted from now, and the first attempt follows the first wait. A no-op while running, or once disposed.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _running) return;
            _running = true;
            _run++;
            _inFlight = false;
            _startedAtMs = _scheduler.NowMs;
            _nextBackoff = 0;
            ScheduleNextLocked();
        }
    }

    /// <summary>Ends the run quietly - the host connected some other way. The abandoned callback is not called.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    /// <summary>
    /// A user's request (Enter in a pane, spec §7.3): the pending wait is cancelled and an attempt starts now,
    /// on the caller's thread - the attempt only starts the host's connect - and the backoff starts over from
    /// its first wait. An attempt already in flight is that attempt: only the backoff is reset. A no-op when
    /// the loop is not running.
    /// </summary>
    public void TryNow()
    {
        int run;
        lock (_gate)
        {
            if (!_running) return;
            _nextBackoff = 0;
            if (_inFlight) return;
            CancelPendingLocked();
            _inFlight = true;
            run = _run;
        }

        RunAttempt(run);
    }

    /// <summary>
    /// A user's request that already runs an attempt of its own (<see cref="MuxConnectionHost.GetClient"/>):
    /// that attempt is the loop's attempt now. The pending wait is cancelled, <paramref name="attempt"/>'s
    /// outcome counts as the loop's attempt, and the backoff starts over. Unlike <see cref="TryNow()"/> it starts
    /// nothing itself, so a user's attempt that has already finished costs no extra connect: its outcome just
    /// schedules the first wait again. While an attempt of the loop's is in flight, only the backoff is reset.
    /// </summary>
    public void TryNow(Task<bool> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        int run;
        lock (_gate)
        {
            if (!_running) return;
            _nextBackoff = 0;
            if (_inFlight) return;
            CancelPendingLocked();
            _inFlight = true;
            run = _run;
        }

        Follow(run, attempt);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            StopLocked();
        }
    }

    private void StopLocked()
    {
        if (!_running) return;
        _running = false;
        _run++;
        _inFlight = false;
        CancelPendingLocked();
    }

    private void CancelPendingLocked()
    {
        _pending?.Dispose();
        _pending = null;
        _pendingToken = null;
    }

    private void ScheduleNextLocked()
    {
        TimeSpan delay = Delay(_nextBackoff, _jitter);
        _nextBackoff++;
        // The last attempt lands on the budget's end, not past it.
        TimeSpan remaining = Budget - TimeSpan.FromMilliseconds(_scheduler.NowMs - _startedAtMs);
        if (delay > remaining) delay = remaining;

        var token = new object();
        _pendingToken = token;
        _pending = _scheduler.Schedule(delay, () => OnWaitOver(token));
    }

    /// <summary>The scheduler's thread: starts the attempt and returns.</summary>
    private void OnWaitOver(object token)
    {
        int run;
        lock (_gate)
        {
            if (!_running || !ReferenceEquals(token, _pendingToken)) return;
            _pending = null;
            _pendingToken = null;
            _inFlight = true;
            run = _run;
        }

        RunAttempt(run);
    }

    private void RunAttempt(int run)
    {
        Task<bool> attempt;
        try
        {
            attempt = _attempt();
        }
        catch (Exception ex)
        {
            attempt = Task.FromException<bool>(ex);
        }

        Follow(run, attempt);
    }

    private void Follow(int run, Task<bool> attempt)
    {
        // Synchronously when it already completed; otherwise on the thread that completes it (the host's
        // connect, on the pool), where scheduling the next wait is all there is to do.
        _ = attempt.ContinueWith(done => OnAttemptDone(run, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void OnAttemptDone(int run, Task<bool> attempt)
    {
        bool connected = attempt.IsCompletedSuccessfully && attempt.Result;
        lock (_gate)
        {
            if (!_running || run != _run) return;
            _inFlight = false;
            if (connected)
            {
                StopLocked();
                return;
            }

            if (_scheduler.NowMs - _startedAtMs < (long)Budget.TotalMilliseconds)
            {
                ScheduleNextLocked();
                return;
            }

            StopLocked();
        }

        _abandoned();
    }
}
