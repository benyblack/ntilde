using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Pty;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Local panes → a session in the daemon (spawned, or reopened by id); SSH → <see cref="_fallback"/>.
/// Returns the MuxClientSession UNATTACHED: the pane attaches after wiring its handlers, so no
/// snapshot can be missed (spec §7). Never throws for a daemon problem: a new pane falls back to a
/// normal local session (<see cref="PersistentSessionOutcome.Unavailable"/>); a pane reopening a
/// daemon session gets no session at all (<see cref="PersistentSessionOutcome.DaemonUnreachable"/>),
/// because its shell is still in the daemon and a stand-in local shell would lose it.
/// Every result that names a daemon session carries its <see cref="MuxEndpointId"/> (Phase 4 spec §5),
/// which the pane persists as PaneNode.MuxEndpoint.
/// </summary>
internal sealed class MuxTerminalSessionFactory : IPersistentSessionFactory
{
    private readonly ITerminalSessionFactory _fallback;
    private readonly Action<string>? _log;
    private readonly TimeSpan? _connectTimeout; // set: overrides every host's policy (tests)
    private readonly TimeSpan? _rpcTimeout;     // set: overrides every host's policy (tests)

    public MuxTerminalSessionFactory(MuxConnectionHosts hosts, ITerminalSessionFactory fallback, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        Hosts = hosts;
        _fallback = fallback;
        _log = log;
    }

    /// <summary>The local daemon only: a registry with no remote endpoints (existing callers and tests).</summary>
    public MuxTerminalSessionFactory(MuxConnectionHost local, ITerminalSessionFactory fallback, Action<string>? log)
        : this(new MuxConnectionHosts(local, static _ => null, log), fallback, log)
    {
    }

    /// <summary>One host per endpoint; the window owns and disposes it.</summary>
    public MuxConnectionHosts Hosts { get; }

    /// <summary>The local daemon's host.</summary>
    public MuxConnectionHost Host => Hosts.Local;

    /// <summary>The local host's connect wait (its <see cref="MuxHostPolicy"/>); a value set here overrides every host's.</summary>
    public TimeSpan ConnectTimeout
    {
        get => _connectTimeout ?? Host.Policy.ConnectTimeout;
        init => _connectTimeout = value;
    }

    /// <summary>The local host's request wait (its <see cref="MuxHostPolicy"/>); a value set here overrides every host's.</summary>
    public TimeSpan RpcTimeout
    {
        get => _rpcTimeout ?? Host.Policy.RpcTimeout;
        init => _rpcTimeout = value;
    }

    /// <summary>The plain factory contract has no "no session" answer: an unreachable reopen (or an ended share) falls back here.</summary>
    public ITerminalSession Create(TerminalSessionRequest request) => CreatePersistent(request).Session ?? _fallback.Create(request);

