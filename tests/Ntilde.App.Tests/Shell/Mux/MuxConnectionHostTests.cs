using Ntilde.Mux;
using Ntilde.Mux.Daemon;
using Ntilde.Mux.Tests.Support;
using Ntilde.Shell.Mux;

namespace Ntilde.Tests.Shell.Mux;

public sealed class MuxConnectionHostTests
{
    [Fact]
    public void Concurrent_GetClient_calls_spawn_one_daemon()
    {
        using var mux = new MuxTestHost();
        int connects = 0;
        using var host = new MuxConnectionHost(async ct =>
        {
            Interlocked.Increment(ref connects);
            await Task.Delay(200, ct);   // a slow daemon start
            return await MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct);
        }, endpoint: "test", log: null);

        MuxClient?[] clients = new MuxClient?[8];
        Parallel.For(0, clients.Length, i => clients[i] = host.GetClient(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, connects);
        Assert.NotNull(clients[0]);
        Assert.All(clients, c => Assert.Same(clients[0], c));
    }

    [Fact]
    public void A_disconnected_client_is_replaced_on_the_next_GetClient()
    {
        using var mux = new MuxTestHost();
        using var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null);
        MuxClient first = host.GetClient(TimeSpan.FromSeconds(5))!;
        first.Dispose();
        MuxClient second = host.GetClient(TimeSpan.FromSeconds(5))!;
        Assert.NotSame(first, second);
        Assert.True(second.IsConnected);
    }

    [Fact]
    public async Task A_failing_connect_returns_null_and_is_retried_only_after_the_cooldown()
    {
        int attempts = 0;
        using var host = new MuxConnectionHost(_ => { Interlocked.Increment(ref attempts); throw new MuxUnavailableException("nope"); }, "test", null)
        {
            FailureCooldown = TimeSpan.FromMilliseconds(200),
        };
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, Volatile.Read(ref attempts)); // still cooling down: no second attempt

        await Task.Delay(350, TestContext.Current.CancellationToken);
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Equal(2, host.ConnectAttempts);
    }

    [Fact]
    public async Task After_a_failed_connect_GetClient_returns_immediately_without_a_new_attempt()
    {
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var host = new MuxConnectionHost(_ => throw new MuxUnavailableException("daemon will not start"), "test", logs.Enqueue);
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, host.ConnectAttempts);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(5)));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"took {sw.Elapsed}");
        host.WarmUp(); // a no-op while cooling down
        Assert.Equal(1, host.ConnectAttempts);
        await TestWait.UntilAsync(() => logs.Any(l => l.Contains("daemon will not start", StringComparison.Ordinal)), "failure reason logged");
    }

    [Fact]
    public void A_GetClient_that_times_out_also_enters_the_cooldown()
    {
        using var host = new MuxConnectionHost(async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; }, "test", null);
        Assert.Null(host.GetClient(TimeSpan.FromMilliseconds(300)));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(5)));
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(100), $"took {sw.Elapsed}");
        Assert.Equal(1, host.ConnectAttempts);
    }

    [Fact]
    public async Task Dispose_flushes_frames_queued_before_it()
    {
        // A small pipe and a backlog of input ahead of the kill: the sender is still writing when
        // Dispose runs. Closing without the flush would drop the kill (MainWindow's last-tab close).
        using var mux = new MuxTestHost(pipeCapacityBytes: 16 * 1024);
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null);
        MuxClient client = host.GetClient(TimeSpan.FromSeconds(5))!;
        Guid id = await MuxTestHost.SpawnAsync(client);
        MuxClientSession session = client.OpenSession(id, "scripted");
        string chunk = new('x', 16 * 1024);
        for (int i = 0; i < 64; i++) session.SendInput(chunk);
        session.Kill();

        host.Dispose();

        await TestWait.UntilAsync(() => !mux.Server.GetSessionIds().Contains(id), "the kill queued before Dispose reached the daemon");
        Assert.DoesNotContain(id, mux.Server.GetSessionIds());
        Assert.Null(host.CurrentClient);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Dispose_is_idempotent_and_safe_without_a_client()
    {
        var host = new MuxConnectionHost(ct => Task.FromException<MuxClient>(new MuxUnavailableException("none")), "test", null);
        host.Dispose();
        host.Dispose();
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void GetClient_times_out_rather_than_blocking_forever()
    {
        using var host = new MuxConnectionHost(async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; }, "test", null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(host.GetClient(TimeSpan.FromMilliseconds(300)));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Dispose_waits_for_a_tracked_kill()
    {
        using var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null)
        {
            KillFlushTimeout = TimeSpan.FromSeconds(10),
            DisposeFlushTimeout = TimeSpan.Zero,
        };
        Assert.NotNull(host.GetClient(TimeSpan.FromSeconds(5)));
        var kill = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.TrackPendingKill(kill.Task);

        Task dispose = Task.Run(host.Dispose, TestContext.Current.CancellationToken);

        Assert.False(dispose.Wait(300, TestContext.Current.CancellationToken), "Dispose returned before the kill was confirmed");
        kill.SetResult();
        Assert.True(dispose.Wait(5_000, TestContext.Current.CancellationToken), "Dispose returned once the kill was confirmed");
    }

    [Fact]
    public void Dispose_gives_up_on_an_unconfirmed_kill_after_the_timeout()
    {
        using var mux = new MuxTestHost();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(mux.Listener.Connect(), null, ct), "test", null)
        {
            KillFlushTimeout = TimeSpan.FromMilliseconds(200),
        };
        Assert.NotNull(host.GetClient(TimeSpan.FromSeconds(5)));
        host.TrackPendingKill(new TaskCompletionSource().Task); // never completes

        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Dispose took {sw.Elapsed}");
    }

    /// <summary>
    /// Review fix (Task 4, round 1): one bounded wait, not two. Before the fix, a pending kill that
    /// never got a reply made Dispose wait KillFlushTimeout AND THEN the separate ping-flush wait
    /// (DisposeFlushTimeout) on top - up to ~4s total with the production defaults. A kill's own
    /// timeout already means the daemon is unresponsive, so the ping flush must be skipped entirely
    /// whenever there was a kill to wait on, whether it completed or timed out. The daemon answers the
    /// hello and nothing after it, so a ping flush could only run out its whole (long) timeout.
    /// </summary>
    [Fact]
    public void Dispose_does_not_also_run_the_ping_flush_after_waiting_on_a_kill()
    {
        using var daemon = FakeMuxServerEnd.Create();
        Task hello = daemon.AcceptHelloAsync();
        var host = new MuxConnectionHost(ct => MuxClient.ConnectAsync(daemon.ClientEnd, null, ct), "test", null)
        {
            KillFlushTimeout = TimeSpan.FromMilliseconds(300),
            DisposeFlushTimeout = TimeSpan.FromSeconds(10),
        };
        Assert.NotNull(host.GetClient(TimeSpan.FromSeconds(5)));
        Assert.True(hello.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); // the fake completes only after its welcome write returns
        host.TrackPendingKill(new TaskCompletionSource().Task); // an unresponsive daemon: never completes

        var sw = System.Diagnostics.Stopwatch.StartNew();
        host.Dispose();

        // At least 10.3 s (KillFlushTimeout + DisposeFlushTimeout) if the ping flush still ran after
        // the kill wait timed out; the margin to 5 s absorbs a slow CI machine.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {sw.Elapsed}");
    }
}
