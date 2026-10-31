namespace Ntilde.VT.Tests;

public sealed class CursorStyleInvalidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void A_cursor_style_only_batch_invalidates_once(int style)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        int invalidations = 0;
        buffer.OnInvalidate += () => invalidations++;
        parser.Process($"\u001b[{style} q");
        Assert.Equal(1, invalidations);
        Assert.False(buffer.Modes.CursorStyleIsDefault);
    }

    [Fact]
    public void Multiple_cursor_style_changes_share_one_batch_invalidation()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        int invalidations = 0;
        buffer.OnInvalidate += () => invalidations++;
        parser.Process("\u001b[1 q\u001b[6 q");
        Assert.Equal(1, invalidations);
        Assert.Equal(CursorStyle.Beam, buffer.Modes.CursorStyle);
        Assert.False(buffer.Modes.IsCursorBlinkEnabled);
    }

    [Fact]
    public void An_unsupported_cursor_style_does_not_change_or_invalidate_the_cursor()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        int invalidations = 0;
        buffer.OnInvalidate += () => invalidations++;
        parser.Process("\u001b[7 q");
        Assert.Equal(0, invalidations);
        Assert.True(buffer.Modes.CursorStyleIsDefault);
    }
}
