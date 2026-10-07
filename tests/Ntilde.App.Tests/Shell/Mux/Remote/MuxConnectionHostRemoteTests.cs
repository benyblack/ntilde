using System.Collections.Concurrent;
using Ntilde.Mux;
using Ntilde.Mux.Cli;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Platform.Ssh.Native;
using Ntilde.Platform.Ssh.Sessions;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// A remote <see cref="MuxConnectionHost"/> (Phase 4 spec §7.2, §7.3) as <see cref="RemoteMuxHostFactory"/>
/// builds it, over a <see cref="FakeRemoteHost"/> and a clock the test advances: it notices a dead link,
/// tells a stopped daemon from a lost link, reconnects with backoff, and delivers the kills queued while it
/// was down. A local host does none of this.
/// </summary>
public sealed class MuxConnectionHostRemoteTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The loop's first wait at its longest: one second, jittered by 20%.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1.2);

    private readonly FakeRemoteHost _remote = new();
    private readonly FakeMuxTimerScheduler _clock = new();
    private readonly SshProfile _profile = RemoteMuxConnectorTests.Profile();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<IDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _remote.Dispose();
        if (Directory.Exists(_askPassRecords)) Directory.Delete(_askPassRecords, recursive: true);
    }

    /// <summary>Where this test's askpass helper - played by the test - records what it did for each attempt's ssh.</summary>
    private readonly string _askPassRecords = Path.Combine(Path.GetTempPath(), "ntilde-askpass-tests", Guid.NewGuid().ToString("N"));

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private MuxConnectionHost Create(
        Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport>? transportFor = null,
        ISshInteractionHandler? user = null,
        Func<SshProfile, string?>? savedPassword = null,
        Func<SshInteractionRequest, bool>? isTrustedHostKey = null) =>
        Own(RemoteMuxHostFactory.Create(
                MuxEndpointId.ForSsh(_profile.Id), _ => _profile, transportFor ?? ((_, _) => _remote), _log.Enqueue, user, _clock,
                isTrustedHostKey: isTrustedHostKey, savedPassword: savedPassword, askPassRecords: new Ntilde.SshAskPassSessionMarkers(() => _askPassRecords))
            ?? throw new InvalidOperationException("the factory declined"));

    private bool Logged(string text) => _log.Any(l => l.Contains(text, StringComparison.Ordinal));

    /// <summary>
    /// Waits for the real clock (it ticks every 10-16 ms) to move past the client's last inbound frame, so
    /// that the liveness slice the test starts next begins after that frame and does not count it.
    /// </summary>
    private static Task ClockPastLastFrameAsync(MuxClient client)
    {
        long last = client.LastReceivedTicks;
        return TestWait.UntilAsync(() => Environment.TickCount64 > last, "the clock moved past the last frame", Patient);
    }

    /// <summary>
    /// A host over a fake daemon that answers only the hello: the test sends every other frame itself.
    /// <paramref name="ping"/> replaces the liveness ping, so the test decides when and how each one ends.
    /// </summary>
    private async Task<(MuxConnectionHost Host, MuxClient Client)> OverFakeDaemonAsync(FakeMuxServerEnd daemon, Func<MuxClient, CancellationToken, Task>? ping = null)
    {
        Task hello = daemon.AcceptHelloAsync();
        MuxConnectionHost host = Own(new MuxConnectionHost(ct => MuxClient.ConnectAsync(daemon.ClientEnd, null, ct), "remote", _log.Enqueue, MuxHostPolicy.Remote("box"))
        {
            Scheduler = _clock,
            Ping = ping ?? ((client, ct) => client.PingAsync(ct)),
        });
        MuxClient client = host.GetClient(Patient)!;
        await hello.WaitAsync(Patient, Ct);
        return (host, client);
    }

    /// <summary>Output for a session nobody opened: the client drops it, but it is a frame that arrived. Returns once it has.</summary>
    private static async Task SendOutputAsync(FakeMuxServerEnd daemon, MuxClient client, long seq)
    {
        MuxOutboundFrame frame = MuxFrames.Output(Guid.Empty, seq, "x"u8);
        byte[] bytes = frame.Bytes.ToArray();
        frame.Release();
        await SendBytesAsync(daemon, client, bytes);
    }

    /// <summary>Raw bytes from the daemon - a whole frame, or part of one. Returns once the client has read them.</summary>
    private static async Task SendBytesAsync(FakeMuxServerEnd daemon, MuxClient client, byte[] bytes)
    {
        await ClockPastLastFrameAsync(client);
        long before = Environment.TickCount64; // later than every earlier stamp: only these bytes can reach it
        daemon.Raw.WriteRaw(bytes);
        await TestWait.UntilAsync(() => client.LastReceivedTicks >= before, "the bytes arrived", Patient);
    }

    /// <summary>A host over the in-memory daemon whose disconnect classification the test holds open, then decides.</summary>
    private MuxConnectionHost WithHeldClassification(MuxTestHost mux, TaskCompletionSource classifying, Task<MuxDisconnectKind> verdict) =>
        Own(new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "remote", _log.Enqueue, MuxHostPolicy.Remote("box"))
        {
            Scheduler = _clock,
            ClassifyDisconnect = _ =>
            {
                classifying.TrySetResult();
                return verdict;
            },
            ClassifyTimeout = Patient,
        });

    /// <summary>
    /// Lets the reconnect loop's attempts run out one by one: waits for its next wait to be scheduled, then
    /// skips the clock to it. Attempts run on the pool, so each is awaited before the next skip.
    /// </summary>
    private async Task RetryUntilAsync(Func<bool> done, string because, int maxAttempts = 100)
    {
        for (int i = 0; i < maxAttempts; i++)
        {
            await TestWait.UntilAsync(() => done() || _clock.PendingCount == 1, "the loop scheduled its next attempt", Patient);
            if (done()) return;
            _clock.Advance(_clock.NextDueIn!.Value);
        }

        Assert.Fail($"Not done after {maxAttempts} attempts: {because}.");
    }

    [Fact]
    public async Task A_dropped_link_raises_ConnectionLost_then_Reconnected()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient first = host.GetClient(Patient)!;
        Guid id = await MuxTestHost.SpawnAsync(first);

        _remote.CutLink();
        await events.WaitForAsync("lost");

        Assert.True(host.IsReconnecting);
        Assert.Equal(1, _remote.StartCount);   // the loop waits first

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");

        MuxClient second = Assert.IsType<MuxClient>(events.LastReconnected);
        Assert.NotSame(first, second);
        Assert.True(second.IsConnected);
        Assert.Same(second, host.CurrentClient);
        Assert.False(host.IsReconnecting);
        Assert.Equal(2, _remote.StartCount);
        Assert.Equal(new[] { "lost:disconnected", "reconnected" }, events.Seen);
        Assert.Contains(id, (await second.ListSessionsAsync(Ct)).Select(s => s.SessionId));   // the daemon kept it
    }

    [Fact]
    public async Task Ping_timeout_disconnects_a_silent_link()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient client = host.GetClient(Patient)!;
        _remote.StallLink();   // nothing gets through any more, and no EOF says so
        await ClockPastLastFrameAsync(client);   // the welcome is older than the ping's slice

        _clock.Advance(TimeSpan.FromSeconds(15));   // the liveness tick: a ping goes out

        Assert.Equal(2, _clock.PendingCount);   // the next tick, and the ping's timeout
        Assert.True(client.IsConnected);
        Assert.Empty(events.Seen);

        _clock.Advance(TimeSpan.FromSeconds(10));   // no answer within the timeout
        await events.WaitForAsync("lost");

        Assert.False(client.IsConnected);
        Assert.Equal(new[] { "lost:ping timeout" }, events.Seen);
        Assert.True(host.IsReconnecting);

        // A new channel is a new link.
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");
        Assert.True(host.CurrentClient!.IsConnected);
    }

    [Fact]
    public async Task A_link_that_answers_its_pings_stays_up()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient client = host.GetClient(Patient)!;

        for (int tick = 0; tick < 4; tick++)
        {
            _clock.Advance(TimeSpan.FromSeconds(15));
            await TestWait.UntilAsync(() => _clock.PendingCount == 1, "the ping was answered and its timeout cancelled", Patient);
        }

        _clock.Advance(TimeSpan.FromSeconds(10));   // past where the last ping's timeout would have been
        Assert.True(client.IsConnected);
        Assert.Same(client, host.CurrentClient);
        Assert.Empty(events.Seen);
        Assert.Equal(1, _remote.StartCount);
    }

    [Fact]
    public async Task A_tick_while_the_previous_ping_is_unanswered_sends_no_second_ping()
    {
        using var daemon = FakeMuxServerEnd.Create();
        Task hello = daemon.AcceptHelloAsync();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(daemon.ClientEnd, null, ct), "remote", null, MuxHostPolicy.Remote("box"))
        {
            Scheduler = _clock,
            LivenessTimeout = TimeSpan.FromSeconds(40),   // longer than the interval, so a tick finds the ping still out
        };
        MuxClient client = host.GetClient(Patient)!;
        await hello.WaitAsync(Patient, Ct);

        _clock.Advance(TimeSpan.FromSeconds(15));
        MuxRequest ping = await daemon.ReadRequestAsync().WaitAsync(Patient, Ct);
        Assert.Equal(MuxMethods.Ping, ping.Method);
        Assert.Equal(2, _clock.PendingCount);   // the next tick, and the ping's timeout

        _clock.Advance(TimeSpan.FromSeconds(15));   // a tick while it is unanswered
        Assert.Equal(2, _clock.PendingCount);   // skipped: no second ping, so no second timeout

        daemon.Reply(ping.Id, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);
        await TestWait.UntilAsync(() => _clock.PendingCount == 1, "the answer cancelled the timeout", Patient);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(MuxMethods.Ping, (await daemon.ReadRequestAsync().WaitAsync(Patient, Ct)).Method);
        Assert.True(client.IsConnected);
    }

    /// <summary>
    /// Controller ruling on spec §7.2: a ping queued behind a large snapshot on a slow link is late, not lost.
    /// While frames keep arriving the link is alive however long the answer takes; only a whole timeout with
    /// nothing arriving at all drops it.
    /// </summary>
    [Fact]
    public async Task A_ping_held_up_behind_a_busy_link_does_not_drop_it()
    {
        using var daemon = FakeMuxServerEnd.Create();
        (MuxConnectionHost host, MuxClient client) = await OverFakeDaemonAsync(daemon);
        var events = new HostEvents(host);
        await ClockPastLastFrameAsync(client);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(MuxMethods.Ping, (await daemon.ReadRequestAsync().WaitAsync(Patient, Ct)).Method);   // its answer is held back

        for (int slice = 1; slice <= 3; slice++)   // 30 s without the answer, while output keeps arriving
        {
            await SendOutputAsync(daemon, client, slice);
            await ClockPastLastFrameAsync(client);
            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(2, _clock.PendingCount);   // the next tick, and the ping's next slice: not dropped
        }

        Assert.True(client.IsConnected);
        Assert.Empty(events.Seen);

        _clock.Advance(TimeSpan.FromSeconds(10));   // a whole timeout with nothing arriving
        await events.WaitForAsync("lost");

        Assert.Equal(new[] { "lost:ping timeout" }, events.Seen);
        Assert.False(client.IsConnected);
    }

    /// <summary>
    /// Review fix: a snapshot is one frame of up to tens of MiB, and over a slow link it can take longer than
    /// the timeout to arrive. Every read that brings bytes counts, so the link stays up while it trickles in.
    /// </summary>
    [Fact]
    public async Task A_single_large_frame_arriving_slowly_keeps_the_link_alive()
    {
        using var daemon = FakeMuxServerEnd.Create();
        (MuxConnectionHost host, MuxClient client) = await OverFakeDaemonAsync(daemon);
        var events = new HostEvents(host);
        await ClockPastLastFrameAsync(client);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(MuxMethods.Ping, (await daemon.ReadRequestAsync().WaitAsync(Patient, Ct)).Method);   // its answer would come after the frame
        MuxOutboundFrame snapshotSized = MuxFrames.Output(Guid.Empty, 1, new byte[4096]);
        byte[] frame = snapshotSized.Bytes.ToArray();
        snapshotSized.Release();
        int quarter = frame.Length / 4;

        for (int slice = 0; slice < 3; slice++)   // 30 s for three quarters of one frame
        {
            await SendBytesAsync(daemon, client, frame.AsSpan(slice * quarter, quarter).ToArray());
            await ClockPastLastFrameAsync(client);
            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(2, _clock.PendingCount);   // the next tick, and the ping's next slice: not dropped
        }

        Assert.True(client.IsConnected);
        Assert.Empty(events.Seen);

        _clock.Advance(TimeSpan.FromSeconds(10));   // the rest never comes
        await events.WaitForAsync("lost");

        Assert.Equal(new[] { "lost:ping timeout" }, events.Seen);
    }

    /// <summary>
    /// The ping's own request gives up (the client's request timeout) while frames keep arriving: busy, not
    /// dead, and the next tick pings again. The same failure on a silent link drops it.
    /// </summary>
    [Fact]
    public async Task A_failed_ping_on_a_busy_link_is_sent_again_and_on_a_silent_link_drops_it()
    {
        using var daemon = FakeMuxServerEnd.Create();
        var pings = new ConcurrentQueue<TaskCompletionSource>();
        (MuxConnectionHost host, MuxClient client) = await OverFakeDaemonAsync(daemon, (_, _) =>
        {
            var ping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pings.Enqueue(ping);
            return ping.Task;
        });
        var events = new HostEvents(host);
        await ClockPastLastFrameAsync(client);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await TestWait.UntilAsync(() => pings.Count == 1, "the first ping went out", Patient);

        await SendOutputAsync(daemon, client, 1);   // the link is busy...
        pings.ElementAt(0).SetException(new TimeoutException("The request timed out."));   // ...when the ping's request gives up
        await TestWait.UntilAsync(() => _clock.PendingCount == 1, "the failed ping was let go, its timeout cancelled", Patient);

        Assert.True(client.IsConnected);
        Assert.Empty(events.Seen);

        await ClockPastLastFrameAsync(client);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await TestWait.UntilAsync(() => pings.Count == 2, "the next tick pinged again", Patient);
        pings.ElementAt(1).SetException(new TimeoutException("The request timed out."));   // and nothing arrived meanwhile
        await events.WaitForAsync("lost");

        Assert.Equal(new[] { "lost:ping failed" }, events.Seen);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Daemon_exit_code_3_raises_DaemonStopped_without_a_loop()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));

        _remote.StopDaemon();   // every proxy exits 3
        await events.WaitForAsync("daemon-stopped");

        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);   // no loop, and no liveness tick for a dead client
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(new[] { "daemon-stopped" }, events.Seen);

        // Enter: the proxy brings up a new daemon.
        MuxClient? again = host.GetClient(Patient);
        Assert.True(again?.IsConnected);
    }

    /// <summary>
    /// Codex D1: a daemon that drops this host's connection but runs on - here another connection with the host's
    /// client instance id replaces it; a client too slow to keep up goes the same way - makes the proxy exit 4,
    /// not 3. That is a lost link: the host reconnects, the daemon's sessions are still there, and a kill queued
    /// meanwhile is kept and delivered, not dropped as a stopped daemon's would be.
    /// </summary>
    [Fact]
    public async Task A_connection_the_daemon_drops_while_it_runs_on_is_a_lost_link()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient first = host.GetClient(Patient)!;
        Guid kept = await MuxTestHost.SpawnAsync(first);
        Guid closed = await MuxTestHost.SpawnAsync(first);
        RemoteMuxConnector connector = Assert.IsType<RemoteMuxConnector>(host.Connector);

        (Stream stream, _) = await _remote.ConnectDaemonAsync(Ct);
        using MuxClient twin = await MuxClient.ConnectAsync(stream, new MuxClientOptions { ClientInstanceId = connector.ClientInstanceId }, Ct);
        await events.WaitForAsync("lost");
        host.KillWhenConnected(closed);

        Assert.Equal(MuxProxyExitCodes.ConnectionClosed, await connector.ExitAsync(first, Patient));
        Assert.Equal(MuxDisconnectKind.LinkLost, await RemoteMuxHostFactory.ClassifyDisconnectAsync(connector, first));
        Assert.True(host.IsReconnecting);
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kept kill reached the daemon", Patient);
        Assert.Contains(kept, _remote.Server.GetSessionIds());
        Assert.DoesNotContain("daemon-stopped", events.Seen);
        Assert.False(Logged("dropping"));
    }

    /// <summary>
    /// Codex D2: a host's SSH target is pinned at its first successful connect. The user moves the profile to
    /// another host while its tabs live on the first one: the loop's reconnect, and the kill it delivers, still
    /// go where the shells are. The change is logged once.
    /// </summary>
    [Fact]
    public async Task An_edited_profile_does_not_move_a_connected_host_to_its_new_target()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b");
        SshProfile current = _profile;
        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(_profile.Id),
            _ => current,
            (profile, _) => profile.Host == "host-b" ? otherHost : _remote,
            _log.Enqueue,
            userPrompts: null,
            _clock)!);
        var events = new HostEvents(host);
        Guid closed = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);

        current = RemoteMuxConnectorTests.Edited(_profile, host: "host-b");
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(closed);
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");

        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kill reached the daemon the shell runs on", Patient);
        Assert.Equal(2, _remote.StartCount);
        Assert.Equal(0, otherHost.StartCount);
        Assert.Single(_log, line => line.Contains("the profile's host, port, user or jump hosts changed; this connection keeps nova@fake-host:22", StringComparison.Ordinal));
    }

    /// <summary>
    /// Codex D2, residual R5: the target is pinned when the host takes a client, not when an attempt's hello is done.
    /// An automatic attempt finishes its hello on the old target just as a user's request supersedes it, after the
    /// user fixed the profile: the host throws that client away, so its target must not stick - the user's attempt,
    /// and the reconnects after it, go where the profile says.
    /// </summary>
    [Fact]
    public async Task A_superseded_automatic_attempt_that_got_in_does_not_pin_its_target()
    {
        using var otherHost = new FakeRemoteHost("nova@host-b");
        using var held = new ManualResetEventSlim();
        var automaticGotIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SshProfile current = _profile;
        void Log(string line)
        {
            _log.Enqueue(line);
            if (line.Contains(": connected (", StringComparison.Ordinal) && line.EndsWith("automatic)", StringComparison.Ordinal))
            {
                automaticGotIn.TrySetResult();   // its hello is done: held here, before the host sees its client
                held.Wait(Patient);
            }
        }

        MuxConnectionHost host = Own(RemoteMuxHostFactory.Create(
            MuxEndpointId.ForSsh(_profile.Id),
            _ => current,
            (profile, _) => profile.Host == "host-b" ? otherHost : _remote,
            Log,
            userPrompts: null,
            _clock)!);
        Task<MuxClient> automatic = host.TryStartAutomaticAttempt()!;
        await automaticGotIn.Task.WaitAsync(Patient, Ct);

        current = RemoteMuxConnectorTests.Edited(_profile, host: "host-b");
        MuxClient? user = await Task.Run(() => host.GetClient(Patient), Ct);   // Enter: supersedes the automatic attempt
        held.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automatic.WaitAsync(Patient, Ct));

        Assert.True(user?.IsConnected);
        Task<string?> lost = WhenDisconnectedAsync(user!);
        otherHost.CutLink();
        await lost;
        Assert.NotNull(await Task.Run(() => host.GetClient(Patient), Ct));
        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(2, otherHost.StartCount);
    }

    private static Task<string?> WhenDisconnectedAsync(MuxClient client)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => tcs.TrySetResult(reason);
        if (!client.IsConnected) tcs.TrySetResult(client.DisconnectReason);
        return tcs.Task.WaitAsync(Patient, Ct);
    }

    /// <summary>Review Focus 1: a remote tab closed while its link is down still ends its shell.</summary>
    [Fact]
    public async Task Kill_requested_while_disconnected_is_sent_after_reconnect()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient first = host.GetClient(Patient)!;
        Guid closed = await MuxTestHost.SpawnAsync(first);
        Guid kept = await MuxTestHost.SpawnAsync(first);
        _remote.CutLink();
        await events.WaitForAsync("lost");

        host.KillWhenConnected(closed);   // the user closes that tab now
        bool? sentBeforeReconnected = null;
        host.Reconnected += _ => sentBeforeReconnected = host.PendingKillCountForTest > 0 || !_remote.Server.GetSessionIds().Contains(closed);

        Assert.Contains(closed, _remote.Server.GetSessionIds());
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");
        await host.EventsForTest.WaitAsync(Patient, Ct);
        Assert.True(sentBeforeReconnected);   // panes reattaching on Reconnected go after the kill
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the queued kill reached the daemon", Patient);
        Assert.Contains(kept, _remote.Server.GetSessionIds());
    }

    [Fact]
    public async Task A_kill_on_a_connected_host_is_sent_at_once()
    {
        MuxConnectionHost host = Create();
        Guid id = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);

        host.KillWhenConnected(id);

        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(id), "the kill reached the daemon", Patient);
    }

    /// <summary>A shell already running on the remote daemon, started without an exec channel of this host's.</summary>
    private async Task<Guid> SpawnWithoutAnExecAsync()
    {
        (Stream stream, _) = await _remote.ConnectDaemonAsync(Ct);
        using MuxClient client = await MuxClient.ConnectAsync(stream, null, Ct);
        return await MuxTestHost.SpawnAsync(client);
    }

    /// <summary>
    /// Task 21 review (Review Focus 1): a restored tab closed before it was shown is the only user of its host,
    /// which has never connected. Its kill must not wait for a connect nobody will start: the host starts one
    /// automatic attempt - no prompt, for a tab the user already closed - and the kill goes out with it.
    /// </summary>
    [Fact]
    public async Task A_kill_on_an_idle_remote_host_starts_one_automatic_attempt_to_deliver_it()
    {
        var attempts = new ConcurrentQueue<bool>();
        MuxConnectionHost host = Create((_, request) =>
        {
            attempts.Enqueue(request.Interactive);
            return _remote;
        });
        Guid closed = await SpawnWithoutAnExecAsync();
        Guid kept = await SpawnWithoutAnExecAsync();
        Assert.Equal(0, _remote.StartCount);

        host.KillWhenConnected(closed); // on the caller's thread: it only starts the attempt

        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kill reached the daemon", Patient);
        Assert.Equal(new[] { false }, attempts);
        Assert.Equal(1, _remote.StartCount);
        Assert.Contains(kept, _remote.Server.GetSessionIds());
        Assert.NotNull(host.CurrentClient);
    }

    /// <summary>The automatic attempt could not get in (a password nobody is there to type): the kill waits for any later connect.</summary>
    [Fact]
    public async Task A_kill_whose_automatic_attempt_fails_stays_queued_for_the_next_connect()
    {
        MuxConnectionHost host = Create();
        Guid closed = await SpawnWithoutAnExecAsync();
        _remote.Script = FakeRemoteScript.ConnectionRefused;

        host.KillWhenConnected(closed);

        await TestWait.UntilAsync(() => host.LastFailure is not null, "the automatic attempt failed", Patient);
        Assert.Equal(1, _remote.StartCount);
        Assert.Equal(0, _clock.PendingCount); // one attempt, not a loop
        Assert.Contains(closed, _remote.Server.GetSessionIds());
        Assert.False(Logged("dropping"));

        _remote.Script = null;
        Assert.NotNull(host.GetClient(Patient)); // a pane's connect, say

        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the queued kill reached the daemon", Patient);
    }

    /// <summary>A kill while the loop runs leaves the connecting to the loop: no attempt of its own.</summary>
    [Fact]
    public async Task A_kill_while_reconnecting_starts_no_attempt_of_its_own()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Guid closed = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        _remote.CutLink();
        await events.WaitForAsync("lost");

        host.KillWhenConnected(closed);

        Assert.True(Logged($"the kill of session {closed} is sent once connected"));
        Assert.False(Logged("connecting to send the kill")); // decided, and logged, before KillWhenConnected returns
        Assert.Equal(1, _remote.StartCount);
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kill went out with the loop's connect", Patient);
        Assert.Equal(2, _remote.StartCount);
    }

    /// <summary>
    /// Controller ruling: a stopped daemon's sessions ended with it. The kills queued for them are dropped when the
    /// host learns it stopped - delivering them would only start a new daemon - and a later connect sends nothing.
    /// </summary>
    [Fact]
    public async Task Daemon_stopped_drops_the_kills_queued_for_its_sessions()
    {
        MuxTestHost mux = Own(new MuxTestHost());
        var classifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<MuxDisconnectKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool reachable = true;
        MuxConnectionHost host = Own(new MuxConnectionHost(
            ct => Volatile.Read(ref reachable) ? MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct) : throw new IOException("unreachable"),
            "remote", _log.Enqueue, MuxHostPolicy.Remote("box"))
        {
            Scheduler = _clock,
            ClassifyDisconnect = _ =>
            {
                classifying.TrySetResult();
                return verdict.Task;
            },
            ClassifyTimeout = Patient,
        });
        var events = new HostEvents(host);
        MuxClient client = host.GetClient(Patient)!;
        Guid closed = await MuxTestHost.SpawnAsync(client);
        Volatile.Write(ref reachable, false);
        client.Dispose(); // the connection ends; why is not known yet
        await classifying.Task.WaitAsync(Patient, Ct);
        host.KillWhenConnected(closed); // queued; the idle host's own attempt cannot get through
        await TestWait.UntilAsync(() => host.LastFailure is not null, "the host's own attempt failed", Patient);

        verdict.SetResult(MuxDisconnectKind.DaemonStopped);
        await events.WaitForAsync("daemon-stopped");
        await host.ClassificationForTest.WaitAsync(Patient, Ct);

        Assert.True(Logged("dropping 1 queued kills: the daemon stopped"));
        // This daemon did not really stop, so a kept kill would land on the next connect: none does.
        Volatile.Write(ref reachable, true);
        MuxClient again = host.GetClient(Patient)!;
        await again.PingAsync(Ct); // anything sent on connect has been handled by now
        Assert.Contains(closed, mux.Server.GetSessionIds());
    }

    /// <summary>Controller ruling: after DaemonStopped, until something connects, a kill is not recorded and starts no connect.</summary>
    [Fact]
    public async Task A_kill_after_the_daemon_stopped_records_nothing_and_starts_nothing()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Guid gone = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        _remote.StopDaemon();
        await events.WaitForAsync("daemon-stopped");

        host.KillWhenConnected(gone); // the tab of a shell that ended with its daemon is closed

        Assert.True(Logged($"not killing session {gone}: the daemon stopped"));
        Assert.False(Logged("connecting to send the kill"));
        Assert.Equal(1, _remote.StartCount);

        // A connect (a pane's Enter) brings up a new daemon, and from then on kills count again.
        MuxClient again = host.GetClient(Patient)!;
        Assert.Equal(2, _remote.StartCount);
        Guid fresh = await MuxTestHost.SpawnAsync(again);
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(fresh);
        Assert.True(Logged($"the kill of session {fresh} is sent once connected"));
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(fresh), "the kill went out on the new daemon", Patient);
    }

    /// <summary>Controller ruling: the user meant to end that shell, so its kill outlives the loop giving up.</summary>
    [Fact]
    public async Task A_kill_queued_while_down_outlives_a_give_up_and_goes_out_on_the_next_connect()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        MuxClient first = host.GetClient(Patient)!;
        Guid closed = await MuxTestHost.SpawnAsync(first);
        Guid kept = await MuxTestHost.SpawnAsync(first);
        _remote.Script = FakeRemoteScript.ConnectionRefused;   // and it stays unreachable
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(closed);

        await RetryUntilAsync(() => events.Has("abandoned"), "the loop gave up");

        Assert.Equal(MuxReconnectLoop.Budget, TimeSpan.FromMilliseconds(_clock.NowMs));
        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        Assert.False(Logged("dropping"));
        Assert.Contains(closed, _remote.Server.GetSessionIds());

        // Enter, once the host is back: the connection returns, and the kill goes out with it.
        _remote.Script = null;
        Assert.NotNull(host.GetClient(Patient));
        await events.WaitForAsync("reconnected");
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kept kill reached the daemon", Patient);
        Assert.Contains(kept, _remote.Server.GetSessionIds());
        Assert.Equal(new[] { "lost:disconnected", "abandoned", "reconnected" }, events.Seen);
    }

    [Fact]
    public async Task GetClient_while_reconnecting_attempts_at_once_and_resets_the_backoff()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.Script = FakeRemoteScript.ConnectionRefused;
        _remote.CutLink();
        await events.WaitForAsync("lost");
        await RetryUntilAsync(() => _remote.StartCount == 5, "four retries failed");
        await TestWait.UntilAsync(() => _clock.PendingCount == 1, "the loop waits again", Patient);
        Assert.True(_clock.NextDueIn > TimeSpan.FromSeconds(12));   // backed off to about 16 s

        Assert.Null(host.GetClient(Patient));   // Enter, while the host still refuses

        Assert.Equal(6, _remote.StartCount);   // tried at once
        await TestWait.UntilAsync(() => _clock.PendingCount == 1 && _clock.NextDueIn <= FirstRetry, "the backoff restarted", Patient);
        Assert.True(host.IsReconnecting);
        _remote.Script = null;
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");
        Assert.Equal(7, _remote.StartCount);
    }

    /// <summary>
    /// Ruling: an automatic attempt that would need the user - a password nobody may ask for - ends the
    /// episode at once instead of making a pre-auth connection every 30 s for ten minutes. Enter, an
    /// interactive attempt, is the way back.
    /// </summary>
    [Fact]
    public async Task An_automatic_attempt_that_needs_a_password_stops_the_loop_and_raises_ReconnectAbandoned()
    {
        var user = new ScriptedUser();
        MuxConnectionHost host = Create(
            (_, request) =>
            {
                _remote.OnStart = _ =>
                {
                    _remote.Script = null;
                    if (request.Interactive) return;   // the user's key, say: nothing to ask
                    try
                    {
                        request.Prompts.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (RemoteMuxPromptAbortedException ex)
                    {
                        // As the native channel does: the session closes without answering.
                        _remote.Script = FakeRemoteScript.NativeFailure($"the native session failed: {ex.Message}");
                    }
                };
                return _remote;
            },
            user);
        var events = new HostEvents(host);
        Guid closed = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(closed);

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("abandoned");

        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(2, _remote.StartCount);   // one automatic attempt, not one every 30 s
        Assert.Empty(user.Asked);
        Assert.False(Logged("dropping"));
        await TestWait.UntilAsync(() => host.LastFailure is not null, "the failure was recorded", Patient);
        Assert.Equal(RemoteFailureKind.NeedsUser, Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure.Kind);

        Assert.NotNull(host.GetClient(Patient));   // Enter
        await events.WaitForAsync("reconnected");
        Assert.Equal(new[] { "lost:disconnected", "abandoned", "reconnected" }, events.Seen);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the kill kept through the give-up reached the daemon", Patient);
    }

    /// <summary>
    /// Codex D3: an automatic attempt that cancels an encrypted key's passphrase prompt, with nothing remembered, and
    /// then fails SSH needed the user as much as one with a password to give: the loop stops at once
    /// (<see cref="MuxConnectionHost.ReconnectAbandoned"/>), with nothing scheduled, instead of retrying for ten minutes.
    /// </summary>
    [Fact]
    public async Task An_automatic_attempt_that_cancels_an_unremembered_passphrase_and_fails_stops_the_loop()
    {
        var user = new ScriptedUser();
        var answers = new ConcurrentQueue<SshInteractionResponse>();
        MuxConnectionHost host = Create(
            (_, request) =>
            {
                _remote.OnStart = _ =>
                {
                    _remote.Script = null;
                    if (request.Interactive) return;   // the user's request got in
                    answers.Enqueue(request.Prompts.HandleAsync(RemoteMuxConnectorTests.PassphrasePrompt, CancellationToken.None).GetAwaiter().GetResult());
                    // Cancelled: the key was not offered, and nothing else got in.
                    _remote.Script = FakeRemoteScript.NativeFailure("Authentication failed: no authentication method succeeded");
                };
                return _remote;
            },
            user);
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("abandoned");

        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(2, _remote.StartCount);   // one automatic attempt
        Assert.True(Assert.Single(answers).IsCanceled);
        Assert.Empty(user.Asked);
        await TestWait.UntilAsync(() => host.LastFailure is not null, "the failure was recorded", Patient);
        Assert.Equal(RemoteFailureKind.NeedsUser, Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure.Kind);
    }

    /// <summary>
    /// The native auth shape against a server whose password is <paramref name="serverPassword"/>: a user's request gets in
    /// (its key, say); an automatic attempt is asked for a password, and gets in only with the right one. What its prompts
    /// answered is recorded - "&lt;aborted&gt;" for an attempt that ended the session at the prompt instead.
    /// </summary>
    private Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> PasswordServer(string serverPassword, ConcurrentQueue<string> answered) => (_, request) =>
    {
        _remote.OnStart = _ =>
        {
            _remote.Script = null;
            if (request.Interactive) return;
            try
            {
                string answer = request.Prompts.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult().Secret;
                answered.Enqueue(answer);
                if (answer != serverPassword) _remote.Script = FakeRemoteScript.NativeFailure("Authentication failed: no authentication method succeeded");
            }
            catch (RemoteMuxPromptAbortedException ex)
            {
                // As the native channel does: the session closes without answering.
                answered.Enqueue("<aborted>");
                _remote.Script = FakeRemoteScript.NativeFailure($"the native session failed: {ex.Message}");
            }
        };
        return _remote;
    };

    /// <summary>
    /// The user's choice: a wrong saved password is tried once, and the loop stops (NeedsUser) - at once, nothing scheduled,
    /// not ten minutes of failed logins - saying the saved password was refused. A kill queued meanwhile is delivered by
    /// an automatic attempt of its own on the idle host, which does not send the refused password again.
    /// </summary>
    [Fact]
    public async Task A_refused_saved_password_stops_the_loop_at_once_and_is_not_sent_again()
    {
        var answered = new ConcurrentQueue<string>();
        MuxConnectionHost host = Create(PasswordServer("right", answered), new ScriptedUser(), savedPassword: _ => "wrong");
        var events = new HostEvents(host);
        Guid closed = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("abandoned");

        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (failure.Kind, failure.Cause));
        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(2, _remote.StartCount);   // one automatic attempt

        host.KillWhenConnected(closed);         // the idle host tries once to deliver it
        await TestWait.UntilAsync(() => answered.Count == 2, "the kill's automatic attempt reached the prompt", Patient);

        Assert.Equal(new[] { "wrong", "<aborted>" }, answered);
        Assert.Contains(closed, _remote.Server.GetSessionIds());
    }

    /// <summary>A saved password the server takes: the loop's first attempt reconnects, with no NeedsUser and no give-up.</summary>
    [Fact]
    public async Task A_good_saved_password_reconnects_with_no_NeedsUser()
    {
        var answered = new ConcurrentQueue<string>();
        MuxConnectionHost host = Create(PasswordServer("right", answered), new ScriptedUser(), savedPassword: _ => "right");
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");

        Assert.Equal(new[] { "lost:disconnected", "reconnected" }, events.Seen);
        Assert.Equal(new[] { "right" }, answered);
        Assert.Null(host.LastFailure);
        Assert.False(host.IsReconnecting);
    }

    /// <summary>
    /// The user's live smoke test (native, no jump hops, the server's password changed so the saved value is wrong): the
    /// user had signed in with Enter, the window's handler filling the saved password, so the host remembered it. The link
    /// dropped; the loop's attempt answered rusty_ssh's password prompt from that memory - the saved value - and rusty_ssh
    /// failed authentication (an Error event, then Closed, as the real native layer reports it) - or sshd, at MaxAuthTries
    /// after the agent's keys, cut the connection, which reaches it as russh's bare "Disconnected". That attempt must stop
    /// the loop at once, as the saved password refused: no second attempt scheduled, and the pane's line is the refused one.
    /// </summary>
    [Theory]
    [InlineData("SSH authentication failed")]
    [InlineData("Disconnected")]
    public async Task A_remembered_saved_password_refused_on_native_stops_the_loop_at_once_as_the_saved_password_refused(string nativeError)
    {
        _profile.BackendKind = SshBackendKind.Native;
        var interop = new PromptingNativeSshInterop(
            PromptingNativeSshInterop.PasswordPrompt,
            PromptingNativeSshInterop.Error(nativeError),
            NativeSshEvent.Closed());
        int nativeAttempts = 0;
        MuxConnectionHost host = Create(
            (profile, request) =>
            {
                if (!request.Interactive)
                {
                    Interlocked.Increment(ref nativeAttempts);
                    return RemoteMuxHostFactory.CreateTransport(
                        profile,
                        request,
                        (_, _) => throw new InvalidOperationException("a native profile never plans an ssh command line"),
                        () => interop,
                        static () => true,
                        askPassHelperPath: null,
                        log: _ => { },
                        openSshVersions: RemoteMuxConnectorTests.ModernSsh);
                }

                // The user's Enter: the window's handler fills the saved password into the prompt, and it gets in.
                _remote.OnStart = _ => request.Prompts.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
                return _remote;
            },
            new ScriptedUser(SshInteractionResponse.FromSecret("stale")),
            savedPassword: _ => "stale");
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => events.Has("abandoned") || _clock.PendingCount == 1, "the loop's attempt ended", Patient);

        Assert.True(events.Has("abandoned"), "the refused saved password must stop the loop at once");
        Assert.Equal(0, _clock.PendingCount);
        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (failure.Kind, failure.Cause));
        Assert.Equal(1, Volatile.Read(ref nativeAttempts));
        Assert.Equal(new[] { """{"text":"stale"}""" }, interop.Submissions.Select(s => s.PayloadJson));
    }

    /// <summary>
    /// The OpenSSH twin of the smoke test: the loop's attempt runs the vault-only askpass, the helper fills the saved
    /// password (its record written under the transport's token), and sshd refuses it. The loop stops at once, as the saved
    /// password refused.
    /// </summary>
    [Fact]
    public async Task A_refused_saved_password_on_OpenSSH_stops_the_loop_at_once_as_the_saved_password_refused()
    {
        var records = new Ntilde.SshAskPassSessionMarkers(() => _askPassRecords);
        int automaticAttempts = 0;
        MuxConnectionHost host = Create(
            (profile, request) =>
            {
                _remote.OnStart = null;
                _remote.Script = null;
                if (request.Interactive) return _remote;
                Interlocked.Increment(ref automaticAttempts);
                var transport = (OpenSshExecTransport)RemoteMuxHostFactory.CreateTransport(
                    profile,
                    request,
                    (_, _) => new Ntilde.Services.Ssh.SshLaunchDetails
                    {
                        SshPath = "/usr/bin/ssh",
                        ConfigPath = "cfg",
                        Alias = "ntilde_0123",
                        CommandLine = "/usr/bin/ssh -F cfg ntilde_0123",
                        PlanArguments = ["-F", "cfg", "ntilde_0123"],
                    },
                    () => throw new InvalidOperationException("an OpenSSH profile never needs the native layer"),
                    static () => true,
                    askPassHelperPath: "/opt/ntilde/ntilde",
                    log: _ => { },
                    openSshVersions: RemoteMuxConnectorTests.ModernSsh);
                if (transport.SavedPasswordOnly) records.TryClaim(transport.AskPassSession!);
                _remote.Script = new FakeRemoteScript(Stderr: "nova@fake-host: Permission denied (publickey,password).\r\n", ExitCode: FakeRemoteHost.LinkLostExitCode);
                return _remote;
            },
            savedPassword: _ => "stale");
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => events.Has("abandoned") || _clock.PendingCount == 1, "the loop's attempt ended", Patient);

        Assert.True(events.Has("abandoned"), "the refused saved password must stop the loop at once");
        Assert.Equal(0, _clock.PendingCount);
        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal((RemoteFailureKind.NeedsUser, RemoteNeedsUserCause.SavedPasswordRefused), (failure.Kind, failure.Cause));
        Assert.Equal(1, Volatile.Read(ref automaticAttempts));
    }

    /// <summary>
    /// The loop runs after a drop, and its first attempt fails as <paramref name="automatic"/> makes it. The loop must stop
    /// there: abandoned at once, nothing scheduled, no attempt in the rest of its budget, and <paramref name="cause"/> recorded.
    /// </summary>
    private async Task AssertHostKeyStopsTheLoopAfterOneAttemptAsync(Action<RemoteMuxTransportRequest> automatic, RemoteNeedsUserCause cause = RemoteNeedsUserCause.HostKey)
    {
        int automaticAttempts = 0;
        MuxConnectionHost host = Create(
            (_, request) =>
            {
                _remote.OnStart = null;
                _remote.Script = null;
                if (request.Interactive) return _remote;
                Interlocked.Increment(ref automaticAttempts);
                automatic(request);
                return _remote;
            },
            new ScriptedUser(),
            savedPassword: _ => "s3cret",
            isTrustedHostKey: _ => false);
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => events.Has("abandoned") || _clock.PendingCount == 1, "the loop's attempt ended", Patient);

        Assert.True(events.Has("abandoned"), "a host key nobody trusts must stop the loop at once");
        Assert.Equal(0, _clock.PendingCount);
        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal((RemoteFailureKind.NeedsUser, cause), (failure.Kind, failure.Cause));
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(1, Volatile.Read(ref automaticAttempts));
    }

    /// <summary>
    /// Native: the loop's attempt meets a host key nobody trusts - never seen, or changed - and rejects it. Another
    /// automatic attempt would meet the same key, so the loop stops after this one, not ten minutes of knocking. The only
    /// answer given was the rejection: no password, the saved one included.
    /// </summary>
    [Theory]
    [InlineData(nameof(SshInteractionKind.UnknownHostKey))]
    [InlineData(nameof(SshInteractionKind.ChangedHostKey))]
    public async Task A_host_key_nobody_trusts_stops_the_loop_after_one_native_attempt(string kind)
    {
        var answers = new ConcurrentQueue<SshInteractionResponse>();

        await AssertHostKeyStopsTheLoopAfterOneAttemptAsync(request => _remote.OnStart = _ =>
        {
            SshInteractionResponse answer = request.Prompts.HandleAsync(RemoteMuxConnectorTests.HostKeyPrompt(Enum.Parse<SshInteractionKind>(kind)), CancellationToken.None)
                .GetAwaiter().GetResult();
            answers.Enqueue(answer);
            if (!answer.IsAccepted) _remote.Script = FakeRemoteScript.NativeFailure("Unknown server key");
        });

        Assert.False(Assert.Single(answers).IsAccepted);
    }

    /// <summary>
    /// OpenSSH: the loop's attempt hands ssh's askpass the saved password, but ssh fails at the host key before asking for
    /// it, exit 255. The loop stops after this one attempt, with the host-key cause - or the changed-key one, for a key that
    /// changed, which Enter cannot show.
    /// </summary>
    [Theory]
    [InlineData(RemoteMuxFailureClassifierTests.UnknownHostKeyStderr, false)]
    [InlineData(RemoteMuxFailureClassifierTests.ChangedHostKeyStderr, true)]
    public async Task A_host_key_nobody_trusts_stops_the_loop_after_one_OpenSSH_attempt(string sshSaid, bool changed)
    {
        bool offered = false;

        await AssertHostKeyStopsTheLoopAfterOneAttemptAsync(request =>
        {
            offered = request.OfferSavedPassword?.Invoke() == true;
            _remote.Script = new FakeRemoteScript(Stderr: sshSaid, ExitCode: FakeRemoteHost.LinkLostExitCode);
        }, changed ? RemoteNeedsUserCause.HostKeyChanged : RemoteNeedsUserCause.HostKey);

        Assert.True(offered);
    }

    /// <summary>
    /// The app's transport factory as far as the global native SSH switch goes (codex4 F): a native profile's attempt is
    /// refused while <paramref name="nativeSshEnabled"/> says off, as <see cref="RemoteMuxHostFactory.CreateTransport"/>
    /// refuses it; any other attempt gets the fake remote.
    /// </summary>
    private Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport> NativeSwitched(Func<bool> nativeSshEnabled) => (profile, _) =>
    {
        RemoteMuxHostFactory.ThrowIfNativeSshDisabled(profile, nativeSshEnabled);
        return _remote;
    };

    /// <summary>
    /// Codex4 F: native SSH turned off (Settings &gt; SSH) while a native profile's host is connected. The live connection
    /// stays; once the link drops, the loop's first attempt is refused before anything connects, and the loop stops at
    /// once - <see cref="MuxConnectionHost.ReconnectAbandoned"/>, nothing scheduled, not ten minutes of retries - with the
    /// refusal as the host's last failure by then. Enter while it is still off is refused too; once it is on, Enter
    /// connects, and the kill queued meanwhile goes out with it.
    /// </summary>
    [Fact]
    public async Task Native_ssh_turned_off_stops_the_reconnect_at_once_and_enter_connects_once_it_is_on()
    {
        _profile.BackendKind = SshBackendKind.Native;
        bool enabled = true;
        MuxConnectionHost host = Create(NativeSwitched(() => Volatile.Read(ref enabled)));
        var events = new HostEvents(host);
        Guid closed = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        Volatile.Write(ref enabled, false);
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(closed);

        _clock.Advance(FirstRetry);
        await events.WaitForAsync("abandoned");

        // Recorded before the event is raised: a pane reads it there, to say why.
        RemoteMuxFailure failure = Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure;
        Assert.Equal((RemoteFailureKind.NeedsUser, SshSessionFactory.NativeSshDisabledMessage), (failure.Kind, failure.Reason));
        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(1, _remote.StartCount);   // the refused attempt started nothing

        Assert.Null(host.GetClient(Patient));  // Enter, still off
        Assert.Equal(1, _remote.StartCount);
        Assert.Contains(closed, _remote.Server.GetSessionIds());

        Volatile.Write(ref enabled, true);
        Assert.NotNull(host.GetClient(Patient));   // Enter
        await events.WaitForAsync("reconnected");
        Assert.Equal(new[] { "lost:disconnected", "abandoned", "reconnected" }, events.Seen);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the queued kill went out once connected", Patient);
        Assert.Equal(2, _remote.StartCount);
    }

    /// <summary>
    /// Codex4 F: a kill on an idle native host while native SSH is off. Its automatic attempt is refused before anything
    /// connects, so the kill is never sent over native; it stays queued, and goes out with the first connect once the
    /// switch is on again.
    /// </summary>
    [Fact]
    public async Task A_kill_while_native_ssh_is_off_stays_queued_and_nothing_connects_for_it()
    {
        _profile.BackendKind = SshBackendKind.Native;
        bool enabled = false;
        MuxConnectionHost host = Create(NativeSwitched(() => Volatile.Read(ref enabled)));
        Guid closed = await SpawnWithoutAnExecAsync();

        host.KillWhenConnected(closed);

        await TestWait.UntilAsync(() => host.LastFailure is not null, "the automatic attempt was refused", Patient);
        Assert.Equal(RemoteFailureKind.NeedsUser, Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure.Kind);
        Assert.Equal(0, _remote.StartCount);
        Assert.Equal(0, _clock.PendingCount);
        Assert.Contains(closed, _remote.Server.GetSessionIds());
        Assert.False(Logged("dropping"));

        Volatile.Write(ref enabled, true);
        Assert.NotNull(host.GetClient(Patient));   // a pane's connect, say

        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(closed), "the queued kill reached the daemon", Patient);
    }

    /// <summary>Codex4 F: the switch is the native backend's. An OpenSSH profile's host connects and reconnects while it is off.</summary>
    [Fact]
    public async Task An_OpenSSH_host_connects_and_reconnects_while_native_ssh_is_off()
    {
        _profile.BackendKind = SshBackendKind.OpenSsh;
        MuxConnectionHost host = Create(NativeSwitched(static () => false));
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");

        _clock.Advance(FirstRetry);

        await events.WaitForAsync("reconnected");
        Assert.Equal(2, _remote.StartCount);
    }

    /// <summary>
    /// Ruling: a user's request never joins an automatic attempt in flight - that attempt may not prompt,
    /// so the user would wait on an attempt that can only fail for want of a password. It is cancelled, and
    /// an interactive one starts.
    /// </summary>
    [Fact]
    public async Task A_user_request_during_an_automatic_attempt_starts_an_interactive_one()
    {
        var kinds = new ConcurrentQueue<bool>();
        var automaticCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MuxConnectionHost host = Create((_, request) =>
        {
            kinds.Enqueue(request.Interactive);
            _remote.OnStart = request.Interactive ? null : ct =>
            {
                ct.WaitHandle.WaitOne();   // a prompt nobody may answer, until the attempt is cancelled
                automaticCancelled.TrySetResult();
                ct.ThrowIfCancellationRequested();
            };
            return _remote;
        });
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => _remote.StartCount == 2, "the automatic attempt started", Patient);

        MuxClient? client = host.GetClient(TimeSpan.FromSeconds(10));   // Enter in a pane

        Assert.True(client?.IsConnected);
        await automaticCancelled.Task.WaitAsync(Patient, Ct);
        Assert.Equal(new[] { true, false, true }, kinds);
        await events.WaitForAsync("reconnected");
        Assert.Same(client, events.LastReconnected);
        Assert.False(host.IsReconnecting);
        Assert.Equal(new[] { "lost:disconnected", "reconnected" }, events.Seen);
    }

    /// <summary>Ruling: a new attempt's outcome is its own - an earlier attempt's classified failure is not read for it.</summary>
    [Fact]
    public void A_new_attempt_does_not_report_the_previous_attempts_failure()
    {
        MuxConnectionHost host = Create();
        _remote.Script = FakeRemoteScript.NotInstalledDash;
        Assert.Null(host.GetClient(Patient));
        Assert.Equal(RemoteFailureKind.NotInstalled, Assert.IsType<RemoteMuxUnavailableException>(host.LastFailure).Failure.Kind);

        _remote.Script = FakeRemoteScript.Silent;   // the next attempt never finishes: a blackholed host
        Assert.Null(host.GetClient(TimeSpan.FromMilliseconds(300)));

        Assert.Null(host.LastFailure);
    }

    /// <summary>Review Focus 5: closing the app while a remote connect waits on ssh does not hang.</summary>
    [Fact]
    public async Task Dispose_during_a_blocked_remote_connect_returns_promptly()
    {
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remote.OnStart = ct =>
        {
            ct.WaitHandle.WaitOne();   // a prompt nobody answers
            sawCancellation.TrySetResult();
            ct.ThrowIfCancellationRequested();
        };
        MuxConnectionHost host = Create();
        Task<MuxClient?> connecting = Task.Run(() => host.GetClient(MuxHostPolicy.RemoteConnectTimeout), Ct);
        await TestWait.UntilAsync(() => _remote.StartCount == 1, "the connect is waiting in ssh", Patient);

        Task dispose = Task.Run(host.Dispose, Ct);

        Assert.True(dispose.Wait(2_000, Ct), "Dispose did not return within 2 s");
        await sawCancellation.Task.WaitAsync(Patient, Ct);
        Assert.Null(await connecting.WaitAsync(Patient, Ct));
    }

    /// <summary>
    /// Final review F4: closing the app while a connect waits for the proxy's greeting (behind a password
    /// prompt, say) kills that channel before the host's Dispose returns. Ending it gracefully on the pool left
    /// ssh, blocked on its askpass dialog, running after the app had exited.
    /// </summary>
    [Fact]
    public async Task Dispose_aborts_the_channel_of_a_pending_connect_before_it_returns()
    {
        _remote.Script = FakeRemoteScript.Silent;   // the channel runs, and no greeting ever comes
        MuxConnectionHost host = Create();
        Task<MuxClient?> connecting = Task.Run(() => host.GetClient(MuxHostPolicy.RemoteConnectTimeout), Ct);
        await TestWait.UntilAsync(() => _remote.LastChannel is { PendingStdoutReads: > 0 }, "the connect waits for the greeting", Patient);
        FakeRemoteChannel channel = _remote.LastChannel!;

        host.Dispose();

        Assert.Equal(1, channel.AbortCount);
        Assert.True(channel.Completion.IsCompleted, "the remote command still ran when Dispose returned");
        Assert.Null(await connecting.WaitAsync(Patient, Ct));
    }

    /// <summary>Final review F4: a user's request that supersedes an automatic attempt aborts that attempt's channel at once.</summary>
    [Fact]
    public async Task A_superseded_automatic_attempt_has_its_channel_aborted()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");
        _remote.Script = FakeRemoteScript.Silent;   // the automatic attempt's channel waits for a greeting that never comes
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => _remote.StartCount == 2 && _remote.LastChannel is { PendingStdoutReads: > 0 }, "the automatic attempt waits", Patient);
        FakeRemoteChannel automatic = _remote.LastChannel!;
        _remote.Script = null;

        MuxClient? client = host.GetClient(Patient);   // Enter in a pane

        Assert.True(client?.IsConnected);
        Assert.Equal(1, automatic.AbortCount);
    }

    [Fact]
    public async Task Dispose_during_a_blocked_automatic_reconnect_returns_promptly()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");
        var sawCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remote.OnStart = ct =>
        {
            ct.WaitHandle.WaitOne();
            sawCancellation.TrySetResult();
            ct.ThrowIfCancellationRequested();
        };
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => _remote.StartCount == 2, "the automatic attempt is waiting in ssh", Patient);

        Task dispose = Task.Run(host.Dispose, Ct);

        Assert.True(dispose.Wait(2_000, Ct), "Dispose did not return within 2 s");
        await sawCancellation.Task.WaitAsync(Patient, Ct);
    }

    [Fact]
    public async Task Dispose_stops_the_loop_and_drops_the_queued_kills()
    {
        MuxConnectionHost host = Create();
        var events = new HostEvents(host);
        Guid id = await MuxTestHost.SpawnAsync(host.GetClient(Patient)!);
        _remote.CutLink();
        await events.WaitForAsync("lost");
        host.KillWhenConnected(id);
        Assert.Equal(1, _clock.PendingCount);

        host.Dispose();

        Assert.Equal(0, _clock.PendingCount);
        Assert.False(host.IsReconnecting);
        Assert.True(Logged("dropping 1 queued kill"));
        _clock.Advance(MuxReconnectLoop.Budget);
        Assert.Equal(1, _remote.StartCount);
    }

    [Fact]
    public async Task Local_host_has_no_loop_and_no_ping()
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "local", null)
        {
            Scheduler = _clock,
        };
        var events = new HostEvents(host);
        MuxClient client = host.GetClient(Patient)!;

        Assert.Null(host.WatchedClientForTest);   // no Disconnected handler, no liveness
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, client.PendingRequestCount);   // never pinged
        Assert.True(client.IsConnected);

        client.Dispose();
        await host.ClassificationForTest.WaitAsync(Patient, Ct);   // none ran: it is the completed placeholder
        await host.EventsForTest.WaitAsync(Patient, Ct);

        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        Assert.Empty(events.Seen);
    }

    /// <summary>
    /// A user's Enter reconnects while the loss is still being classified: the panes still hear the loss,
    /// then at once that the connection is back - in that order, and with no loop.
    /// </summary>
    [Fact]
    public async Task A_connection_back_before_its_loss_is_classified_raises_lost_then_reconnected_without_a_loop()
    {
        using var mux = new MuxTestHost();
        var classifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<MuxDisconnectKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        MuxConnectionHost host = WithHeldClassification(mux, classifying, verdict.Task);
        var events = new HostEvents(host);
        MuxClient first = host.GetClient(Patient)!;

        first.Dispose();   // the connection ends...
        await classifying.Task.WaitAsync(Patient, Ct);
        MuxClient second = host.GetClient(Patient)!;   // ...and Enter reconnects before anyone knows why
        verdict.SetResult(MuxDisconnectKind.LinkLost);
        await host.ClassificationForTest.WaitAsync(Patient, Ct);
        await host.EventsForTest.WaitAsync(Patient, Ct);

        Assert.Equal(new[] { "lost:client_disposed", "reconnected" }, events.Seen);
        Assert.Same(second, events.LastReconnected);
        Assert.False(host.IsReconnecting);
        Assert.Equal(1, _clock.PendingCount);   // the new client's liveness tick, and no loop
        Assert.True(Logged("already back"));
    }

    [Fact]
    public async Task Dispose_during_a_classification_raises_nothing_and_starts_no_loop()
    {
        using var mux = new MuxTestHost();
        var classifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verdict = new TaskCompletionSource<MuxDisconnectKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        MuxConnectionHost host = WithHeldClassification(mux, classifying, verdict.Task);
        var events = new HostEvents(host);
        host.GetClient(Patient)!.Dispose();
        await classifying.Task.WaitAsync(Patient, Ct);

        host.Dispose();
        verdict.SetResult(MuxDisconnectKind.LinkLost);
        await host.ClassificationForTest.WaitAsync(Patient, Ct);
        await host.EventsForTest.WaitAsync(Patient, Ct);

        Assert.Empty(events.Seen);
        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
    }

    /// <summary>
    /// Review fix (ruling 2): an automatic attempt that ends needing the user just after a user's Enter
    /// superseded it does not give up - the user's attempt is running, and the panes must not flash the
    /// give-up banner meanwhile.
    /// </summary>
    [Fact]
    public async Task A_superseded_automatic_attempt_that_needs_the_user_does_not_give_up_under_a_user_retry()
    {
        using var userAnswered = new ManualResetEventSlim(initialState: true);
        using var abortedChannelEnds = new ManualResetEventSlim();
        var abortedChannelEnding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MuxConnectionHost host = Create((_, request) =>
        {
            if (!request.Interactive) return new PasswordPromptTransport(request.Prompts, abortedChannelEnding, abortedChannelEnds);
            _remote.OnStart = _ => userAnswered.Wait();   // the user, at a prompt
            return _remote;
        });
        var events = new HostEvents(host);
        Assert.NotNull(host.GetClient(Patient));
        _remote.CutLink();
        await events.WaitForAsync("lost");
        userAnswered.Reset();

        _clock.Advance(FirstRetry);   // the automatic attempt ends at a password prompt, and its channel is slow to close
        await abortedChannelEnding.Task.WaitAsync(Patient, Ct);
        Task<MuxClient?> enter = Task.Run(() => host.GetClient(Patient), Ct);   // Enter supersedes it, and waits on the user
        await TestWait.UntilAsync(() => _remote.StartCount == 2, "the user's attempt started", Patient);
        abortedChannelEnds.Set();   // the superseded attempt now fails: NeedsUser
        await TestWait.UntilAsync(() => Logged("superseded") || Logged("stopped reconnecting"), "its failure was handled", Patient);

        Assert.False(Logged("stopped reconnecting"));
        Assert.True(host.IsReconnecting);
        userAnswered.Set();
        Assert.True((await enter.WaitAsync(Patient, Ct))?.IsConnected);
        await events.WaitForAsync("reconnected");
        await host.EventsForTest.WaitAsync(Patient, Ct);
        Assert.Equal(new[] { "lost:disconnected", "reconnected" }, events.Seen);
    }

    /// <summary>
    /// An automatic attempt's transport as the native backend behaves on a password-only host: the password
    /// prompt has nobody to answer it, so the attempt ends the session there (the connector then fails it
    /// NeedsUser). Its channel is as slow to close as the test says.
    /// </summary>
    private sealed class PasswordPromptTransport(ISshInteractionHandler prompts, TaskCompletionSource ending, ManualResetEventSlim ends) : ISshExecTransport
    {
        public string DisplayName => "nova@fake-host";

        public ISshExecChannel Start(string remoteCommand, CancellationToken ct)
        {
            try
            {
                prompts.HandleAsync(RemoteMuxConnectorTests.PasswordPrompt, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (RemoteMuxPromptAbortedException)
            {
                // Nothing to answer with: the session closes at the prompt.
            }

            return new AbortedChannel(ending, ends);
        }

        private sealed class AbortedChannel(TaskCompletionSource ending, ManualResetEventSlim ends) : ISshExecChannel
        {
            public Stream Stdout { get; } = new FailingStream();
            public Stream Stdin { get; } = Stream.Null;
            public string StderrTail => string.Empty;
            public Task<int?> Completion { get; } = Task.FromResult<int?>(null);

            public void Dispose()
            {
                ending.TrySetResult();
                ends.Wait();
            }

            /// <summary>At once: only a graceful end is slow.</summary>
            public void Abort() => ending.TrySetResult();
        }

        private sealed class FailingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new SshExecTransportException("SSH to nova@fake-host failed: the session closed at a prompt", "the session closed at a prompt");
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    /// <summary>What a host raised, in order.</summary>
    private sealed class HostEvents
    {
        private readonly ConcurrentQueue<string> _seen = new();
        private MuxClient? _lastReconnected;

        public HostEvents(MuxConnectionHost host)
        {
            host.ConnectionLost += reason => _seen.Enqueue($"lost:{reason}");
            host.Reconnected += client =>
            {
                Volatile.Write(ref _lastReconnected, client);
                _seen.Enqueue("reconnected");
            };
            host.ReconnectAbandoned += () => _seen.Enqueue("abandoned");
            host.DaemonStopped += () => _seen.Enqueue("daemon-stopped");
        }

        public string[] Seen => _seen.ToArray();

        public MuxClient? LastReconnected => Volatile.Read(ref _lastReconnected);

        public bool Has(string prefix) => _seen.Any(s => s.StartsWith(prefix, StringComparison.Ordinal));

        public Task WaitForAsync(string prefix) => TestWait.UntilAsync(() => Has(prefix), $"the host raised {prefix}", Patient);
    }
}
