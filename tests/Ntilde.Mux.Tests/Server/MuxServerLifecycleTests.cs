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
    public async Task Shutdown_reply_reaches_the_client_even_when_the_handler_tears_the_server_down()
    {
        // What the daemon host does on ShutdownRequested. It aborts every connection, which drops
        // any frame not yet written - so the reply must already be on the wire when the event fires.
        // Looped: the race is between the sender thread and the handler.
        for (int i = 0; i < 50; i++)
        {
            using var host = new MuxTestHost();
            host.Server.ShutdownRequested += () =>
            {
                host.Server.KillAllSessions();
                host.Server.Dispose();
            };
            MuxClient client = await host.ConnectClientAsync();
            await MuxTestHost.SpawnAsync(client);

            await client.ShutdownServerAsync(Ct); // throws "connection closed" if the reply was dropped
            await TestWait.UntilAsync(() => !client.IsConnected, $"iteration {i}: the server closed the connection after replying");
        }
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

    /// <summary>
    /// PR #489 review 2, item 11: kill removed the session from the server but never disposed it,
    /// leaving its queues, token source and threads' resources to the finalizer. It must be
    /// disposed - and only after the terminal exit went out, so an attached client still sees Exited.
    /// </summary>
    [Fact]
    public async Task Kill_disposes_the_session_after_attached_clients_saw_the_exit()
    {
        using var host = new MuxTestHost();
        MuxClient killer = await host.ConnectClientAsync();
        MuxClient watcher = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(killer);
        ClientPaneModel pane = await MuxTestHost.AttachPaneAsync(watcher, id);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pane.Session.OnExit += _ => exited.TrySetResult();
        HeadlessTerminalSession session = host.Mux(id);

        await killer.KillAsync(id, Ct);

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await TestWait.UntilAsync(() => session.IsDisposedForTest, "the killed session was disposed");
        await TestWait.UntilAsync(() => !session.IsParseThreadAliveForTest && !session.IsInputThreadAliveForTest,
            "the killed session's parse and input threads stopped");
        Assert.DoesNotContain(id, host.Server.GetSessionIds());
    }
}
