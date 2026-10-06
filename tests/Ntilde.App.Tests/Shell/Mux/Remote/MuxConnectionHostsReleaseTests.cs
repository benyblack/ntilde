using System.Collections.Concurrent;
using Ntilde.Mux;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Platform.Ssh.Models;
using Ntilde.Shell.Mux;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Tests.Shell.Mux.Remote;

/// <summary>
/// Final review F1: a remote host is released once no pane uses its endpoint and its kills are delivered
/// (<see cref="MuxConnectionHosts.Release"/>), over a <see cref="FakeRemoteHost"/> and a clock the test advances.
/// Before, nothing ever released one: it kept reconnecting, pinging and holding the remote daemon until the window
/// closed. The window decides when an endpoint is unused (MainWindowMuxRemoteTests); this is what a release does.
/// </summary>
public sealed class MuxConnectionHostsReleaseTests : IDisposable
{
    private static readonly TimeSpan Patient = TimeSpan.FromSeconds(30);

    /// <summary>The reconnect loop's first wait at its longest: one second, jittered by 20%.</summary>
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(1.2);

    private readonly FakeRemoteHost _remote = new();
    private readonly FakeMuxTimerScheduler _clock = new();
    private readonly SshProfile _profile = RemoteMuxConnectorTests.Profile();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly MuxConnectionHosts _hosts;
    private readonly MuxConnectionHost _local;
    private int _built;

    public MuxConnectionHostsReleaseTests()
    {
        _local = new MuxConnectionHost(_ => throw new MuxUnavailableException("no local daemon here"), "local", null);
        _hosts = new MuxConnectionHosts(_local, id =>
        {
            Interlocked.Increment(ref _built);
            return RemoteMuxHostFactory.Create(id, _ => _profile, (_, _) => _remote, _log.Enqueue, userPrompts: null, scheduler: _clock);
        }, _log.Enqueue);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MuxEndpointId Endpoint => MuxEndpointId.ForSsh(_profile.Id);

    public void Dispose()
    {
        _hosts.Dispose();
        _remote.Dispose();
    }

    /// <summary>A connected host for the endpoint, and a shell running on its daemon.</summary>
    private async Task<(MuxConnectionHost Host, Guid Session)> ConnectedWithASessionAsync()
    {
        MuxConnectionHost host = _hosts.GetOrCreate(Endpoint)!;
        MuxClient client = host.GetClient(Patient)!;
        Guid id = await MuxTestHost.SpawnAsync(client);
        return (host, id);
    }

    private static TaskCompletionSource ClosedSignal(MuxConnectionHost host)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Closed += _ => closed.TrySetResult();
        return closed;
    }

