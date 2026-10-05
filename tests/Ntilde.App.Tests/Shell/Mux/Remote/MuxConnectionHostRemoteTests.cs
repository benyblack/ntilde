using System.Collections.Concurrent;
using Ntilde.Mux;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Exec;
using Ntilde.Platform.Ssh.Interactions;
using Ntilde.Platform.Ssh.Models;
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
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private MuxConnectionHost Create(Func<SshProfile, RemoteMuxTransportRequest, ISshExecTransport>? transportFor = null, ISshInteractionHandler? user = null) =>
        Own(RemoteMuxHostFactory.Create(MuxEndpointId.ForSsh(_profile.Id), _ => _profile, transportFor ?? ((_, _) => _remote), _log.Enqueue, user, _clock)
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

    /// <summary>A host over a fake daemon that answers only the hello: the test sends every other frame itself.</summary>
    private async Task<(MuxConnectionHost Host, MuxClient Client)> OverFakeDaemonAsync(FakeMuxServerEnd daemon, MuxClientOptions? options = null)
    {
        Task hello = daemon.AcceptHelloAsync();
        MuxConnectionHost host = Own(new MuxConnectionHost(ct => MuxClient.ConnectAsync(daemon.ClientEnd, options, ct), "remote", null, MuxHostPolicy.Remote("box"))
        {
            Scheduler = _clock,
        });
        MuxClient client = host.GetClient(Patient)!;
        await hello.WaitAsync(Patient, Ct);
        return (host, client);
    }

    /// <summary>Output for a session nobody opened: the client drops it, but it is a frame that arrived. Returns once it has.</summary>
    private static async Task SendOutputAsync(FakeMuxServerEnd daemon, MuxClient client, long seq)
    {
        await ClockPastLastFrameAsync(client);
        long before = Environment.TickCount64; // later than every earlier frame's stamp: only this one can reach it
        daemon.Raw.Send(MuxFrames.Output(Guid.Empty, seq, "x"u8));
        await TestWait.UntilAsync(() => client.LastReceivedTicks >= before, "the output arrived", Patient);
    }

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

    /// <summary>The ping's request gives up on its own (the client's request timeout) while frames keep arriving: busy, not dead.</summary>
    [Fact]
    public async Task A_ping_whose_request_times_out_on_a_busy_link_is_sent_again_not_dropped()
    {
        using var daemon = FakeMuxServerEnd.Create();
        (MuxConnectionHost host, MuxClient client) = await OverFakeDaemonAsync(daemon, new MuxClientOptions { RequestTimeout = TimeSpan.FromSeconds(2) });
        var events = new HostEvents(host);
        await ClockPastLastFrameAsync(client);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(MuxMethods.Ping, (await daemon.ReadRequestAsync().WaitAsync(Patient, Ct)).Method);

        await SendOutputAsync(daemon, client, 1);
        await TestWait.UntilAsync(() => _clock.PendingCount == 1, "the ping's request timed out and its timeout was cancelled", Patient);

        Assert.True(client.IsConnected);
        _clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(MuxMethods.Ping, (await daemon.ReadRequestAsync().WaitAsync(Patient, Ct)).Method);   // pinged again
        Assert.True(client.IsConnected);
        Assert.Empty(events.Seen);
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

        Assert.Contains(closed, _remote.Server.GetSessionIds());
        _clock.Advance(FirstRetry);
        await events.WaitForAsync("reconnected");
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

        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, client.PendingRequestCount);   // never pinged
        Assert.True(client.IsConnected);

        client.Dispose();
        await Task.Delay(50, Ct);   // what would be a remote host's classification

        Assert.False(host.IsReconnecting);
        Assert.Equal(0, _clock.PendingCount);
        Assert.Empty(events.Seen);
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
