using System.Collections.Concurrent;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class SessionEventsClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionChanged_reaches_every_attached_v2_client()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);

        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 2 && p2.Session.AttachedClients == 2, "both hear the count");
        Assert.True(p1.Session.SupportsSessionEvents);

        p2.Session.Dispose();
        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 1, "the detach is announced to the one left");
    }

    [Fact]
    public async Task InteractiveOthers_excludes_read_only_peers_and_falls_back_to_AttachedClients()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        MuxClientSession s = (await MuxTestHost.AttachPaneAsync(c1, id)).Session;
        await TestWait.UntilAsync(() => s.AttachedClients == 1, "the first count arrived");
        Assert.Equal(0, s.InteractiveOthers);

        s.DeliverSessionChanged(new SessionChangedNotification { SessionId = id, AttachedClients = 2, InteractiveClients = 1 });
        Assert.Equal(1, s.InteractiveClients);
        Assert.Equal(0, s.InteractiveOthers);

        s.DeliverSessionChanged(new SessionChangedNotification { SessionId = id, AttachedClients = 2 }); // no InteractiveClients: fallback
        Assert.Null(s.InteractiveClients);
        Assert.Equal(1, s.InteractiveOthers);
    }

    [Fact]
    public async Task KilledElsewhere_precedes_OnExit_and_the_killer_is_not_told()
    {
        using var host = new MuxTestHost();
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient killer = await host.ConnectClientAsync(new MuxClientOptions { ClientKind = "killer-kind" });
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        ClientPaneModel pk = await MuxTestHost.AttachPaneAsync(killer, id);
        var events = new ConcurrentQueue<string>();
        p1.Session.KilledElsewhere += kind => events.Enqueue("killed:" + kind);
        p1.Session.OnExit += code => events.Enqueue("exit:" + code);
        int killerTold = 0;
        pk.Session.KilledElsewhere += _ => Interlocked.Increment(ref killerTold);

        await killer.KillAsync(id, Ct);

        await TestWait.UntilAsync(() => events.Count == 2, "killed and exit arrived");
        Assert.Equal(["killed:killer-kind", "exit:-1"], events.ToArray());
        Assert.True(p1.Session.WasKilledElsewhere);
        Assert.Equal(0, Volatile.Read(ref killerTold));
    }

    [Fact]
    public async Task A_throwing_KilledElsewhere_handler_neither_drops_the_connection_nor_suppresses_OnExit()
    {
        using var host = new MuxTestHost();
        var log = new ConcurrentQueue<string>();
        MuxClient c1 = await host.ConnectClientAsync(new MuxClientOptions { Log = log.Enqueue });
        MuxClient killer = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        await MuxTestHost.AttachPaneAsync(killer, id);
        int exits = 0;
        int laterHandlerRan = 0;
        p1.Session.KilledElsewhere += _ => throw new InvalidOperationException("handler boom");
        p1.Session.KilledElsewhere += _ => Interlocked.Increment(ref laterHandlerRan);
        p1.Session.OnExit += _ => Interlocked.Increment(ref exits);

        await killer.KillAsync(id, Ct);

        await TestWait.UntilAsync(() => Volatile.Read(ref exits) == 1, "OnExit still fired");
        Assert.True(p1.Session.WasKilledElsewhere);
        Assert.Equal(1, Volatile.Read(ref laterHandlerRan));
        Assert.True(c1.IsConnected);
        await c1.PingAsync(Ct);
        Assert.Contains(log, line => line.Contains("handler boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_throwing_SessionChanged_handler_does_not_drop_the_connection()
    {
        using var host = new MuxTestHost();
        var log = new ConcurrentQueue<string>();
        MuxClient c1 = await host.ConnectClientAsync(new MuxClientOptions { Log = log.Enqueue });
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        p1.Session.SessionChanged += () => throw new InvalidOperationException("handler boom");

        ClientPaneModel p2 = await MuxTestHost.AttachPaneAsync(c2, id);
        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 2, "the count arrived");
        await c1.PingAsync(Ct);

        p2.Session.Dispose();
        await TestWait.UntilAsync(() => p1.Session.AttachedClients == 1, "later notifications still arrive");
        Assert.True(c1.IsConnected);
        Assert.Contains(log, line => line.Contains("handler boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IfUnattached_races_between_clients_have_exactly_one_winner()
    {
        using var host = new MuxTestHost();
        MuxClient spawner = await host.ConnectClientAsync();
        MuxClient a = await host.ConnectClientAsync();
        MuxClient b = await host.ConnectClientAsync();
        for (int i = 0; i < 30; i++)
        {
            Guid id = await MuxTestHost.SpawnAsync(spawner);
            MuxClientSession sa = a.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);
            MuxClientSession sb = b.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);

            async Task<string> Attach(MuxClientSession s)
            {
                try
                {
                    await s.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
                    return "attached";
                }
                catch (MuxProtocolException ex)
                {
                    return ex.Code;
                }
            }

            string[] outcomes = await Task.WhenAll(Attach(sa), Attach(sb));
            Assert.Single(outcomes, o => o == "attached");
            Assert.Single(outcomes, o => o == MuxErrorCodes.SessionAttached);
            sa.Dispose();
            sb.Dispose();
        }
    }

    [Fact]
    public async Task A_read_only_session_attaches_without_resizing()
    {
        using var host = new MuxTestHost();
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);                  // 80x24
        MuxClientSession ro = c.OpenSession(id, "scripted", null, MuxAttachMode.ReadOnly);
        var model = new ClientPaneModel(ro);

        await ro.AttachAsync(0, MuxTestHost.DefaultPresentation with { Cols = 120, Rows = 40 }, Ct);

        Assert.Equal(MuxAttachMode.ReadOnly, ro.AttachMode);
        Assert.Equal((80, 24), (host.Mux(id).Cols, host.Mux(id).Rows));
        Assert.Equal((80, 24), (model.Buffer.Cols, model.Buffer.Rows));
    }
}
