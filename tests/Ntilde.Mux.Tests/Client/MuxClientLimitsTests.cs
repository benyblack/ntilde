using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Client;

public sealed class MuxClientLimitsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(MuxTestHost Host, MuxClient Client, Guid Id, ClientPaneModel Pane)> SetupAsync(
        MuxServerOptions? server = null, MuxAttachLimits? limits = null)
    {
        var host = new MuxTestHost(server);
        MuxClient client = await host.ConnectClientAsync(new MuxClientOptions { AttachLimits = limits ?? new MuxAttachLimits() });
        Guid id = await MuxTestHost.SpawnAsync(client);
        var pane = new ClientPaneModel(client.OpenSession(id, "scripted"));
        return (host, client, id, pane);
    }

    [Fact]
    public async Task The_server_refuses_a_snapshot_over_its_ceiling()
    {
        (MuxTestHost host, _, _, ClientPaneModel pane) = await SetupAsync(server: new MuxServerOptions { MaxSnapshotBytes = 1024, ForceConPtyFiltering = false });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_client_refuses_a_snapshot_over_its_byte_ceiling_and_detaches()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxSnapshotBytes = 1024 });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
            await TestWait.UntilAsync(() => host.Mux(id).AttachedClients == 0, "the refusing client is detached server-side");
        }
    }

    [Fact]
    public async Task The_client_refuses_too_many_cells()
    {
        (MuxTestHost host, _, _, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxCells = (80 * 24) - 1 });
        using (host)
        {
            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(100, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_client_refuses_too_much_scrollback()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(limits: new MuxAttachLimits { MaxScrollbackRows = 10 });
        using (host)
        {
            host.Fake(id).Emit(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\r\n")));
            await host.Mux(id).FlushAsync();

            var ex = await Assert.ThrowsAsync<MuxProtocolException>(() => pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation, Ct));
            Assert.Equal(MuxErrorCodes.SnapshotTooLarge, ex.Code);
            Assert.Empty(pane.Events);
        }
    }

    [Fact]
    public async Task The_server_clamps_the_requested_scrollback()
    {
        (MuxTestHost host, _, Guid id, ClientPaneModel pane) = await SetupAsync(server: new MuxServerOptions { MaxAttachScrollbackRows = 5, ForceConPtyFiltering = false });
        using (host)
        {
            host.Fake(id).Emit(string.Concat(Enumerable.Range(0, 100).Select(i => $"line {i}\r\n")));
            await host.Mux(id).FlushAsync();

            await pane.Session.AttachAsync(1000, MuxTestHost.DefaultPresentation, Ct);

            Assert.Equal(5, pane.LastSnapshot!.ScrollbackRowCount);
        }
    }
}
