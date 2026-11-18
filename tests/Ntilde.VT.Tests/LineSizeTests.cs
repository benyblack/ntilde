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
    public void A_width_change_keeps_a_double_size_line_as_one_unreflowed_row(int newCols)
    {
        var (buffer, parser) = Create();
        parser.Process($"plain\r\n{Dwl}abcde\r\nafter");
        buffer.Resize(newCols, 4);
        Assert.Equal(new[] { LineSize.SingleWidth, LineSize.DoubleWidth, LineSize.SingleWidth, LineSize.SingleWidth }, Sizes(buffer));
        string[] lines = Lines(buffer);
        Assert.Equal("plain", lines[0]);
        Assert.Equal("abcde", lines[1]); // the cells are kept; a narrower page shows fewer of them
        Assert.Equal("after", lines[2]);
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
