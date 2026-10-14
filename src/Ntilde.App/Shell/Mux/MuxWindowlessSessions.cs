using System.Diagnostics;
using Ntilde.AgentHost;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;

namespace Ntilde.Shell.Mux;

/// <summary>
/// The window's windowless mux sessions (Phase 5 spec §3, ruling R4), over its <see cref="MuxConnectionHosts"/>: what the
/// agent host lists, reads, sends input to and kills besides panes. A session is windowless when a daemon the window is
/// connected to now has it, it has not faulted, and no pane of the window shows it or is about to (<c>shownHere</c>, the
/// window's answer, keyed by endpoint and id: one id on two daemons is two sessions). A session another process shows
/// counts as windowless here.
/// </summary>
/// <remarks>
/// <para>
/// Every call asks afresh and keeps nothing for the next (sessions come and go). It lists every host whose
/// <see cref="MuxConnectionHost.CurrentClient"/> is live, in parallel, then asks the window what its panes show; a look-up
/// then acts on the session it found, through the client that listed it, and an act checks its endpoint (<c>mayAct</c>)
/// in that same look-up. Never <see cref="MuxConnectionHost.GetClient"/> nor <see cref="MuxConnectionHosts.GetOrCreate"/>:
/// the agent path neither connects nor prompts, and builds no host.
/// </para>
/// <para>
/// Each public call has one <see cref="OperationBudget"/> (fix round 1), well inside the MCP server's 10 s round trip, and
/// each daemon call in it - and the window's answer - waits at most <see cref="CallTimeout"/> or what is left of that
/// budget, whichever is less. A survey costs at most one call timeout for its daemons (asked together) and what is left
/// for the window; a read's halving retries and its <c>sessionInfo</c> fallback, and a kill, stop when the budget runs
/// out: the call is then <see cref="WindowlessOutcome.Unreachable"/>, logged once. Only the caller's own cancellation
/// ends a call with <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// A daemon that fails or times out is left out of the listing, with one log line per call, and a look-up of an id it may
/// have is <see cref="WindowlessOutcome.Unreachable"/>. An id more than one daemon lists is ambiguous: it is neither
/// offered nor acted on, whichever of them a pane shows (an SSH profile can reach this computer's own daemon); one log
/// line per call reports them all.
/// </para>
/// <para>
/// UI-free and thread-safe: <c>shownHere</c> is the one thing that touches the window, and it posts to the UI thread
/// itself; it is awaited, never waited for. A window that does not answer offers nothing: an empty listing, and every
/// look-up <see cref="WindowlessOutcome.Unreachable"/>.
/// </para>
/// </remarks>
internal sealed class MuxWindowlessSessions : IWindowlessSessionSource
{
    /// <summary>The longest one daemon call, or the window's <c>shownHere</c>, is waited for.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    /// <summary>The most one public call takes, everything it asks included (fix round 1).</summary>
    public static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(7);

    /// <summary><see cref="WindowlessSessionInfo.HostDisplayName"/> of the local daemon's sessions.</summary>
    public const string LocalHostDisplayName = "this computer";

    /// <summary>How many ambiguous ids a log line names; it counts them all.</summary>
    private const int AmbiguousIdsLogged = 3;

    private readonly MuxConnectionHosts _hosts;
    private readonly Func<Task<IReadOnlySet<(string Endpoint, Guid Id)>>> _shownHere;
    private readonly Action<string>? _log;

