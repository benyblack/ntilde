using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>
/// The GUI's one connection to the daemon (spec §6). Every pane's MuxClientSession shares it.
/// <see cref="GetClient"/> is synchronous because ITerminalSessionFactory.Create is, and runs on
/// the UI thread: it waits inside Task.Run so no UI sync context is ever captured.
/// </summary>
internal sealed class MuxConnectionHost : IDisposable
{
    private readonly Func<CancellationToken, Task<MuxClient>> _connect;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposed = new();
    private MuxClient? _client;
    private Task<MuxClient>? _connecting;
    private int _connectAttempts;
    private long? _failedAtMs; // Environment.TickCount64 of the last failure; null = none, or cleared by a success
    private Exception? _lastFailure;

    public MuxConnectionHost(Func<CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log)
    {
        _connect = connect;
        Endpoint = endpoint;
        _log = log;
    }

    public static MuxConnectionHost CreateDefault(Action<string>? log)
    {
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(log);
        return new MuxConnectionHost(launcher.EnsureConnectedAsync, MuxDiscovery.GetDefaultEndpoint(), log);
    }

    public string? Endpoint { get; }

    /// <summary>
    /// After a failed attempt (or a GetClient that timed out on one), how long GetClient answers
    /// null at once instead of blocking the UI again (spec §10.6: block only on first use).
    /// </summary>
    public TimeSpan FailureCooldown { get; init; } = TimeSpan.FromSeconds(30);
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
    public MuxClient? CurrentClient { get { lock (_gate) return _client is { IsConnected: true } c ? c : null; } }

    /// <summary>Why the most recent connection attempt failed (its base exception); null once one succeeds.</summary>
    public Exception? LastFailure { get { lock (_gate) return _lastFailure; } }

    /// <summary>Starts connecting (spawning the daemon if needed) in the background. Idempotent; a no-op during the failure cooldown.</summary>
    public void WarmUp() => _ = StartConnecting();

    /// <summary>
    /// The live client, joining the in-flight attempt or starting a new one. Null when the attempt
    /// failed or did not finish within <paramref name="timeout"/> (it keeps running and may still
    /// succeed) - never blocks past the timeout. Either failure starts <see cref="FailureCooldown"/>,
    /// during which this returns null at once so a dead daemon costs one wait, not one per pane.
    /// </summary>
    public MuxClient? GetClient(TimeSpan timeout)
    {
        Task<MuxClient>? attempt = StartConnecting();
        if (attempt is null) return CurrentClient;
        try
        {
            if (!Task.Run(() => attempt).Wait(timeout))
            {
                _log?.Invoke($"[Mux] the multiplexer was not ready within {timeout.TotalSeconds:0.#} s; new panes will not persist for {FailureCooldown.TotalSeconds:0.#} s");
                RecordFailure(attempt);
                return null;
            }

            return attempt.Result;
        }
        catch (AggregateException ex)
        {
            // Logged once by the attempt's own fault continuation. Recorded here as well so the very
            // next call is already inside the cooldown, whichever of the two runs first.
            RecordFailure(attempt, ex.GetBaseException());
            return null;
        }
    }

    /// <summary>Starts the cooldown, unless <paramref name="attempt"/> is stale or a client came up meanwhile.</summary>
    private void RecordFailure(Task<MuxClient> attempt, Exception? reason = null)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(attempt, _connecting) || _client is { IsConnected: true }) return;
            _failedAtMs = Environment.TickCount64;
            if (reason is not null) _lastFailure = reason;
        }
    }

    private bool InCooldown() =>
        _failedAtMs is long failedAt && Environment.TickCount64 - failedAt < (long)FailureCooldown.TotalMilliseconds;

    /// <summary>The in-flight or just-finished attempt; null when a live client already exists (or the host is disposed).</summary>
    private Task<MuxClient>? StartConnecting()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return null;
            if (_client is { IsConnected: true }) return null;
            // Before the in-flight check: a caller that already timed out on the running attempt must
            // not make the next pane wait on it again.
            if (InCooldown()) return null;
            // Every concurrent caller must join the same attempt: one daemon spawn, one client.
            if (_connecting is { IsCompleted: false }) return _connecting;
            if (_connecting is { IsCompletedSuccessfully: true } done && done.Result.IsConnected && _client is null)
            {
                _client = done.Result;
                return null;
            }

            _client = null;
            Interlocked.Increment(ref _connectAttempts);
            CancellationToken token = _disposed.Token;
            _connecting = Task.Run(async () =>
            {
                MuxClient client = await _connect(token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed.IsCancellationRequested) { client.Dispose(); throw new ObjectDisposedException(nameof(MuxConnectionHost)); }
                    _client = client;
                    _failedAtMs = null;
                    _lastFailure = null;
                }

                return client;
            }, token);
            Task<MuxClient> attempt = _connecting;
            // Covers WarmUp too, whose failure nothing awaits: log the reason once, start the cooldown.
            _ = attempt.ContinueWith(
                t =>
                {
                    _log?.Invoke($"[Mux] connection failed: {t.Exception?.GetBaseException().Message}");
                    RecordFailure(attempt, t.Exception?.GetBaseException());
                },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return attempt;
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for the daemon to work through what is already queued.</summary>
    public TimeSpan DisposeFlushTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Closes the connection: the daemon detaches every session on it and keeps them running.
    /// First a bounded flush: closing drops frames still queued, and a pane closed just before the
    /// window (the last tab) has queued a kill. The server reads frames in order, so a ping reply
    /// means every earlier frame was handled. Waited inside Task.Run: no UI sync context captured.
    /// </summary>
    public void Dispose()
    {
        MuxClient? client;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel();
            client = _client;
            _client = null;
        }

        if (client is null) return;
        if (client.IsConnected)
        {
            try
            {
                using var cts = new CancellationTokenSource(DisposeFlushTimeout);
                if (!Task.Run(() => client.PingAsync(cts.Token)).Wait(DisposeFlushTimeout))
                {
                    _log?.Invoke($"[Mux] the multiplexer did not confirm pending requests within {DisposeFlushTimeout.TotalSeconds:0.#} s; closing anyway");
                }
            }
            catch (AggregateException)
            {
                // A dead or cancelled connection: nothing more can be flushed.
            }
        }

        client.Dispose();
    }
}
