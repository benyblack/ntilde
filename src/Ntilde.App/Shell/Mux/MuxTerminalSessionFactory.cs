using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Local panes → a session in the daemon (spawned, or reopened by id); SSH → <see cref="_fallback"/>.
/// Returns the MuxClientSession UNATTACHED: the pane attaches after wiring its handlers, so no
/// snapshot can be missed (spec §7). Never throws for a daemon problem: it falls back to a normal
/// local session and reports <see cref="PersistentSessionOutcome.Unavailable"/>.
/// </summary>
internal sealed class MuxTerminalSessionFactory : IPersistentSessionFactory
{
    private readonly ITerminalSessionFactory _fallback;
    private readonly Action<string>? _log;

    public MuxTerminalSessionFactory(MuxConnectionHost host, ITerminalSessionFactory fallback, Action<string>? log)
    {
        Host = host;
        _fallback = fallback;
        _log = log;
    }

    public MuxConnectionHost Host { get; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public ITerminalSession Create(TerminalSessionRequest request) => CreatePersistent(request).Session;

    public PersistentSessionResult CreatePersistent(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ssh is not null) return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);

        MuxClient? client = Host.GetClient(ConnectTimeout);
        if (client is null) return Fallback(request, "the multiplexer could not be reached");

        try
        {
            if (request.ExistingMuxSessionId is Guid existing)
            {
                IReadOnlyList<SessionSummary> sessions = Rpc(ct => client.ListSessionsAsync(ct));
                if (sessions.Any(s => s.SessionId == existing && s.Running && !s.Faulted))
                {
                    return new(client.OpenSession(existing, request.Command, request.Arguments), PersistentSessionOutcome.Reattached, Host.Endpoint, null);
                }

                Guid fresh = Spawn(client, request);
                return new(client.OpenSession(fresh, request.Command, request.Arguments), PersistentSessionOutcome.PreviousLost, Host.Endpoint, null);
            }

            Guid spawned = Spawn(client, request);
            return new(client.OpenSession(spawned, request.Command, request.Arguments), PersistentSessionOutcome.Spawned, Host.Endpoint, null);
        }
        catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return Fallback(request, ex.Message);
        }
    }

    private Guid Spawn(MuxClient client, TerminalSessionRequest r) => Rpc(ct => client.SpawnAsync(new SpawnParams
    {
        Command = r.Command,
        Arguments = r.Arguments,
        StartingDirectory = r.StartingDirectory,
        Cols = r.Cols,
        Rows = r.Rows,
        EnvironmentOverrides = r.EnvironmentOverrides,
        SkipPowerShellPostLaunchInit = r.SkipPowerShellPostLaunchInit,
        Title = r.Command,
    }, ct));

    /// <summary>
    /// Runs one request off the calling (UI) thread and waits at most <see cref="RpcTimeout"/>.
    /// WaitAny, not Task.Wait: Wait throws AggregateException for a faulted task, which the
    /// caller's filter would not match; GetResult rethrows the original exception instead.
    /// </summary>
    private T Rpc<T>(Func<CancellationToken, Task<T>> call)
    {
        using var cts = new CancellationTokenSource(RpcTimeout);
        Task<T> task = Task.Run(() => call(cts.Token));
        if (Task.WaitAny([task], RpcTimeout + TimeSpan.FromMilliseconds(250)) < 0) throw new TimeoutException("The multiplexer did not answer in time.");
        return task.GetAwaiter().GetResult();
    }

    private PersistentSessionResult Fallback(TerminalSessionRequest request, string why)
    {
        _log?.Invoke($"[Mux] multiplexer unavailable ({why}); this session will not persist");
        return new(_fallback.Create(request), PersistentSessionOutcome.Unavailable, null, why);
    }
}
