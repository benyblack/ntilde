using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class RenderSnapshotCursorPresentationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CursorModeFacts_AreCapturedTogetherAndRemainDetachedFromLaterModes(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[2 q\x1b[?25l");
        var request = new RenderSnapshotRequest { ViewportCols = 20, ViewportRows = 3, Isolated = isolated };
        using var first = buffer.CaptureRenderSnapshot(request, out _);
        Assert.False(first.CursorVisible);
        Assert.False(first.CursorBlinkEnabled);
        Assert.False(first.CursorStyleIsDefault);
        Assert.Equal(CursorStyle.Block, first.CursorStyle);
        parser.Process("\x1b[5 q\x1b[?25h");
        using var second = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(second.CursorVisible);
        Assert.True(second.CursorBlinkEnabled);
        Assert.Equal(CursorStyle.Beam, second.CursorStyle);
        Assert.False(first.CursorVisible);
        Assert.False(first.CursorBlinkEnabled);
        parser.Process("\x1b[!p");
        using var reset = buffer.CaptureRenderSnapshot(request, out _);
        Assert.True(reset.CursorVisible);
        Assert.True(reset.CursorStyleIsDefault);
    }
}
