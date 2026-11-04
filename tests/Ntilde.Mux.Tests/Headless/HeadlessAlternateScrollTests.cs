using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// Issue #828: a mux client pane encodes the wheel from its own mirror of the session's modes.
/// The application's ?1007 choice reaches the mirror by attach snapshot and by live output; the
/// host default stays the client's own, so two clients with different settings can share a session.
/// </summary>
public sealed class HeadlessAlternateScrollTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Attach_and_live_output_carry_the_applications_choice_but_not_the_host_default(int version)
    {
        using var host = new MuxTestHost();
        using var client = await host.ConnectClientAsync(new MuxClientOptions { MinProtocolVersion = version, MaxProtocolVersion = version });
        Guid id = await MuxTestHost.SpawnAsync(client);
        host.Fake(id).Emit("\u001b[?1049h\u001b[?1007l");
        await host.Mux(id).FlushAsync();

        MuxClientSession session = client.OpenSession(id, "scripted");
        var pane = new ClientPaneModel(session);
        pane.Buffer.Modes.AlternateScrollDefault = false; // this client's setting
        await session.AttachAsync(10_000, MuxTestHost.DefaultPresentation, TestContext.Current.CancellationToken);
        await host.SettleAsync(id, client);

        host.AssertPaneMatchesMux(id, pane, "attach carried DECRST ?1007");
        Assert.False(pane.Buffer.Modes.AlternateScrollOverride);
        Assert.False(pane.Buffer.Modes.AlternateScrollDefault);

        host.Fake(id).Emit("\u001b[?1007h");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "live DECSET ?1007");
        Assert.True(pane.Buffer.Modes.IsAlternateScroll);

        // RIS hands each side back to its own default: the daemon's (on), this client's (off).
        host.Fake(id).Emit("\u001bc");
        await host.SettleAsync(id, client);
        host.AssertPaneMatchesMux(id, pane, "RIS cleared the override");
        Assert.Null(pane.Buffer.Modes.AlternateScrollOverride);
        Assert.False(pane.Buffer.Modes.IsAlternateScroll);
        Assert.True(await host.Mux(id).InvokeAsync(() => host.Mux(id).Buffer.Modes.IsAlternateScroll));
    }
}
