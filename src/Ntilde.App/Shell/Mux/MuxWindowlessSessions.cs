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
/// <see cref="MuxConnectionHost.CurrentClient"/> is live, in parallel, while the window says what its panes show; a lookup
/// then acts on the session it found, through the client that listed it. Never <see cref="MuxConnectionHost.GetClient"/>
/// nor <see cref="MuxConnectionHosts.GetOrCreate"/>: the agent path neither connects nor prompts, and builds no host.
/// </para>
/// <para>
/// Each daemon call has its own <see cref="CallTimeout"/>, well inside the MCP server's 10 s round trip. A daemon that
/// fails or times out is left out of the listing, with one log line per call, and a lookup of an id it may have is
/// <see cref="WindowlessOutcome.Unreachable"/>. An id more than one daemon lists is ambiguous: it is neither offered nor
/// acted on, whichever of them a pane shows (an SSH profile can reach this computer's own daemon).
/// </para>
/// <para>
/// UI-free and thread-safe: <c>shownHere</c> is the one thing that touches the window, and it posts to the UI thread
/// itself; it is awaited, never waited for, and bounded by <see cref="CallTimeout"/> too.
/// </para>
/// </remarks>
internal sealed class MuxWindowlessSessions : IWindowlessSessionSource
{
    /// <summary>How long one daemon call, and the window's <c>shownHere</c>, may take.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    /// <summary><see cref="WindowlessSessionInfo.HostDisplayName"/> of the local daemon's sessions.</summary>
    public const string LocalHostDisplayName = "this computer";

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
        Survey survey = await SurveyAsync(ct).ConfigureAwait(false);
        return [.. survey.Windowless.Select(s => s.Info)];
    }

    /// <remarks>
    /// Task 11 review: a wide session's full <see cref="MuxReadScreenLimits.MaxScrollbackRows"/> can pass the daemon's
    /// <see cref="MuxReadScreenLimits.MaxSnapshotBytes"/>. A read refused as too large is asked again with half the rows,
    /// down to none; one that does not fit even then is <see cref="WindowlessOutcome.Unreachable"/>, logged.
    /// </remarks>
    public async Task<WindowlessScreen> ReadScreenAsync(Guid sessionId, int maxScrollbackRows, CancellationToken ct)
    {
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null) return new WindowlessScreen(outcome, null, null, null);

        int rows = Math.Clamp(maxScrollbackRows, 0, MuxReadScreenLimits.MaxScrollbackRows);
        MuxScreenRead? read;
        while (true)
        {
            try
            {
                read = await CallAsync(c => session.Client.ReadScreenAsync(sessionId, rows, c), ct).ConfigureAwait(false);
                break;
            }
            catch (MuxProtocolException ex) when (ex.Code == MuxErrorCodes.SnapshotTooLarge && rows > 0)
            {
                rows /= 2;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return new WindowlessScreen(Failed(session, $"reading the screen ({rows} scrollback rows) of", ex), session.Info, null, null);
            }
        }

        if (read is not null) return new WindowlessScreen(WindowlessOutcome.Ok, session.Info, read.Snapshot, read.Status);

        // A daemon older than readScreen: its status, from sessionInfo, and no screen.
        SessionInfoResult info;
        try
        {
            info = await CallAsync(c => session.Client.GetSessionInfoAsync(sessionId, c), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
    /// Input is fire-and-forget on the wire, and the daemon drops it silently for an unknown or exited session: hence the
    /// listing's check first. A session that exits between that check and the input loses the input unannounced.
    /// </remarks>
    public async Task<WindowlessOutcome> SendInputAsync(Guid sessionId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null) return outcome;
        if (!session.Info.Running) return WindowlessOutcome.NotRunning;
        if (!session.Client.IsConnected)
        {
            _log?.Invoke($"[Mux] sending input to session {sessionId} on {session.Info.HostDisplayName} for the agent host failed: the connection closed");
            return WindowlessOutcome.Unreachable;
        }

        session.Client.SendInputTo(sessionId, text); // never blocks (ruling R6)
        return WindowlessOutcome.Ok;
    }

    public async Task<WindowlessOutcome> KillAsync(Guid sessionId, CancellationToken ct)
    {
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null) return outcome;
        if (!session.Info.Running) return WindowlessOutcome.NotRunning;
        try
        {
            await CallAsync(async c =>
            {
                await session.Client.KillAsync(sessionId, c).ConfigureAwait(false);
                return true;
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Failed(session, "killing", ex);
        }

        return WindowlessOutcome.Ok;
    }

    public async Task<(WindowlessOutcome Outcome, Guid? SshProfileId)> ResolveAsync(Guid sessionId, CancellationToken ct)
    {
        (WindowlessOutcome outcome, Found? session) = await LookUpAsync(sessionId, ct).ConfigureAwait(false);
        return (outcome, session?.Info.SshProfileId);
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

    private async Task<(WindowlessOutcome Outcome, Found? Session)> LookUpAsync(Guid sessionId, CancellationToken ct)
    {
        Survey survey = await SurveyAsync(ct).ConfigureAwait(false);
        if (survey.Windowless.Find(s => s.Info.SessionId == sessionId) is { } found) return (WindowlessOutcome.Ok, found);
        if (survey.ShownUnknown) return (WindowlessOutcome.Unreachable, null);
        // Listed but not windowless: a pane here shows it (its pane id is the one to use), it faulted, or it is ambiguous.
        if (survey.Listed.Contains(sessionId)) return (WindowlessOutcome.NotFound, null);
        return (survey.SomeDaemonUnanswered ? WindowlessOutcome.Unreachable : WindowlessOutcome.NotFound, null);
    }

    private async Task<Survey> SurveyAsync(CancellationToken ct)
    {
        var asked = new List<(MuxEndpointId Endpoint, MuxConnectionHost Host, MuxClient Client, Task<IReadOnlyList<SessionSummary>?> Listing)>();
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host) in _hosts.AllByEndpoint)
        {
            if (host.CurrentClient is not { } client) continue; // never GetClient: the agent path does not connect
            asked.Add((endpoint, host, client, ListHostAsync(endpoint, host, client, ct)));
        }

        Task<IReadOnlySet<(string Endpoint, Guid Id)>?> shownTask = ShownHereAsync(ct);
        var all = new List<Task>(asked.Count + 1) { shownTask };
        all.AddRange(asked.Select(a => a.Listing));
        await Task.WhenAll(all).ConfigureAwait(false);
        IReadOnlySet<(string Endpoint, Guid Id)>? shown = await shownTask.ConfigureAwait(false); // all done: no wait from here on

        var survey = new Survey { ShownUnknown = shown is null };
        var answered = new List<(MuxEndpointId Endpoint, MuxConnectionHost Host, MuxClient Client, IReadOnlyList<SessionSummary> Sessions)>();
        var endpointsListing = new Dictionary<Guid, List<string>>();
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client, Task<IReadOnlyList<SessionSummary>?> listing) in asked)
        {
            if (await listing.ConfigureAwait(false) is not { } sessions)
            {
                survey.SomeDaemonUnanswered = true;
                continue;
            }

            answered.Add((endpoint, host, client, sessions));
            foreach (SessionSummary s in sessions)
            {
                survey.Listed.Add(s.SessionId);
                if (!endpointsListing.TryGetValue(s.SessionId, out List<string>? where)) endpointsListing[s.SessionId] = where = [];
                where.Add(endpoint.ToString());
            }
        }

        foreach ((Guid id, List<string> where) in endpointsListing.Where(e => e.Value.Count > 1))
        {
            _log?.Invoke($"[Mux] session {id} is on more than one endpoint ({string.Join(", ", where)}): ambiguous, so it is not offered to the agent host");
        }

        if (shown is null) return survey;
        foreach ((MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client, IReadOnlyList<SessionSummary> sessions) in answered)
        {
            string endpointText = endpoint.ToString();
            foreach (SessionSummary s in sessions)
            {
                if (s.Faulted || endpointsListing[s.SessionId].Count > 1 || shown.Contains((endpointText, s.SessionId))) continue;
                var info = new WindowlessSessionInfo(s.SessionId, endpointText, endpoint.SshProfileId, DisplayName(endpoint, host),
                    s.Title, s.Cols, s.Rows, s.Running, s.ExitCode);
                survey.Windowless.Add(new Found(info, s, client));
            }
        }

        return survey;
    }

    /// <summary>One daemon's sessions; null, logged, when it fails or does not answer in time.</summary>
    private async Task<IReadOnlyList<SessionSummary>?> ListHostAsync(MuxEndpointId endpoint, MuxConnectionHost host, MuxClient client, CancellationToken ct)
    {
        try
        {
            return await CallAsync(c => client.ListSessionsAsync(c), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[Mux] {DisplayName(endpoint, host)} did not list its sessions for the agent host ({Describe(ex)}); they are left out");
            return null;
        }
    }

    /// <summary>The window's answer; null, logged, when it fails or does not come in time.</summary>
    private async Task<IReadOnlySet<(string Endpoint, Guid Id)>?> ShownHereAsync(CancellationToken ct)
    {
        try
        {
            return await _shownHere().WaitAsync(CallTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.Invoke($"[Mux] the window did not say which sessions its panes show ({Describe(ex)}); no session is offered to the agent host as windowless");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="call"/>, cancelled after <see cref="CallTimeout"/> or with <paramref name="ct"/>. The timeout is a
    /// <see cref="TimeoutException"/>; the caller's cancellation stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    private static async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            return await call(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer within {CallTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>
    /// What a call that failed on a found session tells the agent host, logged. The session may have gone or exited since
    /// the listing; anything else - a timeout, a closed connection, a refusal - means the daemon could not do it now.
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
