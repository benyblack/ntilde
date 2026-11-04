using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// Issue #829: a persistent session's modifyOtherKeys levels reach an attaching GUI through the
/// state snapshot, keep following screen switches afterwards, survive detach/reattach, and the
/// shared session answers XTQMODKEYS from the daemon's authoritative parser.
/// </summary>
public sealed class HeadlessModifyOtherKeysTests
{
    private static readonly MuxPresentation Gui = MuxTestHost.DefaultPresentation with { ModifyOtherKeysEnabled = true };

    // What `ntilde mux attach`'s text client presents: it forwards legacy key bytes only.
    private static readonly MuxPresentation TextClient = MuxTestHost.DefaultPresentation with
    {
        KittyKeyboardEnabled = false,
        ModifyOtherKeysEnabled = false,
    };

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
        var pane = await MuxTestHost.AttachPaneAsync(client, id, Gui);
        host.Fake(id).Emit("\u001b[>4;2m\u001b[?4m");
        await host.SettleAsync(id, client);
        await TestWait.UntilAsync(() => !host.Fake(id).SentInput.IsEmpty, "the XTQMODKEYS reply reached the child");
        Assert.Equal("\u001b[>4;2m", Assert.Single(host.Fake(id).SentInput));
        Assert.Equal(2, pane.Buffer.Modes.ModifyOtherKeys.Level);
    }

    [Fact]
    public async Task Query_reports_level_zero_while_an_interactive_client_cannot_encode_it()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            ForceConPtyFiltering = false,
        });
        var gui = new RecordingFrameSink();
        var text = new RecordingFrameSink();
        var observer = new RecordingFrameSink();

        mux.PostAttach(gui, 1, 0, Gui, MuxProtocol.MaxFrameBytes);
        Assert.True(await mux.ModifyOtherKeysEnabled);

        // The text client forwards its outer terminal's legacy bytes, so while it types the
        // child must not be told Ctrl+I will arrive as CSI 27;5;105~ (as kitty does).
        mux.PostAttach(text, 2, 0, TextClient, MuxProtocol.MaxFrameBytes);
        Assert.False(await mux.ModifyOtherKeysEnabled);
        fake.Emit("\u001b[>4;2m\u001b[?4m");
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => !fake.SentInput.IsEmpty, "the gated XTQMODKEYS reply reached the child");
        Assert.Equal("\u001b[>4;0m", Assert.Single(fake.SentInput));
        Assert.Equal(2, await mux.InvokeAsync(() => mux.Buffer.Modes.ModifyOtherKeys.Level)); // still negotiated

        mux.PostDetach(text);
        Assert.True(await mux.ModifyOtherKeysEnabled);

        // A presentation without the field (an older GUI) cannot encode it either; a read-only
        // observer takes no part.
        mux.PostAttach(observer, 3, 0, MuxTestHost.DefaultPresentation, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        Assert.True(await mux.ModifyOtherKeysEnabled);
        mux.PostResize(gui, 80, 24, MuxTestHost.DefaultPresentation);
        Assert.False(await mux.ModifyOtherKeysEnabled);
    }
}
