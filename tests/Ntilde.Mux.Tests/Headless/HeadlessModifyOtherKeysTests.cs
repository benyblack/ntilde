using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// Issue #829: a persistent session's modifyOtherKeys levels reach an attaching GUI through the
/// state snapshot, keep following screen switches afterwards, survive detach/reattach, and the
/// shared session answers XTQMODKEYS from the daemon's authoritative parser.
/// </summary>
public sealed class HeadlessModifyOtherKeysTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Attach_and_reattach_carry_both_screen_levels(int version)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = version, MaxProtocolVersion = version });
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit("\u001b[>4;1m\u001b[?1049h\u001b[>4;2m");
        await host.Mux(id).FlushAsync();

        var pane = await MuxTestHost.AttachPaneAsync(client, id);
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "attach carried both modifyOtherKeys levels");
        Assert.Equal(2, pane.Buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal((1, 2), (pane.Buffer.Modes.ModifyOtherKeys.MainLevel, pane.Buffer.Modes.ModifyOtherKeys.AltLevel));

        host.Fake(id).Emit("\u001b[?1049l");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "the imported levels follow a later screen switch");
        Assert.Equal(1, pane.Buffer.Modes.ModifyOtherKeys.Level);

        pane.Session.Detach(userDetached: false);
        await client.PingAsync(TestContext.Current.CancellationToken);
        host.Fake(id).Emit("\u001b[>4m\u001b[?1049h");
        await host.Mux(id).FlushAsync();

        var again = await MuxTestHost.AttachPaneAsync(client, id);
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, again, "reattach carried the reset main level and the retained alternate level");
        Assert.Equal(2, again.Buffer.Modes.ModifyOtherKeys.Level);
        Assert.Equal((0, 2), (again.Buffer.Modes.ModifyOtherKeys.MainLevel, again.Buffer.Modes.ModifyOtherKeys.AltLevel));

        host.Fake(id).Emit("\u001b[!p");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, again, "DECSTR resets both levels on client and daemon");
        Assert.Equal(0, again.Buffer.Modes.ModifyOtherKeys.Level);
    }

    [Fact]
    public async Task Query_is_answered_once_by_the_daemon()
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync();
        Guid id = await MuxTestHost.SpawnAsync(client);
        var pane = await MuxTestHost.AttachPaneAsync(client, id);
        host.Fake(id).Emit("\u001b[>4;2m\u001b[?4m");
        await host.SettleAsync(id, client);
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the XTQMODKEYS reply reached the child");
        Assert.Equal("\u001b[>4;2m", Assert.Single(host.Fake(id).SentInput));
        Assert.Equal(2, pane.Buffer.Modes.ModifyOtherKeys.Level);
    }
}
