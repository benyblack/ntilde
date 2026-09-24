using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

public sealed class MuxServerLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_exited_unattached_session_is_reaped_after_the_grace_period()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Exit(0);
        await TestWait.UntilAsync(() => host.Mux(id).IsExited, "the mux saw the exit");

        Assert.NotEqual(0, host.Mux(id).ExitedAtMs);
        Assert.Equal(0, host.Server.ReapExitedSessions(TimeSpan.FromMinutes(5)));   // too young
        Assert.Equal(1, host.Server.ReapExitedSessions(TimeSpan.Zero));
        Assert.DoesNotContain(id, host.Server.GetSessionIds());
    }

    [Fact]
    public async Task A_running_session_is_never_reaped()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);

        Assert.Equal(0, host.Mux(id).ExitedAtMs);
        Assert.Equal(0, host.Server.ReapExitedSessions(TimeSpan.Zero));
        Assert.Contains(id, host.Server.GetSessionIds());
        Assert.Equal(1, host.Server.RunningSessionCount);
    }

    [Fact]
    public async Task An_exited_session_with_an_attached_client_is_kept()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Exit(3);
        await TestWait.UntilAsync(() => host.Mux(id).IsExited, "the mux saw the exit");

        Assert.Equal(0, host.Server.ReapExitedSessions(TimeSpan.Zero));
        pane.Session.Dispose(); // detach
        await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the detach landed");
        Assert.Equal(1, host.Server.ReapExitedSessions(TimeSpan.Zero));
    }

    [Fact]
    public async Task Idle_shutdown_is_refused_while_a_connection_or_running_session_exists()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Assert.False(host.Server.TryBeginIdleShutdown());            // a connection
        Guid id = await MuxTestHost.SpawnAsync(client);
        client.Dispose();
        await TestWait.UntilAsync(() => host.Server.ConnectionCount == 0, "the connection closed");
        Assert.False(host.Server.TryBeginIdleShutdown());            // a running session
        Assert.False(host.Server.IsAcceptingStopped);
        host.Server.KillAllSessions();
        Assert.DoesNotContain(id, host.Server.GetSessionIds());
        Assert.True(host.Server.TryBeginIdleShutdown());
        Assert.True(host.Server.IsAcceptingStopped);
    }

    [Fact]
    public async Task A_connection_after_idle_shutdown_began_is_refused_not_hung()
    {
        using var host = new MuxTestHost();
        Assert.True(host.Server.TryBeginIdleShutdown());
        // The in-memory listener is disposed by the shutdown; accept directly to model the race.
        (Stream clientEnd, Stream serverEnd) = Ntilde.Mux.Transport.InMemoryDuplexPipe.Create(1 << 16);
        host.Server.AcceptConnection(serverEnd);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            MuxClient.ConnectAsync(clientEnd, new MuxClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) }, Ct));
    }

    [Fact]
    public async Task Shutdown_replies_then_raises_ShutdownRequested()
    {
        using var host = new MuxTestHost();
        int raised = 0;
        host.Server.ShutdownRequested += () => Interlocked.Increment(ref raised);
        MuxClient client = await host.ConnectClientAsync();
        await client.ShutdownServerAsync(Ct);
        await TestWait.UntilAsync(() => Volatile.Read(ref raised) == 1, "ShutdownRequested fired");
    }

    [Fact]
    public async Task MuxClientSession_IsConnected_follows_the_client()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        MuxClientSession session = client.OpenSession(id);
        Assert.True(session.IsConnected);
        client.Dispose();
        Assert.False(session.IsConnected);
    }
}
