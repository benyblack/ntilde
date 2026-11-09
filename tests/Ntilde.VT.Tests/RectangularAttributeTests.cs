using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECCARA (CSI Pt;Pl;Pb;Pr;Ps $ r), DECRARA (CSI Pt;Pl;Pb;Pr;Ps $ t) and DECSACE (CSI Ps * x) over stored cells.
/// Contracts follow the VT510 reference: set/clear for DECCARA, toggle for DECRARA, stream (0/1, the default) or
/// rectangle (2) extent, page rather than margin coordinates, DECOM row origin, no cursor/pen/character change.
/// </summary>
public sealed class RectangularAttributeTests
{
    private const string Esc = "\u001b";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 8, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        for (int row = 0; row < rows; row++) parser.Process($"{Esc}[{row + 1};1H" + new string((char)('A' + row), cols));
        parser.Process($"{Esc}[3;6H{Esc}[31;44;1;4m");
        return (buffer, parser);
    }

    private static TerminalCell Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Lines(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return string.Join("|", Enumerable.Range(0, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                .Select(col => buffer.GetCell(col, row).Character).ToArray())));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>One character per cell: I inverse, B bold, U underline, K blink, '.' none; several letters mean several attributes.</summary>
    private static string Marks(TerminalBuffer buffer, int row, int cols)
    {
        var result = new System.Text.StringBuilder();
        for (int col = 0; col < cols; col++)
        {
            var cell = Cell(buffer, col, row);
            string mark = (cell.IsInverse ? "I" : "") + (cell.IsBold ? "B" : "") + (cell.IsUnderline ? "U" : "") + (cell.IsBlink ? "K" : "");
            result.Append(mark.Length == 0 ? "." : mark.Length == 1 ? mark : "[" + mark + "]");
        }
        return result.ToString();
    }

    private static string[] Screen(TerminalBuffer buffer) =>
        Enumerable.Range(0, buffer.Rows).Select(row => Marks(buffer, row, buffer.Cols)).ToArray();

    private static byte[] Bytes(TerminalBuffer buffer) => TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));

    /// <summary>The pinned editing.deccara fixture spelling, with the rectangle extent it depends on selected first.</summary>
    [Fact]
    public void Rectangle_extent_sets_inverse_on_exactly_the_requested_cells()
    {
        var (buffer, parser) = Create();
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[2*x{Esc}[2;3;3;6;7$r");
        Assert.Equal(new[] { "........", "..IIII..", "..IIII..", "........" }, Screen(buffer));
        Assert.Equal("AAAAAAAA|BBBBBBBB|CCCCCCCC|DDDDDDDD", Lines(buffer));
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol, before.IsPendingWrap), (after.CursorRow, after.CursorCol, after.IsPendingWrap));
        Assert.Equal((before.Sgr.IsBold, before.Sgr.IsUnderline, before.Sgr.IsInverse, before.Sgr.Foreground, before.Sgr.Background),
            (after.Sgr.IsBold, after.Sgr.IsUnderline, after.Sgr.IsInverse, after.Sgr.Foreground, after.Sgr.Background));
        Assert.True(buffer.IsBold);
        Assert.False(buffer.IsInverse);
    }

    /// <summary>The default extent is the stream: first row from Pl, whole middle rows, last row through Pr.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    public void Stream_extent_is_the_default_and_wraps_between_rows(string selector)
    {
        var (buffer, parser) = Create();
        if (selector.Length > 0) parser.Process($"{Esc}[{selector}*x");
        parser.Process($"{Esc}[1;3;3;5;7$r");
        Assert.Equal(new[] { "..IIIIII", "IIIIIIII", "IIIII...", "........" }, Screen(buffer));
    }

    /// <summary>Stream or rectangle, a left border past the right one is an invalid area and changes nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Left_border_past_the_right_border_is_invalid_in_both_extents(bool rectangle)
    {
        var (buffer, parser) = Create();
        if (rectangle) parser.Process($"{Esc}[2*x");
        var before = Bytes(buffer);
        parser.Process($"{Esc}[1;3;3;2;7$r{Esc}[1;3;3;2;7$t");
        Assert.Equal(before, Bytes(buffer));
    }

    /// <summary>A one-row stream is the Pl..Pr span, exactly as the rectangle is.</summary>
    [Fact]
    public void Single_row_stream_is_the_column_span()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;3;2;5;7$r");
        Assert.Equal("..III...", Marks(buffer, 1, 8));
    }

    /// <summary>Explicit 0 and 1 return to the stream after a rectangle; other values leave the setting unchanged.</summary>
    [Theory]
    [InlineData("\u001b[2*x\u001b[3*x", true)]
    [InlineData("\u001b[2*x\u001b[9*x", true)]
    [InlineData("\u001b[2*x\u001b[0*x", false)]
    [InlineData("\u001b[2*x\u001b[1*x", false)]
    [InlineData("\u001b[2*x\u001b[*x", false)]
    [InlineData("\u001b[1*x\u001b[2*x", true)]
    public void Extent_selection_accepts_only_defined_values(string selection, bool rectangle)
    {
        var (buffer, parser) = Create();
        parser.Process(selection + $"{Esc}[1;3;2;4;7$r");
        string[] expected = rectangle
            ? ["..II....", "..II....", "........", "........"]
            : ["..IIIIII", "IIII....", "........", "........"];
        Assert.Equal(expected, Screen(buffer));
    }

    /// <summary>Extent state is not protocol-visible except through DECCARA/DECRARA, and malformed selections change nothing.</summary>
    [Theory]
    [InlineData("\u001b[*x")]
    [InlineData("\u001b[2:1*x")]
    [InlineData("\u001b[2;2*x")]
    [InlineData("\u001b[?2*x")]
    [InlineData("\u001b[2x")]
    [InlineData("\u001b[2$x")]
    public void Malformed_extent_selection_leaves_the_stream_default(string sequence)
    {
        var (buffer, parser) = Create();
        parser.Process(sequence);
        parser.Process($"{Esc}[1;3;2;4;7$r");
        Assert.Equal(new[] { "..IIIIII", "IIII....", "........", "........" }, Screen(buffer));
    }

    /// <summary>Extent is independent of margins, origin mode, the pen and the cursor; it does not alter any of them.</summary>
    [Fact]
    public void Extent_selection_does_not_touch_other_state()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;3r{Esc}[?6h{Esc}[1;2H");
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[2*x");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol), (after.CursorRow, after.CursorCol));
        Assert.Equal((before.ScrollTop, before.ScrollBottom, before.Modes.IsOriginMode), (after.ScrollTop, after.ScrollBottom, after.Modes.IsOriginMode));
        Assert.Equal(before.Main.CellsBase64, after.Main.CellsBase64);
        Assert.Equal((before.Sgr.IsBold, before.Sgr.Foreground), (after.Sgr.IsBold, after.Sgr.Foreground));
    }

    /// <summary>RIS restores the stream; DECSTR leaves the setting (the VT510 soft-reset table does not list it).</summary>
    [Fact]
    public void Full_reset_restores_the_stream_but_soft_reset_keeps_the_selection()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[!p{Esc}[1;3;2;4;7$r");
        Assert.Equal("..II....", Marks(buffer, 0, 8));
        parser.Process($"{Esc}c");
        for (int row = 0; row < 4; row++) parser.Process($"{Esc}[{row + 1};1H" + new string((char)('A' + row), 8));
        parser.Process($"{Esc}[1;3;2;4;7$r");
        Assert.Equal(new[] { "..IIIIII", "IIII....", "........", "........" }, Screen(buffer));
    }

    /// <summary>The selected extent is stream state: it survives a full state export/import and a legacy payload reads as the stream.</summary>
    [Fact]
    public void Extent_selection_round_trips_through_state_transfer()
    {
        var (source, parser) = Create();
        parser.Process($"{Esc}[2*x");
        var state = source.ExportState(int.MaxValue);
        var target = new TerminalBuffer(8, 4);
        target.ImportState(state);
        var targetParser = new AnsiParser(target, forceConPtyFiltering: false);
        targetParser.Process($"{Esc}[1;3;2;4;7$r");
        Assert.Equal("..II....", Marks(target, 0, 8));
        Assert.Equal("..II....", Marks(target, 1, 8));

        var streamSource = Create().Buffer;
        var streamState = streamSource.ExportState(int.MaxValue);
        Assert.Null(streamState.Modes.RectangularAttributeExtent);
        var streamTarget = new TerminalBuffer(8, 4);
        streamTarget.ImportState(streamState);
        new AnsiParser(streamTarget, forceConPtyFiltering: false).Process($"{Esc}[1;3;2;4;7$r");
        Assert.Equal("..IIIIII", Marks(streamTarget, 0, 8));
    }

    /// <summary>DECCARA sets and clears exactly the named attributes and never replaces the whole attribute set.</summary>
    [Fact]
    public void Deccara_operands_set_and_clear_independently()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;8;1;5$r");
        Assert.Equal("[BK][BK][BK][BK][BK][BK][BK][BK]", Marks(buffer, 0, 8));
        parser.Process($"{Esc}[1;1;1;4;4;7$r");
        Assert.Equal("[IBUK][IBUK][IBUK][IBUK][BK][BK][BK][BK]", Marks(buffer, 0, 8));
        parser.Process($"{Esc}[1;1;1;2;22;24$r{Esc}[1;3;1;4;25;27$r");
        Assert.Equal("[IK][IK][BU][BU][BK][BK][BK][BK]", Marks(buffer, 0, 8));
        // 0 clears the four VT510 attributes and nothing else.
        parser.Process($"{Esc}[1;1;1;8;0$r");
        Assert.Equal("........", Marks(buffer, 0, 8));
    }

    /// <summary>An omitted selector list means selector 0, so an empty DECCARA clears the four attributes.</summary>
    [Theory]
    [InlineData("\u001b[1;1;1;4$r")]
    [InlineData("\u001b[1;1;1;4;0$r")]
    [InlineData("\u001b[1;1;1;4;$r")]
    public void Omitted_selector_clears_all_four_attributes(string clear)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;8;1;4;5;7$r" + clear);
        Assert.Equal("....[IBUK][IBUK][IBUK][IBUK]", Marks(buffer, 0, 8));
    }

    /// <summary>Unknown selectors are ignored while the rest of the list still applies; italics, faint, colors and strike survive 0.</summary>
    [Fact]
    public void Unknown_selectors_are_ignored_and_unrelated_attributes_survive()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;1H{Esc}[0;3;2;9;53;38;2;10;20;30;48;5;99mQ{Esc}[0m");
        var before = Cell(buffer, 0, 0);
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;1;1;99;8;3;7$r");
        var set = Cell(buffer, 0, 0);
        Assert.True(set.IsBold && set.IsInverse);
        Assert.Equal((before.IsItalic, before.IsFaint, before.IsStrikethrough, before.Fg, before.Bg, before.Character),
            (set.IsItalic, set.IsFaint, set.IsStrikethrough, set.Fg, set.Bg, set.Character));
        parser.Process($"{Esc}[1;1;1;1;0$r");
        var cleared = Cell(buffer, 0, 0);
        Assert.False(cleared.IsBold || cleared.IsInverse || cleared.IsUnderline || cleared.IsBlink);
        Assert.True(cleared.IsItalic && cleared.IsFaint && cleared.IsStrikethrough);
        Assert.Equal((before.Fg, before.Bg, before.Flags & (ushort)(TerminalCellFlags.PaletteBackground | TerminalCellFlags.DefaultForeground)),
            (cleared.Fg, cleared.Bg, cleared.Flags & (ushort)(TerminalCellFlags.PaletteBackground | TerminalCellFlags.DefaultForeground)));
    }

    /// <summary>Selectors 22 clears bold but not faint; 8 and 28 (an xterm extension) are not VT510 operands and change nothing.</summary>
    [Fact]
    public void Operand_22_clears_only_bold_and_extension_operands_are_unsupported()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;1H{Esc}[1;2mQ{Esc}[0m{Esc}[2*x{Esc}[1;1;1;1;22$r");
        var cell = Cell(buffer, 0, 0);
        Assert.False(cell.IsBold);
        Assert.True(cell.IsFaint);
        parser.Process($"{Esc}[1;1;1;1;8$r");
        Assert.False(Cell(buffer, 0, 0).IsHidden);
        parser.Process($"{Esc}[1;1;1;1;28$r");
        Assert.True(Cell(buffer, 0, 0).IsFaint);
    }

    /// <summary>Clearing underline drops its style and color but keeps overline; setting it keeps an existing style.</summary>
    [Fact]
    public void Underline_changes_keep_overline_and_existing_style()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;1H{Esc}[53;4:3;58:5:5mA{Esc}[0m{Esc}[1;2H{Esc}[53mB{Esc}[0m{Esc}[1;3H{Esc}[4:3;58:5:5mC{Esc}[0m");
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;3;24$r");
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count)!;
            Assert.False(row.Cells[0].IsUnderline);
            Assert.Equal(CellUnderline.OverlineOnly, row.GetUnderline(0));
            Assert.Equal(CellUnderline.OverlineOnly, row.GetUnderline(1));
            Assert.False(row.Cells[2].IsUnderline);
            Assert.Null(row.GetUnderline(2));
        }
        finally { buffer.Lock.ExitReadLock(); }
        parser.Process($"{Esc}[1;1;1;3;4$r");
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count)!;
            Assert.True(row.Cells[0].IsUnderline && row.Cells[1].IsUnderline && row.Cells[2].IsUnderline);
            Assert.Equal(CellUnderline.OverlineOnly, row.GetUnderline(0));
            Assert.Null(row.GetUnderline(2));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>The cells keep their stored underline style when only an unrelated attribute changes.</summary>
    [Fact]
    public void Unrelated_selectors_keep_underline_style_and_color()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;1H{Esc}[4:3;58:5:5mA{Esc}[0m{Esc}[2*x{Esc}[1;1;1;1;1;5;7$r");
        buffer.Lock.EnterReadLock();
        try
        {
            var underline = buffer.GetRowAbsolute(buffer.Scrollback.Count)!.GetUnderline(0);
            Assert.NotNull(underline);
            Assert.Equal(UnderlineStyle.Curly, underline!.Style);
            Assert.Equal((short)5, underline.ColorIndex);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>DECRARA toggles: set cells clear, clear cells set, and a second identical application restores the area.</summary>
    [Fact]
    public void Decrara_toggles_and_the_second_application_restores()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[1;1;2;4;7$r{Esc}[2;1;2;2;1$r");
        Assert.Equal("IIII....", Marks(buffer, 0, 8));
        Assert.Equal("[IB][IB]II....", Marks(buffer, 1, 8));
        var seeded = Bytes(buffer);
        parser.Process($"{Esc}[1;1;2;4;7$t");
        Assert.Equal("........", Marks(buffer, 0, 8));
        Assert.Equal("BB......", Marks(buffer, 1, 8));
        parser.Process($"{Esc}[1;1;2;4;7$t");
        Assert.Equal(seeded, Bytes(buffer));
        Assert.Equal("........", Marks(buffer, 2, 8));
    }

    /// <summary>Selector 0 (and the default) reverses all four attributes; selectors that are not DECRARA operands change nothing.</summary>
    [Fact]
    public void Decrara_zero_reverses_the_four_attributes_and_ignores_set_clear_selectors()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;2;1;4$r{Esc}[1;1;1;4;0$t");
        Assert.Equal("[IK][IK][IBUK][IBUK]....", Marks(buffer, 0, 8));
        parser.Process($"{Esc}[1;1;1;8;$t");
        Assert.Equal("[BU][BU]..[IBUK][IBUK][IBUK][IBUK]", Marks(buffer, 0, 8));
        var afterDefault = Bytes(buffer);
        parser.Process($"{Esc}[1;1;1;8;22;24;25;27;8;99$t");
        Assert.Equal(afterDefault, Bytes(buffer));
        parser.Process($"{Esc}[1;1;1;8$t");
        Assert.Equal("[IK][IK][IBUK][IBUK]....", Marks(buffer, 0, 8));
    }

    /// <summary>A repeated toggle cancels within one command (cumulative in sequence).</summary>
    [Fact]
    public void Decrara_repeated_selector_cancels()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;4;1;1;1$t");
        Assert.Equal("BBBB....", Marks(buffer, 0, 8));
        parser.Process($"{Esc}[1;1;1;4;1;1$t");
        Assert.Equal("BBBB....", Marks(buffer, 0, 8));
        parser.Process($"{Esc}[1;1;1;4;1;7;7$t");
        Assert.Equal("........", Marks(buffer, 0, 8));
    }

    /// <summary>DECRARA follows the same extent: stream mode reaches the end of the first row and the start of the last.</summary>
    [Fact]
    public void Decrara_follows_the_selected_extent()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;3;3;5;7$t");
        Assert.Equal(new[] { "..IIIIII", "IIIIIIII", "IIIII...", "........" }, Screen(buffer));
        var (rect, rectParser) = Create();
        rectParser.Process($"{Esc}[2*x{Esc}[1;2;3;4;7$t");
        Assert.Equal(new[] { ".III....", ".III....", ".III....", "........" }, Screen(rect));
    }

    /// <summary>Omitted and zero borders cover the active page, oversized borders clamp, and reversed borders are ignored.</summary>
    [Theory]
    [InlineData("\u001b[;;;;7$r", "IIIIIIII", "IIIIIIII", "IIIIIIII", "IIIIIIII")]
    [InlineData("\u001b[0;0;0;0;7$r", "IIIIIIII", "IIIIIIII", "IIIIIIII", "IIIIIIII")]
    [InlineData("\u001b[3;7;99999;99999;7$r", "........", "........", "......II", "......II")]
    [InlineData("\u001b[3;1;2;4;7$r", "........", "........", "........", "........")]
    [InlineData("\u001b[1;5;3;4;7$r", "........", "........", "........", "........")]
    public void Coordinates_default_clamp_and_reject_reversed_borders(string sequence, string a, string b, string c, string d)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x" + sequence);
        Assert.Equal(new[] { a, b, c, d }, Screen(buffer));
    }

    /// <summary>A bare DECCARA is selector 0 over the whole page: every attribute the page carries is cleared.</summary>
    [Theory]
    [InlineData("\u001b[$r")]
    [InlineData("\u001b[;;;;$r")]
    public void Bare_deccara_clears_the_page(string sequence)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2*x{Esc}[;;;;7$r" + sequence);
        Assert.Equal(new[] { "........", "........", "........", "........" }, Screen(buffer));
    }

    /// <summary>DECOM offsets rows by the top margin but margins never clip the rectangle; columns are page columns.</summary>
    [Theory]
    [InlineData(false, "........", "IIII....", "IIII....", "........")]
    [InlineData(true, "........", "........", "IIII....", "IIII....")]
    public void Origin_mode_offsets_rows_and_margins_do_not_clip(bool origin, string a, string b, string c, string d)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;3r" + (origin ? $"{Esc}[?6h" : "") + $"{Esc}[2*x");
        parser.Process(origin ? $"{Esc}[2;1;99;4;7$r" : $"{Esc}[2;1;3;4;7$r");
        Assert.Equal(new[] { a, b, c, d }, Screen(buffer));
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    /// <summary>Left/right margins do not clip either: the VT510 reference states the commands ignore page margins.</summary>
    [Fact]
    public void Left_right_margin_mode_does_not_clip()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[?69h{Esc}[3;5s{Esc}[2*x{Esc}[1;1;1;8;7$r");
        Assert.Equal("IIIIIIII", Marks(buffer, 0, 8));
    }

    /// <summary>Other identities, subparameters and excess-length forms never change a cell.</summary>
    [Theory]
    [InlineData("\u001b[1;1;4;8;7t")]
    [InlineData("\u001b[?1;1;4;8;7$r")]
    [InlineData("\u001b[>1;1;4;8;7$t")]
    [InlineData("\u001b[1;1;4;8;7$$r")]
    [InlineData("\u001b[1;1;4;8;7$$t")]
    [InlineData("\u001b[1;1;4;8;7 $r")]
    [InlineData("\u001b[1:1;1;4;8;7$r")]
    [InlineData("\u001b[1;1;4;8;7:1$t")]
    [InlineData("\u001b[1;1;4;8;7!r")]
    public void Other_identities_are_ignored(string sequence)
    {
        var (buffer, parser) = Create();
        var before = Bytes(buffer);
        parser.Process(sequence);
        Assert.Equal(before, Bytes(buffer));
    }

    /// <summary>DECCARA never aliases DECSTBM: the scroll region and home cursor stay put.</summary>
    [Fact]
    public void Deccara_is_not_a_scroll_region_command()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;3r{Esc}[3;5H");
        parser.Process($"{Esc}[1;1;2;2;7$r{Esc}[1;1;2;2;7$t");
        Assert.Equal((1, 2), (buffer.ScrollTop, buffer.ScrollBottom));
        Assert.Equal((2, 4), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Text, colors, hyperlinks, extended text and the pen are untouched; only the four attributes change.</summary>
    [Fact]
    public void Text_colors_and_side_tables_are_preserved()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;2H{Esc}]8;;https://example.invalid/a\a{Esc}[38;2;1;2;3;48;5;9mÁ{Esc}]8;;\a{Esc}[0m{Esc}[3;6H{Esc}[31;44;1;4m");
        var before = Cell(buffer, 1, 1);
        Assert.True(before.HasExtendedText);
        parser.Process($"{Esc}[2*x{Esc}[2;2;2;2;1;7$r{Esc}[2;2;2;2;7$t");
        var after = Cell(buffer, 1, 1);
        Assert.Equal(before.Flags & ~(ushort)(TerminalCellFlags.Dirty), after.Flags & ~(ushort)(TerminalCellFlags.Dirty));
        Assert.Equal((before.Character, before.Fg, before.Bg), (after.Character, after.Fg, after.Bg));
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count + 1)!;
            Assert.Equal("Á", row.GetExtendedText(1));
            Assert.NotNull(row.GetHyperlink(1));
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.True(buffer.IsBold);
        Assert.True(buffer.IsUnderline);
        Assert.False(buffer.IsInverse);
    }

    /// <summary>Only the active screen bank changes and history is untouched.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_active_bank_changes(bool alternate)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        for (int line = 0; line < 8; line++) parser.Process($"old{line}\r\n");
        if (alternate) parser.Process($"{Esc}[?1049h");
        for (int row = 0; row < 4; row++) parser.Process($"{Esc}[{row + 1};1H" + new string((char)('A' + row), 8));
        var before = buffer.ExportState(int.MaxValue);
        parser.Process($"{Esc}[2*x{Esc}[2;3;3;6;7$r");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal(before.Scrollback.CellsBase64, after.Scrollback.CellsBase64);
        Assert.Equal(alternate ? before.Main.CellsBase64 : before.Alt.CellsBase64, alternate ? after.Main.CellsBase64 : after.Alt.CellsBase64);
        Assert.Equal(new[] { "........", "..IIII..", "..IIII..", "........" }, Screen(buffer));
    }

    /// <summary>A rectangle edge inside a wide glyph applies to both halves, so the glyph never carries split attributes.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Wide_glyphs_change_as_one_unit(int column)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("界X");
        parser.Process($"{Esc}[2*x{Esc}[1;{column};1;{column};7$r");
        Assert.True(Cell(buffer, 0, 0).IsInverse);
        Assert.True(Cell(buffer, 1, 0).IsInverse);
        Assert.True(Cell(buffer, 0, 0).IsWide);
        Assert.True(Cell(buffer, 1, 0).IsWideContinuation);
        Assert.False(Cell(buffer, 2, 0).IsInverse);
        parser.Process($"{Esc}[1;{column};1;{column};7$t");
        Assert.False(Cell(buffer, 0, 0).IsInverse);
        Assert.False(Cell(buffer, 1, 0).IsInverse);
        Assert.Equal('X', Cell(buffer, 2, 0).Character);
    }

    /// <summary>Only changed rows are touched: revisions of rows outside the rectangle do not move.</summary>
    [Fact]
    public void Rows_outside_the_rectangle_keep_their_revision()
    {
        var (buffer, parser) = Create();
        buffer.Lock.EnterReadLock();
        uint outside, inside;
        try
        {
            outside = buffer.GetRowAbsolute(buffer.Scrollback.Count + 3)!.Revision;
            inside = buffer.GetRowAbsolute(buffer.Scrollback.Count + 1)!.Revision;
        }
        finally { buffer.Lock.ExitReadLock(); }
        parser.Process($"{Esc}[2*x{Esc}[2;1;2;4;7$r");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(outside, buffer.GetRowAbsolute(buffer.Scrollback.Count + 3)!.Revision);
            Assert.NotEqual(inside, buffer.GetRowAbsolute(buffer.Scrollback.Count + 1)!.Revision);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    /// <summary>The stream skips unoccupied cells (past the written extent) while the rectangle changes them.</summary>
    [Fact]
    public void Stream_skips_unoccupied_cells_and_rectangle_fills_them()
    {
        var buffer = new TerminalBuffer(8, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("ab\r\ncd\r\nef");
        parser.Process($"{Esc}[1;1;3;2;7$r");
        Assert.Equal(new[] { "II......", "II......", "II......" }, Screen(buffer));
        parser.Process($"{Esc}[2*x{Esc}[1;1;3;4;27$r{Esc}[1;1;3;4;7$r");
        Assert.Equal(new[] { "IIII....", "IIII....", "IIII...." }, Screen(buffer));
        parser.Process($"{Esc}[0*x{Esc}[1;3;3;4;27$r");
        // The rectangle occupied columns 0..3, so the stream now reaches them: row 0 from column 3, rows 1 and 2 through column 4.
        Assert.Equal(new[] { "II......", "........", "........" }, Screen(buffer));
    }

    /// <summary>Typed spaces inside the written extent are occupied; erased rows are unoccupied again after a full clear.</summary>
    [Fact]
    public void Typed_spaces_are_occupied()
    {
        var buffer = new TerminalBuffer(8, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("a   b");
        parser.Process($"{Esc}[1;1;1;8;7$r");
        Assert.Equal("IIIII...", Marks(buffer, 0, 8));
    }

    /// <summary>Cells filled by DECFRA are occupied, so the stream reaches them; the attribute change is not lost by a later DECFRA either.</summary>
    [Fact]
    public void Filled_cells_are_occupied_for_the_stream()
    {
        var buffer = new TerminalBuffer(8, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process($"{Esc}[88;1;1;1;4$x");
        parser.Process($"{Esc}[1;1;1;8;7$r");
        Assert.Equal("IIII....", Marks(buffer, 0, 8));
        Assert.Equal("XXXX____", new string(Enumerable.Range(0, 8).Select(col => Cell(buffer, col, 0).Character == ' ' ? '_' : Cell(buffer, col, 0).Character).ToArray()));
    }

    /// <summary>DECCRA copies the attributes DECCARA stored, and DECCARA over the copy does not change the source.</summary>
    [Fact]
    public void Copied_cells_carry_the_changed_attributes()
    {
        var buffer = new TerminalBuffer(8, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process("abcd");
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;2;1;7$r{Esc}[0*x");
        parser.Process($"{Esc}[1;1;1;4;1;2;1$v");
        Assert.Equal("[IB][IB]..", Marks(buffer, 0, 4));
        Assert.Equal("[IB][IB]..", Marks(buffer, 1, 4));
        parser.Process($"{Esc}[2*x{Esc}[2;1;2;4;27$r{Esc}[0*x");
        Assert.Equal("[IB][IB]..", Marks(buffer, 0, 4));
        Assert.Equal("BB..", Marks(buffer, 1, 4));
    }

    /// <summary>A rectangle occupies its cells even when the attributes already match, so a later stream change reaches them.</summary>
    [Fact]
    public void Rectangle_occupies_cells_even_when_nothing_changes()
    {
        var buffer = new TerminalBuffer(8, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process($"{Esc}[2*x{Esc}[1;1;1;4;0$r{Esc}[0*x{Esc}[1;1;1;8;7$r");
        Assert.Equal("IIII....", Marks(buffer, 0, 8));
    }

    /// <summary>Selectors apply in order: 24 then 4 drops the underline style and color exactly as two separate commands do.</summary>
    [Fact]
    public void Selector_order_decides_whether_underline_detail_survives()
    {
        static string Detail(string commands)
        {
            var buffer = new TerminalBuffer(8, 2);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
            parser.Process($"{Esc}[4:3;58:5:5mA{Esc}[0m{Esc}[2*x{commands}");
            buffer.Lock.EnterReadLock();
            try
            {
                var row = buffer.GetRowAbsolute(buffer.Scrollback.Count)!;
                return $"{row.Cells[0].IsUnderline}/{row.GetUnderline(0)?.Style}/{row.GetUnderline(0)?.ColorIndex}";
            }
            finally { buffer.Lock.ExitReadLock(); }
        }
        string together = Detail($"{Esc}[1;1;1;1;24;4$r");
        Assert.Equal(Detail($"{Esc}[1;1;1;1;24$r{Esc}[1;1;1;1;4$r"), together);
        Assert.Equal("True//", together);
        Assert.Equal($"True/Curly/5", Detail($"{Esc}[1;1;1;1;4;4$r"));
        Assert.Equal("True//", Detail($"{Esc}[1;1;1;1;4$t{Esc}[1;1;1;1;4$t"));
    }
}
