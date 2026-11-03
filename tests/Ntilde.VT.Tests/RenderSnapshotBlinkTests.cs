using Ntilde.VT;

namespace Ntilde.VT.Tests;

public sealed class RenderSnapshotBlinkTests
{
    private static TerminalRenderSnapshot Capture(TerminalBuffer buffer, bool isolated, int offset = 0)
        => buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = buffer.Rows,
            ViewportCols = buffer.Cols,
            Isolated = isolated,
            ScrollOffset = offset,
        }, out _);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlinkSummary_TracksCellReplacementAndHiddenFlags(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[5mA");
        using (var first = Capture(buffer, isolated))
        {
            Assert.True(first.RowsData.Array![0].ContainsBlink);
            Assert.False(first.RowsData.Array![1].ContainsBlink);
        }
        using (var unchanged = Capture(buffer, isolated)) Assert.True(unchanged.RowsData.Array![0].ContainsBlink);
        parser.Process("\r\x1b[8mA");
        using (var hidden = Capture(buffer, isolated)) Assert.False(hidden.RowsData.Array![0].ContainsBlink);
        parser.Process("\r\x1b[28mA");
        using (var shown = Capture(buffer, isolated)) Assert.True(shown.RowsData.Array![0].ContainsBlink);
        parser.Process("\r\x1b[25mA");
        using var replaced = Capture(buffer, isolated);
        Assert.False(replaced.RowsData.Array![0].ContainsBlink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlinkSummary_SurvivesPagedHistoryAndCacheReuse(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[5mA\x1b[0m\r\nB\r\nC\r\nD");
        Assert.True(buffer.Scrollback.Count > 0);
        using (var first = Capture(buffer, isolated, buffer.Scrollback.Count))
        {
            Assert.Equal('A', first.RowsData.Array![0].Cells[0].Character);
            Assert.True(first.RowsData.Array![0].ContainsBlink);
        }
        using var again = Capture(buffer, isolated, buffer.Scrollback.Count);
        Assert.True(again.RowsData.Array![0].ContainsBlink);
        Assert.False(again.RowsData.Array![1].ContainsBlink);
    }

    [Fact]
    public void BlinkSummary_DescribesTheAlternateScreenIndependently()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[5mA\x1b[0m\x1b[?1049h");
        using (var alternate = Capture(buffer, false)) Assert.False(alternate.RowsData.Array![0].ContainsBlink);
        parser.Process("\x1b[?1049l");
        using var main = Capture(buffer, false);
        Assert.True(main.RowsData.Array![0].ContainsBlink);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlinkSummary_ExcludesCellsOutsideTheRequestedColumns(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer).Process("\x1b[16G\x1b[5mA");
        using (var clipped = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = 3,
            ViewportCols = 10,
            Isolated = isolated,
        }, out _)) Assert.False(clipped.RowsData.Array![0].ContainsBlink);
        using var full = Capture(buffer, isolated);
        Assert.True(full.RowsData.Array![0].ContainsBlink);
    }
}
