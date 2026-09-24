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
    public void A_failing_connect_returns_null_and_is_retried_next_time()
    {
        int attempts = 0;
        using var host = new MuxConnectionHost(_ => { Interlocked.Increment(ref attempts); throw new MuxUnavailableException("nope"); }, "test", null);
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Null(host.GetClient(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, attempts);
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
