namespace Ntilde.VT.Tests;

public sealed class Osc66ResizeTests
{
    private static CellTextSizing? Piece(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetTextSizingAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    public void Rectangles_keep_every_piece_and_style_when_the_new_width_fits(int width)
    {
        var buffer = new TerminalBuffer(12, 5);
        new AnsiParser(buffer).Process("\x1b[31m\x1b]66;s=3:w=2;AB\a");
        buffer.Resize(width, 5);
        var block = Piece(buffer, 0, 0)!.Block;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 6; col++)
            {
                var piece = Assert.IsType<CellTextSizing>(Piece(buffer, col, row));
                Assert.Same(block, piece.Block);
                Assert.Equal(row, piece.RowOffset);
                Assert.Equal(col, piece.ColumnOffset);
            }
        Assert.Equal("AB", block.Text);
        var state = buffer.ExportState(0);
        new TerminalBuffer(width, 5).ImportState(state);
    }

    [Theory]
    [InlineData(4, 5)]
    [InlineData(12, 2)]
    public void Rectangles_that_cannot_fit_are_removed_without_orphaned_text(int cols, int rows)
    {
        var buffer = new TerminalBuffer(12, 5);
        new AnsiParser(buffer).Process("\x1b]66;s=3:w=2;AB\a");
        buffer.Resize(cols, rows);
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Null(buffer.ExportState(int.MaxValue).TextSizing);
        Assert.DoesNotContain("AB", Ntilde.VT.Export.TerminalExporter.ExportToPlainText(buffer));
    }

    [Fact]
    public void Ordinary_lines_on_each_side_still_reflow()
    {
        var buffer = new TerminalBuffer(12, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("abcdefghijklmnop\r\n\x1b]66;s=2;A\a\x1b[5;1Hqrstuvwxyz123456");
        buffer.Resize(8, 8);
        var rows = Ntilde.VT.Export.TerminalExporter.GetVisibleRowTexts(buffer);
        Assert.Equal("abcdefgh", rows[0]);
        Assert.Equal("ijklmnop", rows[1]);
        Assert.Equal("A", Piece(buffer, 0, 2)!.Block.Text);
        Assert.Same(Piece(buffer, 0, 2)!.Block, Piece(buffer, 1, 3)!.Block);
        Assert.Contains("qrstuvwx", rows);
        Assert.Contains("yz123456", rows);
    }
}
