using System.Diagnostics.CodeAnalysis;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// What one connect attempt is for (Phase 4 ruling: automatic reconnects are non-interactive).
/// <see cref="Interactive"/> is true when a user is waiting on it - a pane's spawn, Enter, the warm-up -
/// and the connect may prompt (an SSH password). False for an attempt the host starts on its own (the
/// reconnect loop, <see cref="MuxConnectionHost.TryStartAutomaticAttempt"/>): it must fail rather than
/// put a dialog up.
/// </summary>
internal readonly record struct MuxConnectAttempt(bool Interactive);

/// <summary>
/// The GUI's one connection to one daemon (spec §6): the local daemon, or since Phase 4 one remote
/// endpoint (Phase 4 spec §5; <see cref="MuxConnectionHosts"/> holds one per endpoint). Every pane's
/// MuxClientSession on that daemon shares it.
/// <see cref="GetClient"/> is synchronous because ITerminalSessionFactory.Create is, and runs on
/// the UI thread: it waits inside Task.Run so no UI sync context is ever captured.
/// </summary>
/// <remarks>
/// A remote host (<see cref="MuxHostPolicy.IsRemote"/>) also looks after its connection (Phase 4 spec §7.2,
/// §7.3). After each successful connect it pings the client every <see cref="LivenessInterval"/>, since a
/// dropped link is silent until TCP notices, and handles the client's <see cref="MuxClient.Disconnected"/>: a
/// stopped daemon raises <see cref="DaemonStopped"/>; a lost link raises <see cref="ConnectionLost"/> and
/// starts a <see cref="MuxReconnectLoop"/>, whose success raises <see cref="Reconnected"/>. Kills asked for
/// while it is down (<see cref="KillWhenConnected"/>) are sent after the next connect, which an idle host
/// starts itself (one automatic attempt). All of it runs on
/// <see cref="Scheduler"/> timers and pool continuations, never on a client's delivery thread and never as a
/// polling loop. A local host does none of this: its behaviour is what it has always been.
/// <para>
/// A remote host lives while a pane of the window needs its endpoint: the window releases it once none does
/// (<see cref="MuxConnectionHosts.Release"/>), and the release disposes it when its kills are delivered
/// (<see cref="WhenKillsDrained"/>; final review F1). The window's teardown disposes every host. A local host
/// lives as long as the window.
/// </para>
/// </remarks>
internal sealed class MuxConnectionHost : IDisposable
{
    private readonly Func<MuxConnectAttempt, CancellationToken, Task<MuxClient>> _connect;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposed = new();
    // Captured once: CancellationTokenSource.Token throws once the source is disposed, and Dispose
    // disposes it while a connect attempt may still hold (or be about to read) the token.
    private readonly CancellationToken _disposedToken;
    // Set under _gate by Dispose, which cancels _disposed only after leaving the lock (cancelling runs the
    // token's callbacks, a connector ending its channel among them, and none may run under the host's lock).
    // Read under the lock everywhere it decides something, and lock-free (hence volatile) where it only skips work.
    private volatile bool _closed;
    private MuxClient? _client;
    private Task<MuxClient>? _connecting;
    private AttemptState? _connectingState; // what _connecting was started as
    private int _connectAttempts;
    private long? _failedAtMs; // Environment.TickCount64 of the last failure; null = none, or cleared by a success
    private Exception? _lastFailure;
    private readonly List<Guid> _queuedKills = new(); // guarded by _gate: KillWhenConnected while there was no connection

    // A remote host's watch over its connection (Phase 4 spec §7.2, §7.3), all guarded by _gate.
    private MuxClient? _watched;        // the client of the last successful connect: pinged, and its end handled
    private MuxClient? _lost;           // the watched client whose end is being handled: once, however it is reported
    private MuxClient? _droppedByPing;  // the client the liveness ping dropped ...
    private string? _droppedReason;     // ... and why, which its Disconnected cannot say ("client_disposed")
    private IDisposable? _livenessTick;
    private IDisposable? _pingTimeout;
    private CancellationTokenSource? _ping; // the ping in flight; null when none
    private long _pingSliceStart;           // Environment.TickCount64 when the ping's current timeout slice began
    private MuxReconnectLoop? _loop;
    private Episode _episode;
    private bool _daemonStopped;        // DaemonStopped was raised and nothing has connected since: its sessions are gone
    private Task _events = Task.CompletedTask; // the events, raised one at a time in the order they were queued
    private Task _classification = Task.CompletedTask; // the latest disconnect classification (tests wait on it)

    /// <summary>The local daemon's host: <see cref="MuxHostPolicy.Local"/>.</summary>
    public MuxConnectionHost(Func<CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log)
        : this(connect, endpoint, log, MuxHostPolicy.Local)
    {
    }

    public MuxConnectionHost(Func<CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log, MuxHostPolicy policy)
        : this(IgnoringTheAttempt(connect), endpoint, log, policy)
    {
    }

    /// <summary>
    /// A host whose connect function is told what each attempt is for (<see cref="MuxConnectAttempt"/>):
    /// a remote host's connector must not prompt when nobody is waiting.
    /// </summary>
    public MuxConnectionHost(Func<MuxConnectAttempt, CancellationToken, Task<MuxClient>> connect, string? endpoint, Action<string>? log, MuxHostPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(policy);
        _connect = connect;
        Endpoint = endpoint;
        _log = log;
        Policy = policy;
        FailureCooldown = policy.FailureCooldown;
        _disposedToken = _disposed.Token;
    }

    /// <summary>
    /// The GUI's local host. On a Windows install the daemon it spawns runs from its own copy outside the install root,
    /// and once connected the host starts pruning older versions' copies (<see cref="MuxDaemonImage"/>, Phase 5 spec R9).
    /// </summary>
    public static MuxConnectionHost CreateDefault(Action<string>? log)
    {
        string appDataRoot = AppPaths.RootDirectory;
        MuxDaemonLauncher launcher = MuxDaemonLauncher.CreateDefault(log, MuxCommand.ServeArguments,
            imageResolver: MuxDaemonImage.ResolverFor(appDataRoot, log));
        return new MuxConnectionHost(
            AfterFirstConnect(launcher.EnsureConnectedAsync, () => MuxDaemonImage.StartPruningOnce(appDataRoot, log), log),
            MuxDiscovery.GetDefaultEndpoint(), log);
    }

