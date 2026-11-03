using System;
using System.Threading;

namespace Ntilde.Shell;

/// <summary>UI-owned monotonic text-blink clock; render-thread feedback is a single atomic fact.</summary>
internal sealed class TextBlinkAnimation
{
    private const long HalfPeriodMilliseconds = 530;
    internal static readonly TimeSpan HalfPeriod = TimeSpan.FromMilliseconds(HalfPeriodMilliseconds);
    private readonly TimeProvider _clock;
    private int _reportedCells;
    private bool _active;
    private long _epoch;

    internal TextBlinkAnimation(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    internal bool ForegroundVisible { get; private set; } = true;

    /// <summary>Called by the compositor; it never touches controls or schedules work.</summary>
    internal void ReportCells(bool hasBlinkInk) => Volatile.Write(ref _reportedCells, hasBlinkInk ? 1 : 0);

    /// <summary>Called by the existing UI render tick. Only a visible phase change needs a redraw.</summary>
    internal bool Advance()
    {
        if (Volatile.Read(ref _reportedCells) == 0)
        {
            _active = false;
            ForegroundVisible = true;
            return false;
        }
        if (!_active)
        {
            _epoch = _clock.GetTimestamp();
            _active = true;
        }
        bool next = IsVisibleAt(_clock.GetElapsedTime(_epoch));
        bool changed = next != ForegroundVisible;
        ForegroundVisible = next;
        return changed;
    }

    internal void Restart(bool clearCells = false)
    {
        _active = false;
        ForegroundVisible = true;
        if (clearCells) Volatile.Write(ref _reportedCells, 0);
    }

    internal static bool IsVisibleAtMilliseconds(long elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsed);
        return (elapsed / HalfPeriodMilliseconds & 1) == 0;
    }

    internal static bool IsVisibleAt(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        return (elapsed.Ticks / HalfPeriod.Ticks & 1) == 0;
    }
}
