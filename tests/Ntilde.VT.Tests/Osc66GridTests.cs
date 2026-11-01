namespace Ntilde.VT.Tests;

public sealed class Osc66GridTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 12, int rows = 6)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer));
    }

    private static CellTextSizing? Piece(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetTextSizingAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Text(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGraphemeAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Every_piece_shares_immutable_text_and_relative_geometry()
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b]66;s=2:w=2:n=1:d=2:v=1:h=2;ab\a");
        var anchor = Assert.IsType<CellTextSizing>(Piece(buffer, 0, 0));
        Assert.True(anchor.IsAnchor);
        Assert.Equal("ab", anchor.Block.Text);
        Assert.Equal(4, anchor.Block.Width);
        Assert.Equal(2, anchor.Block.Height);
        Assert.Equal(0.5f, anchor.Block.Options.Fraction);
        Assert.Equal(1, anchor.Block.Options.VerticalAlignment);
        Assert.Equal(2, anchor.Block.Options.HorizontalAlignment);
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 4; col++)
            {
                var part = Assert.IsType<CellTextSizing>(Piece(buffer, col, row));
                Assert.Same(anchor.Block, part.Block);
                Assert.Equal(col, part.ColumnOffset);
                Assert.Equal(row, part.RowOffset);
            }
        Assert.Null(Piece(buffer, 4, 0));
    }

    [Fact]
    public void Automatic_width_scales_individual_graphemes()
    {
        var (buffer, parser) = Create();
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("\x1b]66;s=2;A界\a\x1b[6n");
        Assert.Equal("\x1b[1;7R", Assert.Single(replies));
        Assert.Equal(2, Piece(buffer, 0, 0)!.Block.Width);
        Assert.Equal("界", Piece(buffer, 2, 0)!.Block.Text);
        Assert.Equal(4, Piece(buffer, 2, 0)!.Block.Width);
        Assert.Equal(3, Piece(buffer, 5, 1)!.ColumnOffset);
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(false, 4, 0)]
    public void Width_wraps_or_backs_up_to_fit_the_grid(bool wrap, int col, int row)
    {
        var (buffer, parser) = Create(cols: 8);
        parser.Process((wrap ? "" : "\x1b[?7l") + "\x1b[1;8H\x1b]66;s=2:w=2;X\a");
        Assert.True(Piece(buffer, col, row)!.IsAnchor);
        Assert.Equal(4, Piece(buffer, col, row)!.Block.Width);
    }

    [Theory]
    [InlineData("s=7", 6, 6)]
    [InlineData("s=2:w=7", 12, 6)]
    public void Blocks_larger_than_the_screen_are_ignored(string options, int cols, int rows)
    {
        var (buffer, parser) = Create(cols, rows);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process($"\x1b]66;{options};X\a\x1b[6n");
        Assert.Equal("\x1b[1;1R", Assert.Single(replies));
        Assert.Null(Piece(buffer, 0, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Ordinary_overwrite_of_the_top_row_erases_the_whole_block(int col)
    {
        var (buffer, parser) = Create();
        parser.Process($"\x1b]66;s=2;A\a\x1b[1;{col + 1}HB");
        Assert.Equal("B", Text(buffer, col, 0));
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Null(Piece(buffer, 1, 1));
        Assert.Equal(" ", Text(buffer, 1 - col, 0));
    }

    [Fact]
    public void Ordinary_text_skips_lower_pieces_even_with_wrapping_disabled()
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b]66;s=2;A\a\x1b[?7l\x1b[2;1HB");
        Assert.Equal("A", Text(buffer, 0, 0));
        Assert.Equal(" ", Text(buffer, 0, 1));
        Assert.Equal("B", Text(buffer, 2, 1));
        Assert.NotNull(Piece(buffer, 1, 1));
    }

    [Fact]
    public void A_wide_write_skips_lower_pieces_anywhere_in_its_target_span()
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b[1;2H\x1b]66;s=2;A\a\x1b[2;1H界");
        Assert.Equal("A", Text(buffer, 1, 0));
        Assert.Equal("界", Text(buffer, 3, 1));
        Assert.Equal(0, Piece(buffer, 1, 1)!.ColumnOffset);
    }

    [Theory]
    [InlineData("\x1b[X")]
    [InlineData("\x1b[K")]
    [InlineData("\x1b[1K")]
    [InlineData("\x1b[2K")]
    [InlineData("\x1b[J")]
    [InlineData("\x1b[1J")]
    [InlineData("\x1b[2J")]
    public void Erasing_any_piece_erases_the_complete_block(string erase)
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b]66;s=2;A\a\x1b[2;2H" + erase);
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Null(Piece(buffer, 1, 1));
        Assert.Equal(" ", Text(buffer, 0, 0));
    }

    [Fact]
    public void Erasing_a_visible_piece_also_clears_its_retained_history_anchor()
    {
        var (buffer, parser) = Create(rows: 3);
        parser.Process("\x1b]66;s=2;A\a\x1b[3;1H\n\x1b[1;2H\x1b[X");
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Null(Piece(buffer, 1, 1));
        Assert.Equal(" ", Text(buffer, 0, 0));
    }

    [Theory]
    [InlineData("\x1b[@")]
    [InlineData("\x1b[P")]
    public void Horizontal_edit_erases_multiline_blocks_in_the_shifted_region(string edit)
    {
        var (buffer, parser) = Create();
        parser.Process("\x1b[1;5H\x1b]66;s=2;A\a\x1b[1;1H" + edit);
        Assert.Null(Piece(buffer, 4, 0));
        Assert.Null(Piece(buffer, 5, 1));
    }
}
