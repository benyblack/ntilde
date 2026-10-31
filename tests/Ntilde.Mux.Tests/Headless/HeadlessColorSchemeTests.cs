using Ntilde.Mux.Contracts;
using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>The authoritative parser reports the current shared presentation to the child exactly once.</summary>
public sealed class HeadlessColorSchemeTests
{
    [Fact]
    public async Task Presentation_updates_and_reattach_preserve_the_color_scheme_subscription()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", "", "", 80, 24, null, false, null));
        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "color scheme",
            ForceConPtyFiltering = false
        });
        var dark = new MuxPresentation { Cols = 80, Rows = 24, DefaultBg = TermColor.Black.ToUint() };
        var light = dark with { DefaultBg = TermColor.White.ToUint() };
        var interactive = new RecordingFrameSink();
        var observer = new RecordingFrameSink();
        mux.PostAttach(interactive, 1, 0, dark, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        fake.Emit("\u001b[?996n\u001b[?2031h");
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => !fake.SentInput.IsEmpty, "initial color query reached the child");
        Assert.Equal(new[] { "\u001b[?997;1n" }, fake.SentInput.ToArray());
        mux.PostResize(interactive, 80, 24, light);
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => fake.SentInput.Count >= 2, "light presentation reached the child");
        mux.PostResize(interactive, 80, 24, light);
        mux.PostAttach(observer, 2, 0, dark, MuxProtocol.MaxFrameBytes, MuxAttachMode.ReadOnly);
        await mux.FlushAsync();
        Assert.Equal(new[] { "\u001b[?997;1n", "\u001b[?997;2n" }, fake.SentInput.ToArray());
        mux.PostDetach(interactive);
        await mux.FlushAsync();
        mux.PostAttach(interactive, 3, 0, dark, MuxProtocol.MaxFrameBytes);
        await mux.FlushAsync();
        await TestWait.UntilAsync(() => fake.SentInput.Count >= 3, "reattached dark presentation reached the child");
        Assert.Equal(new[] { "\u001b[?997;1n", "\u001b[?997;2n", "\u001b[?997;1n" }, fake.SentInput.ToArray());
        var snapshot = await mux.InvokeAsync(() => mux.Parser.ExportState());
        Assert.True(snapshot.ColorSchemeReportsEnabled);
        fake.Emit("\u001b[?2031l");
        await mux.FlushAsync();
        mux.PostResize(interactive, 80, 24, light);
        await mux.FlushAsync();
        Assert.Equal(3, fake.SentInput.Count);
    }
}
