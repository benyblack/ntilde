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
                buffer.Lock.EnterReadLock();
                try { Assert.Equal((short)1, buffer.GetCellAbsolute(col, row).FgIndex); }
                finally { buffer.Lock.ExitReadLock(); }
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

    [Theory]
    [InlineData(1, 12, 2, false)]
    [InlineData(1, 12, 2, true)]
    [InlineData(3, 12, 2, false)]
    [InlineData(3, 12, 2, true)]
    [InlineData(1, 4, 5, false)]
    [InlineData(1, 4, 5, true)]
    [InlineData(3, 4, 5, false)]
    [InlineData(3, 4, 5, true)]
    public void History_rectangles_that_cannot_fit_are_discarded_on_either_active_screen(
        int scrolled, int cols, int rows, bool alternate)
    {
        var buffer = new TerminalBuffer(12, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=3:w=2;AB\a\x1b[5;1H" + new string('\n', scrolled));
        if (alternate) parser.Process("\x1b[?47h");
        buffer.Resize(cols, rows);
        if (alternate) parser.Process("\x1b[?47l");
        var state = buffer.ExportState(int.MaxValue);
        Assert.Null(state.TextSizing);
        buffer.Lock.EnterReadLock();
        try
        {
            for (int row = 0; row < buffer.TotalLines; row++)
                for (int col = 0; col < cols; col++)
                {
                    Assert.Null(buffer.GetTextSizingAbsolute(col, row));
                    Assert.NotEqual("AB", buffer.GetGraphemeAbsolute(col, row));
                }
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(cols, rows).ImportState(state);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Alternate_screen_resize_preserves_main_history_rectangles_that_still_fit(int scrolled)
    {
        var buffer = new TerminalBuffer(12, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=3:w=2;AB\a\x1b[5;1H" + new string('\n', scrolled) + "\x1b[?47h");
        buffer.Resize(12, 4);
        parser.Process("\x1b[?47l");
        var state = buffer.ExportState(int.MaxValue);
        Assert.Equal("AB", Assert.Single(state.TextSizing!).Text);
        new TerminalBuffer(12, 4).ImportState(state);
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
