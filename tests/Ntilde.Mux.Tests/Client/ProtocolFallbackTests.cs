using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

/// <summary>Phase 3 spec §2.1: the fallback matrix.</summary>
public sealed class ProtocolFallbackTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static MuxServerOptions V1Server => new() { MaxProtocolVersion = 1, ForceConPtyFiltering = false };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_shared_attach_is_sent_in_the_exact_v1_shape(int negotiated)
    {
        // Shared travels as an absent member, never "mode": null or "shared": a v1 daemon sees the
        // attach it has always seen, and a v2 daemon reads absence as shared.
        using var fake = FakeMuxServerEnd.Create();
        Task<MuxClient> connect = MuxClient.ConnectAsync(fake.ClientEnd, new MuxClientOptions(), Ct);
        await fake.AcceptHelloAsync(negotiated);
        using MuxClient client = await connect;
        MuxClientSession session = client.OpenSession(Guid.NewGuid());

        _ = session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct);
        MuxRequest request = await fake.ReadRequestAsync();

        Assert.Equal(MuxMethods.Attach, request.Method);
        System.Text.Json.JsonElement p = Assert.NotNull(request.Params);
        Assert.DoesNotContain(p.EnumerateObject(), m => string.Equals(m.Name, "mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_v2_client_refuses_a_non_shared_mode_on_a_v1_server_before_sending()
    {
        using var host = new MuxTestHost(V1Server);
        MuxClient client = await host.ConnectClientAsync();
        Assert.Equal(1, client.ProtocolVersion);
        Guid id = await MuxTestHost.SpawnAsync(client);
        using MuxClientSession session = client.OpenSession(id, "scripted", null, MuxAttachMode.IfUnattached);

        var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => session.AttachAsync(0, MuxTestHost.DefaultPresentation, Ct));

        Assert.Equal(MuxErrorCodes.VersionMismatch, ex.Code);
        await client.PingAsync(Ct);
        await host.Mux(id).InvokeAsync(() => 0);
        Assert.Equal(0, host.Mux(id).AttachedClients);   // nothing reached the daemon
        Assert.False(session.IsAttached);
    }

    [Fact]
    public async Task A_v2_client_on_a_v1_server_shares_and_gets_no_session_events()
    {
        using var host = new MuxTestHost(V1Server);
        MuxClient c1 = await host.ConnectClientAsync();
        MuxClient c2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c1);
        ClientPaneModel p1 = await MuxTestHost.AttachPaneAsync(c1, id);
        int changes = 0;
        p1.Session.SessionChanged += () => Interlocked.Increment(ref changes);
        await MuxTestHost.AttachPaneAsync(c2, id);
        await host.SettleAsync(id, c1, c2);

        Assert.False(p1.Session.SupportsSessionEvents);
        Assert.Null(p1.Session.AttachedClients);
        Assert.Equal(0, Volatile.Read(ref changes));
        Assert.Equal(2, await p1.Session.RefreshSharingAsync(Ct));   // listSessions has the count in v1 too
        Assert.Null(p1.Session.AttachedClients);                      // but nothing would keep a cached count current on v1

        await c2.KillAsync(id, Ct);
        await TestWait.UntilAsync(() => !p1.Session.IsProcessRunning, "c1 saw the exit");
        Assert.False(p1.Session.WasKilledElsewhere);                  // v1: a plain exit
    }

    [Fact]
    public async Task A_v1_client_on_a_v2_server_is_sent_no_new_notifications()
    {
        using var host = new MuxTestHost();
        MuxClient v2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(v2);
        RawMuxConnection v1 = host.ConnectRaw();
        Assert.Equal(1, (await v1.HelloAsync(min: 1, max: 1)).Version);
        long attach = v1.Request(MuxMethods.Attach, new AttachParams { SessionId = id, Presentation = MuxTestHost.DefaultPresentation }, MuxJsonContext.Default.AttachParams);

        ClientPaneModel neu = await MuxTestHost.AttachPaneAsync(v2, id);
        await TestWait.UntilAsync(() => neu.Session.AttachedClients == 2, "the v2 client heard the count, so the flush happened");
        long ping = v1.Request(MuxMethods.Ping, new MuxEmpty(), MuxJsonContext.Default.MuxEmpty);

        // Every frame the v1 connection got up to its pong, in order: none may be a notification.
        bool pong = false;
        while (!pong)
        {
            using MuxInboundFrame frame = await v1.ReadAsync() ?? throw new EndOfStreamException();
            Assert.NotEqual(MuxFrameKind.Notification, frame.Kind);
            if (frame.Kind == MuxFrameKind.Response)
            {
                pong = MuxFrames.ParseJson(frame.Payload, MuxJsonContext.Default.MuxResponse).Id == ping;
            }
        }

        Assert.True(attach > 0);
    }

    [Fact]
    public async Task UserDetached_reaches_a_v2_daemon()
    {
        using var host = new MuxTestHost();
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(c, id);

        pane.Session.Detach(userDetached: true);

        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the detach landed");
        Assert.True(host.Mux(id).DetachedByUser);
        Assert.False(pane.Session.IsAttached);
        pane.Session.Dispose();                                  // a no-op after Detach
    }

    [Fact]
    public async Task UserDetached_is_not_sent_to_a_v1_daemon()
    {
        // This server's code would honour the flag if it arrived; negotiating 1 must keep the client from sending it.
        using var host = new MuxTestHost(V1Server);
        MuxClient c = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(c);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(c, id);

        pane.Session.Detach(userDetached: true);

        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the detach landed");
        Assert.False(host.Mux(id).DetachedByUser);
    }
}