    /// <summary>Nothing left to deliver: the host closes at once - no timer left, no attempt after, its channel ended.</summary>
    [Fact]
    public async Task Releasing_an_endpoint_with_nothing_to_deliver_closes_its_host()
    {
        (MuxConnectionHost host, _) = await ConnectedWithASessionAsync();
        FakeRemoteChannel channel = _remote.LastChannel!;
        TaskCompletionSource closed = ClosedSignal(host);
        Assert.Equal(1, _clock.PendingCount);   // the liveness ping

        _hosts.Release(Endpoint);

        await closed.Task.WaitAsync(Patient, Ct);
        Assert.Null(_hosts.TryGet(Endpoint));
        Assert.DoesNotContain(host, _hosts.All);
        await channel.Disposed.WaitAsync(Patient, Ct);
        Assert.Equal(0, _clock.PendingCount);
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, _remote.StartCount);
    }

    /// <summary>The ruling's order: a kill queued while the link was down goes out before the host closes.</summary>
    [Fact]
    public async Task A_queued_kill_is_delivered_before_the_release_closes_the_host()
    {
        (MuxConnectionHost host, Guid id) = await ConnectedWithASessionAsync();
        _remote.CutLink();
        await TestWait.UntilAsync(() => host.IsReconnecting, "the link is down", Patient);
        host.KillWhenConnected(id);   // the last pane closed: its shell must still end
        bool? killedAtClose = null;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Closed += _ =>
        {
            killedAtClose = !_remote.Server.GetSessionIds().Contains(id);
            closed.TrySetResult();
        };

        _hosts.Release(Endpoint);

        Assert.Same(host, _hosts.TryGet(Endpoint));   // the kill waits for the link, and so does the release
        Assert.False(host.IsClosed);
        _clock.Advance(FirstRetry);                     // the link is back: the kill goes out, then the host closes
        await closed.Task.WaitAsync(Patient, Ct);
        Assert.True(killedAtClose);
        Assert.Null(_hosts.TryGet(Endpoint));
        Assert.Equal(0, _clock.PendingCount);
    }

    /// <summary>
    /// The ruling's reuse choice: asking for an endpoint whose release is pending takes the host back - the release
    /// is cancelled and it stays - rather than building a second host beside one about to close.
    /// </summary>
    [Fact]
    public async Task Asking_for_an_endpoint_whose_release_is_pending_takes_the_host_back()
    {
        (MuxConnectionHost host, Guid id) = await ConnectedWithASessionAsync();
        _remote.CutLink();
        await TestWait.UntilAsync(() => host.IsReconnecting, "the link is down", Patient);
        host.KillWhenConnected(id);
        _hosts.Release(Endpoint);

        MuxConnectionHost? again = _hosts.GetOrCreate(Endpoint);   // a pane opens on that endpoint meanwhile

        Assert.Same(host, again);
        Assert.Equal(1, Volatile.Read(ref _built));
        _clock.Advance(FirstRetry);
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(id), "the queued kill went out", Patient);
        await TestWait.UntilAsync(() => host.PendingKillCountForTest == 0, "its reply arrived", Patient);
        Assert.False(host.IsClosed);
        Assert.Same(host, _hosts.TryGet(Endpoint));
        Assert.NotNull(host.GetClient(Patient));
    }

    /// <summary>After a release the endpoint gets a new host, which connects and works.</summary>
    [Fact]
    public async Task Asking_again_after_a_release_builds_a_new_host_that_works()
    {
        (MuxConnectionHost first, _) = await ConnectedWithASessionAsync();
        TaskCompletionSource closed = ClosedSignal(first);
        _hosts.Release(Endpoint);
        await closed.Task.WaitAsync(Patient, Ct);

        MuxConnectionHost second = _hosts.GetOrCreate(Endpoint)!;
        MuxClient client = second.GetClient(Patient)!;
        Guid id = await MuxTestHost.SpawnAsync(client);

        Assert.NotSame(first, second);
        Assert.Equal(2, Volatile.Read(ref _built));
        Assert.Equal(2, _remote.StartCount);
        Assert.Contains(id, _remote.Server.GetSessionIds());
        Assert.Same(second, _hosts.TryGet(Endpoint));
    }

    /// <summary>
    /// A host that gave up reconnecting with a kill still queued stays registered, idle - no loop, no ping - and the
    /// kill goes out on the next use of the endpoint, which takes the host back.
    /// </summary>
    [Fact]
    public async Task A_host_that_gave_up_with_a_queued_kill_stays_idle_until_the_endpoint_is_used_again()
    {
        (MuxConnectionHost host, Guid id) = await ConnectedWithASessionAsync();
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ReconnectAbandoned += () => abandoned.TrySetResult();
        _remote.Script = FakeRemoteScript.ConnectionRefused;   // every reconnect fails
        _remote.CutLink();
        await TestWait.UntilAsync(() => host.IsReconnecting, "the link is down", Patient);
        host.KillWhenConnected(id);
        for (int i = 0; i < 200 && !abandoned.Task.IsCompleted; i++)
        {
            await TestWait.UntilAsync(() => abandoned.Task.IsCompleted || _clock.PendingCount == 1, "the loop scheduled its next attempt", Patient);
            if (!abandoned.Task.IsCompleted) _clock.Advance(_clock.NextDueIn!.Value);
        }

        await abandoned.Task.WaitAsync(Patient, Ct);
        _hosts.Release(Endpoint);

        Assert.Same(host, _hosts.TryGet(Endpoint));
        Assert.False(host.IsClosed);
        Assert.Equal(0, _clock.PendingCount);   // idle: no loop, no ping

        _remote.Script = null;
        Assert.Same(host, _hosts.GetOrCreate(Endpoint));
        Assert.NotNull(host.GetClient(Patient));
        await TestWait.UntilAsync(() => !_remote.Server.GetSessionIds().Contains(id), "the queued kill went out", Patient);
        Assert.False(host.IsClosed);
    }

    /// <summary>A host disposed some other way is forgotten, so the next ask builds a new one rather than return a closed one.</summary>
    [Fact]
    public async Task A_host_disposed_some_other_way_is_forgotten()
    {
        (MuxConnectionHost first, _) = await ConnectedWithASessionAsync();

        first.Dispose();

        Assert.Null(_hosts.TryGet(Endpoint));
        MuxConnectionHost second = _hosts.GetOrCreate(Endpoint)!;
        Assert.NotSame(first, second);
        Assert.NotNull(second.GetClient(Patient));
    }
}
