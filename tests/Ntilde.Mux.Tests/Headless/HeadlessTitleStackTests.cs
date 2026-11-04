using Ntilde.Mux.Tests.Support;
using Ntilde.Pty;
using Ntilde.VT;

namespace Ntilde.Mux.Tests.Headless;

/// <summary>
/// XTWINOPS title stack across a mux attach (#830): the daemon's parser is authoritative for the
/// session title, and the attach snapshot carries the saved entries so the client's parser
/// restores the same raw title when the tail pops it.
/// </summary>
public sealed class HeadlessTitleStackTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public async Task Attach_snapshot_carries_the_title_stack_and_both_sides_restore_the_raw_title()
    {
        var fake = new ScriptedTerminalSession(new TerminalSessionRequest("scripted", "", "", 80, 24, null, false, null));
        using var mux = new HeadlessTerminalSession(Guid.NewGuid(), fake, new HeadlessSessionOptions
        {
            Command = "scripted",
            Title = "spawn title",
            ForceConPtyFiltering = false,
        });
        fake.Emit("\u001b]2;  shell: ~/Ω  \a\u001b[22;0t\u001b[?1049h\u001b]2;file.txt - VIM\a");
        await mux.FlushAsync();
        Assert.Equal("file.txt - VIM", mux.Title);

        TerminalStateSnapshot snapshot = await mux.InvokeAsync(() =>
            TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(mux.CaptureSnapshot(0))));
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        var titles = new List<string>();
        parser.OnTitleChanged = titles.Add;
        TerminalStateTransfer.Restore(buffer, parser, snapshot);
        Assert.Empty(titles);

        const string vimExit = "\u001b[?1049l\u001b[23;0t";
        parser.Process(vimExit);
        fake.Emit(vimExit);
        await mux.FlushAsync();

        Assert.Equal(new[] { "  shell: ~/Ω  " }, titles);
        Assert.Equal("  shell: ~/Ω  ", mux.Title);
        string daemonState = await mux.InvokeAsync(() => mux.Parser.ExportState().ToDebugString());
        Assert.Equal(daemonState, parser.ExportState().ToDebugString());
    }
}