    public PersistentSessionResult CreatePersistent(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ssh is not null) return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);

        return CreateOn(TargetFor(Hosts.Local, MuxEndpointId.Local), request);
    }

    /// <summary>The endpoint a request goes to, its host, and the waits that host's policy gives it.</summary>
    private readonly record struct Target(MuxConnectionHost Host, MuxEndpointId Endpoint, TimeSpan ConnectTimeout, TimeSpan RpcTimeout)
    {
        public string Name => Endpoint.ToString();
    }

    private Target TargetFor(MuxConnectionHost host, MuxEndpointId endpoint) =>
        new(host, endpoint, _connectTimeout ?? host.Policy.ConnectTimeout, _rpcTimeout ?? host.Policy.RpcTimeout);

    private PersistentSessionResult CreateOn(Target target, TerminalSessionRequest request)
    {
        MuxConnectionHost host = target.Host;
        MuxClient? client = host.GetClient(target.ConnectTimeout);
        if (client is null)
        {
            // A daemon of another protocol version is reachable but unusable: say so, and how to fix it.
            if (host.LastFailure is MuxUnavailableException { VersionMismatch: true } mismatch)
            {
                return Fallback(target, request, mismatch.Message) with { VersionMismatch = true };
            }

            if (host.LastFailure is MuxUnavailableException { OrphanedDaemon: true } orphaned)
            {
                return Fallback(target, request, orphaned.Message) with { OrphanedDaemon = true };
            }

            return Fallback(target, request, "the multiplexer could not be reached");
        }

        try
        {
            if (request.ExistingMuxSessionId is Guid existing)
            {
                IReadOnlyList<SessionSummary> sessions = Rpc(target, ct => client.ListSessionsAsync(ct));
                SessionSummary? match = sessions.FirstOrDefault(s => s.SessionId == existing);

                if (request.AttachShared)
                {
                    // A deliberate share: join whatever else is attached, running or exited (an exited
                    // session shows its last screen and exit). Gone or faulted: no shell at all - a
                    // fresh one is not what the user chose.
                    if (match is { Faulted: false })
                    {
                        return new(client.OpenSession(existing, request.Command, request.Arguments, MuxAttachMode.Shared), PersistentSessionOutcome.Reattached, target.Name, null)
                        {
                            AlreadyExited = !match.Running,
                        };
                    }

                    _log?.Invoke($"[Mux] shared session {existing} is gone or faulted; nothing to attach");
                    return new(null, PersistentSessionOutcome.ShareEnded, target.Name, "the shared session has ended");
                }

                if (match is { Running: true, Faulted: false } && !MuxCommandMatch.SameExecutable(match.Command, request.Command))
                {
                    // Not this pane's shell (a hand-edited or foreign session file): leave it running
                    // untouched and start what the pane asked for. Nothing was lost, so no banner.
                    // Checked before the IfUnattached attach, so a lost race is only ever over this pane's own shell.
                    _log?.Invoke($"[Mux] session {existing} runs '{match.Command}', not '{request.Command}'; starting a new shell instead of reattaching");
                    return SpawnFresh(target, client, request, PersistentSessionOutcome.Spawned);
                }

                if (match is { Running: true, Faulted: false })
                {
                    if (client.ProtocolVersion >= MuxProtocol.SessionEventsVersion)
                    {
                        // Exclusive by protocol: the attach itself refuses (session_attached) if another
                        // interactive client holds it, decided on the daemon's parse thread (spec §3).
                        // No client-side count check - two instances could both pass one.
                        return new(client.OpenSession(existing, request.Command, request.Arguments, MuxAttachMode.IfUnattached), PersistentSessionOutcome.Reattached, target.Name, null);
                    }

                    if (match.AttachedClients == 0)
                    {
                        return new(client.OpenSession(existing, request.Command, request.Arguments), PersistentSessionOutcome.Reattached, target.Name, null);
                    }

                    // v1: the Phase 2 check. Another client is showing it; leave it there.
                    _log?.Invoke($"[Mux] session {existing} is attached elsewhere; starting a new shell instead of taking it over");
                    return SpawnFresh(target, client, request, PersistentSessionOutcome.AttachedElsewhere);
                }

                // A faulted session is still running in the daemon: nothing will ever reopen it, so
                // end it now rather than leak its shell until the daemon exits.
                if (match is { Running: true, Faulted: true }) KillQuietly(target, client, existing);

                return SpawnFresh(target, client, request, PersistentSessionOutcome.PreviousLost);
            }

            return SpawnFresh(target, client, request, PersistentSessionOutcome.Spawned);
        }
        catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return Fallback(target, request, ex.Message);
        }
    }

    /// <summary>Spawns the pane's shell in the daemon and opens it (Shared: a new session has no other client).</summary>
    private static PersistentSessionResult SpawnFresh(Target target, MuxClient client, TerminalSessionRequest request, PersistentSessionOutcome outcome) =>
        new(client.OpenSession(Spawn(target, client, request), request.Command, request.Arguments), outcome, target.Name, null);

    private static Guid Spawn(Target target, MuxClient client, TerminalSessionRequest r) => Rpc(target, ct => client.SpawnAsync(new SpawnParams
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

    /// <summary>Best effort, bounded by the target's RPC timeout: a failure is logged, never thrown.</summary>
    private void KillQuietly(Target target, MuxClient client, Guid id)
    {
        try
        {
            Rpc(target, async ct =>
            {
                await client.KillAsync(id, ct).ConfigureAwait(false);
                return true;
            });
        }
        catch (Exception ex) when (ex is MuxProtocolException or IOException or TimeoutException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            _log?.Invoke($"[Mux] could not end faulted session {id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs one request off the calling (UI) thread and waits at most the target's RPC timeout
    /// (its host's <see cref="MuxHostPolicy.RpcTimeout"/>, or <see cref="RpcTimeout"/> when set).
    /// WaitAny, not Task.Wait: Wait throws AggregateException for a faulted task, which the
    /// caller's filter would not match; GetResult rethrows the original exception instead.
    /// </summary>
    private static T Rpc<T>(Target target, Func<CancellationToken, Task<T>> call)
    {
        TimeSpan timeout = target.RpcTimeout;
        using var cts = new CancellationTokenSource(timeout);
        Task<T> task = Task.Run(() => call(cts.Token));
        if (Task.WaitAny([task], timeout + TimeSpan.FromMilliseconds(250)) < 0) throw new TimeoutException("The multiplexer did not answer in time.");
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// A reopen keeps its id for a retry, and so its endpoint: the pane writes both back. A new pane's
    /// local fallback shell lives on no endpoint.
    /// </summary>
    private PersistentSessionResult Fallback(Target target, TerminalSessionRequest request, string why)
    {
        if (request.ExistingMuxSessionId is Guid existing)
        {
            _log?.Invoke($"[Mux] multiplexer unavailable ({why}); session {existing} is kept for a retry, no local shell started");
            return new(null, PersistentSessionOutcome.DaemonUnreachable, target.Name, why);
        }

        _log?.Invoke($"[Mux] multiplexer unavailable ({why}); this session will not persist");
        return new(_fallback.Create(request), PersistentSessionOutcome.Unavailable, null, why);
    }
}
