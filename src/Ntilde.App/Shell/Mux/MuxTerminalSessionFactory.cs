using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Pty;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Local panes → a session in the local daemon (spawned, or reopened by id). SSH panes whose profile
/// persists remote sessions (<see cref="SshMuxOptions.PersistRemoteSessions"/>) → a session in that
/// profile's remote daemon (Phase 4 spec §7.5). Every other SSH pane → <see cref="_fallback"/>.
/// Returns the MuxClientSession UNATTACHED: the pane attaches after wiring its handlers, so no
/// snapshot can be missed (spec §7). Never throws for a daemon problem: a new pane falls back to a
/// normal session - a local shell, or for a remote endpoint a plain SSH session
/// (<see cref="PersistentSessionOutcome.Unavailable"/>); a pane reopening a daemon session gets no
/// session at all (<see cref="PersistentSessionOutcome.DaemonUnreachable"/>), because its shell is
/// still in the daemon and a stand-in would lose it.
/// Every result that names a daemon session carries its <see cref="MuxEndpointId"/> (Phase 4 spec §5),
/// which the pane persists as PaneNode.MuxEndpoint; a remote endpoint's results also carry its host's
/// name and, when the connect failed, its classified failure.
/// </summary>
/// <remarks>
/// A deviation from spec §7.5, by controller ruling: a new remote tab falls back to plain SSH only when
/// SSH worked and ntilde-mux did not (NotInstalled, Unsupported, VersionMismatch, ProxyFailed, or a
/// request that failed once connected). When the SSH connect itself failed (SshFailed, or NeedsUser), or the failure
/// is unclassified (the connect did not finish in time, the connection could not be set up), it gets no
/// session - <see cref="PersistentSessionOutcome.DaemonUnreachable"/> with no id - and the pane offers a
/// retry: a plain SSH session would only fail again, or prompt again.
/// </remarks>
internal sealed class MuxTerminalSessionFactory : IPersistentSessionFactory
{
    private readonly ITerminalSessionFactory _fallback;
    private readonly Func<Guid, SshProfile?> _resolveProfile;
    private readonly Action<string>? _log;
    private readonly TimeSpan? _connectTimeout; // set: overrides every host's policy (tests)
    private readonly TimeSpan? _rpcTimeout;     // set: overrides every host's policy (tests)

    /// <param name="hosts">One connection host per endpoint; the window owns and disposes it, and releases a remote one no pane needs.</param>
    /// <param name="fallback">Serves what no daemon does: a new pane whose daemon failed, and every SSH pane that is not persisted.</param>
    /// <param name="resolveProfile">
    /// The SSH profile store's lookup. An SSH request goes to a remote daemon only when its profile has
    /// <see cref="SshMuxOptions.PersistRemoteSessions"/> (<see cref="RoutesRemote"/>).
    /// </param>
    public MuxTerminalSessionFactory(MuxConnectionHosts hosts, ITerminalSessionFactory fallback, Func<Guid, SshProfile?> resolveProfile, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(resolveProfile);
        Hosts = hosts;
        _fallback = fallback;
        _resolveProfile = resolveProfile;
        _log = log;
    }

    /// <summary>No SSH profile is persisted: every SSH request goes to <paramref name="fallback"/>.</summary>
    public MuxTerminalSessionFactory(MuxConnectionHosts hosts, ITerminalSessionFactory fallback, Action<string>? log)
        : this(hosts, fallback, static _ => null, log)
    {
    }

    /// <summary>The local daemon only: a registry with no remote endpoints (existing callers and tests).</summary>
    public MuxTerminalSessionFactory(MuxConnectionHost local, ITerminalSessionFactory fallback, Action<string>? log)
        : this(new MuxConnectionHosts(local, static _ => null, log), fallback, log)
    {
    }

    /// <summary>One host per endpoint; the window owns and disposes it, and releases a remote one no pane needs (<see cref="MuxConnectionHosts.Release"/>).</summary>
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

