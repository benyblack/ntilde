namespace Ntilde.Shell.Clipboard;

/// <summary>A cumulative budget shared by all native replies within an operation.</summary>
internal sealed class PrimaryOperationDeadline
{
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly TimeSpan _limit;

    public PrimaryOperationDeadline(TimeSpan limit, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _started = _clock.GetTimestamp();
        _limit = limit;
    }

    public void Check()
    {
        if (_clock.GetElapsedTime(_started) >= _limit)
            throw new IOException("X11 PRIMARY operation exceeded its cumulative time budget.");
    }
}
