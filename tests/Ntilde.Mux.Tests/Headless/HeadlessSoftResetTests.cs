using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

public sealed class HeadlessSoftResetTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Attached_and_reattached_clients_keep_saved_cursor_and_soft_reset_parity(int version, bool alternate)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = version, MaxProtocolVersion = version });
        Guid id = await MuxTestHost.SpawnAsync(client);
        if (alternate) host.Fake(id).Emit("\u001b[?47h");
        host.Fake(id).Emit("\u001b[?6h\u001b[?7l\u001b(0\u001b[3;8H\u001b[31;1;4:3m\u001b[?1048h\u001b[?6l\u001b[?7h\u001b(B\u001b[1;1H\u001b[0m");
        await host.Mux(id).FlushAsync();
        var pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("\u001b[?1048lrestored");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "attach retained the active saved-cursor bank");
        Assert.Equal(2, pane.Buffer.CursorRow);
        Assert.True(pane.Buffer.IsBold);
        Assert.True(pane.Buffer.Modes.IsOriginMode);
        Assert.False(pane.Buffer.Modes.IsAutoWrapMode);
        pane.Session.Detach(userDetached: false);
        await client.PingAsync(TestContext.Current.CancellationToken);
        host.Fake(id).Emit("\u001b[2;20r\u001b[?6h\u001b[?7l\u001b[4h\u001b[!p");
        await host.Mux(id).FlushAsync();
        var again = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("\u001b[?1048lZ");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, again, "reattach retained the soft-reset cursor bank");
        Assert.Equal(alternate, again.Buffer.IsAltScreenActive);
        Assert.Equal((0, 1), (again.Buffer.CursorRow, again.Buffer.CursorCol));
        Assert.False(again.Buffer.IsBold);
        Assert.False(again.Buffer.Modes.IsOriginMode);
        Assert.True(again.Buffer.Modes.IsAutoWrapMode);
    }
}