    /// <summary>
    /// Whether <paramref name="request"/> goes to a remote daemon (Phase 4 spec §7.4's routing predicate):
    /// an SSH request whose profile exists and has <see cref="SshMuxOptions.PersistRemoteSessions"/>. Such a
    /// request may wait minutes for its connection: see <see cref="CreatePersistent"/>.
    /// </summary>
    public bool RoutesRemote(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Ssh is { } ssh && PersistedProfile(ssh.ProfileId) is not null;
    }

    /// <summary>
    /// The remote host a request that <see cref="RoutesRemote"/> goes to, as the user knows it (<c>user@host</c>,
    /// what its results' <see cref="PersistentSessionResult.HostDisplayName"/> will say): for the pane's banner
    /// before the first result is back (Phase 4 spec §7.4). Null when the request does not route remote.
    /// </summary>
    public string? RemoteHostDisplayName(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Ssh is { } ssh && PersistedProfile(ssh.ProfileId) is { } profile ? RemoteMuxConnector.DisplayNameOf(profile) : null;
    }

    /// <summary>
    /// The fallback's session, for an SSH request the caller already found does not route remote (plain SSH):
    /// not routed again, so a profile that starts persisting meanwhile can never make the caller - the UI
    /// thread - wait on a remote connection (Phase 4 spec §7.4).
    /// </summary>
    public ITerminalSession CreateNotPersistent(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _fallback.Create(request);
    }

    /// <summary>
    /// The plain factory contract has no "no session" answer: an unreachable reopen (or an ended share) falls back here.
    /// As with <see cref="CreatePersistent"/>, a request that <see cref="RoutesRemote"/> must not be made on the UI thread.
    /// </summary>
    public ITerminalSession Create(TerminalSessionRequest request) => CreatePersistent(request).Session ?? _fallback.Create(request);

