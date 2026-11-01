namespace Ntilde.Shell.Clipboard;

internal sealed record ClipboardReplyPolicy(Func<TimeSpan, CancellationToken, Task>? Delay = null, TimeSpan? Timeout = null, Func<Func<Task>, Task>? Dispatch = null);

/// <summary>Retries bounded device-reply admission without blocking the UI or occupying its reserved input slots.</summary>
internal sealed class ClipboardReplyWriter
{
    private readonly Func<string, bool> _trySend;
    private readonly Func<bool> _alive;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;
    private readonly object _admissionGate;
    private readonly Action? _beforeAdmissionForTest;

    public ClipboardReplyWriter(Func<string, bool> trySend, Func<bool> alive, TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null, object? admissionGate = null, Action? beforeAdmissionForTest = null)
    {
        _trySend = trySend;
        _alive = alive;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _admissionGate = admissionGate ?? new object();
        _beforeAdmissionForTest = beforeAdmissionForTest;
    }

    public bool TrySend(string response)
    {
        lock (_admissionGate) return _alive() && _trySend(response);
    }

    public async Task<bool> SendAsync(string response, CancellationToken cancellation, Action? onAccepted = null)
    {
        long started = _clock.GetTimestamp();
        while (_alive())
        {
            cancellation.ThrowIfCancellationRequested();
            _beforeAdmissionForTest?.Invoke();
            lock (_admissionGate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (_alive() && _trySend(response))
                {
                    onAccepted?.Invoke();
                    return true;
                }
            }
            if (_clock.GetElapsedTime(started) >= _timeout) return false;
            await _delay(TimeSpan.FromMilliseconds(5), cancellation).ConfigureAwait(false);
        }
        return false;
    }
}