    /// <summary>
    /// <paramref name="connect"/>, calling <paramref name="onConnected"/> once, after its first success, on the attempt's
    /// own thread (never the UI thread: the host runs every attempt on the pool). It must not block. If it throws, that
    /// is logged and the connect still succeeds: its client must reach the host, or nothing would ever dispose it.
    /// </summary>
    internal static Func<CancellationToken, Task<T>> AfterFirstConnect<T>(Func<CancellationToken, Task<T>> connect, Action onConnected, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(onConnected);
        int done = 0;
        return async ct =>
        {
            T result = await connect(ct).ConfigureAwait(false);
            if (Interlocked.Exchange(ref done, 1) == 0)
            {
                try
                {
                    onConnected();
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[Mux] after connecting: {ex.Message}");
                }
            }

            return result;
        };
    }

    private static Func<MuxConnectAttempt, CancellationToken, Task<MuxClient>> IgnoringTheAttempt(Func<CancellationToken, Task<MuxClient>> connect)
    {
        ArgumentNullException.ThrowIfNull(connect);
        return (_, ct) => connect(ct);
    }

    /// <summary>
    /// What the connect function keeps for this host's whole life - a remote host's
    /// <c>RemoteMuxConnector</c>, with the secrets it remembers - disposed last by <see cref="Dispose"/>,
    /// after the client.
    /// </summary>
    internal IDisposable? Connector { get; init; }

    /// <summary>
    /// Told each client this host takes as its own, before anything else happens on it; never a client the host throws
    /// away (an automatic attempt's that a user's request superseded). A remote host's connector pins the SSH
    /// destination there (<c>RemoteMuxConnector.Accept</c>; codex D2, residual R5). Called under the host's lock, so it
    /// must not take that lock, block, or call back into the host.
    /// </summary>
    internal Action<MuxClient>? ClientAccepted { get; init; }

    /// <summary>
    /// The transport address, for logs (the local pipe or socket name). Not the pane's persisted
    /// endpoint: that is a <see cref="MuxEndpointId"/> (Phase 4 spec §5).
    /// </summary>
    public string? Endpoint { get; }

    /// <summary>Timeouts and cooldown for this endpoint (Phase 4 spec §5). The factory reads its waits from here.</summary>
    public MuxHostPolicy Policy { get; }

