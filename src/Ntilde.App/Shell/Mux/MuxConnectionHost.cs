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
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
    public MuxClient? CurrentClient { get { lock (_gate) return _client is { IsConnected: true } c ? c : null; } }

    /// <summary>Starts connecting (spawning the daemon if needed) in the background. Idempotent.</summary>
    public void WarmUp() => _ = StartConnecting();

    /// <summary>
    /// The live client, joining the in-flight attempt or starting a new one. Null when the attempt
    /// failed or did not finish within <paramref name="timeout"/> (it keeps running; the next call
    /// joins it) - never blocks past the timeout.
    /// </summary>
    public MuxClient? GetClient(TimeSpan timeout)
    {
        Task<MuxClient>? attempt = StartConnecting();
        if (attempt is null) return CurrentClient;
        try
        {
            if (!Task.Run(() => attempt).Wait(timeout)) return null;
            return attempt.Result;
        }
        catch (AggregateException ex)
        {
            _log?.Invoke($"[Mux] connection failed: {ex.InnerException?.Message}");
            return null;
        }
    }

    /// <summary>The in-flight or just-finished attempt; null when a live client already exists (or the host is disposed).</summary>
    private Task<MuxClient>? StartConnecting()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return null;
            if (_client is { IsConnected: true }) return null;
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
                }

                return client;
            }, token);
            return _connecting;
        }
    }

    /// <summary>Closes the connection: the daemon detaches every session on it and keeps them running.</summary>
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

        client?.Dispose();
    }
}