    /// <param name="hosts">The window's hosts, read at each call.</param>
    /// <param name="shownHere">
    /// Every (endpoint in its persisted form, mux session id) a pane of the window shows or is about to show. Called off
    /// the UI thread; it marshals to it itself, and must not block.
    /// </param>
    /// <param name="log">Where daemons that did not answer, failed calls and ambiguous ids are reported.</param>
    public MuxWindowlessSessions(MuxConnectionHosts hosts, Func<Task<IReadOnlySet<(string Endpoint, Guid Id)>>> shownHere, Action<string>? log)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(shownHere);
        _hosts = hosts;
        _shownHere = shownHere;
        _log = log;
    }

    public async Task<IReadOnlyList<WindowlessSessionInfo>> ListAsync(CancellationToken ct)
    {
        using var op = new Operation(ct);
        Survey survey = await SurveyAsync(op).ConfigureAwait(false);
        return [.. survey.Windowless.Select(s => s.Info)];
    }

    /// <remarks>
    /// Task 11 review: a wide session's full <see cref="MuxReadScreenLimits.MaxScrollbackRows"/> can pass the daemon's
    /// <see cref="MuxReadScreenLimits.MaxSnapshotBytes"/>. A read refused as too large is asked again with half the rows,
    /// down to none, while the budget lasts; one that does not fit even then is <see cref="WindowlessOutcome.Unreachable"/>,
    /// logged.
    /// </remarks>
    public async Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct)
    {
        using var op = new Operation(ct);
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(op, sessionId).ConfigureAwait(false);
        if (session is null) return new WindowlessScreen(outcome, null, null, null);

        int rows = Math.Clamp(maxScrollbackRows, 0, MuxReadScreenLimits.MaxScrollbackRows);
        MuxScreenRead? read;
        while (true)
        {
            try
            {
                read = await CallAsync(op, c => session.Client.ReadScreenAsync(sessionId, rows, c)).ConfigureAwait(false);
                break;
            }
            catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.SnapshotTooLarge && rows > 0)
            {
                rows /= 2; // the next try stops at once if the budget has run out meanwhile
            }
            catch (Exception ex) when (!op.Caller.IsCancellationRequested)
            {
                return new WindowlessScreen(Failed(session, $"reading the screen ({rows} scrollback rows) of", ex), session.Info, null, null);
            }
        }

        if (read is not null) return new WindowlessScreen(WindowlessOutcome.Ok, session.Info, read.Snapshot, read.Status);

        // A daemon older than readScreen: its status, from sessionInfo, and no screen.
        SessionInfoResult info;
        try
        {
            info = await CallAsync(op, c => session.Client.GetSessionInfoAsync(sessionId, c)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!op.Caller.IsCancellationRequested)
        {
            return new WindowlessScreen(Failed(session, "asking for the status of", ex), session.Info, null, null);
        }

        return new WindowlessScreen(WindowlessOutcome.Unsupported, session.Info, null, new ReadScreenResult
        {
            Running = info.Running,
            ExitCode = info.ExitCode,
            HasActiveChildProcesses = info.HasActiveChildProcesses,
            AttachedClients = info.AttachedClients ?? session.Summary.AttachedClients,
            InteractiveClients = info.InteractiveClients,
            Title = info.Title,
            Cwd = info.Cwd,
        });
    }

    /// <remarks>
    /// The act check comes before the running state, as a pane's does (allowlist, then <c>sessionNotRunning</c>). Input is
    /// fire-and-forget on the wire, and the daemon drops it silently for an unknown or exited session: hence the look-up
    /// first. A session that exits between that look-up and the input loses the input unannounced.
    /// </remarks>
    public async Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, Func<Guid?, bool> mayAct, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(mayAct);
        using var op = new Operation(ct);
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(op, sessionId).ConfigureAwait(false);
        if (session is null) return outcome;
        if (!MayAct(session, mayAct, "sending input to")) return WindowlessOutcome.NotAllowed;
        if (!session.Info.Running) return WindowlessOutcome.NotRunning;
        if (!session.Client.IsConnected)
        {
            _log?.Invoke($"[Mux] sending input to session {sessionId} on {session.Info.HostDisplayName} for the agent host failed: the connection closed");
            return WindowlessOutcome.Unreachable;
        }

        session.Client.SendInputTo(sessionId, text); // never blocks (ruling R6)
        return WindowlessOutcome.Ok;
    }

    public async Task<WindowlessOutcome> KillAsync(Guid sessionId, Func<Guid?, bool> mayAct, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mayAct);
        using var op = new Operation(ct);
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(op, sessionId).ConfigureAwait(false);
        if (session is null) return outcome;
        if (!MayAct(session, mayAct, "killing")) return WindowlessOutcome.NotAllowed;
        if (!session.Info.Running) return WindowlessOutcome.NotRunning;
        try
        {
            await CallAsync(op, async c =>
            {
                await session.Client.KillAsync(sessionId, c).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (!op.Caller.IsCancellationRequested)
        {
            return Failed(session, "killing", ex);
        }

        return WindowlessOutcome.Ok;
    }

    public async Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct)
    {
        using var op = new Operation(ct);
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(op, sessionId).ConfigureAwait(false);
        return (outcome, session?.Info.SshProfileId);
    }

    /// <remarks>
    /// Over <see cref="MuxConnectionHosts.AllByEndpoint"/>, the hosts a survey asks, so the name is the one a listing of
    /// that endpoint gives. Takes only the registry's lock; asks, connects and builds nothing.
    /// </remarks>
    public string? HostDisplayName(Guid? sshProfileId)
    {
        if (sshProfileId is null) return LocalHostDisplayName;
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host) in _hosts.AllByEndpoint)
        {
            if (endpoint.SshProfileId == sshProfileId) return DisplayName(endpoint, host);
        }
        return null;
    }

    /// <summary>A windowless session, the summary its daemon listed, and the client that listed it.</summary>
    private sealed record Found(WindowlessSessionInfo Info, SessionSummary Summary, MuxClient Client);

    /// <summary>What one call learned about the window's daemons.</summary>
    private sealed class Survey
    {
        public List<Found> Windowless { get; } = [];

        /// <summary>Every id a daemon that answered listed: windowless, shown here, faulted or ambiguous.</summary>
        public HashSet<Guid> Listed { get; } = [];

        /// <summary>A daemon that was asked did not answer: an id nobody listed may be on it.</summary>
        public bool SomeDaemonUnanswered { get; set; }

        /// <summary>The window did not say what its panes show: no session can be told windowless.</summary>
        public bool ShownUnknown { get; set; }
    }

    /// <summary>
    /// One public call's budget: <see cref="OperationBudget"/> from its start. <see cref="Token"/> is cancelled when the
    /// budget runs out or the caller cancels; <see cref="Caller"/> is the caller's own token, which tells the two apart.
    /// </summary>
    private sealed class Operation : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly long _started = Stopwatch.GetTimestamp();

        public Operation(CancellationToken caller)
        {
            Caller = caller;
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
            _deadline.CancelAfter(OperationBudget);
        }

        public CancellationToken Caller { get; }

        public CancellationToken Token => _deadline.Token;

        public TimeSpan Remaining => OperationBudget - Stopwatch.GetElapsedTime(_started);

        public void Dispose() => _deadline.Dispose();
    }

    private async Task<(WindowlessOutcome Outcome, Found? Session)> LookUpAsync(Operation op, Guid sessionId)
    {
        Survey survey = await SurveyAsync(op).ConfigureAwait(false);
        if (survey.Windowless.Find(s => s.Info.SessionId == sessionId) is { } found) return (WindowlessOutcome.Ok, found);
        if (survey.ShownUnknown) return (WindowlessOutcome.Unreachable, null);
        // Listed but not windowless: a pane here shows it (its pane id is the one to use), it faulted, or it is ambiguous.
        if (survey.Listed.Contains(sessionId)) return (WindowlessOutcome.NotFound, null);
        return (survey.SomeDaemonUnanswered ? WindowlessOutcome.Unreachable : WindowlessOutcome.NotFound, null);
    }

    private async Task<Survey> SurveyAsync(Operation op)
    {
        var asked = new List<(MuxEndpointId Endpoint, MuxConnectionHost Host, MuxClient Client, Task<IReadOnlyList<SessionSummary>?> Listing)>();
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host) in _hosts.AllByEndpoint)
        {
            if (host.CurrentClient is not { } client) continue; // never GetClient: the agent path does not connect
            asked.Add((endpoint, host, client, ListHostAsync(op, endpoint, host, client)));
        }

        await Task.WhenAll(asked.Select(a => a.Listing)).ConfigureAwait(false);

        // Asked only now that every listing is in, not alongside them: a pane that attached, or spawned, while they ran is
        // counted. What is left: a brand-new remote tab holds no session id while its spawn runs off the UI thread (Phase 4
        // spec §7.4) - the id is the pane's once the factory's result comes back - so the shell that spawn starts can be
        // listed as windowless meanwhile.
        IReadOnlySet<(string Endpoint, Guid Id)>? shown = await ShownHereAsync(op).ConfigureAwait(false);

        var survey = new Survey { ShownUnknown = shown is null };
        var answered = new List<(MuxEndpointId Endpoint, MuxConnectionHost Host, MuxClient Client, IReadOnlyList<SessionSummary> Sessions)>();
        var endpointsListing = new Dictionary<Guid, int>(); // how many endpoints list each id
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client, Task<IReadOnlyList<SessionSummary>?> listing) in asked)
        {
            if (await listing.ConfigureAwait(false) is not { } sessions) // done: no wait
            {
                survey.SomeDaemonUnanswered = true;
                continue;
            }

            answered.Add((endpoint, host, client, sessions));
            foreach (SessionSummary s in sessions)
            {
                survey.Listed.Add(s.SessionId);
                endpointsListing[s.SessionId] = endpointsListing.GetValueOrDefault(s.SessionId) + 1;
            }
        }

        Guid[] ambiguous = [.. endpointsListing.Where(e => e.Value > 1).Select(e => e.Key)];
        if (ambiguous.Length > 0)
        {
            string more = ambiguous.Length > AmbiguousIdsLogged ? $" and {ambiguous.Length - AmbiguousIdsLogged} more" : string.Empty;
            _log?.Invoke($"[Mux] {ambiguous.Length} session id(s) are on more than one endpoint, so they are ambiguous and not offered to the agent host: "
                + string.Join(", ", ambiguous.Take(AmbiguousIdsLogged)) + more);
        }

        if (shown is null) return survey;
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client, IReadOnlyList<SessionSummary> sessions) in answered)
        {
            string endpointText = endpoint.ToString();
            foreach (SessionSummary s in sessions)
            {
                if (s.Faulted || endpointsListing[s.SessionId] > 1 || shown.Contains((endpointText, s.SessionId))) continue;
                var info = new WindowlessSessionInfo(s.SessionId, endpointText, endpoint.SshProfileId, DisplayName(endpoint, host),
                    s.Title, s.Cols, s.Rows, s.Running, s.ExitCode);
                survey.Windowless.Add(new Found(info, s, client));
            }
        }

        return survey;
    }

    /// <summary>One daemon's sessions; null, logged, when it fails or does not answer in time.</summary>
    private async Task<IReadOnlyList<SessionSummary>?> ListHostAsync(Operation op, MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client)
    {
        try
        {
            return await CallAsync(op, c => client.ListSessionsAsync(c)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!op.Caller.IsCancellationRequested)
        {
            _log?.Invoke($"[Mux] {DisplayName(endpoint, host)} did not list its sessions for the agent host ({Describe(ex)}); they are left out");
            return null;
        }
    }

    /// <summary>The window's answer; null, logged, when it fails or does not come in time.</summary>
    private async Task<IReadOnlySet<(string Endpoint, Guid Id)>?> ShownHereAsync(Operation op)
    {
        try
        {
            // The delegate takes no token: the wait for its answer is what the call's time bounds.
            return await CallAsync(op, c => _shownHere().WaitAsync(c)).ConfigureAwait(false);
        }
        catch (Exception ex) when (!op.Caller.IsCancellationRequested)
        {
            _log?.Invoke($"[Mux] the window did not say which sessions its panes show ({Describe(ex)}); no session is offered to the agent host as windowless");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="call"/>, given <see cref="CallTimeout"/> or what is left of <paramref name="op"/>'s budget, whichever
    /// is less; none left, it is not made. The caller's cancellation stays an <see cref="OperationCanceledException"/>.
    /// Any other cancellation becomes an exception that says which clock stopped the call: its own timeout ("no answer
    /// within"), the operation's budget, or neither.
    /// </summary>
    private static async Task<T> CallAsync<T>(Operation op, Func<CancellationToken, Task<T>> call)
    {
        TimeSpan remaining = op.Remaining;
        if (remaining <= TimeSpan.Zero) throw BudgetSpent(null);
        bool cutByBudget = remaining < CallTimeout;
        TimeSpan limit = cutByBudget ? remaining : CallTimeout;
        using var timer = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.Token, timer.Token);
        try
        {
            return await call(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!op.Caller.IsCancellationRequested)
        {
            if (timer.IsCancellationRequested && !cutByBudget) throw new TimeoutException($"no answer within {limit.TotalSeconds:0.#} s", ex);
            if (timer.IsCancellationRequested || op.Token.IsCancellationRequested) throw BudgetSpent(ex);
            throw new IOException($"the call was cancelled before it was answered, by neither its timeout nor its budget ({ex.Message})", ex);
        }
    }

    private static TimeoutException BudgetSpent(Exception? inner) =>
        new($"the operation's {OperationBudget.TotalSeconds:0} s budget ran out", inner);

    /// <summary>The act check, failing closed: a check that throws refuses, logged.</summary>
    private bool MayAct(Found session, Func<Guid?, bool> mayAct, string what)
    {
        try
        {
            return mayAct(session.Info.SshProfileId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Mux] the act check for {what} session {session.Info.SessionId} on {session.Info.HostDisplayName} failed ({ex.Message}); not allowed");
            return false;
        }
    }

    /// <summary>
    /// What a call that failed on a found session tells the agent host, logged. The session may have gone or exited since
    /// the look-up; anything else - a timeout, the budget running out, a closed connection, a refusal - means the daemon
    /// could not do it now.
    /// </summary>
    private WindowlessOutcome Failed(Found session, string what, Exception ex)
    {
        WindowlessOutcome outcome = ex switch
        {
            MuxProtocolException { Code: MuxErrorCodes.UnknownSession } => WindowlessOutcome.NotFound,
            MuxProtocolException { Code: MuxErrorCodes.SessionExited } => WindowlessOutcome.NotRunning,
            _ => WindowlessOutcome.Unreachable,
        };
        _log?.Invoke($"[Mux] {what} session {session.Info.SessionId} on {session.Info.HostDisplayName} for the agent host failed ({Describe(ex)})");
        return outcome;
    }

    private static string DisplayName(MuxEndpointId endpoint, MuxConnectionHost host) =>
        endpoint.IsLocal ? LocalHostDisplayName : host.Policy.DisplayName;

    private static string Describe(Exception ex) => ex is MuxProtocolException p ? $"{p.Code}: {p.Message}" : ex.Message;
}
