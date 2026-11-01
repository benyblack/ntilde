namespace Ntilde.VT.Tests;

public sealed class Osc66BoundaryReviewTests
{
    private const string Wide = "\x1b]8;;https://example.test\a\x1b[31;44;4:3m界\u0301\x1b]8;;\a\x1b[0m";

    [Theory]
    [InlineData(4, "abcdef")]
    [InlineData(2, "abcdef")]
    [InlineData(4, "abc   ")]
    [InlineData(2, "abc   ")]
    public void Complete_and_incremental_relocation_preserve_wrap_metadata_and_cross_row_search(int rows, string prefix)
    {
        var complete = new TerminalBuffer(8, rows);
        var incremental = new TerminalBuffer(8, rows);
        new AnsiParser(complete).Process(prefix + "\x1b]66;s=2;❤\uFE0F\a");
        var parser = new AnsiParser(incremental);
        parser.Process(prefix + "\x1b]66;s=2;❤\a");
        parser.Process("\uFE0F");
        var expected = complete.ExportState(int.MaxValue);
        var actual = incremental.ExportState(int.MaxValue);
        Assert.Equal(expected.Main.RowWraps, actual.Main.RowWraps);
        Assert.Equal(expected.Main.RowWrapColumns, actual.Main.RowWrapColumns);
        Assert.Equal(expected.Scrollback.RowWraps, actual.Scrollback.RowWraps);
        Assert.Equal(expected.Scrollback.RowWrapColumns, actual.Scrollback.RowWrapColumns);
        // The vacated source row is padding after the authored prefix, including authored spaces.
        Assert.Equal(6, actual.ScrollbackRowCount > 0
            ? actual.Scrollback.RowWrittenColumns![0] : actual.Main.RowWrittenColumns![0]);
        Assert.Single(complete.FindMatches(prefix + "❤\uFE0F", false, true));
        Assert.Single(incremental.FindMatches(prefix + "❤\uFE0F", false, true));
        new TerminalBuffer(8, rows).ImportState(actual);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Attachment_growth_clears_ordinary_wide_glyphs_crossing_either_edge(bool history, bool leftEdge)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[?7l\x1b[1;4H" + Wide + "\x1b[2;4H" + Wide);
        int oldX = leftEdge ? 6 : 0;
        parser.Process($"\x1b[1;{oldX + 1}H\x1b]66;s=2;❤\a");
        if (history) parser.Process("\x1b[4;1H\n");
        parser.Process($"\x1b[1;{Math.Min(8, oldX + 3)}H\uFE0F");
        int outside = leftEdge ? 3 : 4;
        buffer.Lock.EnterReadLock();
        try
        {
            for (int row = 0; row < 2; row++)
            {
                var cell = buffer.GetCellAbsolute(outside, row);
                Assert.False(cell.IsWide);
                Assert.False(cell.IsWideContinuation);
                Assert.Equal(" ", buffer.GetGraphemeAbsolute(outside, row));
                Assert.Null(buffer.GetHyperlinkAbsolute(outside, row));
                Assert.Null(buffer.GetUnderlineAbsolute(outside, row));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        var state = buffer.ExportState(int.MaxValue);
        Assert.Equal(2, Assert.Single(state.TextSizing!).BaseWidth);
        new TerminalBuffer(8, 4).ImportState(state);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(3, 2, 4)]
    public void Initial_sized_placement_also_retires_an_ordinary_wide_glyph_at_its_edge(int wideX, int sizedX, int outside)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b[1;{wideX + 1}H" + Wide + $"\x1b[1;{sizedX + 1}H\x1b]66;s=2;M\a");
        buffer.Lock.EnterReadLock();
        try
        {
            var cell = buffer.GetCellAbsolute(outside, 0);
            Assert.False(cell.IsWide);
            Assert.False(cell.IsWideContinuation);
            Assert.Equal(" ", buffer.GetGraphemeAbsolute(outside, 0));
            Assert.Null(buffer.GetHyperlinkAbsolute(outside, 0));
            Assert.Null(buffer.GetUnderlineAbsolute(outside, 0));
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(8, 4).ImportState(buffer.ExportState(0));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Insert_mode_preserves_styled_linked_wide_glyphs_shifted_beyond_the_final_footprint(int height)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[1;4H" + Wide + "\x1b[2;4H" + Wide + "\x1b[1;3H\x1b[4h");
        parser.Process(height == 1 ? "\x1b]66;w=2;M\a" : "\x1b]66;s=2;M\a");
        buffer.Lock.EnterReadLock();
        try
        {
            for (int row = 0; row < height; row++)
            {
                Assert.Equal("界\u0301", buffer.GetGraphemeAbsolute(5, row));
                Assert.True(buffer.GetCellAbsolute(5, row).IsWide);
                Assert.True(buffer.GetCellAbsolute(6, row).IsWideContinuation);
                Assert.Equal("https://example.test", buffer.GetHyperlinkAbsolute(5, row));
                Assert.Equal("https://example.test", buffer.GetHyperlinkAbsolute(6, row));
                Assert.NotNull(buffer.GetUnderlineAbsolute(5, row));
                Assert.NotNull(buffer.GetUnderlineAbsolute(6, row));
                Assert.NotNull(buffer.GetTextSizingAbsolute(2, row));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(13)]
    public void Insert_mode_retires_ordinary_wide_glyphs_split_at_the_insertion_or_clipping_boundary(int wideX)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b[1;{wideX + 1}H" + Wide + "\x1b[1;3H\x1b[4h\x1b]66;s=2;M\a");
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < 16; col++)
            {
                Assert.False(buffer.GetCellAbsolute(col, 0).IsWide);
                Assert.False(buffer.GetCellAbsolute(col, 0).IsWideContinuation);
                Assert.Null(buffer.GetHyperlinkAbsolute(col, 0));
                Assert.Null(buffer.GetUnderlineAbsolute(col, 0));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(0));
    }

    [Fact]
    public void Insert_mode_preserves_an_intact_single_row_sized_block_shifted_beyond_the_new_one()
    {
        var buffer = new TerminalBuffer(16, 4);
        new AnsiParser(buffer).Process("\x1b[1;4H\x1b]66;w=2;AB\a\x1b[1;3H\x1b[4h\x1b]66;s=2;M\a");
        var state = buffer.ExportState(0);
        Assert.Equal(2, state.TextSizing!.Count);
        Assert.Contains(state.TextSizing, x => x.Text == "AB" && x.X == 5 && x.Y == 0);
        Assert.Contains(state.TextSizing, x => x.Text == "M" && x.X == 2 && x.Options.Scale == 2);
        new TerminalBuffer(16, 4).ImportState(state);
    }

    [Fact]
    public void Ordinary_insert_mode_output_also_preserves_a_single_row_sized_block()
    {
        var buffer = new TerminalBuffer(16, 4);
        new AnsiParser(buffer).Process("\x1b]66;w=2;AB\a\x1b[1;1H\x1b[4hx");
        var state = buffer.ExportState(0);
        var block = Assert.Single(state.TextSizing!);
        Assert.Equal("AB", block.Text);
        Assert.Equal(1, block.X);
        new TerminalBuffer(16, 4).ImportState(state);
    }

}
