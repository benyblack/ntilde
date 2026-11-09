using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>DECFRA (<c>CSI Pch;Pt;Pl;Pb;Pr $ x</c>) and DECCRA (<c>CSI Pts;Pls;Pbs;Prs;Pps;Ptd;Pld;Ppd $ v</c>) text contracts.</summary>
public sealed class RectangleCopyFillTests
{
    private const string Esc = "\u001b";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 8, int rows = 4, bool alternate = false)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        if (alternate) parser.Process($"{Esc}[?1049h");
        return (buffer, parser);
    }

    /// <summary>Seeds the rows with their own letter ('A', 'B', ...) so a moved row is recognizable, then parks the cursor and pen.</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser) Seeded(bool alternate = false, int cols = 8, int rows = 4)
    {
        var (buffer, parser) = Create(cols, rows, alternate);
        for (int row = 0; row < rows; row++) parser.Process($"{Esc}[{row + 1};1H" + new string((char)('A' + row), cols));
        parser.Process($"{Esc}[3;6H{Esc}[31;44;1;4m");
        return (buffer, parser);
    }

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

    // ------------------------------------------------------------------ DECFRA

    /// <summary>The pinned terminfo.dev 3x5 fixture: every target cell is X, the right flank survives, nothing else moves.</summary>
    [Fact]
    public void Fill_pinned_fixture_fills_three_by_five_and_keeps_the_flank()
    {
        var (buffer, parser) = Create(10, 5);
        parser.Process($"{Esc}[1;1HaaaaaZ{Esc}[2;1HbbbbbY{Esc}[3;1HcccccW{Esc}[1;1H{Esc}[88;1;1;3;5$x");
        Assert.Equal(new[] { "XXXXXZ    ", "XXXXXY    ", "XXXXXW    ", "          ", "          " }, Lines(buffer));
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>All four borders are inclusive and one-based: each border cell changes and its outer neighbour does not.</summary>
    [Fact]
    public void Fill_borders_are_inclusive_and_exact()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[42;2;3;3;6$x");
        Assert.Equal(new[] { "AAAAAAAA", "BB****BB", "CC****CC", "DDDDDDDD" }, Lines(buffer));
    }

    /// <summary>A single-cell rectangle fills exactly that cell.</summary>
    [Fact]
    public void Fill_single_cell_rectangle()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[35;2;2;2;2$x");
        Assert.Equal(new[] { "AAAAAAAA", "B#BBBBBB", "CCCCCCCC", "DDDDDDDD" }, Lines(buffer));
    }

    /// <summary>Omitted and zero borders cover the page; oversized borders clamp; the cursor is irrelevant.</summary>
    [Theory]
    [InlineData("88$x")]
    [InlineData("88;0;0;0;0$x")]
    [InlineData("88;;;;$x")]
    [InlineData("88;1;1;99999;99999$x")]
    public void Fill_default_and_oversized_borders_cover_the_page(string body)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{body}");
        Assert.All(Lines(buffer), line => Assert.Equal("XXXXXXXX", line));
        Assert.Equal((2, 5), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>An out-of-range or missing character code ignores the entire command (VT510 32-126 / 160-255).</summary>
    [Theory]
    [InlineData("$x")]
    [InlineData("0;1;1;4;8$x")]
    [InlineData("31;1;1;4;8$x")]
    [InlineData("127;1;1;4;8$x")]
    [InlineData("128;1;1;4;8$x")]
    [InlineData("159;1;1;4;8$x")]
    [InlineData("256;1;1;4;8$x")]
    [InlineData("99999;1;1;4;8$x")]
    public void Fill_invalid_character_codes_are_ignored(string body)
    {
        var (buffer, parser) = Seeded();
        var before = State(buffer);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(before, State(buffer));
    }

    /// <summary>The boundary codes 32, 126, 160 and 255 are accepted; the upper half is the Latin-1 right half.</summary>
    [Theory]
    [InlineData(32, ' ')]
    [InlineData(126, '~')]
    [InlineData(160, ' ')]
    [InlineData(255, 'ÿ')]
    public void Fill_boundary_character_codes_are_accepted(int code, char expected)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{code};1;1;1;2$x");
        Assert.Equal(expected, Cell(buffer, 0, 0).Character);
        Assert.Equal(expected, Cell(buffer, 1, 0).Character);
        Assert.Equal('A', Cell(buffer, 2, 0).Character);
    }

    /// <summary>The fill code is looked up in the GL set in use, so DEC special graphics maps like printed text.</summary>
    [Fact]
    public void Fill_character_follows_the_active_gl_set()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}(0{Esc}[113;1;1;1;3$x");
        Assert.Equal("───", string.Concat(Enumerable.Range(0, 3).Select(col => Cell(buffer, col, 0).Character)));
    }

    /// <summary>Reversed borders after clipping, extra parameters, subparameters and other identities change nothing.</summary>
    [Theory]
    [InlineData("88;3;1;2;4$x")]
    [InlineData("88;1;5;4;3$x")]
    [InlineData("88;1;1;4;8;1$x")]
    [InlineData("88;1:1;4;8$x")]
    [InlineData("?88;1;1;4;8$x")]
    [InlineData(">88;1;1;4;8$x")]
    [InlineData("88;1;1;4;8x")]
    [InlineData("88;1;1;4;8$$x")]
    [InlineData("88;1;1;4;8$!x")]
    [InlineData("88;1;1;4;8 x")]
    [InlineData("88;1;1;4;8\"x")]
    public void Fill_other_identities_and_invalid_rectangles_are_ignored(string body)
    {
        var (buffer, parser) = Seeded();
        var before = State(buffer);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(before, State(buffer));
    }

    /// <summary>Origin mode offsets rows by the top margin; margins themselves do not clip the rectangle.</summary>
    [Theory]
    [InlineData(false, "AAAAAAAA", "BBXXXXXX", "CCCCCCCC", "DDDDDDDD")]
    [InlineData(true, "AAAAAAAA", "BBBBBBBB", "CCXXXXXX", "DDXXXXXX")]
    public void Fill_origin_and_margins_follow_page_coordinates(bool origin, string a, string b, string c, string d)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;3r" + (origin ? $"{Esc}[?6h" : ""));
        parser.Process(origin ? $"{Esc}[88;2;3;99;99$x" : $"{Esc}[88;2;3;2;99$x");
        Assert.Equal(new[] { a, b, c, d }, Lines(buffer));
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    /// <summary>The fill carries the live pen: attributes, palette-index colors, literal colors and default flags; the pen is unchanged.</summary>
    [Fact]
    public void Fill_carries_the_live_pen_without_changing_it()
    {
        var (buffer, parser) = Seeded();
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[88;1;1;1;1$x{Esc}[38;2;1;2;3;48;2;4;5;6m{Esc}[88;2;1;2;1$x{Esc}[0;3m{Esc}[88;3;1;3;1$x");
        var indexed = Cell(buffer, 0, 0);
        Assert.Equal('X', indexed.Character);
        Assert.True(indexed.IsBold);
        Assert.True(indexed.IsUnderline);
        Assert.True(indexed.IsPaletteForeground && indexed.IsPaletteBackground);
        Assert.Equal((uint)1, indexed.Fg);
        Assert.Equal((uint)4, indexed.Bg);
        var literal = Cell(buffer, 0, 1);
        Assert.False(literal.IsPaletteForeground);
        Assert.False(literal.IsPaletteBackground);
        Assert.Equal(0x010203u, literal.Fg & 0xFFFFFF);
        Assert.Equal(0x040506u, literal.Bg & 0xFFFFFF);
        var plain = Cell(buffer, 0, 2);
        Assert.True(plain.IsItalic);
        Assert.True(plain.IsDefaultForeground && plain.IsDefaultBackground);
        Assert.False(plain.IsBold);
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol, before.IsPendingWrap), (after.CursorRow, after.CursorCol, after.IsPendingWrap));
    }

    /// <summary>Filled cells replace stored extended text, hyperlinks and underline decoration; the pen's underline decoration is applied.</summary>
    [Fact]
    public void Fill_replaces_side_metadata_with_the_pen_decoration()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;1H{Esc}]8;;https://example.invalid/fra\a{Esc}[7;4:3;58:5:5mÁ{Esc}]8;;\a{Esc}[0m");
        Assert.True(Cell(buffer, 0, 0).HasExtendedText);
        parser.Process($"{Esc}[4:2;58:5:2m{Esc}[88;1;1;1;2$x");
        var filled = Cell(buffer, 0, 0);
        Assert.Equal('X', filled.Character);
        Assert.False(filled.HasExtendedText);
        Assert.False(filled.IsInverse);
        Assert.Null(Row(buffer, 0, r => r.GetExtendedText(0)));
        Assert.Null(Row(buffer, 0, r => r.GetHyperlink(0)));
        var decoration = Row(buffer, 0, r => r.GetUnderline(0));
        Assert.NotNull(decoration);
        Assert.Equal(decoration, Row(buffer, 0, r => r.GetUnderline(1)));
    }

    /// <summary>An open hyperlink is not an SGR rendition: filled cells carry none.</summary>
    [Fact]
    public void Fill_does_not_apply_an_open_hyperlink()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}]8;;https://example.invalid/open\a{Esc}[88;1;1;1;3$x");
        Assert.Null(Row(buffer, 0, r => r.GetHyperlink(0)));
        Assert.Equal('X', Cell(buffer, 0, 0).Character);
    }

    /// <summary>Pending wrap, row continuation and the other screen bank are untouched in both banks.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fill_preserves_cursor_pending_wrap_and_the_hidden_bank(bool alternate)
    {
        var (buffer, parser) = Create(8, 4, alternate);
        parser.Process($"{Esc}[1;1H{new string('A', 8)}{new string('B', 8)}");
        parser.Process($"{Esc}[2;8HZ");
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[88;1;1;1;2$x");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol, before.IsPendingWrap), (after.CursorRow, after.CursorCol, after.IsPendingWrap));
        Assert.Equal(before.Scrollback.CellsBase64, after.Scrollback.CellsBase64);
        Assert.Equal(alternate ? before.Main.CellsBase64 : before.Alt.CellsBase64, alternate ? after.Main.CellsBase64 : after.Alt.CellsBase64);
        Assert.Equal('X', Cell(buffer, 0, 0).Character);
    }

    /// <summary>A rectangle edge splitting a wide glyph retires the orphaned half with the erase blank instead of leaving a split glyph.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Fill_does_not_leave_a_split_wide_glyph(int column)
    {
        var (buffer, parser) = Create();
        parser.Process("界X");
        parser.Process($"{Esc}[88;1;{column};1;{column}$x");
        var lead = Cell(buffer, 0, 0);
        var continuation = Cell(buffer, 1, 0);
        Assert.False(lead.IsWide);
        Assert.False(continuation.IsWideContinuation);
        Assert.Equal(column == 1 ? 'X' : ' ', lead.Character);
        Assert.Equal(column == 2 ? 'X' : ' ', continuation.Character);
        Assert.Equal('X', Cell(buffer, 2, 0).Character);
    }

    // ------------------------------------------------------------------ DECCRA

    /// <summary>The pinned terminfo.dev 2x5 fixture: copy to row 5 column 10, source and flanks unchanged.</summary>
    [Fact]
    public void Copy_pinned_fixture_copies_two_by_five_and_keeps_the_source()
    {
        var (buffer, parser) = Create(14, 6);
        parser.Process($"{Esc}[1;1HABCDE.........{Esc}[2;1HFGHIJ.........{Esc}[3;1H..............{Esc}[4;1H..............{Esc}[5;1H.........12345{Esc}[6;1H.........67890");
        parser.Process($"{Esc}[1;1H{Esc}[1;1;2;5;1;5;10$v");
        Assert.Equal(new[]
        {
            "ABCDE.........", "FGHIJ.........", "..............", "..............", ".........ABCDE", ".........FGHIJ",
        }, Lines(buffer));
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Inclusive source borders and an exact destination origin; rows and columns outside both are untouched.</summary>
    [Fact]
    public void Copy_uses_inclusive_borders_and_exact_destination_origin()
    {
        var (buffer, parser) = Create(8, 4);
        parser.Process($"{Esc}[1;1H12345678{Esc}[2;1HABCDEFGH{Esc}[3;1Habcdefgh{Esc}[4;1H--------");
        parser.Process($"{Esc}[1;2;2;4;1;3;5$v");
        Assert.Equal(new[] { "12345678", "ABCDEFGH", "abcd234h", "----BCD-" }, Lines(buffer));
    }

    /// <summary>Forward and backward overlaps behave as if the whole source were read first (no smearing).</summary>
    [Theory]
    [InlineData("1;1;1;6;1;1;3", "ABABCDEF")]   // right-shift by two: incremental left-to-right copying would smear AB
    [InlineData("1;3;1;8;1;1;1", "CDEFGHGH")]   // left-shift by two: incremental right-to-left copying would smear GH
    public void Copy_overlapping_columns_does_not_smear(string args, string expected)
    {
        var (buffer, parser) = Create(8, 2);
        parser.Process($"{Esc}[1;1HABCDEFGH");
        parser.Process($"{Esc}[{args}$v");
        Assert.Equal(expected, Lines(buffer)[0]);
    }

    /// <summary>Vertical overlaps in both directions copy whole snapshot rows.</summary>
    [Theory]
    [InlineData("1;1;3;8;1;2;1", new[] { "AAAAAAAA", "AAAAAAAA", "BBBBBBBB", "CCCCCCCC" })]
    [InlineData("2;1;4;8;1;1;1", new[] { "BBBBBBBB", "CCCCCCCC", "DDDDDDDD", "DDDDDDDD" })]
    public void Copy_overlapping_rows_does_not_smear(string args, string[] expected)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{args}$v");
        Assert.Equal(expected, Lines(buffer));
    }

    /// <summary>A diagonal overlap (down-right) is also read completely first.</summary>
    [Fact]
    public void Copy_overlapping_diagonal_does_not_smear()
    {
        var (buffer, parser) = Create(4, 3);
        parser.Process($"{Esc}[1;1H1234{Esc}[2;1H5678{Esc}[3;1H9ABC");
        parser.Process($"{Esc}[1;1;2;3;1;2;2$v");
        Assert.Equal(new[] { "1234", "5123", "9567" }, Lines(buffer));
    }

    /// <summary>A destination partly past the right or bottom edge is clipped; nothing wraps or scrolls.</summary>
    [Fact]
    public void Copy_clips_the_destination_to_the_page()
    {
        var (buffer, parser) = Create(6, 3);
        parser.Process($"{Esc}[1;1HABCDEF{Esc}[2;1HGHIJKL{Esc}[3;1HMNOPQR");
        parser.Process($"{Esc}[1;1;2;3;1;3;5$v");
        Assert.Equal(new[] { "ABCDEF", "GHIJKL", "MNOPAB" }, Lines(buffer));
        Assert.Equal(0, buffer.Scrollback.Count);
    }

    /// <summary>Attributes, palette-index/literal/default colors, extended text, hyperlink and underline decoration travel with the text.</summary>
    [Fact]
    public void Copy_preserves_all_cell_attributes()
    {
        var (buffer, parser) = Create(8, 3);
        parser.Process($"{Esc}[1;1H{Esc}]8;;https://example.invalid/cra\a{Esc}[1;3;4:3;58:5:5;31;44mA{Esc}[0m{Esc}]8;;\a" +
            $"{Esc}[38;2;1;2;3;48;2;4;5;6mB{Esc}[0mC{Esc}[1;4HÉ");
        parser.Process($"{Esc}[1;1;1;4;1;3;1$v");
        for (int col = 0; col < 4; col++)
        {
            var source = Cell(buffer, col, 0);
            var copy = Cell(buffer, col, 2);
            Assert.Equal(source.Character, copy.Character);
            Assert.Equal(source.Flags | (ushort)TerminalCellFlags.Dirty, copy.Flags | (ushort)TerminalCellFlags.Dirty);
            Assert.Equal((source.Fg, source.Bg), (copy.Fg, copy.Bg));
            Assert.Equal(Row(buffer, 0, r => r.GetExtendedText(col)), Row(buffer, 2, r => r.GetExtendedText(col)));
            Assert.Equal(Row(buffer, 0, r => r.GetHyperlink(col)), Row(buffer, 2, r => r.GetHyperlink(col)));
            Assert.Equal(Row(buffer, 0, r => r.GetUnderline(col)), Row(buffer, 2, r => r.GetUnderline(col)));
        }
        var a = Cell(buffer, 0, 2);
        Assert.True(a.IsBold && a.IsItalic && a.IsUnderline);
        Assert.NotNull(Row(buffer, 2, r => r.GetHyperlink(0)));
        Assert.NotNull(Row(buffer, 2, r => r.GetUnderline(0)));
        Assert.True(Cell(buffer, 3, 2).HasExtendedText);
    }

    /// <summary>The copy takes the source's attributes and never the live pen; the pen and cursor are unchanged.</summary>
    [Fact]
    public void Copy_does_not_apply_the_live_pen()
    {
        var (buffer, parser) = Seeded();
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[1;1;1;2;1;4;1$v");
        var copied = Cell(buffer, 0, 3);
        Assert.Equal('A', copied.Character);
        Assert.False(copied.IsBold);
        Assert.False(copied.IsUnderline);
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol, before.IsPendingWrap), (after.CursorRow, after.CursorCol, after.IsPendingWrap));
        Assert.Equal((before.Sgr.IsBold, before.Sgr.IsUnderline, before.Sgr.Foreground, before.Sgr.Background),
            (after.Sgr.IsBold, after.Sgr.IsUnderline, after.Sgr.Foreground, after.Sgr.Background));
    }

    /// <summary>Omitted and zero parameters default to the whole page copied onto itself (a visible no-op) without side effects.</summary>
    [Theory]
    [InlineData("$v")]
    [InlineData("0;0;0;0;0;0;0;0$v")]
    [InlineData("1;1;99;99;1;1;1;1$v")]
    public void Copy_defaults_copy_the_page_onto_itself(string body)
    {
        var (buffer, parser) = Seeded();
        var lines = Lines(buffer);
        var cursor = (buffer.CursorRow, buffer.CursorCol);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(lines, Lines(buffer));
        Assert.Equal(cursor, (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Source and destination page 0/1 are the only existing page; any other explicit page ignores the command.</summary>
    [Theory]
    [InlineData("1;1;2;8;1;3;1;1", true)]
    [InlineData("1;1;2;8;0;3;1;0", true)]
    [InlineData("1;1;2;8;2;3;1;1", false)]
    [InlineData("1;1;2;8;1;3;1;2", false)]
    [InlineData("1;1;2;8;9;3;1;9", false)]
    public void Copy_unsupported_pages_do_not_alias_the_active_page(string body, bool copies)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[{body}$v");
        Assert.Equal(copies ? new[] { "AAAAAAAA", "BBBBBBBB", "AAAAAAAA", "BBBBBBBB" } : new[] { "AAAAAAAA", "BBBBBBBB", "CCCCCCCC", "DDDDDDDD" }, Lines(buffer));
    }

    /// <summary>Reversed source borders, extra parameters, subparameters and other identities change nothing.</summary>
    [Theory]
    [InlineData("3;1;2;8;1;1;1;1$v")]
    [InlineData("1;5;4;3;1;1;1;1$v")]
    [InlineData("1;1;2;8;1;3;1;1;1$v")]
    [InlineData("1;1:1;2;8;1;3;1;1$v")]
    [InlineData("?1;1;2;8;1;3;1;1$v")]
    [InlineData(">1;1;2;8;1;3;1;1$v")]
    [InlineData("1;1;2;8;1;3;1;1v")]
    [InlineData("1;1;2;8;1;3;1;1$$v")]
    [InlineData("1;1;2;8;1;3;1;1$!v")]
    [InlineData("1;1;2;8;1;3;1;1 v")]
    public void Copy_other_identities_and_invalid_rectangles_are_ignored(string body)
    {
        var (buffer, parser) = Seeded();
        var before = State(buffer);
        parser.Process($"{Esc}[{body}");
        Assert.Equal(before, State(buffer));
    }

    /// <summary>Origin mode offsets both source and destination rows; margins do not clip.</summary>
    [Theory]
    [InlineData(false, "AAAAAAAA", "BBBBBBBB", "AAAAAAAA", "BBBBBBBB")]
    [InlineData(true, "AAAAAAAA", "BBBBBBBB", "CCCCCCCC", "BBBBBBBB")]
    public void Copy_origin_and_margins_follow_page_coordinates(bool origin, string a, string b, string c, string d)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;3r" + (origin ? $"{Esc}[?6h" : ""));
        parser.Process(origin ? $"{Esc}[1;1;1;8;1;3;1$v" : $"{Esc}[1;1;2;8;1;3;1$v");
        Assert.Equal(new[] { a, b, c, d }, Lines(buffer));
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    /// <summary>A destination origin beyond the page clamps to the last row/column per the DEC parameter rule, clipping the rest.</summary>
    [Fact]
    public void Copy_destination_origin_clamps_to_the_page()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;1;2;2;1;99;99$v");
        Assert.Equal(new[] { "AAAAAAAA", "BBBBBBBB", "CCCCCCCC", "DDDDDDDA" }, Lines(buffer));
    }

    /// <summary>A wide glyph cut by the source edge copies as a blank; a whole glyph copies whole with its extended text.</summary>
    [Fact]
    public void Copy_wide_glyphs_are_whole_or_blank()
    {
        var (buffer, parser) = Create(8, 3);
        parser.Process("界XY");
        parser.Process($"{Esc}[1;1;1;1;1;2;1$v{Esc}[1;2;1;3;1;2;5$v{Esc}[1;1;1;2;1;3;1$v");
        Assert.Equal(' ', Cell(buffer, 0, 1).Character);
        Assert.False(Cell(buffer, 0, 1).IsWide);
        Assert.Equal(' ', Cell(buffer, 4, 1).Character);
        Assert.False(Cell(buffer, 4, 1).IsWideContinuation);
        Assert.Equal('X', Cell(buffer, 5, 1).Character);
        Assert.True(Cell(buffer, 0, 2).IsWide);
        Assert.True(Cell(buffer, 1, 2).IsWideContinuation);
    }

    /// <summary>A destination edge inside an existing wide glyph retires the orphaned half.</summary>
    [Fact]
    public void Copy_does_not_split_a_destination_wide_glyph()
    {
        var (buffer, parser) = Create(8, 2);
        parser.Process($"{Esc}[2;1H界XY{Esc}[1;1HQ");
        parser.Process($"{Esc}[1;1;1;1;1;2;2$v");
        Assert.Equal('Q', Cell(buffer, 1, 1).Character);
        Assert.Equal(' ', Cell(buffer, 0, 1).Character);
        Assert.False(Cell(buffer, 0, 1).IsWide);
        Assert.False(Cell(buffer, 1, 1).IsWideContinuation);
    }

    /// <summary>The operations work in both screen banks and leave the other bank, history and the pen alone.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Copy_leaves_history_and_the_hidden_bank_alone(bool alternate)
    {
        var (buffer, parser) = Create(8, 4);
        for (int line = 0; line < 8; line++) parser.Process($"old{line}\r\n");
        if (alternate) parser.Process($"{Esc}[?1049h");
        for (int row = 0; row < 4; row++) parser.Process($"{Esc}[{row + 1};1H" + new string((char)('A' + row), 8));
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[1;1;1;8;1;4;1$v");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal(new[] { "AAAAAAAA", "BBBBBBBB", "CCCCCCCC", "AAAAAAAA" }, Lines(buffer));
        Assert.Equal(before.Scrollback.CellsBase64, after.Scrollback.CellsBase64);
        Assert.Equal(alternate ? before.Main.CellsBase64 : before.Alt.CellsBase64, alternate ? after.Main.CellsBase64 : after.Alt.CellsBase64);
        Assert.Equal((before.CursorRow, before.CursorCol), (after.CursorRow, after.CursorCol));
    }

    /// <summary>Repaint notifications follow synchronized output: nothing is raised until the frame ends.</summary>
    [Theory]
    [InlineData("88;1;1;2;2$x")]
    [InlineData("1;1;2;2;1;3;3$v")]
    public void Rectangle_operations_defer_repaint_during_synchronized_output(string body)
    {
        var (buffer, parser) = Seeded();
        int raised = 0;
        buffer.OnInvalidate += () => raised++;
        parser.Process($"{Esc}[?2026h");
        raised = 0;
        parser.Process($"{Esc}[{body}");
        Assert.True(buffer.IsSynchronizedOutput);
        Assert.Equal(0, raised);
        parser.Process($"{Esc}[?2026l");
        Assert.False(buffer.IsSynchronizedOutput);
        Assert.True(raised > 0);
    }

    // ------------------------------------------------------------------ Sequence-level behavior

    /// <summary>Split input and a stored-state round trip produce the same buffer as one chunk.</summary>
    [Fact]
    public void Rectangle_operations_are_independent_of_chunking()
    {
        string script = $"{Esc}[1;1HAAAAAAAA{Esc}[2;1HBBBBBBBB{Esc}[88;1;2;2;5$x{Esc}[1;1;2;4;1;3;3$v";
        var (whole, wholeParser) = Create(8, 4);
        wholeParser.Process(script);
        var (split, splitParser) = Create(8, 4);
        foreach (char c in script) splitParser.Process(c.ToString());
        Assert.Equal(State(whole), State(split));
    }

    /// <summary>A fill followed by an overlapping copy of the filled block matches a hand-computed model.</summary>
    [Fact]
    public void Fill_then_copy_matches_a_manual_model()
    {
        var (buffer, parser) = Create(6, 4);
        parser.Process($"{Esc}[1;1HabcdEF{Esc}[2;1HghijKL{Esc}[3;1HmnopQR{Esc}[4;1HstuvWX");
        parser.Process($"{Esc}[43;2;2;3;5$x");
        Assert.Equal(new[] { "abcdEF", "g++++L", "m++++R", "stuvWX" }, Lines(buffer));
        parser.Process($"{Esc}[2;2;3;5;1;1;1$v");
        Assert.Equal(new[] { "++++EF", "+++++L", "m++++R", "stuvWX" }, Lines(buffer));
    }
}
