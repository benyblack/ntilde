using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// Phase 4 spec §4 item 5: the mux parser's kitty keyboard flag is the AND over what the interactive
/// subscribers presented. A text client (which presents false) turns kitty off only while it is
/// attached; read-only observers take no part; with nobody interactive the last value stays.
/// </summary>
public sealed class HeadlessKittyPresentationTests
{
    private static readonly MuxPresentation Kitty = new() { Cols = 80, Rows = 24, CellWidthPx = 10, CellHeightPx = 20, KittyKeyboardEnabled = true };
    private static readonly MuxPresentation NoKitty = Kitty with { KittyKeyboardEnabled = false };

    private static (HeadlessTerminalSession Mux, ScriptedTerminalSession Fake) NewSession()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", string.Empty, string.Empty, 80, 24, null, false, null));
        var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "t",
            ForceConPtyFiltering = false,
        });
        return (mux, fake);
    }

    [Fact]
    public async Task Kitty_is_the_and_over_interactive_clients()
    {
        (HeadlessTerminalSession mux, _) = NewSession();
        using (mux)
        {
            var a = new RecordingFrameSink();
            var b = new RecordingFrameSink();
            var c = new RecordingFrameSink();

            mux.PostAttach(a, 1, 0, Kitty, MuxProtocol.MaxFrameBytes);
            Assert.True(await mux.KittyKeyboardEnabled);

            mux.PostAttach(b, 2, 0, NoKitty, MuxProtocol.MaxFrameBytes);
            Assert.False(await mux.KittyKeyboardEnabled); // one interactive client without kitty turns it off ...

            mux.PostDetach(b);
            Assert.True(await mux.KittyKeyboardEnabled); // ... only while it is attached

            mux.PostAttach(c, 3, 0, NoKitty, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
            Assert.True(await mux.KittyKeyboardEnabled); // a read-only observer takes no part

            // Nor does an observer's resize, should one reach the session (the connection drops it).
            mux.PostResize(c, 80, 24, NoKitty);
            Assert.True(await mux.KittyKeyboardEnabled);

            mux.PostResize(a, 80, 24, NoKitty); // a presentation-carrying resize replaces the client's own value
            Assert.False(await mux.KittyKeyboardEnabled);
        }
    }

    [Fact]
    public async Task With_no_interactive_client_the_last_value_stays()
    {
        (HeadlessTerminalSession mux, _) = NewSession();
        using (mux)
        {
            var a = new RecordingFrameSink();
            var b = new RecordingFrameSink();
            var c = new RecordingFrameSink();

            mux.PostAttach(a, 1, 0, NoKitty, MuxProtocol.MaxFrameBytes);
            Assert.False(await mux.KittyKeyboardEnabled);

            mux.PostDetach(a);
            Assert.False(await mux.KittyKeyboardEnabled); // nobody presents anything: the last value, not the default

            mux.PostAttach(c, 2, 0, Kitty, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
            Assert.False(await mux.KittyKeyboardEnabled); // an observer still decides nothing

            mux.PostAttach(b, 3, 0, Kitty, MuxProtocol.MaxFrameBytes);
            Assert.True(await mux.KittyKeyboardEnabled); // the next interactive client does
        }
    }

    [Fact]
    public async Task A_sink_dropped_for_refusing_a_frame_leaves_the_and()
    {
        (HeadlessTerminalSession mux, ScriptedTerminalSession fake) = NewSession();
        using (mux)
        {
            var gui = new RecordingFrameSink();
            var text = new RecordingFrameSink();
            mux.PostAttach(gui, 1, 0, Kitty, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(text, 2, 0, NoKitty, MuxProtocol.MaxFrameBytes);
            Assert.False(await mux.KittyKeyboardEnabled);

            text.Accept = false;
            fake.Emit("x"); // the broadcast drops the refusing sink
            await mux.FlushAsync();

            Assert.Equal(1, mux.AttachedClients);
            Assert.True(await mux.KittyKeyboardEnabled);
        }
    }

    [Fact]
    public async Task An_interactive_client_reattaching_read_only_leaves_the_and()
    {
        (HeadlessTerminalSession mux, _) = NewSession();
        using (mux)
        {
            var gui = new RecordingFrameSink();
            var text = new RecordingFrameSink();
            mux.PostAttach(gui, 1, 0, Kitty, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(text, 2, 0, NoKitty, MuxProtocol.MaxFrameBytes);
            Assert.False(await mux.KittyKeyboardEnabled);

            mux.PostAttach(text, 3, 0, NoKitty, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
            Assert.True(await mux.KittyKeyboardEnabled);
        }
    }

    [Fact]
    public async Task Coalesced_resizes_keep_every_clients_own_value()
    {
        // Resizes coalesce into one pending item (latest wins for the size and the session-wide
        // presentation), but each client's kitty value is its own: the last client's presentation
        // must not erase what an earlier one in the same burst presented.
        (HeadlessTerminalSession mux, _) = NewSession();
        using (mux)
        {
            var a = new RecordingFrameSink();
            var b = new RecordingFrameSink();
            mux.PostAttach(a, 1, 0, Kitty, MuxProtocol.MaxFrameBytes);
            mux.PostAttach(b, 2, 0, Kitty, MuxProtocol.MaxFrameBytes);
            Assert.True(await mux.KittyKeyboardEnabled);

            using var release = new ManualResetEventSlim();
            Task<int> parked = mux.InvokeAsync(() => { release.Wait(TimeSpan.FromSeconds(30)); return 0; });
            await TestWait.UntilAsync(() => mux.QueuedControlCount == 0, "the parse thread is parked inside the invoke");

            mux.PostResize(b, 90, 20, NoKitty with { Cols = 90, Rows = 20 });
            mux.PostResize(a, 100, 30, Kitty with { Cols = 100, Rows = 30 });
            Assert.Equal(1, mux.QueuedControlCount); // one coalesced item

            release.Set();
            await parked;

            Assert.False(await mux.KittyKeyboardEnabled);
            Assert.Equal((100, 30), (mux.Cols, mux.Rows));
        }
    }
}
