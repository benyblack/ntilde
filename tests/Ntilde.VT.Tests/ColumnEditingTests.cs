using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECIC (<c>CSI Pn ' }</c>), DECDC (<c>CSI Pn ' ~</c>), SL (<c>CSI Pn SP @</c>) and SR (<c>CSI Pn SP A</c>) stored-text contracts.
/// Each acts on every row of the top/bottom scrolling region and only while the cursor row is inside it; DECIC/DECDC start at
/// the cursor column, SL/SR at the left margin. Left/right margins (DECLRMM) are not implemented, so the horizontal margins are
/// the screen edges. The cursor, pen and pending wrap never move.
/// </summary>
public sealed class ColumnEditingTests
{
    private const string Esc = "\u001b";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 8, int rows = 4, bool alternate = false)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        if (alternate) parser.Process($"{Esc}[?1049h");
        return (buffer, parser);
    }

    /// <summary>Seeds row <c>r</c> with eight distinct letters so a shifted column is recognizable, then parks the cursor and pen.</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser) Seeded(bool alternate = false, int rows = 4)
    {
        var (buffer, parser) = Create(8, rows, alternate);
        for (int row = 0; row < rows; row++)
            parser.Process($"{Esc}[{row + 1};1H" + new string(Enumerable.Range(0, 8).Select(col => (char)('A' + (row * 8 + col) % 26)).ToArray()));
        parser.Process($"{Esc}[2;3H{Esc}[31;44;1;4m");
        return (buffer, parser);
    }

    private static readonly string[] Seed = ["ABCDEFGH", "IJKLMNOP", "QRSTUVWX", "YZABCDEF"];

    private static string[] Lines(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return Enumerable.Range(0, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                .Select(col => buffer.GetCell(col, row).Character).ToArray())).ToArray();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TerminalCell Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static T Row<T>(TerminalBuffer buffer, int row, Func<TerminalRow, T> read)
    {
        buffer.Lock.EnterReadLock();
        try { return read(buffer.GetRowAbsolute(buffer.Scrollback.Count + row)!); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static byte[] State(TerminalBuffer buffer) => TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));

    private static (int Row, int Col, bool PendingWrap) Cursor(TerminalBuffer buffer) => (buffer.CursorRow, buffer.CursorCol, buffer.IsPendingWrap);

    // ------------------------------------------------------------------ pinned terminfo.dev fixtures

    /// <summary>terminfo.dev editing.decic: 3x8 seed, cursor at row 2 column 3, two columns inserted on every region row.</summary>
    [Fact]
    public void Decic_pinned_fixture_inserts_two_blank_columns_at_column_three()
    {
        var (buffer, parser) = Create(8, 3);
        parser.Process($"{Esc}[1;1HABCDEFGH{Esc}[2;1HIJKLMNOP{Esc}[3;1HQRSTUVWX{Esc}[2;3H{Esc}[2'}}");
        Assert.Equal(new[] { "AB  CDEF", "IJ  KLMN", "QR  STUV" }, Lines(buffer));
        Assert.Equal((1, 2, false), Cursor(buffer));
    }

    /// <summary>terminfo.dev editing.decdc: 3x10 seed, cursor at row 2 column 3, two columns deleted, blanks enter at the right edge.</summary>
    [Fact]
    public void Decdc_pinned_fixture_deletes_two_columns_at_column_three()
    {
        var (buffer, parser) = Create(10, 3);
        parser.Process($"{Esc}[1;1HABCDEFGHIJ{Esc}[2;1HIJKLMNOPQR{Esc}[3;1HQRSTUVWXab{Esc}[2;3H{Esc}[2'~");
        Assert.Equal(new[] { "ABEFGHIJ  ", "IJMNOPQR  ", "QRUVWXab  " }, Lines(buffer));
        Assert.Equal((1, 2, false), Cursor(buffer));
    }

    /// <summary>terminfo.dev editing.sl: 2x11 seed, cursor home, every row shifts left by two from the left edge.</summary>
    [Fact]
    public void Sl_pinned_fixture_shifts_left_by_two()
    {
        var (buffer, parser) = Create(11, 2);
        parser.Process($"{Esc}[1;1HABCDEFGHIJK{Esc}[2;1HJKLMNOPQRST{Esc}[1;1H{Esc}[2 @");
        Assert.Equal(new[] { "CDEFGHIJK  ", "LMNOPQRST  " }, Lines(buffer));
        Assert.Equal((0, 0, false), Cursor(buffer));
    }

    /// <summary>terminfo.dev editing.sr: 2x9 seed, cursor home, every row shifts right by two; columns pushed past the edge are lost.</summary>
    [Fact]
    public void Sr_pinned_fixture_shifts_right_by_two()
    {
        var (buffer, parser) = Create(9, 2);
        parser.Process($"{Esc}[1;1HABCDEFGHI{Esc}[2;1HJKLMNOPQR{Esc}[1;1H{Esc}[2 A");
        Assert.Equal(new[] { "  ABCDEFG", "  JKLMNOP" }, Lines(buffer));
        Assert.Equal((0, 0, false), Cursor(buffer));
    }

    // ------------------------------------------------------------------ counts and defaults

    [Theory]
    [InlineData("'}", new[] { "ABCDEFGH", "IJ KLMNO", "QR STUVW", "YZ ABCDE" })]
    [InlineData("0'}", new[] { "ABCDEFGH", "IJ KLMNO", "QR STUVW", "YZ ABCDE" })]
    [InlineData("3'}", new[] { "ABCDEFGH", "IJ   KLM", "QR   STU", "YZ   ABC" })]
    [InlineData("6'}", new[] { "ABCDEFGH", "IJ      ", "QR      ", "YZ      " })]
    [InlineData("99999'}", new[] { "ABCDEFGH", "IJ      ", "QR      ", "YZ      " })]
    [InlineData("'~", new[] { "ABCDEFGH", "IJLMNOP ", "QRTUVWX ", "YZBCDEF " })]
    [InlineData("0'~", new[] { "ABCDEFGH", "IJLMNOP ", "QRTUVWX ", "YZBCDEF " })]
    [InlineData("3'~", new[] { "ABCDEFGH", "IJNOP   ", "QRVWX   ", "YZDEF   " })]
    [InlineData("6'~", new[] { "ABCDEFGH", "IJ      ", "QR      ", "YZ      " })]
    [InlineData("99999'~", new[] { "ABCDEFGH", "IJ      ", "QR      ", "YZ      " })]
    [InlineData(" @", new[] { "ABCDEFGH", "JKLMNOP ", "RSTUVWX ", "ZABCDEF " })]
    [InlineData("0 @", new[] { "ABCDEFGH", "JKLMNOP ", "RSTUVWX ", "ZABCDEF " })]
    [InlineData("8 @", new[] { "ABCDEFGH", "        ", "        ", "        " })]
    [InlineData("99999 @", new[] { "ABCDEFGH", "        ", "        ", "        " })]
    [InlineData(" A", new[] { "ABCDEFGH", " IJKLMNO", " QRSTUVW", " YZABCDE" })]
    [InlineData("0 A", new[] { "ABCDEFGH", " IJKLMNO", " QRSTUVW", " YZABCDE" })]
    [InlineData("8 A", new[] { "ABCDEFGH", "        ", "        ", "        " })]
    [InlineData("99999 A", new[] { "ABCDEFGH", "        ", "        ", "        " })]
    public void Omitted_zero_and_oversized_counts_inside_a_region(string body, string[] expected)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;4r{Esc}[2;3H{Esc}[{body}");
        Assert.Equal(expected, Lines(buffer));
        Assert.Equal((1, 2, false), Cursor(buffer));
    }

    // ------------------------------------------------------------------ scrolling region and cursor

    /// <summary>Only the rows of the top/bottom scrolling region move; rows above and below are untouched.</summary>
    [Theory]
    [InlineData("2'}", new[] { "ABCDEFGH", "IJ  KLMN", "QR  STUV", "YZABCDEF" })]
    [InlineData("2'~", new[] { "ABCDEFGH", "IJMNOP  ", "QRUVWX  ", "YZABCDEF" })]
    [InlineData("2 @", new[] { "ABCDEFGH", "KLMNOP  ", "STUVWX  ", "YZABCDEF" })]
    [InlineData("2 A", new[] { "ABCDEFGH", "  IJKLMN", "  QRSTUV", "YZABCDEF" })]
    public void Only_region_rows_move(string body, string[] expected)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;3r{Esc}[3;3H{Esc}[{body}");
        Assert.Equal(expected, Lines(buffer));
        Assert.Equal((2, 2, false), Cursor(buffer));
    }

    /// <summary>With the cursor row outside the scrolling region every operation is a complete no-op.</summary>
    [Theory]
    [InlineData("1;1H", "2'}")]
    [InlineData("4;3H", "2'~")]
    [InlineData("1;5H", "2 @")]
    [InlineData("4;1H", "2 A")]
    public void Cursor_outside_the_region_is_a_no_op(string position, string body)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;3r{Esc}[{position}");
        var before = State(buffer);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(before, State(buffer));
    }

    /// <summary>SL and SR start at the left margin whatever the cursor column is; DECIC/DECDC start at the cursor column.</summary>
    [Fact]
    public void Shift_left_and_right_ignore_the_cursor_column()
    {
        var (left, leftParser) = Seeded();
        leftParser.Process($"{Esc}[2;7H{Esc}[1 @");
        Assert.Equal(new[] { "BCDEFGH ", "JKLMNOP ", "RSTUVWX ", "ZABCDEF " }, Lines(left));
        Assert.Equal((1, 6, false), Cursor(left));

        var (right, rightParser) = Seeded();
        rightParser.Process($"{Esc}[2;7H{Esc}[1 A");
        Assert.Equal(new[] { " ABCDEFG", " IJKLMNO", " QRSTUVW", " YZABCDE" }, Lines(right));
        Assert.Equal((1, 6, false), Cursor(right));
    }

    /// <summary>At the first column DECIC shifts whole rows; at the last column DECIC/DECDC blank only that column.</summary>
    [Theory]
    [InlineData("2;1H", "1'}", new[] { " ABCDEFG", " IJKLMNO", " QRSTUVW", " YZABCDE" })]
    [InlineData("2;8H", "1'}", new[] { "ABCDEFG ", "IJKLMNO ", "QRSTUVW ", "YZABCDE " })]
    [InlineData("2;8H", "5'~", new[] { "ABCDEFG ", "IJKLMNO ", "QRSTUVW ", "YZABCDE " })]
    [InlineData("2;1H", "1'~", new[] { "BCDEFGH ", "JKLMNOP ", "RSTUVWX ", "ZABCDEF " })]
    public void Column_edits_at_the_page_edges(string position, string body, string[] expected)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{position}{Esc}[{body}");
        Assert.Equal(expected, Lines(buffer));
    }

    /// <summary>Origin mode does not change the region rows or the cursor-relative column.</summary>
    [Fact]
    public void Origin_mode_keeps_region_semantics()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;3r{Esc}[?6h{Esc}[1;3H{Esc}[2'}}");
        Assert.Equal(new[] { "ABCDEFGH", "IJ  KLMN", "QR  STUV", "YZABCDEF" }, Lines(buffer));
        Assert.Equal((1, 2, false), Cursor(buffer));
    }

    /// <summary>Pending wrap at the last column survives, and the cursor column is that last column.</summary>
    [Theory]
    [InlineData("1'}", "IJKLMNO ")]
    [InlineData("1'~", "IJKLMNO ")]
    [InlineData("1 @", "JKLMNOP ")]
    [InlineData("1 A", " IJKLMNO")]
    public void Pending_wrap_is_preserved(string body, string secondRow)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[0m{Esc}[2;1HIJKLMNOP");
        Assert.Equal((1, 7, true), Cursor(buffer));
        parser.Process($"{Esc}[{body}");
        Assert.Equal(secondRow, Lines(buffer)[1]);
        Assert.Equal((1, 7, true), Cursor(buffer));
    }

    // ------------------------------------------------------------------ attributes and side tables

    /// <summary>Introduced blanks carry the live colors (as ICH/DCH do) and no visual attributes; the pen itself is unchanged.</summary>
    [Theory]
    [InlineData("2'}", 2)]
    [InlineData("2'~", 6)]
    [InlineData("2 @", 6)]
    [InlineData("2 A", 0)]
    public void Introduced_blanks_have_live_colors_and_no_attributes(string body, int blankCol)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{body}");
        var blank = Cell(buffer, blankCol, 1);
        Assert.Equal(' ', blank.Character);
        Assert.Equal((short)1, blank.FgIndex);
        Assert.Equal((short)4, blank.BgIndex);
        Assert.False(blank.IsBold);
        Assert.False(blank.IsUnderline);
        Assert.Null(Row(buffer, 1, row => row.GetUnderline(blankCol)));
        Assert.Null(Row(buffer, 1, row => row.GetHyperlink(blankCol)));
        Assert.False(Row(buffer, 1, row => row.IsProtected(blankCol)));

        parser.Process($"{Esc}[1;1HZ");
        var written = Cell(buffer, 0, 0);
        Assert.True(written.IsBold);
        Assert.Equal((short)4, written.BgIndex);
    }

    /// <summary>Shifted cells keep their character, flags, literal/index colors, extended text, hyperlink, underline decoration and protection.</summary>
    [Theory]
    [InlineData("2'}", 3, 5)]
    [InlineData("2'~", 5, 3)]
    [InlineData("2 @", 5, 3)]
    [InlineData("2 A", 3, 5)]
    public void Shifted_cells_keep_attributes_and_side_tables(string body, int sourceCol, int destinationCol)
    {
        var (buffer, parser) = Create(8, 2);
        parser.Process($"{Esc}[1;1Habc{Esc}[1\"q{Esc}]8;;https://example.test{Esc}\\{Esc}[3;4:3;58;5;2;38;2;1;2;3;48;5;77m");
        parser.Process($"{Esc}[1;{sourceCol + 1}He\u0301{Esc}]8;;{Esc}\\{Esc}[0m{Esc}[0\"q");
        var source = Cell(buffer, sourceCol, 0);
        var underline = Row(buffer, 0, row => row.GetUnderline(sourceCol));
        Assert.NotNull(underline);
        Assert.True(Row(buffer, 0, row => row.IsProtected(sourceCol)));

        parser.Process($"{Esc}[1;3H{Esc}[{body}");

        var moved = Cell(buffer, destinationCol, 0);
        Assert.Equal(source.Character, moved.Character);
        Assert.Equal(source.Fg, moved.Fg);
        Assert.Equal(source.Bg, moved.Bg);
        Assert.True(moved.IsItalic);
        Assert.Equal((short)77, moved.BgIndex);
        Assert.Equal("e\u0301", Row(buffer, 0, row => row.GetExtendedText(destinationCol)));
        Assert.Equal("https://example.test", Row(buffer, 0, row => row.GetHyperlink(destinationCol))?.Uri);
        Assert.Equal(underline, Row(buffer, 0, row => row.GetUnderline(destinationCol)));
        Assert.True(Row(buffer, 0, row => row.IsProtected(destinationCol)));
        Assert.Null(Row(buffer, 0, row => row.GetExtendedText(sourceCol)));
        Assert.Null(Row(buffer, 0, row => row.GetHyperlink(sourceCol)));
        Assert.False(Row(buffer, 0, row => row.IsProtected(sourceCol)));
    }

    /// <summary>Columns pushed past the right edge take their side-table entries with them.</summary>
    [Fact]
    public void Columns_pushed_off_the_edge_drop_their_side_tables()
    {
        var (buffer, parser) = Create(4, 1);
        parser.Process($"{Esc}]8;;https://edge.test{Esc}\\abcd{Esc}]8;;{Esc}\\{Esc}[1;1H{Esc}[2 A");
        Assert.Equal(new[] { "  ab" }, Lines(buffer));
        Assert.Null(Row(buffer, 0, row => row.GetHyperlink(0)));
        Assert.Null(Row(buffer, 0, row => row.GetHyperlink(1)));
        Assert.Equal("https://edge.test", Row(buffer, 0, row => row.GetHyperlink(3))?.Uri);
        Assert.Equal(2, Row(buffer, 0, row => row.GetHyperlinkMap()?.Count ?? 0));
    }

    // ------------------------------------------------------------------ wide glyphs

    /// <summary>Inserting inside a wide glyph retires both halves; nothing is left as an orphan half.</summary>
    [Fact]
    public void Insert_inside_a_wide_glyph_retires_it()
    {
        var (buffer, parser) = Create(8, 1);
        parser.Process($"a\u4e2dbcd{Esc}[1;3H{Esc}[1'}}");
        Assert.Equal(new[] { "a   bcd " }, Lines(buffer));
        AssertNoOrphanHalves(buffer, 0);
    }

    /// <summary>Deleting the lead of a wide glyph retires its continuation instead of shifting it in as an orphan.</summary>
    [Fact]
    public void Delete_of_a_wide_lead_retires_the_continuation()
    {
        var (buffer, parser) = Create(8, 1);
        parser.Process($"ab\u4e2dcd{Esc}[1;2H{Esc}[2'~");
        Assert.Equal(new[] { "a cd    " }, Lines(buffer));
        AssertNoOrphanHalves(buffer, 0);
    }

    /// <summary>A wide glyph whose continuation is pushed past the right edge is retired.</summary>
    [Fact]
    public void Shift_right_cuts_a_wide_glyph_at_the_edge()
    {
        var (buffer, parser) = Create(6, 1);
        parser.Process($"abcd\u4e2d{Esc}[1 A");
        Assert.Equal(new[] { " abcd " }, Lines(buffer));
        AssertNoOrphanHalves(buffer, 0);
    }

    /// <summary>A whole wide glyph shifts intact.</summary>
    [Fact]
    public void Shift_left_moves_a_whole_wide_glyph()
    {
        var (buffer, parser) = Create(6, 1);
        parser.Process($"ab\u4e2dc{Esc}[2 @");
        Assert.True(Cell(buffer, 0, 0).IsWide);
        Assert.True(Cell(buffer, 1, 0).IsWideContinuation);
        Assert.Equal('c', Cell(buffer, 2, 0).Character);
        AssertNoOrphanHalves(buffer, 0);
    }

    private static void AssertNoOrphanHalves(TerminalBuffer buffer, int row)
    {
        for (int col = 0; col < buffer.Cols; col++)
        {
            var cell = Cell(buffer, col, row);
            if (cell.IsWide) Assert.True(col + 1 < buffer.Cols && Cell(buffer, col + 1, row).IsWideContinuation, $"orphan lead at {col}");
            if (cell.IsWideContinuation) Assert.True(col > 0 && Cell(buffer, col - 1, row).IsWide, $"orphan continuation at {col}");
        }
    }

    // ------------------------------------------------------------------ identities

    /// <summary>Only the exact unprefixed one-parameter identities are admitted; every other spelling leaves the state byte-identical.</summary>
    [Theory]
    [InlineData("2}")]
    [InlineData("2~")]
    [InlineData("?2'}")]
    [InlineData(">2'~")]
    [InlineData("2''}")]
    [InlineData("2 '~")]
    [InlineData("2:1'}")]
    [InlineData("2;1'~")]
    [InlineData("2;1 @")]
    [InlineData("2:1 A")]
    [InlineData("?2 @")]
    [InlineData("2  A")]
    [InlineData("2!@")]
    [InlineData("2$A")]
    public void Other_identities_are_ignored(string body)
    {
        var (buffer, parser) = Seeded();
        var before = State(buffer);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(before, State(buffer));
    }

    /// <summary>The bare finals keep their meanings: CUU and ICH are not column operations.</summary>
    [Fact]
    public void Bare_finals_keep_cuu_and_ich()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1A");
        Assert.Equal((0, 2, false), Cursor(buffer));
        parser.Process($"{Esc}[2;3H{Esc}[1@");
        Assert.Equal(new[] { "ABCDEFGH", "IJ KLMNO", "QRSTUVWX", "YZABCDEF" }, Lines(buffer));
    }

    // ------------------------------------------------------------------ screens, history and state

    /// <summary>Column operations act on the alternate screen and leave the main screen and its history untouched.</summary>
    [Fact]
    public void Alternate_screen_is_edited_in_place()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[?1049h");
        for (int row = 0; row < 4; row++) parser.Process($"{Esc}[{row + 1};1H" + Seed[row]);
        parser.Process($"{Esc}[2;3H{Esc}[2'}}");
        Assert.Equal(new[] { "AB  CDEF", "IJ  KLMN", "QR  STUV", "YZ  ABCD" }, Lines(buffer));
        parser.Process($"{Esc}[?1049l");
        Assert.Equal(Seed, Lines(buffer));
    }

    /// <summary>Scrollback is outside the page: a full-screen shift never edits history rows.</summary>
    [Fact]
    public void Scrollback_rows_are_not_edited()
    {
        var (buffer, parser) = Create(8, 2);
        parser.Process("ABCDEFGH\r\nIJKLMNOP\r\nQRSTUVWX");
        Assert.Equal(1, buffer.Scrollback.Count);
        parser.Process($"{Esc}[2 @");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.Equal("ABCDEFGH", new string(buffer.Scrollback.GetRow(0).ToArray().Select(cell => cell.Character).ToArray()));
        Assert.Equal(new[] { "KLMNOP  ", "STUVWX  " }, Lines(buffer));
    }

    /// <summary>The edited page round-trips through the full-state serializer.</summary>
    [Theory]
    [InlineData("2'}")]
    [InlineData("2'~")]
    [InlineData("2 @")]
    [InlineData("2 A")]
    public void Edited_state_round_trips(string body)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}]8;;https://x.test{Esc}\\{Esc}[1;5Hq{Esc}]8;;{Esc}\\{Esc}[2;3H{Esc}[{body}");
        var exported = buffer.ExportState(int.MaxValue);
        var restored = new TerminalBuffer(8, 4);
        restored.ImportState(TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(exported)));
        Assert.Equal(Lines(buffer), Lines(restored));
        Assert.Equal(State(buffer), State(restored));
    }

    /// <summary>Replaying the same bytes in arbitrary chunks reaches the same state as one write.</summary>
    [Fact]
    public void Chunked_input_matches_a_single_write()
    {
        string bytes = $"{Esc}[1;1HABCDEFGH{Esc}[2;1HIJKLMNOP{Esc}[2;3H{Esc}[2'}}{Esc}[1'~{Esc}[1 @{Esc}[3 A";
        var (whole, wholeParser) = Create(8, 2);
        wholeParser.Process(bytes);
        var (chunked, chunkedParser) = Create(8, 2);
        foreach (char ch in bytes) chunkedParser.Process(ch.ToString());
        Assert.Equal(State(whole), State(chunked));
        Assert.Equal(new[] { "   B CDE", "   J KLM" }, Lines(whole));
    }
}
