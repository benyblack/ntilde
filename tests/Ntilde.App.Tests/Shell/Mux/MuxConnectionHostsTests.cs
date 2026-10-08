using Ntilde.Mux;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

/// <summary>Phase 4 spec §5: one <see cref="MuxConnectionHost"/> per endpoint, built lazily.</summary>
public sealed class MuxConnectionHostsTests
{
    private static MuxConnectionHost Unreachable(string label = "x", MuxHostPolicy? policy = null) =>
        new(_ => throw new MuxUnavailableException("down"), label, null, policy ?? MuxHostPolicy.Remote(label));

    private static MuxConnectionHost On(MuxTestHost mux, string label, MuxHostPolicy policy, TimeSpan? killFlush = null) =>
        new(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), label, null, policy)
        {
            KillFlushTimeout = killFlush ?? TimeSpan.FromSeconds(10),
        };

    [Fact]
    public void Local_is_the_given_host_and_never_asks_the_remote_creator()
    {
        using var local = new MuxConnectionHost(_ => throw new MuxUnavailableException("down"), "local-pipe", null);
        int asked = 0;
        using var hosts = new MuxConnectionHosts(local, _ => { asked++; return null; });

        Assert.Same(local, hosts.Local);
        Assert.Same(local, hosts.GetOrCreate(MuxEndpointId.Local));
        Assert.Same(local, hosts.TryGet(MuxEndpointId.Local));
        Assert.Same(local, hosts.GetOrCreate(MuxEndpointId.Parse("ntilde-mux-legacy")));
        Assert.Equal(0, asked);
        Assert.Same(MuxHostPolicy.Local, local.Policy);
        Assert.Equal(TimeSpan.FromSeconds(30), local.FailureCooldown);
    }

    /// <summary>
    /// One host per endpoint: once registered it is reused without asking again. Asks that race (the
    /// creator runs outside the lock) may each build one, but all get the same registered host and every
    /// other one is disposed unused (a disposed host never attempts a connection).
    /// </summary>
    [Fact]
    public void A_remote_host_is_registered_once_per_endpoint()
    {
        using var local = Unreachable("local", MuxHostPolicy.Local);
        MuxEndpointId a = MuxEndpointId.ForSsh(Guid.NewGuid()), b = MuxEndpointId.ForSsh(Guid.NewGuid());
        var built = new System.Collections.Concurrent.ConcurrentQueue<(MuxEndpointId Id, MuxConnectionHost Host)>();
        using var hosts = new MuxConnectionHosts(local, id =>
        {
            MuxConnectionHost h = Unreachable(id.ToString());
            built.Enqueue((id, h));
            return h;
        });

        Assert.Null(hosts.TryGet(a)); // nothing until asked for
        var fromA = new MuxConnectionHost?[16];
        Parallel.For(0, fromA.Length, i => fromA[i] = hosts.GetOrCreate(a));
        int builtForA = built.Count;
        MuxConnectionHost? hostB = hosts.GetOrCreate(b);
        Assert.Same(hostB, hosts.GetOrCreate(b));

        Assert.NotNull(fromA[0]);
        Assert.All(fromA, h => Assert.Same(fromA[0], h));
        Assert.Same(fromA[0], hosts.TryGet(a));
        Assert.Same(fromA[0], hosts.GetOrCreate(a));
        Assert.Equal(builtForA + 1, built.Count); // registered: asked once more for b, never again for a
        Assert.NotSame(fromA[0], hostB);
        Assert.Equal([local, fromA[0]!, hostB!], hosts.All);
        foreach ((MuxEndpointId _, MuxConnectionHost extra) in built.Where(x => x.Id == a && !ReferenceEquals(x.Host, fromA[0])))
        {
            extra.WarmUp();
            Assert.Equal(0, extra.ConnectAttempts);
        }
    }

    [Fact]
    public void A_declined_endpoint_returns_null_and_is_asked_again_next_time()
    {
        // Declined: the profile is gone (a profile can come back, by an import or a backup restore),
        // so the answer is not remembered.
        using var local = Unreachable("local", MuxHostPolicy.Local);
        MuxEndpointId remote = MuxEndpointId.ForSsh(Guid.NewGuid());
        int asked = 0;
        using var hosts = new MuxConnectionHosts(local, _ => { asked++; return null; });

        Assert.Null(hosts.GetOrCreate(remote));
        Assert.Null(hosts.GetOrCreate(remote));
        Assert.Null(hosts.TryGet(remote));
        Assert.Equal(2, asked);
        Assert.Equal([local], hosts.All);
    }

    /// <summary>
    /// The local host goes last: a remote's flush can take its whole KillFlushTimeout, and the
    /// local daemon's connection must still be open (its own kills flushed) only after that.
    /// </summary>
    [Fact]
    public void Dispose_closes_every_remote_before_the_local_host()
    {
        using var localMux = new MuxTestHost();
        using var remoteMux = new MuxTestHost();
        var local = On(localMux, "local", MuxHostPolicy.Local);
        MuxEndpointId id = MuxEndpointId.ForSsh(Guid.NewGuid());
        MuxConnectionHost remote = On(remoteMux, "remote", MuxHostPolicy.Remote("box"));
        var hosts = new MuxConnectionHosts(local, e => e == id ? remote : null);
        MuxClient localClient = local.GetClient(TimeSpan.FromSeconds(5))!;
        Assert.NotNull(hosts.GetOrCreate(id)!.GetClient(TimeSpan.FromSeconds(5)));

        // The remote's Dispose waits for this kill; while it does, the local connection must still be open.
        bool? localOpenDuringRemoteFlush = null;
        remote.TrackPendingKill(Task.Run(async () =>
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            localOpenDuringRemoteFlush = localClient.IsConnected;
        }, TestContext.Current.CancellationToken));

        hosts.Dispose();

        Assert.True(localOpenDuringRemoteFlush, "the local host was closed before a remote finished flushing");
        Assert.False(localClient.IsConnected);
        Assert.Null(remote.CurrentClient);
        Assert.Null(hosts.GetOrCreate(MuxEndpointId.ForSsh(Guid.NewGuid()))); // nothing new after Dispose
        hosts.Dispose(); // idempotent
    }

    /// <summary>A task that completes when <paramref name="token"/> is cancelled.</summary>
    private static Task WhenCancelled(CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => done.TrySetResult());
        return done.Task;
    }

    /// <summary>
    /// Each remote's tracked kill is confirmed only once the OTHER remote's Dispose has begun: the token a
    /// host hands its connect function is its disposal token, cancelled first thing in Dispose. Disposed
    /// together, both begin at once and both kills land at once. One after the other, the first host
    /// waits out its whole KillFlushTimeout for a kill only the second can release, and logs it.
    /// </summary>
    [Fact]
    public void Remote_hosts_are_disposed_in_parallel()
    {
        using var muxA = new MuxTestHost();
        using var muxB = new MuxTestHost();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var disposalTokens = new System.Collections.Concurrent.ConcurrentDictionary<MuxEndpointId, CancellationToken>();
        MuxEndpointId a = MuxEndpointId.ForSsh(Guid.NewGuid()), b = MuxEndpointId.ForSsh(Guid.NewGuid());
        var hosts = new MuxConnectionHosts(Unreachable("local", MuxHostPolicy.Local), e => new MuxConnectionHost(
            ct =>
            {
                disposalTokens[e] = ct;
                return MuxClient.ConnectAsync((e == a ? muxA : muxB).Listener.Connect(), null, ct);
            },
            e.ToString(), logs.Enqueue, MuxHostPolicy.Remote("box"))
        {
            KillFlushTimeout = TimeSpan.FromSeconds(30),
        });
        MuxConnectionHost hostA = hosts.GetOrCreate(a)!, hostB = hosts.GetOrCreate(b)!;
        Assert.NotNull(hostA.GetClient(TimeSpan.FromSeconds(5)));
        Assert.NotNull(hostB.GetClient(TimeSpan.FromSeconds(5)));
        hostA.TrackPendingKill(WhenCancelled(disposalTokens[b]));
        hostB.TrackPendingKill(WhenCancelled(disposalTokens[a]));

        Task dispose = Task.Run(hosts.Dispose, TestContext.Current.CancellationToken);

        // A safety bound, not the measure: together this returns at once; one after the other it takes
        // the 30 s flush, and the test fails here instead of hanging on it.
        Assert.True(dispose.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "the remote hosts were disposed one after the other");
        Assert.DoesNotContain(logs, l => l.Contains("not confirmed", StringComparison.Ordinal));
        Assert.Null(hostA.CurrentClient);
        Assert.Null(hostB.CurrentClient);
    }

    /// <summary>
    /// The creator runs outside the registry's lock, so two asks for one endpoint can both build a host.
    /// Each creator is held until both threads are inside one (impossible if the lock were held); both
    /// callers then get the host registered first, and the other is disposed unused.
    /// </summary>
    [Fact]
    public void Racing_asks_for_one_endpoint_get_one_host_and_the_loser_is_disposed()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        using var local = Unreachable("local", MuxHostPolicy.Local);
        MuxEndpointId id = MuxEndpointId.ForSsh(Guid.NewGuid());
        var built = new System.Collections.Concurrent.ConcurrentQueue<MuxConnectionHost>();
        using var bothBuilding = new Barrier(2);
        using var hosts = new MuxConnectionHosts(local, e =>
        {
            Assert.True(bothBuilding.SignalAndWait(TimeSpan.FromSeconds(10), testToken), "the second ask was blocked while the first was building");
            MuxConnectionHost h = Unreachable(e.ToString());
            built.Enqueue(h);
            return h;
        });

        Task<MuxConnectionHost?> Ask() => Task.Factory.StartNew(() => hosts.GetOrCreate(id), testToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<MuxConnectionHost?> first = Ask(), second = Ask();
        Assert.True(Task.WaitAll([first, second], (int)TimeSpan.FromSeconds(20).TotalMilliseconds, testToken), "the racing asks did not finish");

        MuxConnectionHost winner = Assert.IsType<MuxConnectionHost>(first.Result);
        Assert.Same(winner, second.Result);
        Assert.Same(winner, hosts.TryGet(id));
        Assert.Equal([local, winner], hosts.All);
        Assert.Equal(2, built.Count);
        MuxConnectionHost loser = Assert.Single(built, h => !ReferenceEquals(h, winner));
        loser.WarmUp(); // a disposed host never attempts a connection; a live one would
        Assert.Equal(0, loser.ConnectAttempts);
        winner.WarmUp();
        Assert.Equal(1, winner.ConnectAttempts);
    }

    /// <summary>
    /// A creator that waits on another thread reading the registry - one that marshals to the UI thread
    /// while the UI thread calls TryGet - must not deadlock, and one that reads it itself must not block.
    /// </summary>
    [Fact]
    public void A_creator_may_read_the_registry_from_its_own_thread_or_another()
    {
        CancellationToken testToken = TestContext.Current.CancellationToken;
        using var local = Unreachable("local", MuxHostPolicy.Local);
        MuxEndpointId id = MuxEndpointId.ForSsh(Guid.NewGuid()), other = MuxEndpointId.ForSsh(Guid.NewGuid());
        MuxConnectionHosts? hosts = null;
        hosts = new MuxConnectionHosts(local, e =>
        {
            Assert.Null(hosts!.TryGet(e));
            Task<MuxConnectionHost?> read = Task.Factory.StartNew(() => hosts!.TryGet(other), testToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(read.Wait(TimeSpan.FromSeconds(10), testToken), "a TryGet on another thread was blocked while the creator ran");
            return Unreachable(e.ToString());
        });

        using (hosts)
        {
            Assert.NotNull(hosts.GetOrCreate(id));
        }
    }

    /// <summary>
    /// A remote host whose Dispose throws (here its connector's) is logged and skipped: the other remote and
    /// the local host still close, and nothing escapes into the window's teardown, which has no try/catch of
    /// its own around this. A transport callback that throws when the host cancels its attempt (final review
    /// I2: cancelled outside the host's lock) is logged by the host, and its Dispose goes on to the end.
    /// </summary>
    [Fact]
    public void A_remote_host_that_fails_to_dispose_is_logged_and_the_rest_still_close()
    {
        using var localMux = new MuxTestHost();
        using var goodMux = new MuxTestHost();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var local = On(localMux, "local", MuxHostPolicy.Local);
        MuxEndpointId bad = MuxEndpointId.ForSsh(Guid.NewGuid()), good = MuxEndpointId.ForSsh(Guid.NewGuid());
        var hosts = new MuxConnectionHosts(local, e => e == bad
            ? new MuxConnectionHost(
                ct =>
                {
                    ct.Register(() => throw new InvalidOperationException("transport teardown failed"));
                    return Task.FromException<MuxClient>(new MuxUnavailableException("down"));
                },
                "bad", logs.Enqueue, MuxHostPolicy.Remote("bad box"))
            {
                Connector = new ThrowingDisposable("connector teardown failed"),
            }
            : On(goodMux, "good", MuxHostPolicy.Remote("good box")), logs.Enqueue);
        MuxClient localClient = local.GetClient(TimeSpan.FromSeconds(5))!;
        MuxClient goodClient = hosts.GetOrCreate(good)!.GetClient(TimeSpan.FromSeconds(5))!;
        Assert.Null(hosts.GetOrCreate(bad)!.GetClient(TimeSpan.FromSeconds(5))); // the attempt registered its callback, then failed

        hosts.Dispose();

        Assert.Contains(logs, l => l.Contains("bad box", StringComparison.Ordinal) && l.Contains("transport teardown failed", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("bad box", StringComparison.Ordinal) && l.Contains("connector teardown failed", StringComparison.Ordinal));
        Assert.False(goodClient.IsConnected);
        Assert.False(localClient.IsConnected);
    }

    /// <summary>
    /// Phase 5 Task 23: one place reports every host's connections - the local one's, and each remote one's from the moment
    /// it is registered - with its endpoint, so the window can offer to restart a daemon of another build wherever it runs.
    /// </summary>
    [Fact]
    public async Task Every_hosts_connections_are_reported_with_its_endpoint()
    {
        using var localMux = new MuxTestHost();
        using var remoteMux = new MuxTestHost();
        var local = On(localMux, "local", MuxHostPolicy.Local);
        MuxEndpointId remoteId = MuxEndpointId.ForSsh(Guid.NewGuid());
        using var hosts = new MuxConnectionHosts(local, _ => On(remoteMux, "remote", MuxHostPolicy.Remote("box")));
        var reported = new System.Collections.Concurrent.ConcurrentQueue<(MuxEndpointId, MuxConnectionHost, MuxClient)>();
        hosts.HostConnected += (id, host, client) => reported.Enqueue((id, host, client));

        MuxConnectionHost remote = hosts.GetOrCreate(remoteId)!;
        MuxClient remoteClient = remote.GetClient(TimeSpan.FromSeconds(5))!;
        await remote.EventsForTest;
        MuxClient localClient = local.GetClient(TimeSpan.FromSeconds(5))!;
        await local.EventsForTest;

        Assert.Equal([(remoteId, remote, remoteClient), (MuxEndpointId.Local, local, localClient)], reported);
    }

    private sealed class ThrowingDisposable(string message) : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException(message);
    }
}
