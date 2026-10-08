using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization.Metadata;
using Ntilde.AgentHost;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;
using Ntilde.Tests.Shell.Mux.Remote;
using Ntilde.VT;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>
/// Phase 5 spec §3, ruling R4 (Task 12): the window's source of windowless mux sessions, over its hosts. The local
/// endpoint and a remote one are in-memory daemons, or a daemon the test scripts; which sessions the window's panes
/// show is a stub. The window's own answer to that is pinned in MainWindowMuxRemoteTests.
/// </summary>
public sealed class MuxWindowlessSessionsTests : IDisposable
{
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly MuxTestHost _localMux = new();
    private readonly MuxTestHost _remoteMux = new();
    private readonly FakeMuxTimerScheduler _clock = new(); // a remote host's liveness ping and reconnect loop wait on it: never run
    private readonly List<IDisposable> _owned = [];
    private readonly ConcurrentQueue<string> _log = new();
    private readonly HashSet<(string Endpoint, Guid Id)> _shown = []; // what the window's panes show, or are about to
    private MuxConnectionHosts? _hosts;

    public void Dispose()
    {
        _hosts?.Dispose();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _remoteMux.Dispose();
        _localMux.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string RemoteEndpoint => MuxEndpointId.ForSsh(_profileId).ToString();

    private static MuxConnectionHost LocalHost(Func<CancellationToken, Task<MuxClient>> connect) =>
        new(connect, "local", null) { DisposeFlushTimeout = TimeSpan.FromMilliseconds(100) };

    private MuxConnectionHost RemoteHost(Func<CancellationToken, Task<MuxClient>> connect, string name = "box") =>
        new(connect, name, null, MuxHostPolicy.Remote(name)) { Scheduler = _clock, DisposeFlushTimeout = TimeSpan.FromMilliseconds(100) };

    private static Func<CancellationToken, Task<MuxClient>> To(MuxTestHost mux) => ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct);

    /// <summary>The window's hosts: the local daemon's, and each remote one built as a pane's first use of its endpoint builds it.</summary>
    private MuxConnectionHosts Hosts(MuxConnectionHost local, params (MuxEndpointId Id, MuxConnectionHost Host)[] remotes)
    {
        var hosts = _hosts = new MuxConnectionHosts(local, id => remotes.Single(r => r.Id == id).Host);
        foreach ((MuxEndpointId id, MuxConnectionHost host) in remotes) Assert.Same(host, hosts.GetOrCreate(id));
        return hosts;
    }

    /// <summary>Connects <paramref name="host"/>, as a pane's spawn would: the source never does.</summary>
    private static async Task<MuxClient> ConnectAsync(MuxConnectionHost host) =>
        await Task.Run(() => host.GetClient(TimeSpan.FromSeconds(10)), Ct)
        ?? throw new InvalidOperationException($"{host.Endpoint} did not connect: {host.LastFailure?.Message}");

    private MuxWindowlessSessions Source(MuxConnectionHosts hosts) =>
        new(hosts, () => Task.FromResult<IReadOnlySet<(string Endpoint, Guid Id)>>(new HashSet<(string, Guid)>(_shown)), _log.Enqueue);

    /// <summary>
    /// The window most tests need: connected to the local daemon and to the remote endpoint of <see cref="_profileId"/>.
    /// The clients returned are the test's own, to spawn with; the hosts' clients are the window's.
    /// </summary>
    private async Task<(MuxWindowlessSessions Source, MuxClient Local, MuxClient Remote)> ConnectedAsync()
    {
        MuxConnectionHost local = LocalHost(To(_localMux));
        MuxConnectionHost remote = RemoteHost(To(_remoteMux));
        MuxConnectionHosts hosts = Hosts(local, (MuxEndpointId.ForSsh(_profileId), remote));
        await ConnectAsync(local);
        await ConnectAsync(remote);
        return (Source(hosts), await _localMux.ConnectClientAsync(), await _remoteMux.ConnectClientAsync());
    }

    private static async Task FaultAsync(MuxTestHost mux, Guid id)
    {
        await mux.Mux(id).MakeParserThrowOnReplyAsync();
        mux.Fake(id).Emit("\x1b[c"); // DA1: the reply throws inside the daemon's parser, which faults the session
        await mux.Mux(id).FlushAsync();
        Assert.True(mux.Mux(id).IsFaulted);
    }

    private static async Task ExitAsync(MuxTestHost mux, Guid id, int code)
    {
        mux.Fake(id).Exit(code);
        await TestWait.UntilAsync(() => mux.Mux(id).IsExited, "the shell exited");
    }

