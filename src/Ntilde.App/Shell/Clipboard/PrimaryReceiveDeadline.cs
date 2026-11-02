namespace Ntilde.Shell.Clipboard;

internal sealed class PrimaryReceiveDeadline(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long? _started;
    private long _lastProgress;

    public void Progress()
    {
        _lastProgress = _clock.GetTimestamp();
        _started ??= _lastProgress;
    }

    public void Check()
    {
        if (!_started.HasValue) throw new InvalidOperationException("PRIMARY receive deadline was not started.");
        if (_clock.GetElapsedTime(_lastProgress) >= PrimaryTransferRegistry.IdleLifetime ||
            _clock.GetElapsedTime(_started.Value) >= PrimaryTransferRegistry.MaxLifetime)
            throw new IOException("X11 PRIMARY receive timed out.");
    }
}