    /// <summary>
    /// After a failed attempt (or a GetClient that timed out on one), how long GetClient answers
    /// null at once instead of blocking the UI again (spec §10.6: block only on first use).
    /// <see cref="MuxHostPolicy.FailureCooldown"/> unless set here (tests).
    /// </summary>
    public TimeSpan FailureCooldown { get; init; }
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);
    public MuxClient? CurrentClient { get { lock (_gate) return _client is { IsConnected: true } c ? c : null; } }

    /// <summary>
    /// Why the most recent connection attempt failed (its base exception); null once one succeeds, and null
    /// from the moment a new attempt starts - an attempt that has not failed (yet) has no reason, and an
    /// earlier attempt's must not be read for it. The failure cooldown starts no attempt, so through it this
    /// still says why the last one failed.
    /// </summary>
    public Exception? LastFailure { get { lock (_gate) return _lastFailure; } }

    /// <summary>Starts connecting (spawning the daemon if needed) in the background. Idempotent; a no-op during the failure cooldown.</summary>
    public void WarmUp() => _ = TryStartConnecting(interactive: true, out _);

    /// <summary>
    /// Starts an attempt nobody is waiting on - the reconnect loop's - or joins the one in flight, which
    /// keeps whatever it was started as. Non-interactive (<see cref="MuxConnectAttempt"/>): it must fail
    /// rather than prompt. Null when a live client exists, during the failure cooldown, or once disposed.
    /// </summary>
    internal Task<MuxClient>? TryStartAutomaticAttempt() => TryStartConnecting(interactive: false, out Task<MuxClient>? attempt) ? attempt : null;

    /// <summary>
    /// The live client, joining the in-flight attempt or starting a new one. Null when the attempt
    /// failed or did not finish within <paramref name="timeout"/> (it keeps running and may still
    /// succeed) - never blocks past the timeout. Either failure starts <see cref="FailureCooldown"/>,
    /// during which this returns null at once so a dead daemon costs one wait, not one per pane.
    /// </summary>
    /// <remarks>
    /// It is the user's request: it never joins an automatic attempt, but supersedes it with an
    /// interactive one (see <see cref="TryStartConnecting"/>). On a remote host that is reconnecting it is
    /// also the loop's attempt, now: the loop joins it, and its backoff starts over (Phase 4 spec §7.3).
    /// This is how Enter in a pane retries at once.
    /// </remarks>
    public MuxClient? GetClient(TimeSpan timeout)
    {
        if (!TryStartConnecting(interactive: true, out Task<MuxClient>? attempt)) return CurrentClient;
        if (Policy.IsRemote) TryReconnectNow(attempt);
        try
        {
            // Not cancelled by Dispose on purpose: Dispose cancels the attempt itself, which then
            // faults and ends this wait through the AggregateException path below.
            if (!Task.Run(() => attempt, CancellationToken.None).Wait(timeout, CancellationToken.None))
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

    /// <summary>
    /// The in-flight or just-finished attempt in <paramref name="attempt"/>; false when a live client
    /// already exists, during the failure cooldown, or once the host is disposed. A new attempt is
    /// started as <paramref name="interactive"/> says; joining one in flight keeps what it was started as,
    /// except that an interactive request never joins an automatic attempt (Phase 4 ruling): that attempt
    /// may not prompt, so the user would wait on an attempt that can only fail for want of the password
    /// they are there to type. It is superseded instead - cancelled, and whatever it still connects thrown
    /// away - and an interactive one starts.
    /// </summary>
    private bool TryStartConnecting(bool interactive, [NotNullWhen(true)] out Task<MuxClient>? attempt)
    {
        attempt = null;
        CancellationTokenSource? superseded = null;
        try
        {
            lock (_gate)
            {
                if (_closed) return false;
                if (_client is { IsConnected: true }) return false;
                // Before the in-flight check: a caller that already timed out on the running attempt must
                // not make the next pane wait on it again.
                if (InCooldown()) return false;
                // Every concurrent caller must join the same attempt: one daemon spawn, one client.
                if (_connecting is { IsCompleted: false })
                {
                    if (!interactive || _connectingState is not { Interactive: false } automatic)
                    {
                        attempt = _connecting;
                        return true;
                    }

                    automatic.Superseded = true;
                    superseded = automatic.Cancellation;
                }

                if (_connecting is { IsCompletedSuccessfully: true } done && done.Result.IsConnected && _client is null)
                {
                    _client = done.Result;
                    return false;
                }

                _client = null;
                // A new attempt has no failure yet; an earlier attempt's classified one must not be read for
                // it (a later timeout is unclassified). The cooldown above starts no attempt, so it keeps it.
                _lastFailure = null;
                Interlocked.Increment(ref _connectAttempts);
                // An automatic attempt gets a cancellation of its own, so that a user's request can supersede it;
                // linked, so that Dispose still cancels it. A user's attempt keeps the host's.
                var state = new AttemptState(interactive, interactive ? null : CancellationTokenSource.CreateLinkedTokenSource(_disposedToken));
                CancellationToken token = state.Cancellation?.Token ?? _disposedToken;
                var purpose = new MuxConnectAttempt(interactive);
                _connecting = Task.Run(async () =>
                {
                    MuxClient client = await _connect(purpose, token).ConfigureAwait(false);
                    bool takenOver;
                    lock (_gate)
                    {
                        if (_closed) { client.Dispose(); throw new ObjectDisposedException(nameof(MuxConnectionHost)); }
                        takenOver = state.Superseded;
                        if (!takenOver)
                        {
                            _client = client;
                            _failedAtMs = null;
                            _lastFailure = null;
                            // Under the lock, with _client: no attempt can start between the host taking the client
                            // and the connector pinning its destination (a client that drops at once would otherwise
                            // let a pane's next attempt plan unpinned). The connector never takes this lock, nor blocks.
                            ClientAccepted?.Invoke(client);
                        }
                    }

                    if (takenOver)
                    {
                        // A user's request took over: its interactive attempt connects instead.
                        client.Dispose();
                        throw new OperationCanceledException(token);
                    }

                    OnConnected(client);
                    return client;
                }, token);
                _connectingState = state;
                Task<MuxClient> started = _connecting;
                // Covers WarmUp too, whose failure nothing awaits: log the reason once, start the cooldown.
                _ = started.ContinueWith(
                    t =>
                    {
                        _log?.Invoke($"[Mux] connection failed: {t.Exception?.GetBaseException().Message}");
                        RecordFailure(started, t.Exception?.GetBaseException());
                    },
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                if (state.Cancellation is { } own)
                {
                    // Released with the attempt, and with it its link to the host's token.
                    _ = started.ContinueWith(_ => own.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }

                attempt = started;
                return true;
            }
        }
        finally
        {
            // Outside the lock: cancelling runs the superseded attempt's callbacks (the connector ends its channel).
            CancelQuietly(superseded);
        }
    }

    private void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null) return;
        try
        {
            source.Cancel();
        }
        catch (AggregateException ex)
        {
            // A callback registered on it threw: what was cancelled is cancelled all the same.
            _log?.Invoke($"[Mux] {Policy.DisplayName}: cancelling failed: {ex.GetBaseException().Message}");
        }
        catch (ObjectDisposedException)
        {
            // The attempt already ended, and released it.
        }
    }

    /// <summary>What one connect attempt was started as.</summary>
    private sealed class AttemptState(bool interactive, CancellationTokenSource? cancellation)
    {
        public bool Interactive { get; } = interactive;

        /// <summary>An automatic attempt's own cancellation, linked to the host's disposal; null for a user's.</summary>
        public CancellationTokenSource? Cancellation { get; } = cancellation;

        /// <summary>A user's request took over: whatever this attempt still connects is thrown away. Guarded by the host's lock.</summary>
        public bool Superseded { get; set; }
    }

    /// <summary>Where a remote host is between losing its link and getting it back (Phase 4 spec §7.3).</summary>
    private enum Episode
    {
        /// <summary>Connected, connecting for the first time, or after <see cref="MuxConnectionHost.DaemonStopped"/>: nothing to get back.</summary>
        None,

        /// <summary><see cref="MuxConnectionHost.ConnectionLost"/> was raised and the loop runs.</summary>
        Reconnecting,

        /// <summary><see cref="MuxConnectionHost.ReconnectAbandoned"/> was raised: the loop stopped, and only a user's request reconnects.</summary>
        Abandoned,
    }

    /// <summary>
    /// A remote host's connection was lost - a ping timeout, ssh's exit 255, the channel's EOF, a native
    /// disconnect (Phase 4 spec §7.3) - and the host reconnects on its own: <see cref="IsReconnecting"/> is
    /// true from now on. The argument is the reason, for logs.
    /// </summary>
    /// <remarks>
    /// Every event of this host is raised on a pool thread, one at a time and in the order of what caused it
    /// (<see cref="ConnectionLost"/> always before the <see cref="Reconnected"/> that ends that loss), never
    /// under the host's lock, and not once the host is disposed. A handler must not block - the next event
    /// waits for it - and must not wait on <see cref="GetClient"/>.
    /// </remarks>
    public event Action<string>? ConnectionLost;

    /// <summary>
    /// The connection is back after <see cref="ConnectionLost"/>, with this client: through the loop, or
    /// through a user's request (<see cref="GetClient"/>) while the loop ran or after it gave up. Once per
    /// loss. The kills queued meanwhile (<see cref="KillWhenConnected"/>) were sent first. Not raised for a
    /// connect after <see cref="DaemonStopped"/>.
    /// </summary>
    public event Action<MuxClient>? Reconnected;

    /// <summary>
    /// The loop stopped without a connection: its <see cref="MuxReconnectLoop.Budget"/> ran out, or signing in
    /// needs the user (<see cref="RemoteFailureKind.NeedsUser"/>), which another automatic attempt cannot
    /// give; in that case the failure is <see cref="LastFailure"/> by the time this is raised. <see cref="GetClient"/>
    /// still reconnects, and then raises <see cref="Reconnected"/>; the kills queued meanwhile are kept, and go out
    /// with that connect.
    /// </summary>
    public event Action? ReconnectAbandoned;

    /// <summary>
    /// The remote daemon stopped (the proxy exited 3: its process is gone): its sessions are gone, and the host does
    /// not reconnect on its own. <see cref="GetClient"/> connects again, to a new daemon. Kills queued for its
    /// sessions are dropped, and none is recorded until that connect (<see cref="KillWhenConnected"/>).
    /// </summary>
    public event Action? DaemonStopped;

    /// <summary>The reconnect loop runs: after <see cref="ConnectionLost"/>, until <see cref="Reconnected"/> or <see cref="ReconnectAbandoned"/>.</summary>
    public bool IsReconnecting { get { lock (_gate) return _episode == Episode.Reconnecting; } }

    /// <summary>
    /// A remote host's disconnect classifier (Phase 4 spec §7.3): <see cref="MuxDisconnectKind.DaemonStopped"/>
    /// when the proxy under the lost client exited 3. Waited for at most <see cref="ClassifyTimeout"/>; null,
    /// a throw or no answer in time all mean <see cref="MuxDisconnectKind.LinkLost"/>.
    /// <see cref="RemoteMuxHostFactory"/> sets it.
    /// </summary>
    internal Func<MuxClient, Task<MuxDisconnectKind>>? ClassifyDisconnect { get; init; }

    /// <summary>
    /// The cap on <see cref="ClassifyDisconnect"/>: <see cref="RemoteMuxHostFactory.ClassifyTimeout"/>, the last of the
    /// waits for a lost client's exit status (see their order at <see cref="RemoteMuxHostFactory.ChannelExitGrace"/>).
    /// Tests: longer, to hold a classification open.
    /// </summary>
    internal TimeSpan ClassifyTimeout { get; init; } = RemoteMuxHostFactory.ClassifyTimeout;

    /// <summary>The liveness ping: <see cref="MuxClient.PingAsync"/>. Tests replace it to decide when, and how, a ping ends.</summary>
    internal Func<MuxClient, CancellationToken, Task> Ping { get; init; } = static (client, ct) => client.PingAsync(ct);

    /// <summary>
    /// One kill: <see cref="MuxClient.KillAsync"/>, not cancelled by the host's disposal - Dispose flushes kills in flight
    /// with a bounded wait of its own. Tests replace it to decide when, and how, a kill ends.
    /// </summary>
    internal Func<MuxClient, Guid, Task> Kill { get; init; } = static (client, sessionId) => client.KillAsync(sessionId, CancellationToken.None);

    /// <summary>Tests: the client a remote host watches (pings, and handles the end of); null for a local host.</summary>
    internal MuxClient? WatchedClientForTest { get { lock (_gate) return _watched; } }

    /// <summary>Tests: the latest disconnect classification (completed when none ran).</summary>
    internal Task ClassificationForTest => Volatile.Read(ref _classification);

    /// <summary>Tests: the event queue; it completes once every event queued so far has been raised (or skipped).</summary>
    internal Task EventsForTest { get { lock (_gate) return _events; } }

    /// <summary>The clock of a remote host's liveness ping and reconnect loop.</summary>
    internal IMuxTimerScheduler Scheduler { get; init; } = SystemMuxTimerScheduler.Instance;

    /// <summary>How often a remote host pings its client (Phase 4 spec §7.2).</summary>
    internal TimeSpan LivenessInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a ping may go unanswered, with no other frame arriving either, before the client is dropped as
    /// dead (Phase 4 spec §7.2). While frames keep arriving the ping waits on, one such slice at a time.
    /// </summary>
    internal TimeSpan LivenessTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Kills <paramref name="sessionId"/> on this host's daemon - a tab the user closed - at once when
    /// connected, otherwise right after the next successful connect, whatever starts it: sent with
    /// <see cref="MuxClient.KillAsync"/> and tracked like <see cref="TrackPendingKill"/> (Review Focus 1: a
    /// remote tab closed while its link is down must not orphan its shell). Connected, it is sent from the pool,
    /// never on the caller's thread, which may be the UI thread: a stalled link's full send queue would hold it. A
    /// kill whose connection closes before the daemon answers - or whose request times out once that connection is
    /// gone - is queued again. Queued kills outlive the reconnect loop giving up
    /// (<see cref="ReconnectAbandoned"/>): a later connect - a user's Enter, or the next pane opened on the
    /// endpoint - still sends them (controller ruling). They are dropped, with a log line, in two cases only:
    /// <see cref="DaemonStopped"/> (that daemon's sessions ended with it) and the host's disposal. A release
    /// (<see cref="MuxConnectionHosts.Release"/>) waits for them (<see cref="WhenKillsDrained"/>).
    /// </summary>
    /// <remarks>
    /// A remote host that is idle - no live client, no attempt in flight, no reconnect loop running - would
    /// otherwise hold the kill until something else connects, which may be never: a restored tab closed
    /// before it was ever shown is the only user of its host. So it starts one automatic attempt (it never
    /// prompts, and runs off the caller's thread, which may be the UI thread): the kill goes out if keys, an
    /// agent or a remembered secret let it in. If that attempt fails the kill stays queued for any later
    /// connect (controller ruling, Task 21 review).
    /// <para>
    /// After <see cref="DaemonStopped"/>, until the next successful connect, a kill is neither recorded nor
    /// connected for: that daemon's sessions ended with it, and a connect now would only start a new daemon. The
    /// kills queued when it stopped are dropped then, for the same reason.
    /// </para>
    /// </remarks>
    public void KillWhenConnected(Guid sessionId)
    {
        if (!TryKillWhenConnected(sessionId))
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: dropping the kill of session {sessionId}: the host is closed");
        }
    }

    /// <summary>
    /// <see cref="KillWhenConnected"/>, except that a closed host neither takes the kill nor logs it as dropped: it
    /// returns false, for a caller that can still reach the daemon another way - through the host the registry takes
    /// back or builds for the endpoint (codex E1). True once the kill is sent, queued, or moot (the daemon stopped).
    /// </summary>
    internal bool TryKillWhenConnected(Guid sessionId)
    {
        MuxClient? live = null;
        bool moot = false;
        bool idle = false;
        lock (_gate)
        {
            if (_closed) return false;
            live = _client is { IsConnected: true } c ? c : null;
            // After DaemonStopped, until the next connect: the session went with that daemon (controller ruling).
            moot = live is null && _daemonStopped;
            if (live is null && !moot && !_queuedKills.Contains(sessionId)) _queuedKills.Add(sessionId);
            idle = live is null && !moot && Policy.IsRemote && _connecting is not { IsCompleted: false } && _episode != Episode.Reconnecting;
            // Counted here, before the send is handed off: a release right behind this close waits for it, and
            // the count holds until the send's continuation settles it.
            if (live is not null) _killsUnsettled++;
        }

        if (moot)
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: not killing session {sessionId}: the daemon stopped, and its sessions ended with it");
        }
        else if (live is null)
        {
            _log?.Invoke(idle
                ? $"[Mux] {Policy.DisplayName}: not connected; connecting to send the kill of session {sessionId}"
                : $"[Mux] {Policy.DisplayName}: not connected; the kill of session {sessionId} is sent once connected");
            if (idle) _ = TryStartAutomaticAttempt();
        }
        else
        {
            HandOffKill(live, sessionId);
        }

        return true;
    }

    /// <summary>
    /// Sends one kill, on this thread, and tracks it. The caller counted it in <see cref="_killsUnsettled"/> under the
    /// lock when it decided to send it, and it stays counted until <see cref="SettleWhenDone"/> has settled it. The caller
    /// is <see cref="OnConnected"/>, on its attempt's pool thread: a fresh connection's queue has room, and its kills
    /// must be queued before <see cref="Reconnected"/> lets the panes reattach. A caller that must not block hands the
    /// kill off instead (<see cref="HandOffKill"/>).
    /// </summary>
    private void SendKill(MuxClient client, Guid sessionId)
    {
        Task kill;
        try
        {
            kill = Kill(client, sessionId);
        }
        catch (Exception ex)
        {
            kill = Task.FromException(ex);
        }

        lock (_gate) TrackKillLocked(kill);
        SettleWhenDone(client, sessionId, kill);
    }

    /// <summary>
    /// <see cref="SendKill"/> for a caller that must not wait on the link - a pane closing, on the UI thread. On a
    /// stalled link the client's send queue fills, and a send then waits until the link drains or is dropped (the
    /// liveness ping is sent from the pool for the same reason). So the send runs on the pool, behind the kills handed
    /// off before it: a stalled link holds one pool thread, however many panes close on it. The kill is tracked before
    /// this returns, for <see cref="Dispose"/>'s flush, as the caller counted it for a release.
    /// </summary>
    private void HandOffKill(MuxClient client, Guid sessionId)
    {
        Task kill;
        lock (_gate)
        {
            // Not ExecuteSynchronously: behind a send already done, that would send on this very thread.
            Task<Task> sending = _killSends.ContinueWith(_ => Kill(client, sessionId), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _killSends = sending;
            kill = sending.Unwrap();
            TrackKillLocked(kill);
        }

        SettleWhenDone(client, sessionId, kill);
    }

    /// <summary>
    /// Settles a kill once it ends: answered, or failed (<see cref="OnKillFailed"/>, which may queue it again). Only then
    /// does the count drop and the drain get checked (<see cref="WhenKillsDrained"/>), so a failed kill is never read as
    /// delivered, not even in the moment between its task faulting (at once, on a client already dead) and this running.
    /// </summary>
    private void SettleWhenDone(MuxClient client, Guid sessionId, Task kill) =>
        _ = kill.ContinueWith(
            t =>
            {
                try
                {
                    if (t.IsFaulted) OnKillFailed(client, sessionId, t.Exception!.GetBaseException());
                }
                finally
                {
                    lock (_gate) _killsUnsettled--;
                    NotifyIfKillsDrained();
                }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>
    /// A kill <paramref name="client"/> did not get answered. Its connection closed first, or its request timed out and
    /// by then the connection is gone (found dead, and still being closed when the request's time ran out): either way the
    /// kill may never have arrived, so it goes out again on the next connection - a second kill of a session already gone
    /// only fails. A timeout on a connection still up is logged and left: queued, the kill would wait for a next
    /// connection a working one never needs, and hold the host's release meanwhile.
    /// </summary>
    private void OnKillFailed(MuxClient client, Guid sessionId, Exception error)
    {
        bool timedOut = error is TimeoutException;
        if (!_closed && (error is IOException || (timedOut && !client.IsConnected)))
        {
            _log?.Invoke(timedOut
                ? $"[Mux] {Policy.DisplayName}: the kill of session {sessionId} timed out, and its connection is gone; trying again once connected"
                : $"[Mux] {Policy.DisplayName}: the connection closed before session {sessionId} was killed; trying again once connected");
            KillWhenConnected(sessionId);
            return;
        }

        _log?.Invoke(timedOut && !_closed
            ? $"[Mux] {Policy.DisplayName}: the daemon did not answer the kill of session {sessionId} in time; its connection is up, so it is not sent again"
            : $"[Mux] {Policy.DisplayName}: killing session {sessionId} failed: {error.Message}");
    }

    private Guid[] DrainQueuedKillsLocked()
    {
        Guid[] kills = _queuedKills.ToArray();
        _queuedKills.Clear();
        return kills;
    }

    private void LogDroppedKills(Guid[] dropped, string why)
    {
        if (dropped.Length == 0) return;
        _log?.Invoke($"[Mux] {Policy.DisplayName}: dropping {dropped.Length} queued kill(s) ({string.Join(", ", dropped)}): {why}; those sessions keep running");
    }

    /// <summary>
    /// After every successful connect, on the attempt's own pool thread: the queued kills go out, and a remote
    /// host starts watching the client - liveness timer, <see cref="MuxClient.Disconnected"/> - and ends a
    /// loss in progress with <see cref="Reconnected"/>, raised once those kills have been sent.
    /// </summary>
    private void OnConnected(MuxClient client)
    {
        Guid[] kills;
        string? note = null;
        TaskCompletionSource? killsSent = null;
        lock (_gate)
        {
            if (_closed) return; // Dispose closes it
            // No longer the host's client (it dropped, and a new attempt started, before this ran): nothing to
            // watch, and the queued kills wait for that attempt.
            if (!ReferenceEquals(client, _client)) return;
            _daemonStopped = false; // a daemon is up again: kills are worth recording from here on
            kills = DrainQueuedKillsLocked();
            _killsUnsettled += kills.Length; // sent below, outside the lock; each counted until its continuation settles it
            if (Policy.IsRemote)
            {
                _watched = client;
                _lost = null;
                _droppedByPing = null;
                _droppedReason = null;
                StopWatchingLocked();
                _livenessTick = Scheduler.Schedule(LivenessInterval, () => OnLivenessTick(client));
                if (_episode != Episode.None)
                {
                    _episode = Episode.None;
                    _loop?.Stop();
                    // Queued now, in order with the other events, but held until the kills below are sent:
                    // a pane reattaching on Reconnected goes after them.
                    if (kills.Length > 0) killsSent = HoldEventsLocked();
                    RaiseLocked(nameof(Reconnected), () => Invoke(nameof(Reconnected), Reconnected, client));
                    note = $"[Mux] {Policy.DisplayName}: reconnected";
                }
            }
        }

        try
        {
            if (note is not null) _log?.Invoke(note);
            if (Policy.IsRemote)
            {
                client.Disconnected += reason => OnWatchedClientDisconnected(client, reason);
                if (!client.IsConnected) OnWatchedClientDisconnected(client, client.DisconnectReason);
            }

            foreach (Guid sessionId in kills) SendKill(client, sessionId);
        }
        finally
        {
            killsSent?.TrySetResult();
        }
    }

    /// <summary>
    /// The watched client's <see cref="MuxClient.Disconnected"/>, raised on whichever thread ended it - its
    /// reader, its sender, a Dispose caller (the ping's timeout, this host's Dispose) - so nothing here
    /// blocks: telling why waits for ssh's exit status (up to <see cref="ClassifyTimeout"/>), and does so on the pool.
    /// </summary>
    private void OnWatchedClientDisconnected(MuxClient client, string? reason)
    {
        string why;
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(client, _watched) || ReferenceEquals(client, _lost)) return;
            _lost = client;
            StopWatchingLocked();
            why = ReferenceEquals(client, _droppedByPing) && _droppedReason is { } dropped ? dropped : reason ?? "disconnected";
        }

        Volatile.Write(ref _classification, Task.Run(() => OnConnectionEndedAsync(client, why), CancellationToken.None));
    }

    private async Task OnConnectionEndedAsync(MuxClient client, string reason)
    {
        MuxDisconnectKind kind = MuxDisconnectKind.LinkLost;
        if (ClassifyDisconnect is { } classify)
        {
            try
            {
                kind = await classify(client).WaitAsync(ClassifyTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Not known in time: a lost link.
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[Mux] {Policy.DisplayName}: could not tell why the connection ended ({ex.Message}); treating it as a lost link");
            }
        }

        string note;
        Guid[] moot = [];
        lock (_gate)
        {
            if (_closed) return;
            if (kind == MuxDisconnectKind.DaemonStopped)
            {
                RaiseLocked(nameof(DaemonStopped), () => Invoke(nameof(DaemonStopped), DaemonStopped));
                note = $"[Mux] {Policy.DisplayName}: ntilde-mux stopped ({reason}); its sessions ended, and the host does not reconnect until asked";
                if (ReferenceEquals(client, _watched))
                {
                    // Its sessions went with it: a kill queued for one has nothing left to end, and delivering it would
                    // start a new daemon for nothing (controller ruling). Until the next connect, none is recorded.
                    _daemonStopped = true;
                    moot = DrainQueuedKillsLocked();
                }
            }
            else
            {
                RaiseLocked(nameof(ConnectionLost), () => Invoke(nameof(ConnectionLost), ConnectionLost, reason));
                if (!ReferenceEquals(client, _watched))
                {
                    // A newer connection came up while this one was being classified (a user's request):
                    // the connection is back already, and no loop is needed.
                    if (_watched is { IsConnected: true } back) RaiseLocked(nameof(Reconnected), () => Invoke(nameof(Reconnected), Reconnected, back));
                    note = $"[Mux] {Policy.DisplayName}: connection lost ({reason}), and already back";
                }
                else
                {
                    _episode = Episode.Reconnecting;
                    (_loop ??= new MuxReconnectLoop(Scheduler, ReconnectAttemptAsync, () => GiveUp($"no connection within {MuxReconnectLoop.Budget.TotalMinutes:0} min"))).Start();
                    note = $"[Mux] {Policy.DisplayName}: connection lost ({reason}); reconnecting";
                }
            }
        }

        _log?.Invoke(note);
        if (moot.Length > 0)
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: dropping {moot.Length} queued kills: the daemon stopped, and its sessions ended with it ({string.Join(", ", moot)})");
            NotifyIfKillsDrained(); // dropped, so nothing is left to deliver (final review F1)
        }
    }

    /// <summary>
    /// The loop's attempt (Phase 4 spec §7.3): the host's normal connect, automatic, joining whatever attempt
    /// is in flight - a user's included. Success is handled where every connect is (<see cref="OnConnected"/>).
    /// </summary>
    private Task<bool> ReconnectAttemptAsync() =>
        TryStartAutomaticAttempt() is { } attempt ? AsLoopAttemptAsync(attempt) : Task.FromResult(CurrentClient is not null);

    /// <summary>
    /// <paramref name="attempt"/>'s outcome as the loop counts it: connected, or not. A failure that needs the
    /// user ends the loop (ruling 1) - unless that attempt is no longer the host's (a user's request superseded it).
    /// That failure is <see cref="LastFailure"/> before <see cref="ReconnectAbandoned"/> is raised, so a pane can say
    /// why there (codex4 F): the attempt's own fault continuation records it too, but in no set order with this one.
    /// </summary>
    private async Task<bool> AsLoopAttemptAsync(Task<MuxClient> attempt)
    {
        try
        {
            await attempt.ConfigureAwait(false);
            return true;
        }
        catch (RemoteMuxUnavailableException ex) when (ex.Failure.Kind == RemoteFailureKind.NeedsUser)
        {
            // Ruling: another automatic attempt cannot sign in either - it would only knock again.
            RecordFailure(attempt, ex);
            GiveUp($"signing in needs the user: {ex.Failure.Reason}", attempt);
            return false;
        }
        catch (Exception)
        {
            return false; // logged by the attempt itself; the loop backs off
        }
    }

    /// <summary>
    /// A user's request while the loop runs (spec §7.3): <paramref name="userAttempt"/> is the loop's attempt
    /// now, and its backoff starts over. The loop starts nothing itself, so a user's attempt that already
    /// finished costs no extra connect.
    /// </summary>
    private void TryReconnectNow(Task<MuxClient> userAttempt)
    {
        MuxReconnectLoop? loop;
        lock (_gate) loop = _episode == Episode.Reconnecting ? _loop : null;
        loop?.TryNow(AsLoopAttemptAsync(userAttempt));
    }

    /// <summary>
    /// The loop stops for good (its budget, or a sign-in only the user can do), and <see cref="ReconnectAbandoned"/>
    /// tells the panes to offer Enter. Not while a user's attempt runs: that attempt decides, and the panes show
    /// no give-up meanwhile (ruling 2) - if it fails, this is decided again. Not for a <paramref name="failed"/>
    /// attempt that is no longer the host's (a user's request superseded it). Queued kills stay queued: the user
    /// meant to end those shells, and a later connect (Enter) still delivers them (controller ruling).
    /// </summary>
    private void GiveUp(string why, Task<MuxClient>? failed = null)
    {
        int queued = 0;
        Task<MuxClient>? userAttempt = null;
        lock (_gate)
        {
            if (_closed || _episode != Episode.Reconnecting) return;
            if (failed is not null && !ReferenceEquals(failed, _connecting))
            {
                why = $"{why}, from an attempt a user's request superseded: not giving up on it";
            }
            else if (_connecting is { IsCompleted: false } running && _connectingState is { Interactive: true })
            {
                userAttempt = running;
            }
            else
            {
                _episode = Episode.Abandoned;
                _loop?.Stop();
                queued = _queuedKills.Count;
                RaiseLocked(nameof(ReconnectAbandoned), () => Invoke(nameof(ReconnectAbandoned), ReconnectAbandoned));
                why = $"stopped reconnecting: {why}" + (queued > 0 ? $"; {queued} queued kill(s) wait for the next connection" : string.Empty);
            }
        }

        if (userAttempt is not null)
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: {why}; a user's attempt is running, and decides first");
            _ = userAttempt.ContinueWith(
                t =>
                {
                    if (!t.IsCompletedSuccessfully) GiveUp(why);
                },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return;
        }

        _log?.Invoke($"[Mux] {Policy.DisplayName}: {why}");
    }

    /// <summary>
    /// A liveness tick, on the scheduler's thread (Phase 4 spec §7.2): schedules the next tick and sends a
    /// ping, unless the previous one is still unanswered. The ping is sent from the pool - on a stalled link
    /// the client's send queue may be full, and sending would block - and its timeout is a timer of its own.
    /// </summary>
    /// <remarks>
    /// The link is declared dead only when no inbound frame of any kind arrived for a whole
    /// <see cref="LivenessTimeout"/> while the ping was out (<see cref="MuxClient.LastReceivedTicks"/>, by
    /// controller ruling, after spec §7.2's "any reply resets the clock"). A ping queued behind a large
    /// snapshot on a slow link is late, not lost: while output keeps arriving, its timeout waits another slice.
    /// </remarks>
    private void OnLivenessTick(MuxClient client)
    {
        CancellationTokenSource ping;
        lock (_gate)
        {
            if (_closed || !ReferenceEquals(client, _watched) || ReferenceEquals(client, _lost) || !client.IsConnected) return;
            _livenessTick = Scheduler.Schedule(LivenessInterval, () => OnLivenessTick(client));
            if (_ping is not null) return; // the previous ping is still unanswered: skip this tick
            ping = _ping = new CancellationTokenSource();
            _pingSliceStart = Environment.TickCount64;
            _pingTimeout = Scheduler.Schedule(LivenessTimeout, () => OnPingTimedOut(client, ping));
        }

        _ = Task.Run(() => Ping(client, ping.Token), CancellationToken.None)
            .ContinueWith(t => OnPingDone(client, ping, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// The ping's timeout slice ended without its answer. Frames arrived during the slice: the link is busy,
    /// not dead - another slice. None did: the link is dead, and the client is dropped on the pool, which
    /// raises its Disconnected ("ping timeout").
    /// </summary>
    private void OnPingTimedOut(MuxClient client, CancellationTokenSource ping)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(ping, _ping)) return; // answered meanwhile, or no longer watched
            if (client.LastReceivedTicks >= _pingSliceStart)
            {
                _pingSliceStart = Environment.TickCount64;
                _pingTimeout = Scheduler.Schedule(LivenessTimeout, () => OnPingTimedOut(client, ping));
                return;
            }

            _ping = null;
            _pingTimeout = null;
            _droppedByPing = client;
            _droppedReason = "ping timeout";
        }

        _log?.Invoke($"[Mux] {Policy.DisplayName}: no answer to a ping within {LivenessTimeout.TotalSeconds:0.#} s; dropping the connection");
        CancelQuietly(ping);
        _ = Task.Run(client.Dispose, CancellationToken.None);
    }

    /// <summary>
    /// On the pool: an answer resets the clock. A failure - the request's own timeout, say - drops the client
    /// as the timeout does, unless frames are still arriving: then the next tick pings again.
    /// </summary>
    private void OnPingDone(MuxClient client, CancellationTokenSource ping, Task result)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(ping, _ping)) return; // timed out (handled there), or no longer watched
            _ping = null;
            _pingTimeout?.Dispose();
            _pingTimeout = null;
            if (result.IsCompletedSuccessfully || !client.IsConnected) return; // closed: its own Disconnected says why
            if (client.LastReceivedTicks >= _pingSliceStart) return; // frames still arrive: busy, not dead
            _droppedByPing = client;
            _droppedReason = "ping failed";
        }

        _log?.Invoke($"[Mux] {Policy.DisplayName}: a ping failed ({result.Exception?.GetBaseException().Message}); dropping the connection");
        client.Dispose();
    }

    /// <summary>Stops the liveness timer and forgets a ping in flight (its completion then finds itself stale).</summary>
    private void StopWatchingLocked()
    {
        _livenessTick?.Dispose();
        _livenessTick = null;
        _pingTimeout?.Dispose();
        _pingTimeout = null;
        _ping = null;
    }

    /// <summary>
    /// Queues an event behind those queued before it. Called under the lock, with the change it reports,
    /// so the events go out in the order of the changes; raised on the pool, never under the lock.
    /// </summary>
    private void RaiseLocked(string name, Action raise)
    {
        _events = _events.ContinueWith(
            _ =>
            {
                if (_closed) return;
                try
                {
                    raise();
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[Mux] raising {name} failed: {ex}");
                }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Holds the event queue until the returned source completes: what is queued after this - under the same
    /// lock - waits for it, without a thread waiting. The caller completes it, in a finally.
    /// </summary>
    private TaskCompletionSource HoldEventsLocked()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _events = _events.ContinueWith(_ => released.Task, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return released;
    }

    /// <summary>Calls each handler in turn: one that throws is logged, and neither stops the rest nor reaches the host.</summary>
    private void Invoke(string name, Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(); }
            catch (Exception ex) { _log?.Invoke($"[Mux] a {name} handler threw: {ex}"); }
        }
    }

    private void Invoke<T>(string name, Action<T>? handlers, T argument)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in Delegate.EnumerateInvocationList(handlers))
        {
            try { handler(argument); }
            catch (Exception ex) { _log?.Invoke($"[Mux] a {name} handler threw: {ex}"); }
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for the daemon to work through what is already queued.</summary>
    public TimeSpan DisposeFlushTimeout { get; init; } = TimeSpan.FromSeconds(1);

    private readonly List<Task> _pendingKills = new(); // guarded by _gate

    /// <summary>How long <see cref="Dispose"/> waits for the replies of tracked kills (closing the last tab).</summary>
    public TimeSpan KillFlushTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A kill a user close sent (its reply means it landed). <see cref="Dispose"/> waits for these
    /// first, so the last tab's kill cannot be dropped by the teardown right behind it (PR #489).
    /// </summary>
    public void TrackPendingKill(Task kill)
    {
        ArgumentNullException.ThrowIfNull(kill);
        lock (_gate) TrackKillLocked(kill);
        _ = kill.ContinueWith(_ => NotifyIfKillsDrained(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void TrackKillLocked(Task kill)
    {
        _pendingKills.RemoveAll(t => t.IsCompleted);
        _pendingKills.Add(kill);
    }

    internal int PendingKillCountForTest { get { lock (_gate) return _pendingKills.Count(t => !t.IsCompleted); } }

    private int _killsUnsettled; // guarded by _gate: kills decided on (OnConnected, KillWhenConnected) whose SettleWhenDone continuation has not finished
    private Task _killSends = Task.CompletedTask; // guarded by _gate: the last send HandOffKill gave the pool; the next waits for it
    private readonly List<Action> _drainWaiters = new(); // guarded by _gate: WhenKillsDrained callbacks still waiting

    /// <summary>
    /// Final review F1: calls <paramref name="drained"/> once no kill is left to deliver - none queued for a
    /// connection (<see cref="KillWhenConnected"/>), none sent and not yet settled (answered, or queued again because
    /// its connection closed) - at once, on this thread, when that holds now, otherwise on the pool when the last one
    /// settles. A kill whose connection closed is queued again before it stops counting, so it never counts as delivered; kills <see cref="DaemonStopped"/> dropped count as
    /// settled. Never called once the host is disposed, nor for kills a give-up (<see cref="ReconnectAbandoned"/>)
    /// left queued until something connects again. <see cref="MuxConnectionHosts.Release"/> waits on it to close
    /// a remote host no pane uses.
    /// </summary>
    internal void WhenKillsDrained(Action drained)
    {
        ArgumentNullException.ThrowIfNull(drained);
        bool now;
        lock (_gate)
        {
            if (_closed) return;
            now = KillsDrainedLocked();
            if (!now) _drainWaiters.Add(drained);
        }

        if (now) InvokeDrained(drained);
    }

    private bool KillsDrainedLocked() => _queuedKills.Count == 0 && _killsUnsettled == 0 && !_pendingKills.Exists(t => !t.IsCompleted);

    private void NotifyIfKillsDrained()
    {
        Action[] waiters;
        lock (_gate)
        {
            if (_closed || _drainWaiters.Count == 0 || !KillsDrainedLocked()) return;
            waiters = _drainWaiters.ToArray();
            _drainWaiters.Clear();
        }

        foreach (Action waiter in waiters) InvokeDrained(waiter);
    }

    private void InvokeDrained(Action drained)
    {
        try
        {
            drained();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: a kills-drained callback threw: {ex}");
        }
    }

    /// <summary>
    /// Raised once by <see cref="Dispose"/>, on its thread, as it begins (the host is closed from then on):
    /// <see cref="MuxConnectionHosts"/> forgets the host, so the next ask for its endpoint builds a new one.
    /// </summary>
    internal event Action<MuxConnectionHost>? Closed;

    /// <summary>Whether <see cref="Dispose"/> has begun.</summary>
    internal bool IsClosed => _closed;

    /// <summary>
    /// Closes the connection: the daemon detaches every session on it and keeps them running.
    /// First a bounded flush: closing drops frames still queued, and a pane closed just before the
    /// window (the last tab) has queued a kill. One wait, not two: a tracked kill's own reply (or its
    /// timeout) already tells us whether earlier frames landed, so the ping flush below runs only
    /// when there was no kill to wait on. Waited inside Task.Run: no UI sync context captured.
    /// A remote host's loop and liveness timers stop first; the attempt in flight - one blocked in ssh,
    /// waiting on a prompt, included - is cancelled, and its transport kills ssh or closes the native
    /// session (Review Focus 5). Kills still queued for a connection are dropped, with a log line.
    /// </summary>
    public void Dispose()
    {
        MuxClient? client;
        MuxReconnectLoop? loop;
        Guid[] dropped;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            client = _client;
            _client = null;
            loop = _loop;
            _episode = Episode.None;
            StopWatchingLocked();
            dropped = DrainQueuedKillsLocked();
            _drainWaiters.Clear();
        }

        RaiseClosed();

        // Outside the lock, as TryStartConnecting cancels a superseded attempt: cancelling runs the token's
        // callbacks - the attempt in flight ending its channel - and none of them may run under the host's lock.
        CancelQuietly(_disposed);
        loop?.Dispose();
        LogDroppedKills(dropped, "the host is closing");

        // Safe to dispose now: every disposed check reads _closed, and the attempt holds the token captured
        // in the constructor, never _disposed.Token.
        _disposed.Dispose();

        try
        {
            if (client is not null) FlushAndClose(client);
        }
        finally
        {
            // Last: the client's flush above still runs over what the connector owns (a remote host's
            // exec channel), and its Disconnected is what ends that channel.
            Connector?.Dispose();
        }
    }

    private void RaiseClosed()
    {
        try
        {
            Closed?.Invoke(this);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Mux] {Policy.DisplayName}: a Closed handler threw: {ex.Message}");
        }
    }

    private void FlushAndClose(MuxClient client)
    {
        Task[] kills;
        lock (_gate) kills = _pendingKills.Where(t => !t.IsCompleted).ToArray();
        if (kills.Length > 0 && client.IsConnected)
        {
            try
            {
                // Inside Task.Run like the ping below: no UI sync context is captured.
                if (!Task.Run(() => Task.WhenAll(kills), CancellationToken.None).Wait(KillFlushTimeout, CancellationToken.None))
                {
                    _log?.Invoke($"[Mux] {kills.Length} kill(s) not confirmed within {KillFlushTimeout.TotalSeconds:0.#} s; closing anyway");
                }
            }
            catch (AggregateException)
            {
                // A kill that failed was logged by whoever sent it; closing proceeds either way.
            }
        }

        // One bounded wait, not two: when there were kills to wait on, a completed kill's reply
        // already proves every earlier frame reached the daemon, and a timed-out wait already means
        // the daemon is unresponsive - either way the ping flush below would only add its own wait
        // on top for nothing. The ping flush only runs when there was no kill to wait on at all.
        if (kills.Length == 0 && client.IsConnected)
        {
            try
            {
                using var cts = new CancellationTokenSource(DisposeFlushTimeout);
                // Deliberately not tied to _disposed (already cancelled): the flush is bounded by its own timeout.
                if (!Task.Run(() => client.PingAsync(cts.Token), CancellationToken.None).Wait(DisposeFlushTimeout, CancellationToken.None))
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