    private static string ScreenText(TerminalStateSnapshot snapshot)
    {
        var buffer = new TerminalBuffer(snapshot.Cols, snapshot.Rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null, AllowNativeKittyGraphics = false };
        TerminalStateTransfer.Restore(buffer, parser, snapshot);
        return MuxTestText.VisibleText(buffer);
    }

    [Fact]
    public async Task It_lists_the_sessions_no_pane_shows_on_every_connected_endpoint_and_leaves_out_faulted_ones()
    {
        (MuxWindowlessSessions source, MuxClient local, MuxClient remote) = await ConnectedAsync();
        Guid shownLocal = await MuxTestHost.SpawnAsync(local);
        Guid windowlessLocal = await MuxTestHost.SpawnAsync(local, cols: 100, rows: 30);
        Guid faulted = await MuxTestHost.SpawnAsync(local);
        Guid pendingRemote = await MuxTestHost.SpawnAsync(remote);
        Guid windowlessRemote = await MuxTestHost.SpawnAsync(remote);
        await FaultAsync(_localMux, faulted);
        await ExitAsync(_remoteMux, windowlessRemote, 3);
        _shown.Add(("local", shownLocal));
        _shown.Add((RemoteEndpoint, pendingRemote));
        _shown.Add((RemoteEndpoint, windowlessLocal)); // one id on two daemons is two sessions: this names no local one

        IReadOnlyList<WindowlessSessionInfo> listed = await source.ListAsync(Ct);

        Assert.Equal(
            [
                new WindowlessSessionInfo(windowlessLocal, "local", null, "this computer", "test", 100, 30, true, null),
                new WindowlessSessionInfo(windowlessRemote, RemoteEndpoint, _profileId, "box", "test", 80, 24, false, 3),
            ],
            listed);
        Assert.Equal((WindowlessOutcome.Ok, (Guid?)null), await source.ResolveAsync(windowlessLocal, Ct));
        Assert.Equal((WindowlessOutcome.Ok, (Guid?)_profileId), await source.ResolveAsync(windowlessRemote, Ct));
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), await source.ResolveAsync(shownLocal, Ct)); // its pane id is the one to use
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), await source.ResolveAsync(pendingRemote, Ct));
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), await source.ResolveAsync(faulted, Ct));
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), await source.ResolveAsync(Guid.NewGuid(), Ct));
    }

    /// <summary>
    /// Ruling R4: the agent path never connects and never prompts. A host that never connected and one whose link
    /// dropped are skipped - neither connect function runs again - and the sessions only they could show are not offered.
    /// </summary>
    [Fact]
    public async Task Only_hosts_with_a_live_connection_are_asked_and_none_is_ever_connected()
    {
        int deadConnects = 0, droppedConnects = 0;
        MuxConnectionHost local = LocalHost(To(_localMux));
        MuxConnectionHost dead = RemoteHost(_ =>
        {
            Interlocked.Increment(ref deadConnects);
            throw new IOException("no route to host");
        }, "dead");
        MuxConnectionHost dropped = RemoteHost(ct =>
        {
            Interlocked.Increment(ref droppedConnects);
            return MuxClient.ConnectAsync(_remoteMux.Listener.Connect(), null, ct);
        }, "dropped");
        MuxConnectionHosts hosts = Hosts(local, (MuxEndpointId.ForSsh(Guid.NewGuid()), dead), (MuxEndpointId.ForSsh(Guid.NewGuid()), dropped));
        await ConnectAsync(local);
        MuxClient droppedClient = await ConnectAsync(dropped);
        Guid onLocal = await MuxTestHost.SpawnAsync(await _localMux.ConnectClientAsync());
        Guid onDropped = await MuxTestHost.SpawnAsync(await _remoteMux.ConnectClientAsync());
        droppedClient.Dispose(); // the link is gone; the host's reconnect loop waits on the test's clock
        Assert.Null(dropped.CurrentClient);
        Assert.Null(dead.CurrentClient);
        MuxWindowlessSessions source = Source(hosts);

        IReadOnlyList<WindowlessSessionInfo> listed = await source.ListAsync(Ct);
        (WindowlessOutcome, Guid?) resolved = await source.ResolveAsync(onDropped, Ct);

        Assert.Equal([onLocal], listed.Select(s => s.SessionId));
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), resolved); // an endpoint with no live connection has no windowless sessions
        Assert.Equal((0, 1), (Volatile.Read(ref deadConnects), Volatile.Read(ref droppedConnects)));
        Assert.Equal((0, 1), (dead.ConnectAttempts, dropped.ConnectAttempts));
    }

    [Fact]
    public async Task A_daemon_that_never_answers_costs_one_call_timeout_and_the_others_are_still_listed()
    {
        var hung = new ScriptedDaemon(answer: null); // reads every request, answers none
        _owned.Add(hung);
        MuxConnectionHost local = LocalHost(To(_localMux));
        MuxConnectionHost remote = RemoteHost(ct => hung.ConnectAsync(2, ct), "hung");
        MuxConnectionHosts hosts = Hosts(local, (MuxEndpointId.ForSsh(_profileId), remote));
        await ConnectAsync(local);
        await ConnectAsync(remote);
        Guid onLocal = await MuxTestHost.SpawnAsync(await _localMux.ConnectClientAsync());
        MuxWindowlessSessions source = Source(hosts);

        var clock = Stopwatch.StartNew();
        Task<IReadOnlyList<WindowlessSessionInfo>> listing = source.ListAsync(Ct);
        Task<(WindowlessOutcome, Guid?)> lookUp = source.ResolveAsync(Guid.NewGuid(), Ct); // it could be on the hung daemon
        await Task.WhenAll(listing, lookUp);
        TimeSpan took = clock.Elapsed;

        Assert.Equal([onLocal], (await listing).Select(s => s.SessionId));
        Assert.Equal((WindowlessOutcome.Unreachable, (Guid?)null), await lookUp);
        Assert.True(took < TimeSpan.FromSeconds(4.5), $"the calls took {took}");
        Assert.True(took >= TimeSpan.FromSeconds(3.5), $"the calls took {took}: shorter than the call timeout");
        Assert.Equal(2, _log.Count(l => l.Contains("hung", StringComparison.Ordinal))); // once per call
    }

    [Fact]
    public async Task A_read_from_a_daemon_without_readScreen_is_unsupported_with_the_status_sessionInfo_gives()
    {
        Guid id = Guid.NewGuid();
        var old = new ScriptedDaemon(request => request.Method switch
        {
            MuxMethods.ListSessions => Answer(
                new ListSessionsResult { Sessions = [new SessionSummary { SessionId = id, Title = "old", Command = "sh", Cols = 80, Rows = 24, Running = true }] },
                MuxJsonContext.Default.ListSessionsResult),
            MuxMethods.SessionInfo => Answer(
                new SessionInfoResult { Running = true, HasActiveChildProcesses = true, Pid = 42, Title = "vim", Cwd = "/srv", AttachedClients = 1 },
                MuxJsonContext.Default.SessionInfoResult),
            _ => Refuse(MuxErrorCodes.ProtocolError), // how every daemon answers a method it does not know
        });
        _owned.Add(old);
        MuxConnectionHosts hosts = Hosts(LocalHost(ct => old.ConnectAsync(1, ct)));
        await ConnectAsync(hosts.Local);
        MuxWindowlessSessions source = Source(hosts);

        WindowlessScreen screen = await source.ReadScreenAsync(id, 100, Ct);

        Assert.Equal(WindowlessOutcome.Unsupported, screen.Outcome);
        Assert.Equal(new WindowlessSessionInfo(id, "local", null, "this computer", "old", 80, 24, true, null), screen.Session);
        Assert.Null(screen.Snapshot);
        ReadScreenResult status = Assert.IsType<ReadScreenResult>(screen.Status);
        Assert.Equal(
            (true, (int?)null, true, 1, "vim", "/srv", (long?)null),
            (status.Running, status.ExitCode, status.HasActiveChildProcesses, status.AttachedClients, status.Title, status.Cwd, status.LastOutputUnixMs));
        Assert.Empty(status.Snapshot);
        Assert.Equal([MuxMethods.ListSessions, MuxMethods.ReadScreen, MuxMethods.SessionInfo], old.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task A_read_returns_the_screen_and_the_status_taken_with_it()
    {
        (MuxWindowlessSessions source, _, MuxClient remote) = await ConnectedAsync();
        Guid id = await MuxTestHost.SpawnAsync(remote);
        _remoteMux.Fake(id).Emit("hello from the box");
        await _remoteMux.Mux(id).FlushAsync();

        WindowlessScreen screen = await source.ReadScreenAsync(id, 100, Ct);

        Assert.Equal(WindowlessOutcome.Ok, screen.Outcome);
        Assert.Equal((id, RemoteEndpoint, (Guid?)_profileId, "box"), (screen.Session!.SessionId, screen.Session.Endpoint, screen.Session.SshProfileId, screen.Session.HostDisplayName));
        Assert.Contains("hello from the box", ScreenText(screen.Snapshot!), StringComparison.Ordinal);
        Assert.True(screen.Status!.Running);
        Assert.NotNull(screen.Status.LastOutputUnixMs);
    }

    /// <summary>Task 11 review: a wide session's 2000 rows pass the daemon's 4 MiB cap. Fewer fit: the read asks again with half.</summary>
    [Fact]
    public async Task A_read_too_large_for_one_reply_is_asked_again_with_half_the_rows_until_one_fits()
    {
        Guid id = Guid.NewGuid();
        ScriptedDaemon daemon = ReadScreenDaemon(id, fits: rows => rows <= 250);
        MuxConnectionHosts hosts = Hosts(LocalHost(ct => daemon.ConnectAsync(2, ct)));
        await ConnectAsync(hosts.Local);

        WindowlessScreen screen = await Source(hosts).ReadScreenAsync(id, 50_000, Ct); // clamped to the daemon's most

        Assert.Equal(WindowlessOutcome.Ok, screen.Outcome);
        Assert.Contains("it fits", ScreenText(screen.Snapshot!), StringComparison.Ordinal);
        Assert.Equal([2000, 1000, 500, 250], ReadScreenRows(daemon));
    }

    [Fact]
    public async Task A_read_that_does_not_fit_even_without_scrollback_is_unreachable_and_logged()
    {
        Guid id = Guid.NewGuid();
        ScriptedDaemon daemon = ReadScreenDaemon(id, fits: _ => false);
        MuxConnectionHosts hosts = Hosts(LocalHost(ct => daemon.ConnectAsync(2, ct)));
        await ConnectAsync(hosts.Local);

        WindowlessScreen screen = await Source(hosts).ReadScreenAsync(id, 2000, Ct);

        Assert.Equal((WindowlessOutcome.Unreachable, id), (screen.Outcome, screen.Session!.SessionId));
        Assert.Null(screen.Snapshot);
        Assert.Equal([2000, 1000, 500, 250, 125, 62, 31, 15, 7, 3, 1, 0], ReadScreenRows(daemon));
        Assert.Single(_log, l => l.Contains(id.ToString(), StringComparison.Ordinal) && l.Contains(MuxErrorCodes.SnapshotTooLarge, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Input_reaches_a_running_session_and_an_exited_one_is_not_running()
    {
        (MuxWindowlessSessions source, _, MuxClient remote) = await ConnectedAsync();
        Guid running = await MuxTestHost.SpawnAsync(remote);
        Guid exited = await MuxTestHost.SpawnAsync(remote);
        await ExitAsync(_remoteMux, exited, 0);

        Assert.Equal(WindowlessOutcome.NotRunning, await source.SendInputAsync(exited, "ls\r", Ct));
        Assert.Equal(WindowlessOutcome.NotFound, await source.SendInputAsync(Guid.NewGuid(), "ls\r", Ct));
        Assert.Equal(WindowlessOutcome.Ok, await source.SendInputAsync(running, "ls\r", Ct));

        await TestWait.UntilAsync(() => _remoteMux.Fake(running).SentInput.Contains("ls\r"), "the input reached the shell");
    }

    [Fact]
    public async Task A_kill_ends_the_session_and_its_daemon_lists_it_no_more()
    {
        (MuxWindowlessSessions source, _, MuxClient remote) = await ConnectedAsync();
        Guid killed = await MuxTestHost.SpawnAsync(remote);
        Guid exited = await MuxTestHost.SpawnAsync(remote);
        Guid other = await MuxTestHost.SpawnAsync(remote);
        await ExitAsync(_remoteMux, exited, 0);

        Assert.Equal(WindowlessOutcome.Ok, await source.KillAsync(killed, Ct));
        Assert.Equal(WindowlessOutcome.NotRunning, await source.KillAsync(exited, Ct));
        Assert.Equal(WindowlessOutcome.NotFound, await source.KillAsync(killed, Ct));

        Assert.Equal(new[] { exited, other }.Order(), (await remote.ListSessionsAsync(Ct)).Select(s => s.SessionId).Order());
    }

    /// <summary>
    /// "One id on two daemons is two sessions": an agent's id names neither, so it is not offered and not acted on, even
    /// when a pane of the window shows one of them (an SSH profile can reach this computer's own daemon).
    /// </summary>
    [Fact]
    public async Task An_id_on_two_endpoints_is_ambiguous_so_it_is_neither_listed_nor_found()
    {
        (MuxWindowlessSessions source, MuxClient local, MuxClient remote) = await ConnectedAsync();
        Guid twin = Guid.NewGuid();
        var spawn = new SpawnParams { Command = "scripted", Cols = 80, Rows = 24, Title = "test" };
        Assert.Equal(twin, await local.SpawnAsync(spawn, twin, Ct));
        Assert.Equal(twin, await remote.SpawnAsync(spawn, twin, Ct));
        Guid single = await MuxTestHost.SpawnAsync(local);
        _shown.Add(("local", twin));

        Assert.Equal([single], (await source.ListAsync(Ct)).Select(s => s.SessionId));
        Assert.Equal((WindowlessOutcome.NotFound, (Guid?)null), await source.ResolveAsync(twin, Ct));
        Assert.Equal(WindowlessOutcome.NotFound, (await source.ReadScreenAsync(twin, 0, Ct)).Outcome);
        Assert.Equal(WindowlessOutcome.NotFound, await source.SendInputAsync(twin, "x", Ct));
        Assert.Equal(WindowlessOutcome.NotFound, await source.KillAsync(twin, Ct));

        Assert.Contains(twin, _localMux.Server.GetSessionIds());
        Assert.Contains(twin, _remoteMux.Server.GetSessionIds());
        Assert.Contains(_log, l => l.Contains(twin.ToString(), StringComparison.Ordinal) && l.Contains("ambiguous", StringComparison.Ordinal));
    }

    private static MuxResponse Answer<T>(T result, JsonTypeInfo<T> info) => new() { Result = MuxFrames.ToElement(result, info) };

    private static MuxResponse Refuse(string code) => new() { Error = new MuxError { Code = code, Message = $"refused ({code})" } };

    /// <summary>A daemon with one session, <paramref name="id"/>, whose screen fits in a reply only when <paramref name="fits"/> says so.</summary>
    private ScriptedDaemon ReadScreenDaemon(Guid id, Func<int, bool> fits)
    {
        var daemon = new ScriptedDaemon(request =>
        {
            switch (request.Method)
            {
                case MuxMethods.ListSessions:
                    return Answer(
                        new ListSessionsResult { Sessions = [new SessionSummary { SessionId = id, Title = "wide", Command = "sh", Cols = 80, Rows = 24, Running = true }] },
                        MuxJsonContext.Default.ListSessionsResult);
                case MuxMethods.ReadScreen:
                    int rows = MuxFrames.ParseParams(request.Params, MuxJsonContext.Default.ReadScreenParams).MaxScrollbackRows;
                    return fits(rows)
                        ? Answer(new ReadScreenResult { Snapshot = FakeMuxServerEnd.SnapshotJson("it fits"), Running = true, Title = "wide" }, MuxJsonContext.Default.ReadScreenResult)
                        : Refuse(MuxErrorCodes.SnapshotTooLarge);
                default:
                    return Refuse(MuxErrorCodes.ProtocolError);
            }
        });
        _owned.Add(daemon);
        return daemon;
    }

    private static int[] ReadScreenRows(ScriptedDaemon daemon) =>
        [.. daemon.Requests.Where(r => r.Method == MuxMethods.ReadScreen).Select(r => MuxFrames.ParseParams(r.Params, MuxJsonContext.Default.ReadScreenParams).MaxScrollbackRows)];

    /// <summary>
    /// A daemon the test scripts, over a <see cref="FakeMuxServerEnd"/>: after the hello it answers each request with what
    /// <c>answer</c> returns, in order, or - with no <c>answer</c> - reads every request and answers none.
    /// </summary>
    private sealed class ScriptedDaemon(Func<MuxRequest, MuxResponse>? answer) : IDisposable
    {
        private readonly FakeMuxServerEnd _end = FakeMuxServerEnd.Create();

        public ConcurrentQueue<MuxRequest> Requests { get; } = new();

        public async Task<MuxClient> ConnectAsync(int version, CancellationToken ct)
        {
            Task<MuxClient> connect = MuxClient.ConnectAsync(_end.ClientEnd, null, ct);
            await _end.AcceptHelloAsync(version);
            MuxClient client = await connect;
            _ = Task.Run(ServeAsync, CancellationToken.None);
            return client;
        }

        private async Task ServeAsync()
        {
            while (true)
            {
                MuxRequest request;
                try
                {
                    request = await _end.ReadRequestAsync();
                }
                catch (Exception)
                {
                    return; // the client went away, or the test ended
                }

                Requests.Enqueue(request);
                if (answer is not null) _end.Raw.Send(MuxFrames.Response(answer(request) with { Id = request.Id }));
            }
        }

        public void Dispose() => _end.Dispose();
    }
}
