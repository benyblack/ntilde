using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>VT100 line attributes: DECDHL (<c>ESC # 3</c> / <c>ESC # 4</c>), DECSWL (<c>ESC # 5</c>) and DECDWL (<c>ESC # 6</c>).</summary>
public sealed class LineSizeTests
{
    private const string Esc = "\u001b";
    private const string Dwl = "\u001b#6", Swl = "\u001b#5", Top = "\u001b#3", Bottom = "\u001b#4";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 10, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer));
    }

    private static string[] Lines(TerminalBuffer buffer) => BufferSnapshot.Capture(buffer).Lines.Select(line => line.TrimEnd()).ToArray();

    private static LineSize[] Sizes(TerminalBuffer buffer) => Enumerable.Range(0, buffer.Rows).Select(buffer.GetLineSize).ToArray();

    private static LineSize[] AllSizes(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return Enumerable.Range(0, buffer.TotalLines).Select(buffer.GetLineSizeAbsolute).ToArray(); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string AllText(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try { return string.Join("\n", Enumerable.Range(0, buffer.TotalLines).Select(row => string.Concat(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetCellAbsolute(col, row).Character)).TrimEnd())); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static bool IsWrapped(TerminalBuffer buffer, int absRow)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.IsRowWrappedAbsolute(absRow); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData("\u001b#3", LineSize.DoubleHeightTop)]
    [InlineData("\u001b#4", LineSize.DoubleHeightBottom)]
    [InlineData("\u001b#6", LineSize.DoubleWidth)]
    public void Each_control_sets_the_attribute_of_the_cursor_row_only(string control, LineSize expected)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;1H{control}");
        Assert.Equal(new[] { LineSize.SingleWidth, expected, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
        parser.Process(Swl);
        Assert.All(Sizes(buffer), size => Assert.Equal(LineSize.SingleWidth, size));
    }

    [Theory]
    [InlineData("\u001b#3")]
    [InlineData("\u001b#4")]
    [InlineData("\u001b#5")]
    [InlineData("\u001b#6")]
    [Trait("Category", "Replay")]
    public void A_control_split_at_every_byte_applies_once_and_prints_nothing(string control)
    {
        for (int split = 0; split <= control.Length; split++)
        {
            var (buffer, parser) = Create();
            parser.Process(Dwl); // so DECSWL has something to undo
            parser.Process(control[..split]);
            parser.Process(control[split..]);
            parser.Process("A");
            Assert.Equal("A", Lines(buffer)[0]);
            Assert.Equal(control == Swl ? LineSize.SingleWidth : control == Top ? LineSize.DoubleHeightTop
                : control == Bottom ? LineSize.DoubleHeightBottom : LineSize.DoubleWidth, buffer.GetLineSize(0));
        }
    }

    [Fact]
    public void Making_a_line_double_loses_the_characters_right_of_centre_and_single_width_restores_nothing()
    {
        var (buffer, parser) = Create();
        parser.Process("0123456789\r" + Dwl);
        Assert.Equal("01234", Lines(buffer)[0]);
        parser.Process(Swl);
        Assert.Equal("01234", Lines(buffer)[0]);
    }

    [Fact]
    public void Changing_between_double_sizes_keeps_the_visible_half()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abcde\r" + Top);
        Assert.Equal("abcde", Lines(buffer)[0]);
        parser.Process(Bottom);
        Assert.Equal("abcde", Lines(buffer)[0]);
    }

    [Fact]
    public void A_wide_glyph_the_centre_would_split_is_retired_whole()
    {
        var (buffer, parser) = Create();
        parser.Process("abcd你z\r" + Dwl); // the wide glyph occupies columns 4 and 5
        Assert.Equal("abcd", Lines(buffer)[0]);
    }

    [Fact]
    public void The_cursor_is_brought_back_inside_a_line_made_double()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1;9H{Dwl}");
        Assert.Equal(4, buffer.CursorCol);
        parser.Process($"{Esc}[1;3H{Top}");
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void A_pending_wrap_at_the_page_edge_does_not_survive_the_line_becoming_double()
    {
        var (buffer, parser) = Create();
        parser.Process("0123456789" + Dwl + "X");
        Assert.Equal(new[] { "0123X", "", "", "" }, Lines(buffer));
        Assert.False(IsWrapped(buffer, 0));
    }

    [Fact]
    public void Text_wraps_at_the_halved_width_and_leaves_no_soft_wrap_mark()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abcdefg");
        Assert.Equal(new[] { "abcde", "fg", "", "" }, Lines(buffer));
        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.False(IsWrapped(buffer, 0));
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(1));
    }

    [Fact]
    public void Without_autowrap_text_stops_at_the_last_column_of_the_half()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[?7l{Dwl}abcdefg");
        Assert.Equal(new[] { "abcdg", "", "", "" }, Lines(buffer));
        Assert.Equal((0, 4), (buffer.CursorRow, buffer.CursorCol));
    }

    [Theory]
    [InlineData("\u001b[1;9H", 4)]  // CUP
    [InlineData("\u001b[9G", 4)]    // CHA
    [InlineData("\u001b[9`", 4)]    // HPA
    [InlineData("\u001b[99C", 4)]   // CUF
    [InlineData("\u001b[3G", 2)]
    [InlineData("\t", 4)]           // HT has no stop inside the half past column 0
    public void Cursor_addressing_stops_at_the_last_column_of_the_half(string move, int column)
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + move);
        Assert.Equal((0, column), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void A_cursor_that_arrives_right_of_the_half_by_a_vertical_move_writes_on_the_last_column()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;1H{Dwl}{Esc}[1;9H{Esc}[BXY");
        Assert.Equal(new[] { "", "    X", "Y", "" }, Lines(buffer));
    }

    [Theory]
    [InlineData("\u001b[X", "abcd")]   // ECH
    [InlineData("\u001b[P", "abcd")]   // DCH
    [InlineData("\u001b[@Z", "abcdZ")] // ICH pushes e out, Z takes the last column
    [InlineData("\u001b[K", "abcd")]   // EL 0
    [InlineData("\u001b[DZ", "abcZe")] // CUB starts from the last column
    [InlineData("\bZ", "abcZe")]       // and so does BS
    public void A_cursor_that_arrives_right_of_the_half_by_a_vertical_move_edits_at_the_last_column(string edit, string expected)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[2;1H{Dwl}abcde{Esc}[1;9H{Esc}[B");
        Assert.Equal((1, 4), (buffer.CursorRow, buffer.CursorCol));
        parser.Process(edit);
        Assert.Equal(expected, Lines(buffer)[1]);
    }

    [Fact]
    public void Making_a_line_double_keeps_a_character_that_replaced_the_first_half_of_a_wide_glyph()
    {
        var (buffer, parser) = Create();
        parser.Process($"abcd\u4f60{Esc}[1;5HX\r{Dwl}");
        Assert.Equal("abcdX", Lines(buffer)[0]);
    }

    [Theory]
    [InlineData("\u001b[?J")]
    [InlineData("\u001b[?1J")]
    [InlineData("\u001b[?2J")]
    public void Selective_erase_in_display_keeps_line_attributes_with_nothing_protected(string decsed)
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a\r\n{Dwl}b\r\n{Dwl}c{Esc}[2;1H{decsed}");
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer));
    }

    [Fact]
    public void A_row_snapshot_carries_the_page_width_its_half_is_measured_against()
    {
        // A resize in flight: the page is already 12 columns, the view still asks for 20.
        var (buffer, parser) = Create(cols: 20);
        parser.Process(Dwl + "MMMMMMMMMM");
        buffer.Resize(12, 4);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 4, ViewportCols = 20, ScrollOffset = 0 }, out _);
        Assert.Equal(20, snapshot.RowsData.Array![0].Cols);
        Assert.Equal(12, snapshot.RowsData.Array[0].BufferCols);
    }

    [Fact]
    public void Selective_erase_with_something_protected_starts_at_the_column_the_cursor_is_shown_on()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[1\"qP{Esc}[0\"q{Esc}[2;1H{Dwl}abcde{Esc}[1;9H{Esc}[B{Esc}[?J");
        Assert.Equal(new[] { "P", "abcd", "", "" }, Lines(buffer));
    }

    [Fact]
    public void A_blank_double_size_line_survives_a_shrink_in_height()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[4;1H{Dwl}{Esc}[H");
        buffer.Resize(10, 3);
        Assert.Contains(LineSize.DoubleWidth, AllSizes(buffer));
    }

    [Fact]
    public void Erasing_the_screen_drops_a_wrap_pending_at_a_double_lines_last_column()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}abcde{Esc}[2JX");
        Assert.Equal(new[] { "     X", "", "", "" }, Lines(buffer));
    }

    [Fact]
    public void A_page_narrower_than_a_double_lines_text_wraps_it_into_double_rows_and_widening_rejoins_them()
    {
        var (buffer, parser) = Create(rows: 8);
        parser.Process(Dwl + "abcde\r\nplain text");
        buffer.Resize(4, 8);
        Assert.Equal(new[] { "ab", "cd", "e", "plai", "n te", "xt", "", "" }, Lines(buffer));
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer).Take(4));
        Assert.True(IsWrapped(buffer, 0));
        Assert.True(IsWrapped(buffer, 1));
        Assert.False(IsWrapped(buffer, 2));
        buffer.Resize(10, 8);
        Assert.Equal(new[] { "abcde", "plain text", "", "", "", "", "", "" }, Lines(buffer));
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer).Take(2));
        Assert.False(IsWrapped(buffer, 0));
    }

    [Fact]
    public void A_double_height_line_re_wraps_with_each_top_half_straight_above_its_bottom_half()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef\r\nafter");
        buffer.Resize(6, 8);
        Assert.Equal(new[] { "abc", "abc", "def", "def", "after", "", "", "" }, Lines(buffer));
        Assert.Equal(
            new[] { LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth },
            Sizes(buffer).Take(5));
        buffer.Resize(12, 8);
        Assert.Equal(new[] { "abcdef", "abcdef", "after", "", "", "", "", "" }, Lines(buffer));
        Assert.Equal(new[] { LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth }, Sizes(buffer).Take(3));
    }

    [Fact]
    public void Two_double_width_lines_one_under_the_other_stay_two_lines_through_a_resize()
    {
        var (buffer, parser) = Create(rows: 8);
        parser.Process($"{Dwl}one\r\n{Dwl}two");
        buffer.Resize(4, 8);
        buffer.Resize(20, 8);
        Assert.Equal(new[] { "one", "two" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_wide_glyph_that_does_not_fit_the_narrower_half_moves_to_the_next_row_whole()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process(Dwl + "ab\u6f22");
        buffer.Resize(6, 6);
        Assert.Equal(new[] { "ab", "\u6f22" }, Lines(buffer).Take(2));
        buffer.Resize(10, 6);
        Assert.Equal("ab\u6f22", Lines(buffer)[0]);
    }

    [Fact]
    public void A_wide_glyph_ending_a_double_line_keeps_both_its_cells_through_a_width_change()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process(Dwl + "a\u6f22\r\nnext");
        buffer.Resize(4, 6);
        Assert.Equal(new[] { "a", "\u6f22", "next" }, Lines(buffer).Take(3));
        buffer.Resize(10, 6);
        Assert.Equal(new[] { "a\u6f22", "next" }, Lines(buffer).Take(2));
        buffer.Lock.EnterReadLock();
        try { Assert.True(buffer.GetCellAbsolute(2, 0).IsWideContinuation); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Typed_spaces_at_the_end_of_a_double_line_re_wrap_with_it()
    {
        var (buffer, parser) = Create(cols: 20, rows: 8);
        parser.Process(Dwl + "abc      \r\nafter");
        buffer.Resize(6, 8);
        Assert.Equal(new[] { "abc", "", "", "after" }, Lines(buffer).Take(4));
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer).Take(4));
    }

    [Fact]
    public void A_shell_mark_after_the_text_is_dropped_when_the_re_wrap_fills_its_row()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abcd{Esc}]133;B{Esc}\\");
        Assert.NotNull(buffer.CommandStartMark);
        buffer.Resize(4, 6);
        Assert.Null(buffer.CommandStartMark);
    }

    [Fact]
    public void A_shell_mark_inside_a_double_line_follows_its_character()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abc{Esc}]133;B{Esc}\\de");
        buffer.Resize(4, 6);
        Assert.Equal((1, 1), (buffer.CommandStartMark!.Value.Row, buffer.CommandStartMark.Value.Column));
    }

    [Fact]
    public void The_cursor_stays_on_its_character_when_a_double_line_re_wraps()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process(Dwl + "abcde");
        buffer.Resize(6, 6);
        parser.Process("X");
        Assert.Equal(new[] { "abc", "deX" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_cursor_after_the_text_waits_at_the_last_column_when_the_re_wrap_fills_its_row()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process(Dwl + "abcd");
        buffer.Resize(4, 6);
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(1, buffer.CursorCol);
        parser.Process("X");
        Assert.Equal(new[] { "ab", "cd", "X" }, Lines(buffer).Take(3));
    }

    [Fact]
    public void Search_reads_each_half_of_a_re_wrapped_double_height_line_as_one_line()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef\r\nafter");
        buffer.Resize(6, 8);
        Assert.Equal(2, buffer.FindMatches("abcdef", false, true).Count);
        Assert.Empty(buffer.FindMatches("abcabc", false, true));
        Assert.False(IsWrapped(buffer, 0)); // the row below is the other half, not a continuation
    }

    [Fact]
    public void A_saved_cursor_past_the_text_of_a_double_line_keeps_its_column_through_a_widening()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}a{Esc}[1;5H{Esc}7{Esc}[3;1H");
        buffer.Resize(20, 6);
        parser.Process($"{Esc}8");
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
    }

    [Fact]
    public void A_saved_cursor_after_the_text_does_not_overwrite_it_once_the_re_wrap_fills_its_row()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abcd{Esc}7{Esc}[5;1H");
        buffer.Resize(4, 6);
        parser.Process($"{Esc}8X");
        Assert.Equal(new[] { "ab", "cd", "X" }, Lines(buffer).Take(3));
    }

    [Fact]
    public void Search_follows_a_lone_double_height_half_through_its_re_wrapped_rows()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\nafter");
        buffer.Resize(6, 8);
        Assert.Equal(new[] { "abc", "def", "after" }, Lines(buffer).Take(3));
        Assert.Single(buffer.FindMatches("abcdef", false, true));
    }

    [Fact]
    public void Returning_a_re_wrapped_double_height_line_to_single_width_leaves_its_rows_as_separate_lines()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef");
        buffer.Resize(6, 8);
        parser.Process($"{Esc}[?69h");
        buffer.Resize(12, 8);
        Assert.Equal(new[] { "abc", "abc", "def", "def" }, Lines(buffer).Take(4));
    }

    [Fact]
    public void Underlined_blanks_at_the_end_of_a_double_line_survive_a_width_change()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}{Esc}[4m  {Esc}[0m\r\nafter");
        buffer.Resize(12, 6);
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.True(buffer.GetCellAbsolute(0, 0).IsUnderline);
            Assert.True(buffer.GetCellAbsolute(1, 0).IsUnderline);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_background_erase_past_the_half_of_a_double_line_adds_no_row_on_a_width_change()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abc{Esc}[41m{Esc}[K{Esc}[0m\r\nafter");
        buffer.Resize(12, 6);
        Assert.Equal(new[] { "abc", "after" }, Lines(buffer).Take(2));
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer).Take(2));
    }

    [Fact]
    public void Reverse_wrap_lands_on_the_last_column_of_the_double_row_above_not_inside_a_wide_glyph()
    {
        var (buffer, parser) = Create(cols: 20, rows: 6);
        parser.Process(Dwl + "ab\u6f22cd");
        buffer.Resize(8, 6);
        Assert.Equal(new[] { "ab\u6f22", "cd" }, Lines(buffer).Take(2));
        parser.Process($"{Esc}[?45h{Esc}[2;1H\b");
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void With_autowrap_off_a_saved_cursor_gains_no_pending_wrap_from_the_re_wrap()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}{Esc}[?7labcd{Esc}7{Esc}[5;1H");
        buffer.Resize(4, 6);
        parser.Process($"{Esc}8X");
        Assert.Equal(new[] { "ab", "cX" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_wrap_pending_from_one_re_wrap_becomes_an_ordinary_column_in_the_next()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process(Dwl + "abcd");
        buffer.Resize(4, 6);
        buffer.Resize(10, 6);
        Assert.False(buffer.IsPendingWrap);
        Assert.Equal(4, buffer.CursorCol);
        parser.Process("\bX");
        Assert.Equal("abcX", Lines(buffer)[0]);
    }

    [Fact]
    public void A_double_height_line_across_the_top_of_the_screen_keeps_its_bottom_half_on_the_screen()
    {
        var (buffer, parser) = Create(cols: 12, rows: 2);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef\r\n");
        buffer.Resize(14, 2);
        Assert.Equal(new[] { "abcdef", "" }, Lines(buffer));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(new[] { LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth }, AllSizes(buffer));
    }

    [Fact]
    public void Typed_spaces_survive_a_width_change_after_an_insert_on_the_double_line()
    {
        var (buffer, parser) = Create(cols: 20, rows: 8);
        parser.Process($"{Dwl}abc      {Esc}[1;1H{Esc}[@{Esc}[3;1H");
        buffer.Resize(22, 8);
        buffer.Resize(6, 8);
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth }, Sizes(buffer).Take(4));
        Assert.Equal(" ab", Lines(buffer)[0]);
    }

    [Fact]
    public void A_saved_pending_wrap_survives_a_resize_made_while_autowrap_is_off()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abcde{Esc}7{Esc}[?7l{Esc}[4;1H");
        buffer.Resize(11, 6);
        parser.Process($"{Esc}[?7h{Esc}8X");
        Assert.Equal(new[] { "abcde", "X" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_combining_mark_after_a_re_wrap_still_attaches_to_the_last_character()
    {
        var (buffer, parser) = Create(rows: 8);
        parser.Process(Dwl + "abcd");
        buffer.Resize(4, 8);
        parser.Process("\u0301");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("c", buffer.GetGraphemeAbsolute(0, 1));
            Assert.Equal("d\u0301", buffer.GetGraphemeAbsolute(1, 1));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Text_a_row_still_holds_past_its_half_is_re_wrapped_not_dropped()
    {
        // A row resized before double-size lines re-wrapped: its text runs past the half of the narrower page.
        var (source, sourceParser) = Create(cols: 6, rows: 4);
        sourceParser.Process("abcde");
        var state = source.ExportState(100);
        state.Parser = sourceParser.ExportState();
        state.Main.RowLineSizes = new[] { (int)LineSize.DoubleWidth, 0, 0, 0 };
        var (buffer, parser) = Create(cols: 6, rows: 4);
        TerminalStateTransfer.Restore(buffer, parser, state);
        buffer.Resize(10, 4);
        Assert.Equal("abcde", Lines(buffer)[0]);
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
    }

    [Fact]
    public void A_combining_mark_attaches_to_a_character_on_a_row_the_re_wrap_pushed_down()
    {
        var (buffer, parser) = Create(rows: 8);
        parser.Process(Dwl + "abcde\r\nz");
        buffer.Resize(6, 8);
        parser.Process("\u0301");
        buffer.Lock.EnterReadLock();
        try { Assert.Equal("z\u0301", buffer.GetGraphemeAbsolute(0, 2)); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void The_alternate_screens_cursor_stays_put_while_the_main_screens_double_line_re_wraps()
    {
        var (buffer, parser) = Create(rows: 8);
        parser.Process($"{Dwl}abcde{Esc}[?1049h{Esc}[1;4H");
        buffer.Resize(6, 8);
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(3, buffer.CursorCol);
        parser.Process($"{Esc}[?1049l");
        Assert.Equal(new[] { "abc", "de" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void Search_results_on_a_re_wrapped_double_height_line_come_in_reading_order()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcabc\r\n{Bottom}abcabc");
        buffer.Resize(6, 8);
        Assert.Equal(new[] { 0, 1, 2, 3 }, buffer.FindMatches("abc", false, true).Select(match => match.AbsRow));
    }

    [Fact]
    public void A_bottom_half_whose_first_top_half_is_gone_still_reads_as_one_line()
    {
        var (source, sourceParser) = Create(cols: 12, rows: 4);
        sourceParser.Process($"x\r\n{Top}abcdef\r\n{Bottom}abcdef\r\nz");
        source.Resize(6, 4);
        Assert.Equal(new[] { "abc", "def", "def", "z" }, Lines(source));
        var state = source.ExportState(0);
        state.Parser = sourceParser.ExportState();
        var (buffer, parser) = Create(cols: 6, rows: 4);
        TerminalStateTransfer.Restore(buffer, parser, state);
        Assert.Single(buffer.FindMatches("abcdef", false, true));
        buffer.Resize(12, 4);
        Assert.Contains("abcdef", Lines(buffer));
    }

    [Fact]
    public void The_alternate_screens_saved_cursor_is_not_moved_by_the_main_screens_double_line()
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}ab{Esc}[?1049h0123456789{Esc}7{Esc}[3;1H");
        buffer.Resize(20, 6);
        parser.Process($"{Esc}8");
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(9, buffer.CursorCol);
    }

    [Fact]
    public void The_command_line_reader_joins_a_re_wrapped_double_line_without_the_hidden_half()
    {
        var (buffer, parser) = Create(cols: 12, rows: 6);
        parser.Process($"{Dwl}{Esc}]133;B{Esc}\\abcdef");
        buffer.Resize(6, 6);
        Assert.True(GridQueryReader.TryReadCommandLine(buffer, buffer.CommandStartMark!.Value, out var line));
        Assert.Equal("abcdef", line.Text);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1047)]
    [InlineData(1049)]
    public void The_main_screens_cursor_follows_its_double_line_through_a_resize_behind_the_alternate_screen(int mode)
    {
        var (buffer, parser) = Create(rows: 6);
        parser.Process($"{Dwl}abcde{Esc}[?{mode}h");
        buffer.Resize(6, 6);
        parser.Process($"{Esc}[?{mode}lX");
        Assert.Equal(new[] { "abc", "deX" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_bottom_half_followed_by_an_unrelated_top_half_re_wraps_as_two_lines()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Bottom}abcdef\r\n{Top}UVWXYZ");
        buffer.Resize(6, 8);
        Assert.Equal(new[] { "abc", "def", "UVW", "XYZ" }, Lines(buffer).Take(4));
    }

    [Fact]
    public void A_combining_mark_finds_its_character_when_the_cursor_falls_on_a_row_boundary()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process(Dwl + "abcde\rabc");
        buffer.Resize(6, 8);
        parser.Process("\u0301");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("c\u0301", buffer.GetGraphemeAbsolute(2, 0));
            Assert.Equal("d", buffer.GetGraphemeAbsolute(0, 1));
        }
        finally { buffer.Lock.ExitReadLock(); }
        parser.Process("\rX");
        Assert.Equal(new[] { "abc\u0301", "Xe" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_combining_mark_finds_its_character_before_a_wide_glyph_that_wrapped_early()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process(Dwl + "ab\u6f22\rab");
        buffer.Resize(6, 8);
        parser.Process("\u0301");
        Assert.Equal(new[] { "ab\u0301", "\u6f22" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void Returning_the_second_row_of_a_re_wrapped_double_line_to_single_width_ends_the_line_before_it()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process(Dwl + "abcdef");
        buffer.Resize(6, 8);
        Assert.True(IsWrapped(buffer, 0));
        parser.Process($"{Esc}[2;1H{Swl}");
        Assert.False(IsWrapped(buffer, 0));
        Assert.Empty(buffer.FindMatches("abcdef", false, true));
    }

    [Fact]
    public void Returning_a_re_wrapped_double_width_line_to_single_width_leaves_its_rows_as_separate_lines()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process(Dwl + "abcdef");
        buffer.Resize(6, 8);
        parser.Process($"{Esc}[?69h");
        buffer.Resize(12, 8);
        Assert.Equal(new[] { "abc", "def" }, Lines(buffer).Take(2));
    }

    [Fact]
    public void A_cursor_on_a_row_boundary_of_a_double_height_line_stays_in_its_own_half()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef{Esc}[1;4H");
        buffer.Resize(6, 8);
        parser.Process("X");
        Assert.Equal(new[] { "abc", "abc", "Xef", "def" }, Lines(buffer).Take(4));
    }

    [Fact]
    public void A_combining_mark_does_not_reach_for_a_character_that_left_the_screen()
    {
        // Autowrap off, so the cursor stays at the start of the second row while the character before it goes into history.
        var (buffer, parser) = Create(cols: 12, rows: 1);
        parser.Process($"{Dwl}{Esc}[?7labcd\rab");
        buffer.Resize(4, 1);
        Assert.Equal("ab\ncd", AllText(buffer));
        parser.Process("\u0301");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal("b", buffer.GetGraphemeAbsolute(1, 0));
            Assert.Equal("d", buffer.GetGraphemeAbsolute(1, 1));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_combining_mark_finds_its_character_on_a_row_boundary_of_a_double_height_line()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abc de\r\n{Bottom}abc de{Esc}[1;1Habc");
        buffer.Resize(6, 8);
        parser.Process("\u0301");
        buffer.Lock.EnterReadLock();
        try { Assert.Equal("c\u0301", buffer.GetGraphemeAbsolute(2, 0)); }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.Equal(new[] { "abc\u0301", "abc", " de", " de" }, Lines(buffer).Take(4));
    }

    [Fact]
    public void A_wide_glyph_that_wraps_early_goes_on_in_the_same_half_of_a_re_wrapped_double_height_line()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef");
        buffer.Resize(6, 8);
        parser.Process($"{Esc}[1;3H\u6f22");
        Assert.Equal(new[] { "abc", "abc", "\u6f22f", "def" }, Lines(buffer).Take(4));
    }

    [Fact]
    public void A_wrap_at_the_bottom_margin_does_not_leave_the_scrolling_region_for_the_next_row_of_the_half()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef");
        buffer.Resize(6, 8);
        parser.Process($"{Esc}[1;2r{Esc}[1;1HabcX");
        string[] lines = Lines(buffer);
        Assert.Equal("def", lines[2]);
        Assert.Equal("def", lines[3]);
    }

    [Fact]
    public void Erasing_one_row_of_a_re_wrapped_half_leaves_the_other_half_one_line()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Top}abcdef\r\n{Bottom}abcdef");
        buffer.Resize(6, 8);
        parser.Process($"{Esc}[1;1H{Esc}[2K");
        Assert.Single(buffer.FindMatches("abcdef", false, true));
        buffer.Resize(12, 8);
        Assert.Equal(new[] { "", "abcdef", "def", "" }, Lines(buffer).Take(4));
        Assert.Single(buffer.FindMatches("abcdef", false, true));
    }

    [Fact]
    public void An_image_past_the_text_of_a_double_line_keeps_its_column_through_a_widening()
    {
        var (buffer, parser) = Create(cols: 12, rows: 8);
        parser.Process($"{Dwl}a{Esc}[3;1H");
        buffer.AddImage(new TerminalImage(new object(), 4, 0, 1, 1));
        buffer.Resize(14, 8);
        Assert.Equal((4, 0), (buffer.Images[0].CellX, buffer.Images[0].CellY));
    }

    [Fact]
    public void An_image_snapshot_carries_the_line_attribute_of_the_row_it_was_placed_on_even_when_that_row_is_above_the_view()
    {
        var (buffer, parser) = Create(cols: 12, rows: 4);
        parser.Process($"{Dwl}a\r\nb");
        buffer.AddImage(new TerminalImage(new object(), 2, 0, 1, 3));
        buffer.AddImage(new TerminalImage(new object(), 2, 1, 1, 1));
        buffer.Lock.EnterReadLock();
        try
        {
            var fromTheAnchor = buffer.GetVisibleImagesSnapshot(0, 4);
            Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth }, fromTheAnchor.Select(image => image.LineSize));
            // Seen from the row below, the first image's anchor row is out of view: it is still a double-width row's image.
            var fromBelow = buffer.GetVisibleImagesSnapshot(1, 3);
            Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth }, fromBelow.Select(image => image.LineSize));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static LineSize[] ImageLineSizes(TerminalBuffer buffer, int start, int rows)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetVisibleImagesSnapshot(start, rows).Select(image => image.LineSize).ToArray(); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void An_image_keeps_its_line_attribute_after_its_anchor_row_scrolls_off_the_alternate_screen()
    {
        var (buffer, parser) = Create(cols: 12, rows: 4);
        parser.Process($"{Esc}[?1049h{Dwl}a");
        buffer.AddImage(new TerminalImage(new object(), 2, 0, 1, 3));
        parser.Process($"{Esc}[4;1H\n");
        Assert.Equal(-1, buffer.Images[0].CellY);
        Assert.Equal(new[] { LineSize.DoubleWidth }, ImageLineSizes(buffer, 0, 4));
    }

    [Fact]
    public void An_image_keeps_its_line_attribute_after_its_anchor_row_is_trimmed_from_history()
    {
        var (buffer, parser) = Create(cols: 12, rows: 4);
        parser.Process($"{Dwl}a");
        buffer.AddImage(new TerminalImage(new object(), 2, 0, 1, 3));
        parser.Process($"{Esc}[4;1H\n{Esc}[3J");
        Assert.Equal(-1, buffer.Images[0].CellY);
        Assert.Equal(new[] { LineSize.DoubleWidth }, ImageLineSizes(buffer, 0, 4));
    }

    [Fact]
    public void An_image_keeps_its_line_attribute_after_history_evicts_its_anchor_row()
    {
        var buffer = new TerminalBuffer(12, 4) { MaxScrollbackBytes = 1 };
        var parser = new AnsiParser(buffer);
        parser.Process($"{Dwl}a");
        buffer.AddImage(new TerminalImage(new object(), 2, 0, 1, 400));
        for (int i = 0; i < 300 && buffer.Images[0].CellY >= 0; i++) parser.Process($"{Esc}[4;1H\n");
        Assert.True(buffer.Images[0].CellY < 0);
        Assert.Equal(new[] { LineSize.DoubleWidth }, ImageLineSizes(buffer, 0, 4));
    }

    [Fact]
    public void An_image_takes_the_new_line_attribute_when_the_row_at_its_origin_changes()
    {
        var (buffer, parser) = Create(cols: 12, rows: 4);
        buffer.AddImage(new TerminalImage(new object(), 2, 0, 1, 1));
        buffer.AddImage(new TerminalImage(new object(), 2, 1, 1, 1));
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth }, ImageLineSizes(buffer, 0, 4));
        parser.Process(Dwl);
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth }, ImageLineSizes(buffer, 0, 4));
        parser.Process($"{Esc}#5");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth }, ImageLineSizes(buffer, 0, 4));
        // DECLRMM and DECALN return every line to single width, and the images with them.
        parser.Process($"{Dwl}{Esc}[?69h");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth }, ImageLineSizes(buffer, 0, 4));
        parser.Process($"{Esc}[?69l{Dwl}{Esc}#8");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth }, ImageLineSizes(buffer, 0, 4));
    }

    [Fact]
    public void An_image_keeps_its_line_attribute_when_a_scrolling_region_takes_its_row()
    {
        var (buffer, parser) = Create(cols: 12, rows: 6);
        parser.Process($"{Esc}[2;1H{Dwl}a");
        buffer.AddImage(new TerminalImage(new object(), 2, 1, 1, 3));
        // Rows 2 to 5 scroll: the image moves up to a row above the region, which is not the row it was placed on.
        parser.Process($"{Esc}[2;5r{Esc}[S");
        Assert.Equal(0, buffer.Images[0].CellY);
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
        Assert.Equal(new[] { LineSize.DoubleWidth }, ImageLineSizes(buffer, 0, 6));
    }

    [Fact]
    public void A_state_transfer_carries_an_images_line_attribute_rather_than_reading_it_off_a_row()
    {
        var source = new TerminalBuffer(20, 6);
        // The placement keeps its source, which is what a transfer carries. Four rows tall.
        var sourceParser = new AnsiParser(source) { ImageDecoder = new FixedSizeImageDecoder(20, 80), RetainSixelSources = true };
        // Placed on a double-width row, then moved by a scrolling region onto a single-width one.
        sourceParser.Process($"{Esc}[2;1H{Dwl}{Esc}[2;3H{Esc}Pq!30~-~{Esc}\\{Esc}[2;5r{Esc}[S");
        var placed = Assert.Single(source.Images);
        Assert.Equal(LineSize.DoubleWidth, placed.LineSize);
        Assert.Equal(LineSize.SingleWidth, source.GetLineSize(Math.Max(0, placed.CellY)));
        var state = source.ExportState(100);
        state.Parser = sourceParser.ExportState();
        Assert.Equal((int)LineSize.DoubleWidth, Assert.Single(state.Sixels!).LineSize);
        var buffer = new TerminalBuffer(20, 6);
        var parser = new AnsiParser(buffer) { ImageDecoder = new FixedSizeImageDecoder(20, 80), RetainSixelSources = true };
        TerminalStateTransfer.Restore(buffer, parser, state);
        Assert.Equal(LineSize.DoubleWidth, Assert.Single(buffer.Images).LineSize);
    }

    [Fact]
    public void A_double_line_in_history_re_wraps_too_and_keeps_its_text()
    {
        var (buffer, parser) = Create(cols: 20, rows: 2);
        parser.Process(Dwl + "abcdefgh\r\n1\r\n2\r\n3");
        buffer.Resize(8, 2);
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, AllSizes(buffer).Take(3));
        buffer.Resize(20, 6);
        Assert.Equal(LineSize.DoubleWidth, AllSizes(buffer)[0]);
        Assert.Equal(LineSize.SingleWidth, AllSizes(buffer)[1]);
        Assert.Contains("abcdefgh", AllText(buffer));
    }

    [Fact]
    public void Erase_below_from_a_pending_wrap_at_the_first_column_erases_the_whole_line()
    {
        var (buffer, parser) = Create(cols: 3);
        parser.Process($"{Dwl}A{Esc}[J");
        Assert.Equal("", Lines(buffer)[0]);
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
    }

    [Fact]
    public void A_tab_stop_set_after_a_vertical_move_is_set_at_the_column_the_cursor_is_shown_on()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}[3g{Esc}[2;1H{Dwl}{Esc}[1;10H{Esc}[B{Esc}H\r\t");
        Assert.Equal((1, 4), (buffer.CursorRow, buffer.CursorCol));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_glyph_widened_at_the_half_by_a_later_variation_selector_wraps_the_same_in_one_chunk_or_two(bool split)
    {
        var (buffer, parser) = Create();
        if (split)
        {
            parser.Process(Dwl + "abc\u2764");
            parser.Process("\uFE0FX");
        }
        else parser.Process(Dwl + "abc\u2764\uFE0FX");
        Assert.Equal("X", Lines(buffer)[1]);
        Assert.StartsWith("abc", Lines(buffer)[0]);
        Assert.DoesNotContain("X", Lines(buffer)[0]);
    }

    [Fact]
    public void A_glyph_widened_at_a_double_lines_last_column_writes_nothing_right_of_the_half()
    {
        // As at the page's last column on a single-width row: the glyph stays where it is, with no second cell.
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abcd\u2764");
        parser.Process("\uFE0FX");
        Assert.Equal("X", Lines(buffer)[1]);
        buffer.Lock.EnterReadLock();
        try { Assert.False(buffer.GetRowAbsolute(0)!.Cells[5].IsWideContinuation); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Sized_text_returns_the_lines_it_is_written_on_to_single_width()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}abcd{Esc}]66;w=2;Y\aX");
        Assert.Equal(new[] { "abcdY X", "", "", "" }, Lines(buffer));
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));

        // A rectangle that wraps onto a double-size line, or reaches one from above, does the same.
        (buffer, parser) = Create();
        parser.Process($"{Esc}[2;1H{Dwl}{Esc}[1;10H{Esc}]66;w=7;ABCDEFG\a");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
    }

    [Fact]
    public void Sized_text_that_wraps_away_from_a_full_double_size_line_leaves_that_line_alone()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}abcde{Esc}]66;w=7;ABCDEFG\a");
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
        Assert.Equal("abcde", Lines(buffer)[0]);
        Assert.False(IsWrapped(buffer, 0));
        Assert.Equal(1, buffer.CursorRow);
    }

    [Fact]
    public void Making_a_line_double_retires_a_sized_rectangle_that_touches_it()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Esc}]66;s=2;A\a{Esc}[H{Dwl}");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Null(buffer.GetTextSizingAbsolute(0, 0));
            Assert.Null(buffer.GetTextSizingAbsolute(0, 1));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void A_wrap_pending_on_a_full_double_line_is_dropped_when_the_page_grows()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abcde");
        buffer.Resize(20, 4);
        parser.Process("X");
        Assert.Equal("abcdeX", Lines(buffer)[0]);
    }

    [Fact]
    public void A_wrap_pending_on_the_main_screen_is_dropped_when_left_right_margin_mode_widens_its_line_from_the_alternate_screen()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}abcde{Esc}[?1049h{Esc}[?69h{Esc}[?1049l{Esc}[?69lX");
        Assert.Equal("abcdeX", Lines(buffer)[0]);
    }

    [Fact]
    public void A_protected_glyph_widened_at_a_double_lines_last_column_stays_protected()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}{Esc}[1\"qabcd\u2764");
        parser.Process($"\uFE0F{Esc}[0\"q{Esc}[?2J");
        Assert.StartsWith("abcd\u2764", Lines(buffer)[0]);
    }

    [Fact]
    public async Task A_viewport_only_recording_checkpoint_carries_line_attributes()
    {
        var (source, sourceParser) = Create();
        sourceParser.Process(Dwl + "abc");
        string path = Path.GetTempFileName();
        try
        {
            using (var recorder = new PtyRecorder(path, 10, 4)) recorder.RecordSnapshot(source);
            var (buffer, parser) = Create();
            bool applied = false;
            await new ReplayRunner(path).RunAsync(_ => Task.CompletedTask,
                onSnapshotCallback: snapshot =>
                {
                    Assert.Null(snapshot.State);
                    ReplayRunner.ApplySnapshot(buffer, parser, snapshot);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            parser.Process("def");
            Assert.Equal(new[] { "abcde", "f", "", "" }, Lines(buffer));
            Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_refused_sized_block_leaves_a_double_size_line_alone()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}ab{Esc}]66;w=7:s=7;X\a"); // taller than the page: refused
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
        Assert.Equal((0, 2), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Returning_to_single_width_drops_a_wrap_pending_at_the_old_last_column()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abcde" + Swl + "X");
        Assert.Equal(new[] { "abcdeX", "", "", "" }, Lines(buffer));
    }

    [Theory]
    [InlineData("\u001b#5")]
    [InlineData("\u001b[?69h")]
    public void Returning_a_line_to_single_width_asks_for_a_redraw(string control)
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "ab");
        int invalidations = 0;
        buffer.OnInvalidate += () => invalidations++;
        parser.Process(control);
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
        Assert.True(invalidations > 0);
    }

    [Fact]
    public void Insert_and_delete_character_work_inside_the_half()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + $"abcde{Esc}[1;2H{Esc}[2@");
        Assert.Equal("a  bc", Lines(buffer)[0]); // d and e are pushed past the centre and lost
        parser.Process($"{Esc}[3P");
        Assert.Equal("ac", Lines(buffer)[0]);
    }

    [Theory]
    [InlineData("\u001b[K")]
    [InlineData("\u001b[1K")]
    [InlineData("\u001b[2K")]
    [InlineData("\u001b[3X")]
    public void Erasing_within_the_line_keeps_the_attribute(string erase)
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + "abc" + erase);
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
    }

    [Fact]
    public void ED_2_returns_every_line_to_single_width()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a\r\n{Top}b\r\n{Bottom}b{Esc}[2J");
        Assert.All(Sizes(buffer), size => Assert.Equal(LineSize.SingleWidth, size));
    }

    [Fact]
    public void ED_0_resets_the_lines_it_erases_completely()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a\r\n{Dwl}b\r\n{Dwl}c\r\n{Dwl}d{Esc}[2;2H{Esc}[J");
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
        parser.Process($"{Esc}[2;1H{Esc}[J");
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
    }

    [Fact]
    public void ED_1_resets_the_lines_above_the_cursor()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a\r\n{Dwl}b\r\n{Dwl}c{Esc}[1J");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer));
    }

    [Fact]
    public void RIS_and_DECALN_reset_the_attribute_and_DECSTR_keeps_it()
    {
        var (buffer, parser) = Create();
        parser.Process(Dwl + $"{Esc}[!p");
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));
        parser.Process($"{Esc}#8");
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
        Assert.Equal("EEEEEEEEEE", Lines(buffer)[0]);
        parser.Process(Dwl + $"{Esc}c");
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
    }

    [Fact]
    public void Scrolling_carries_the_attribute_with_its_line_and_into_history()
    {
        var (buffer, parser) = Create();
        parser.Process($"one\r\n{Dwl}two\r\n{Top}three\r\n{Bottom}four");
        parser.Process("\r\n");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth }, AllSizes(buffer));
        parser.Process("\r\n\r\n");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom,
            LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, AllSizes(buffer));
    }

    [Fact]
    public void Reverse_index_insert_line_and_delete_line_move_the_attribute_with_the_line()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a{Esc}[1;1H{Esc}M");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
        parser.Process($"{Esc}[1;1H{Esc}[2L");
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth, LineSize.DoubleWidth }, Sizes(buffer));
        parser.Process($"{Esc}[3M");
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
    }

    [Fact]
    public void Left_right_margin_mode_and_double_size_lines_do_not_combine()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}{Esc}[?69h");
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
        parser.Process(Dwl + Top + Bottom);
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
        parser.Process($"{Esc}[?69l{Dwl}");
        Assert.Equal(LineSize.DoubleWidth, buffer.GetLineSize(0));

        // Setting the mode on the alternate screen resets the main screen's lines too.
        parser.Process($"{Esc}[?1049h{Esc}[?69h{Esc}[?1049l");
        Assert.Equal(LineSize.SingleWidth, buffer.GetLineSize(0));
    }

    [Fact]
    public void The_alternate_screen_starts_single_width_and_the_main_screen_keeps_its_lines()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}main{Esc}[?1049h");
        Assert.All(Sizes(buffer), size => Assert.Equal(LineSize.SingleWidth, size));
        parser.Process($"{Esc}[2;1H{Top}alt{Esc}[?1049l");
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
        parser.Process($"{Esc}[?1049h");
        Assert.All(Sizes(buffer), size => Assert.Equal(LineSize.SingleWidth, size));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(6)]
    public void A_width_change_re_wraps_a_double_size_line_at_half_the_new_width(int newCols)
    {
        var (buffer, parser) = Create();
        parser.Process($"plain\r\n{Dwl}abcde\r\nafter");
        buffer.Resize(newCols, 4);
        if (newCols == 20)
        {
            Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
            Assert.Equal(new[] { "plain", "abcde", "after", "" }, Lines(buffer));
        }
        else
        {
            Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer));
            Assert.Equal(new[] { "plain", "abc", "de", "after" }, Lines(buffer));
        }
        parser.Process($"{Esc}[2;1H{Esc}[99C");
        Assert.Equal(newCols / 2 - 1, buffer.CursorCol);
    }

    [Fact]
    public void A_height_change_keeps_the_attribute_on_lines_pushed_into_history()
    {
        var (buffer, parser) = Create(rows: 4);
        parser.Process($"{Dwl}one\r\n{Top}two\r\n{Bottom}two\r\nlast");
        buffer.Resize(10, 2);
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth }, AllSizes(buffer));
        buffer.Resize(10, 4);
        Assert.Equal(new[] { LineSize.DoubleWidth, LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.SingleWidth }, AllSizes(buffer).Take(4));
        Assert.All(AllSizes(buffer).Skip(4), size => Assert.Equal(LineSize.SingleWidth, size));
    }

    [Fact]
    public void State_transfer_carries_the_attribute_on_the_screen_and_in_history()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}one\r\n{Top}two\r\n{Bottom}two\r\nx\r\ny\r\n{Dwl}z");
        var state = buffer.ExportState(100);
        state.Parser = parser.ExportState();
        Assert.NotNull(state.Main.RowLineSizes);
        Assert.NotNull(state.Scrollback.RowLineSizes);
        Assert.Null(state.Alt.RowLineSizes);

        var (target, targetParser) = Create(cols: 30, rows: 9);
        TerminalStateTransfer.Restore(target, targetParser, state);
        Assert.Equal(AllSizes(buffer), AllSizes(target));
        var again = target.ExportState(100);
        again.Parser = targetParser.ExportState();
        Assert.Equal(TerminalStateSerializer.ToBytes(state), TerminalStateSerializer.ToBytes(again));
    }

    [Fact]
    public void A_buffer_with_no_double_size_line_exports_no_line_size_field()
    {
        var (buffer, parser) = Create();
        parser.Process($"{Dwl}a{Swl}\r\nb\r\nc\r\nd\r\ne");
        var state = buffer.ExportState(100);
        Assert.Null(state.Main.RowLineSizes);
        Assert.Null(state.Scrollback.RowLineSizes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Malformed_line_sizes_are_rejected_before_anything_changes(int corruption)
    {
        var (buffer, parser) = Create();
        parser.Process("preserved");
        var state = new TerminalBuffer(10, 4).ExportState(0);
        state.Main.RowLineSizes = corruption switch
        {
            0 => [0, 1, 0],          // wrong row count
            1 => [0, 4, 0, 0],       // not a line size
            _ => [0, -1, 0, 0],
        };
        Assert.Throws<InvalidOperationException>(() => TerminalStateTransfer.Restore(buffer, parser, state));
        Assert.Equal("preserved", Lines(buffer)[0]);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void A_replay_checkpoint_restores_the_attribute()
    {
        var (source, sourceParser) = Create();
        sourceParser.Process($"{Top}big\r\n{Bottom}big\r\n{Dwl}wide");
        var checkpoint = new ReplaySnapshot { Cols = 10, Rows = 4, State = source.ExportState(0) };
        checkpoint.State.Parser = sourceParser.ExportState();

        var (buffer, parser) = Create(cols: 20, rows: 6);
        ReplayRunner.ApplySnapshot(buffer, parser, checkpoint);
        Assert.Equal(new[] { LineSize.DoubleHeightTop, LineSize.DoubleHeightBottom, LineSize.DoubleWidth, LineSize.SingleWidth }, Sizes(buffer));
        parser.Process("!!");
        Assert.Equal(new[] { "big", "big", "wide!", "!" }, Lines(buffer));
    }

    [Fact]
    public void The_render_snapshot_carries_the_attribute_and_repaints_the_whole_row_when_it_changes()
    {
        var (buffer, parser) = Create();
        var request = new RenderSnapshotRequest { ViewportRows = 4, ViewportCols = 10 };
        parser.Process("ab\r\ncd");
        using (buffer.CaptureRenderSnapshot(request, out _)) { }

        parser.Process($"{Esc}[1;1H{Top}");
        using (var changed = buffer.CaptureRenderSnapshot(request, out _))
        {
            Assert.Equal(LineSize.DoubleHeightTop, changed.RowsData.Array[0].LineSize);
            Assert.Equal(LineSize.SingleWidth, changed.RowsData.Array[1].LineSize);
            var span = Assert.Single(Enumerable.Range(0, changed.DirtySpans.Length).Select(i => changed.DirtySpans.Array[i]));
            Assert.Equal((0, 0, 10), (span.Row, span.ColStart, span.ColEnd));
        }

        parser.Process("\r\n\r\n\r\n\r\n\r\n"); // into history
        using var history = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest { ViewportRows = 4, ViewportCols = 10, ScrollOffset = 2 }, out _);
        Assert.Equal(LineSize.DoubleHeightTop, history.RowsData.Array[0].LineSize);
    }
}
