using Ntilde.Mux;
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
}
