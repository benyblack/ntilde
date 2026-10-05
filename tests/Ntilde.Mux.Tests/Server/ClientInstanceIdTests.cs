using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Server;

/// <summary>Spec §2.5: a hello's ClientInstanceId evicts the dead half-open twin of a reconnecting client.</summary>
public sealed class ClientInstanceIdTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(30);

    private static Task<string?> WhenDisconnected(MuxClient client)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += reason => tcs.TrySetResult(reason);
        if (!client.IsConnected) tcs.TrySetResult(client.DisconnectReason);
        return tcs.Task.WaitAsync(SafetyTimeout);
    }

    [Fact]
    public async Task A_reconnect_with_the_same_instance_id_evicts_the_dead_twin()
    {
        using var host = new MuxTestHost();
        MuxClient a = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = "x" });
        Guid id = await MuxTestHost.SpawnAsync(a);
        await MuxTestHost.AttachPaneAsync(a, id);
        Assert.Equal(1, host.Mux(id).AttachedClients);

        Task<string?> aDisconnected = WhenDisconnected(a);
        MuxClient b = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = "x" });

        await aDisconnected;
        Assert.False(a.IsConnected);
        Assert.True(b.IsConnected);

        await host.SettleAsync(id, b);
        Assert.Equal(0, host.Mux(id).AttachedClients); // B has not attached; A's sink is gone
        Assert.False(host.Mux(id).IsExited);           // the session itself kept running

        await MuxTestHost.AttachPaneAsync(b, id);
        Assert.Equal(1, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task Different_instance_ids_are_independent()
    {
        using var host = new MuxTestHost();
        MuxClient a = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = "x" });
        Guid id = await MuxTestHost.SpawnAsync(a);
        await MuxTestHost.AttachPaneAsync(a, id);

        MuxClient b = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = "y" });
        await MuxTestHost.AttachPaneAsync(b, id);
        await host.SettleAsync(id, a, b);

        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);
        Assert.Equal(2, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task No_instance_id_never_evicts()
    {
        using var host = new MuxTestHost();
        MuxClient a = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = "x" });
        MuxClient noId1 = await host.ConnectClientAsync();
        MuxClient noId2 = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(noId1);
        await MuxTestHost.AttachPaneAsync(noId1, id);
        await MuxTestHost.AttachPaneAsync(noId2, id);
        await host.SettleAsync(id, a, noId1, noId2);

        Assert.True(a.IsConnected);
        Assert.True(noId1.IsConnected);
        Assert.True(noId2.IsConnected);
        Assert.Equal(2, host.Mux(id).AttachedClients);
    }

    [Fact]
    public async Task An_overlong_instance_id_is_ignored_and_logged()
    {
        var log = new List<string>();
        using var host = new MuxTestHost(new MuxServerOptions { ForceConPtyFiltering = false, Log = m => { lock (log) log.Add(m); } });
        string longId = new('z', 65);
        MuxClient a = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = longId });
        MuxClient b = await host.ConnectClientAsync(new MuxClientOptions { ClientInstanceId = longId });
        await a.PingAsync(TestContext.Current.CancellationToken);
        await b.PingAsync(TestContext.Current.CancellationToken);

        Assert.True(a.IsConnected);
        Assert.True(b.IsConnected);
        lock (log) Assert.Equal(2, log.Count(m => m.Contains("ignoring a clientInstanceId")));
    }
}
