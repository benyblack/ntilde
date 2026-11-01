namespace Ntilde.VT.Tests;

public sealed class Osc66FlowTests
{
    private static CellTextSizing? Piece(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetTextSizingAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("s=0")]
    [InlineData("s=8")]
    [InlineData("w=8")]
    [InlineData("n=16")]
    [InlineData("d=16")]
    [InlineData("n=2:d=2")]
    [InlineData("v=3")]
    [InlineData("h=3")]
    [InlineData("s=-1")]
    [InlineData("s=2147483648")]
    [InlineData("broken")]
    public void Invalid_metadata_does_not_move_the_cursor(string metadata)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process($"\x1b]66;{metadata};X\a\x1b[6n");
        Assert.Equal("\x1b[1;1R", Assert.Single(replies));
        Assert.Null(Piece(buffer, 0, 0));
    }

    [Fact]
    public void Unknown_metadata_is_ignored_and_last_recognized_value_wins()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;future=value:s=3:s=2;X\a");
        Assert.Equal(2, Piece(buffer, 0, 0)!.Block.Height);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    [InlineData(0x85)]
    public void Malformed_or_control_payload_is_rejected_as_a_whole(int codeUnit)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        string text = "a" + new string((char)codeUnit, 1);
        parser.Process("\x1b]66;w=1;" + text + "\a");
        Assert.Null(Piece(buffer, 0, 0));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void Payload_limit_is_4096_utf8_bytes(int length, bool accepted)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;w=1;" + new string('a', length) + "\a");
        Assert.Equal(accepted, Piece(buffer, 0, 0) != null);
    }

    [Fact]
    public void Supplementary_text_is_counted_as_utf8_bytes()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;w=1;" + string.Concat(Enumerable.Repeat("😀", 1025)) + "\a");
        Assert.Null(Piece(buffer, 0, 0));
    }

    [Fact]
    public void Combining_text_updates_every_piece_without_changing_geometry_or_pen()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[31m\x1b]66;s=2;A\a\x1b[32m\u0301");
        var anchor = Piece(buffer, 0, 0)!;
        Assert.Equal("A\u0301", anchor.Block.Text);
        Assert.Equal(2, anchor.Block.Width);
        Assert.Same(anchor.Block, Piece(buffer, 1, 1)!.Block);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal(1, buffer.GetCellAbsolute(0, 0).FgIndex); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Line_insertion_preserves_contained_blocks_and_erases_split_blocks()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=2;A\a\x1b[4;1H\x1b]66;s=2;B\a\x1b[2;1H\x1b[L");
        Assert.Null(Piece(buffer, 0, 0)); // A straddles the insertion boundary.
        Assert.Equal("B", Piece(buffer, 0, 4)!.Block.Text);
        Assert.Equal(1, Piece(buffer, 0, 5)!.RowOffset);
    }

    [Fact]
    public void Line_insertion_erases_blocks_split_at_the_bottom()
    {
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[5;1H\x1b]66;s=2;A\a\x1b[1;1H\x1b[L");
        Assert.Null(Piece(buffer, 0, 5));
    }

    [Fact]
    public void Line_deletion_erases_intersected_blocks_and_moves_the_rest()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=2;A\a\x1b[5;1H\x1b]66;s=2;B\a\x1b[2;1H\x1b[M");
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Equal("B", Piece(buffer, 0, 3)!.Block.Text);
        Assert.Equal(1, Piece(buffer, 0, 4)!.RowOffset);
    }

    [Fact]
    public void Full_main_scroll_retains_a_block_across_the_history_boundary()
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=2;A\a\x1b[3;1H\n");
        Assert.Same(Piece(buffer, 0, 0)!.Block, Piece(buffer, 0, 1)!.Block);
        Assert.Equal(0, Piece(buffer, 0, 0)!.RowOffset);
        Assert.Equal(1, Piece(buffer, 0, 1)!.RowOffset);
    }

    [Fact]
    public void Scroll_region_does_not_leave_a_block_straddling_a_moving_boundary()
    {
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=2;A\a\x1b[2;5r\x1b[5;1H\n");
        Assert.Null(Piece(buffer, 0, 0));
        Assert.Null(Piece(buffer, 0, 1));
    }
}
