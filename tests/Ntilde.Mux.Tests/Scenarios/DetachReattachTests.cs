using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Scenarios;

public sealed class DetachReattachTests
{
    [Fact]
    public async Task A_detached_session_keeps_running_and_a_reattach_converges()
    {
        using var host = new MuxTestHost();
        MuxClient client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        ClientPaneModel first = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("before detach\r\n");
        await host.SettleAsync(id, client);

        first.Session.Dispose();
        await host.SettleAsync(id, client);
        Assert.Equal(0, host.Mux(id).AttachedClients);
        host.Fake(id).Emit("\x1b[1mwhile detached\x1b[0m\r\n");
        await host.SettleAsync(id, client);
        Assert.True(host.Mux(id).IsExited is false);

        ClientPaneModel second = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("after reattach");
        await host.SettleAsync(id, client);

        host.AssertPaneMatchesMux(id, second, "reattached");
        Assert.DoesNotContain(first.Events, e => e.Contains("after reattach", StringComparison.Ordinal));
    }
}
