using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// A clock the test moves by hand (Phase 4 spec §7.2, §7.3): nothing runs until <see cref="Advance(long)"/>,
/// which then runs every callback that falls due, in due order, on the test's thread. Time does not pass on
/// its own, so a liveness ping or a reconnect backoff is exactly as long as the test says.
/// </summary>
internal sealed class FakeMuxTimerScheduler : IMuxTimerScheduler
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private long _now;
    private long _sequence;

    public long NowMs
    {
        get { lock (_gate) return _now; }
    }

    /// <summary>Callbacks scheduled and neither run nor cancelled.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>How long until the earliest pending callback; null when none is pending.</summary>
    public TimeSpan? NextDueIn
    {
        get
        {
            lock (_gate) return _entries.Count == 0 ? null : TimeSpan.FromMilliseconds(_entries.Min(e => e.DueMs) - _now);
        }
    }

    public IDisposable Schedule(TimeSpan due, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            var entry = new Entry(this, _now + Math.Max(0L, (long)due.TotalMilliseconds), _sequence++, callback);
            _entries.Add(entry);
            return entry;
        }
    }

    public void Advance(TimeSpan by) => Advance((long)by.TotalMilliseconds);

    /// <summary>
    /// Moves the clock <paramref name="ms"/> forward, running each callback whose time comes on the way at
    /// that time - those that the callbacks themselves schedule inside the window included.
    /// </summary>
    public void Advance(long ms)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ms);
        long target;
        lock (_gate) target = _now + ms;

        while (true)
        {
            Entry? next;
            lock (_gate)
            {
                next = _entries.Where(e => e.DueMs <= target).OrderBy(e => e.DueMs).ThenBy(e => e.Sequence).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _entries.Remove(next);
                _now = Math.Max(_now, next.DueMs);
            }

            next.Callback();
        }
    }

    private void Cancel(Entry entry)
    {
        lock (_gate) _entries.Remove(entry);
    }

    private sealed class Entry(FakeMuxTimerScheduler owner, long dueMs, long sequence, Action callback) : IDisposable
    {
        public long DueMs { get; } = dueMs;
        public long Sequence { get; } = sequence;
        public Action Callback { get; } = callback;

        public void Dispose() => owner.Cancel(this);
    }
}
