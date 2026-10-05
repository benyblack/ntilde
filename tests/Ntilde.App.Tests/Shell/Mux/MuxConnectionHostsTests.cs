using System.Diagnostics;
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

    [Fact]
    public void A_remote_host_is_created_once_per_endpoint()
    {
        using var local = Unreachable("local", MuxHostPolicy.Local);
        MuxEndpointId a = MuxEndpointId.ForSsh(Guid.NewGuid()), b = MuxEndpointId.ForSsh(Guid.NewGuid());
        var created = new System.Collections.Concurrent.ConcurrentQueue<MuxEndpointId>();
        using var hosts = new MuxConnectionHosts(local, id =>
        {
            created.Enqueue(id);
            return Unreachable(id.ToString());
        });

        Assert.Null(hosts.TryGet(a)); // nothing until asked for
        var fromA = new MuxConnectionHost?[16];
        Parallel.For(0, fromA.Length, i => fromA[i] = hosts.GetOrCreate(a));
        MuxConnectionHost? hostB = hosts.GetOrCreate(b);

        Assert.Equal([a, b], created.ToArray());
        Assert.NotNull(fromA[0]);
        Assert.All(fromA, h => Assert.Same(fromA[0], h));
        Assert.Same(fromA[0], hosts.TryGet(a));
        Assert.Same(fromA[0], hosts.GetOrCreate(a));
        Assert.NotSame(fromA[0], hostB);
        Assert.Equal([local, fromA[0]!, hostB!], hosts.All);
    }

    [Fact]
    public void A_declined_endpoint_returns_null_and_is_asked_again_next_time()
    {
        // Declined: the profile is gone, or its PersistRemoteSessions flag is off. Either can change,
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

    [Fact]
    public void Remote_hosts_are_disposed_in_parallel()
    {
        // Each remote host waits out its own unconfirmed kill: one after the other would take the sum.
        TimeSpan flush = TimeSpan.FromSeconds(2);
        using var localMux = new MuxTestHost();
        using var muxA = new MuxTestHost();
        using var muxB = new MuxTestHost();
        var local = On(localMux, "local", MuxHostPolicy.Local);
        MuxEndpointId a = MuxEndpointId.ForSsh(Guid.NewGuid()), b = MuxEndpointId.ForSsh(Guid.NewGuid());
        var hosts = new MuxConnectionHosts(local, e => On(e == a ? muxA : muxB, e.ToString(), MuxHostPolicy.Remote("box"), flush));
        foreach (MuxEndpointId e in new[] { a, b })
        {
            MuxConnectionHost h = hosts.GetOrCreate(e)!;
            Assert.NotNull(h.GetClient(TimeSpan.FromSeconds(5)));
            h.TrackPendingKill(new TaskCompletionSource().Task); // never confirmed
        }

        var sw = Stopwatch.StartNew();
        hosts.Dispose();

        // Sequential is at least 4 s; parallel is about 2 s.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"Dispose took {sw.Elapsed}");
    }
}