    /// <summary>The session for <paramref name="request"/>, and what became of its persistence.</summary>
    /// <remarks>
    /// A remote request (<see cref="RoutesRemote"/>) must not be made on the UI thread. It waits for its
    /// connection for up to the remote host's connect timeout (<see cref="MuxHostPolicy.Remote"/>: two
    /// minutes, the time a user may take over an SSH password or host-key prompt), and those prompts are
    /// shown through the UI thread: a UI-thread caller would block the very dialog it waits on until the
    /// timeout (Phase 4 spec §7.4). A local request waits at most the local daemon's few seconds.
    /// <para>
    /// A remote request whose SSH connect failed (<see cref="RemoteFailureKind.SshFailed"/>, or
    /// <see cref="RemoteFailureKind.NeedsUser"/>: signing in needed an answer nobody gave), or failed
    /// unclassified (<see cref="PersistentSessionResult.RemoteFailure"/> null: no answer in time, or the
    /// connection could not be set up), gets <see cref="PersistentSessionOutcome.DaemonUnreachable"/> and no
    /// session even when it names no existing session - a deviation from spec §7.5, which falls a new tab
    /// back to plain SSH; by controller ruling that happens only when SSH itself worked. The result still
    /// names the endpoint and the host, so the pane can show "[&lt;host&gt; not reachable - press Enter to
    /// retry]".
    /// </para>
    /// </remarks>
    public PersistentSessionResult CreatePersistent(TerminalSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ssh is { } ssh && PersistedProfile(ssh.ProfileId) is { } profile) return CreateRemote(request, ssh.ProfileId, profile);
        if (request.Ssh is not null) return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);

        return CreateOn(TargetFor(Hosts.Local, MuxEndpointId.Local), request);
    }

    /// <summary>The profile, when it persists its remote sessions; null when it is gone or does not.</summary>
    private SshProfile? PersistedProfile(Guid profileId) =>
        _resolveProfile(profileId) is { MuxOptions.PersistRemoteSessions: true } profile ? profile : null;

    /// <summary>
    /// Phase 4 spec §7.5: the request goes to its profile's remote daemon, through that endpoint's host -
    /// built on first use and then shared by every pane of the profile, so they share one connection and
    /// one set of prompts. Every result names the endpoint and its host.
    /// </summary>
    private PersistentSessionResult CreateRemote(TerminalSessionRequest request, Guid profileId, SshProfile profile)
    {
        MuxEndpointId endpoint = MuxEndpointId.ForSsh(profileId);
        MuxConnectionHost? host;
        try
        {
            host = Hosts.GetOrCreate(endpoint);
        }
        catch (Exception ex)
        {
            // The connection could not even be set up (the window's creator threw): nothing is known
            // to work, so - as for an SSH failure - no plain SSH stand-in, and the pane offers a retry.
            _log?.Invoke($"[Mux] could not set up the connection for {endpoint} ({ex.Message}); no session started, the tab offers a retry");
            return new(null, PersistentSessionOutcome.DaemonUnreachable, endpoint.ToString(), ex.Message)
            {
                HostDisplayName = RemoteMuxConnector.DisplayNameOf(profile),
            };
        }

        if (host is null)
        {
            // Declined after all: the profile changed since the check above, or the window is closing.
            _log?.Invoke($"[Mux] no connection for {endpoint}; this SSH session will not persist");
            return new(_fallback.Create(request), PersistentSessionOutcome.NotPersistent, null, null);
        }

        return CreateOn(TargetFor(host, endpoint) with { Remote = profile }, request) with { HostDisplayName = host.Policy.DisplayName };
    }

    /// <summary>
    /// The endpoint a request goes to, its host, and the waits that host's policy gives it. <see cref="Remote"/>
    /// is a remote endpoint's SSH profile; null for the local daemon.
    /// </summary>
    private readonly record struct Target(MuxConnectionHost Host, MuxEndpointId Endpoint, TimeSpan ConnectTimeout, TimeSpan RpcTimeout, SshProfile? Remote = null)
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
            if (target.Remote is not null)
            {
                // The connect's classified failure (spec §7.1): its kind decides what the notice offers.
                RemoteMuxFailure? failure = (host.LastFailure as RemoteMuxUnavailableException)?.Failure;
                string why = failure?.Reason ?? "ntilde-mux could not be reached";

                // Controller ruling, a deviation from spec §7.5: plain SSH only when SSH itself worked
                // and only ntilde-mux did not. An SSH failure - a sign-in that needs the user included
                // (NeedsUser), or one nobody classified (a connect that did not finish in time) - gives
                // no session, even for a new tab: a plain SSH session would only fail again, or prompt
                // again. The pane offers a retry instead.
                if (failure is null or { Kind: RemoteFailureKind.SshFailed or RemoteFailureKind.NeedsUser })
                {
                    return Unreachable(target, request.ExistingMuxSessionId, why) with { RemoteFailure = failure };
                }

                return Fallback(target, request, why) with { RemoteFailure = failure };
            }

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
                        return new(Open(target, client, existing, request, MuxAttachMode.Shared), PersistentSessionOutcome.Reattached, target.Name, null)
                        {
                            AlreadyExited = !match.Running,
                        };
                    }

                    _log?.Invoke($"[Mux] shared session {existing} is gone or faulted; nothing to attach");
                    return new(null, PersistentSessionOutcome.ShareEnded, target.Name, "the shared session has ended");
                }

                if (request.ReattachAfterDrop && match is { Running: true, Faulted: false })
                {
                    // Phase 4 spec §7.4: the pane is taking back its own session after its connection
                    // dropped. That dead connection may still hold it (a daemon without the hello's
                    // eviction keeps it attached until TCP notices), so an exclusive attach would be
                    // refused by this pane's own ghost. A session gone meanwhile falls through to PreviousLost.
                    return new(Open(target, client, existing, request, MuxAttachMode.Shared), PersistentSessionOutcome.Reattached, target.Name, null);
                }

                if (target.Remote is null && match is { Running: true, Faulted: false } && !MuxCommandMatch.SameExecutable(match.Command, request.Command))
                {
                    // Not this pane's shell (a hand-edited or foreign session file): leave it running
                    // untouched and start what the pane asked for. Nothing was lost, so no banner.
                    // Checked before the IfUnattached attach, so a lost race is only ever over this pane's own shell.
                    // Local only: a remote session runs the remote default shell, which the GUI never
                    // knows (Phase 4 spec §7.5).
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
                        return new(Open(target, client, existing, request, MuxAttachMode.IfUnattached), PersistentSessionOutcome.Reattached, target.Name, null);
                    }

                    if (match.AttachedClients == 0)
                    {
                        return new(Open(target, client, existing, request), PersistentSessionOutcome.Reattached, target.Name, null);
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
        new(Open(target, client, Spawn(target, client, request), request), outcome, target.Name, null);

    /// <summary>
    /// Opens <paramref name="id"/> unattached. Locally the session reports the pane's command line as its
    /// shell; a remote one runs the remote default shell, which the GUI does not know, so it reports none.
    /// </summary>
    private static MuxClientSession Open(Target target, MuxClient client, Guid id, TerminalSessionRequest request, MuxAttachMode mode = MuxAttachMode.Shared) =>
        target.Remote is null
            ? client.OpenSession(id, request.Command, request.Arguments, mode)
            : client.OpenSession(id, string.Empty, null, mode);

    private static Guid Spawn(Target target, MuxClient client, TerminalSessionRequest r) => Rpc(target, ct => client.SpawnAsync(SpawnParamsFor(target, r), ct));

    /// <summary>
    /// Locally: the pane's own command line, with its shell-integration environment. On a remote daemon
    /// (Phase 4 spec §7.5): its default login shell (an empty command, spec §3), started in the profile's
    /// working directory and titled with the profile's name. Nothing of this machine's environment goes
    /// there - the bootstrap variables name files on this machine. OSC 133 comes from the remote
    /// shell-integration installer, as for plain SSH, and the daemon sets NTILDE_MUX_SESSION itself (§7.4).
    /// </summary>
    private static SpawnParams SpawnParamsFor(Target target, TerminalSessionRequest r) => target.Remote is { } profile
        ? new SpawnParams
        {
            Command = string.Empty,
            Arguments = string.Empty,
            StartingDirectory = profile.WorkingDirectory ?? string.Empty, // a profile file may leave it out
            Cols = r.Cols,
            Rows = r.Rows,
            EnvironmentOverrides = null,
            SkipPowerShellPostLaunchInit = false,
            Title = profile.Name,
        }
        : new SpawnParams
        {
            Command = r.Command,
            Arguments = r.Arguments,
            StartingDirectory = r.StartingDirectory,
            Cols = r.Cols,
            Rows = r.Rows,
            EnvironmentOverrides = r.EnvironmentOverrides,
            SkipPowerShellPostLaunchInit = r.SkipPowerShellPostLaunchInit,
            Title = r.Command,
        };

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
    /// A reopen keeps its id for a retry, and so its endpoint: the pane writes both back. A new pane gets
    /// the fallback factory's session: for a local request a local shell, which lives on no endpoint; for a
    /// remote one a plain SSH session, whose result still names the endpoint it was meant for (spec §7.5).
    /// </summary>
    private PersistentSessionResult Fallback(Target target, TerminalSessionRequest request, string why)
    {
        if (request.ExistingMuxSessionId is Guid existing) return Unreachable(target, existing, why);

        _log?.Invoke($"[Mux] multiplexer unavailable on {target.Name} ({why}); this session will not persist");
        return new(_fallback.Create(request), PersistentSessionOutcome.Unavailable, target.Remote is null ? null : target.Name, why);
    }

    /// <summary>
    /// No session at all, and the pane offers a retry. A reopen keeps its id; a new remote tab whose
    /// SSH connect failed has none to keep (see <see cref="CreatePersistent"/>).
    /// </summary>
    private PersistentSessionResult Unreachable(Target target, Guid? existing, string why)
    {
        _log?.Invoke(existing is Guid id
            ? $"[Mux] multiplexer unavailable on {target.Name} ({why}); session {id} is kept for a retry, no session started"
            : $"[Mux] multiplexer unavailable on {target.Name} ({why}); no session started, the tab offers a retry");
        return new(null, PersistentSessionOutcome.DaemonUnreachable, target.Name, why);
    }
}
