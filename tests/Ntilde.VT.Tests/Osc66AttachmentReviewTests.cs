namespace Ntilde.VT.Tests;

public sealed class Osc66AttachmentReviewTests
{
    private static TerminalTextSizingState Block(TerminalBuffer buffer)
        => Assert.Single(buffer.ExportState(0).TextSizing!);

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void Blocks_above_margins_use_the_screen_height(int scale)
    {
        var buffer = new TerminalBuffer(24, 8);
        new AnsiParser(buffer).Process($"\x1b[4;5r\x1b[1;1H\x1b]66;s={scale};M\a");
        Assert.Equal(scale, Block(buffer).Options.Scale);
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(scale, buffer.CursorCol);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void Combining_input_after_cursor_movement_inside_the_rectangle_preserves_it(int col, int row)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b]66;s=2:w=2;M\a\x1b[{row + 1};{col + 1}H\u0301");
        Assert.Equal("M\u0301", Block(buffer).Text);
        Assert.Equal(col, buffer.CursorCol);
        Assert.Equal(row, buffer.CursorRow);
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(int.MaxValue));
    }

    [Theory]
    [InlineData("❤", "\uFE0F", 1, 2)]
    [InlineData("😀", "\uFE0E", 2, 1)]
    public void Automatic_width_tracks_presentation_selectors_and_keeps_the_original_style(
        string text, string selector, int before, int after)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[31m\x1b]66;s=2;" + text + "\a");
        Assert.Equal(before, Block(buffer).BaseWidth);
        parser.Process("\x1b[32m" + selector);
        var block = Block(buffer);
        Assert.Equal(text + selector, block.Text);
        Assert.Equal(after, block.BaseWidth);
        Assert.Equal(after * 2, buffer.CursorCol);
        buffer.Lock.EnterReadLock();
        try
        {
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < after * 2; col++)
                {
                    Assert.NotNull(buffer.GetTextSizingAbsolute(col, row));
                    Assert.Equal((short)1, buffer.GetCellAbsolute(col, row).FgIndex);
                }
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(int.MaxValue));
    }

    [Fact]
    public void Explicit_width_wins_over_a_presentation_selector()
    {
        var buffer = new TerminalBuffer(16, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2:w=1;❤\a\uFE0F");
        Assert.Equal(1, Block(buffer).BaseWidth);
        Assert.Equal(2, buffer.CursorCol);
        Assert.Equal("❤\uFE0F", Block(buffer).Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Widening_near_the_edge_obeys_wrap_and_restores_a_valid_rectangle(bool wrap)
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer);
        parser.Process((wrap ? "" : "\x1b[?7l") + "\x1b[1;7H\x1b]66;s=2;❤\a\uFE0F");
        var block = Block(buffer);
        Assert.Equal(2, block.BaseWidth);
        Assert.Equal(wrap ? 0 : 4, block.X);
        Assert.Equal(wrap ? 1 : 0, block.Y);
        Assert.Equal(wrap ? 4 : 7, buffer.CursorCol);
        Assert.Equal(wrap ? 1 : 0, buffer.CursorRow);
        new TerminalBuffer(8, 4).ImportState(buffer.ExportState(int.MaxValue));
    }

    [Fact]
    public void Legacy_viewport_replacement_retires_a_block_spanning_history()
    {
        var target = new TerminalBuffer(16, 4);
        new AnsiParser(target).Process("\x1b]66;s=2;M\a\x1b[4;1H\n");
        var source = new TerminalBuffer(16, 4);
        source.Write("later");
        var state = source.ExportState(0);
        target.ApplySnapshot(new ReplaySnapshot
        {
            Cols = 16,
            Rows = 4,
            ScrollBottom = 3,
            IsAutoWrapMode = true,
            CellsBase64 = state.Main.CellsBase64,
            ExtendedText = state.Main.ExtendedText,
            RowWraps = state.Main.RowWraps,
            RowWrittenColumns = state.Main.RowWrittenColumns
        });
        var restored = target.ExportState(int.MaxValue);
        Assert.Null(restored.TextSizing);
        var copy = new TerminalBuffer(16, 4);
        copy.ImportState(restored);
        copy.Lock.EnterReadLock();
        try { Assert.NotEqual("M", copy.GetGraphemeAbsolute(0, 0)); }
        finally { copy.Lock.ExitReadLock(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_selector_inside_a_history_fragment_updates_every_retained_piece(bool withoutHistory)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[31m\x1b]66;s=2;❤\a\x1b[4;1H\n");
        if (withoutHistory)
        {
            var cropped = new TerminalBuffer(16, 4);
            cropped.ImportState(buffer.ExportState(0));
            buffer = cropped;
            parser = new AnsiParser(buffer);
        }
        parser.Process("\x1b[1;2H\x1b[32m\uFE0F");
        var state = buffer.ExportState(0);
        var block = Assert.Single(state.TextSizing!);
        Assert.Equal(-1, block.Y);
        Assert.Equal(2, block.BaseWidth);
        Assert.Equal("❤\uFE0F", block.Text);
        buffer.Lock.EnterReadLock();
        try
        {
            for (int col = 0; col < 4; col++)
            {
                Assert.Equal(1, buffer.GetTextSizingAbsolute(col, buffer.Scrollback.Count)!.RowOffset);
                Assert.Equal((short)1, buffer.GetCellAbsolute(col, buffer.Scrollback.Count).FgIndex);
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(int.MaxValue));
        new TerminalBuffer(16, 4).ImportState(state);
    }

    [Fact]
    public void Widening_at_the_bottom_wraps_and_scrolls_the_full_main_screen_once()
    {
        var buffer = new TerminalBuffer(8, 2);
        new AnsiParser(buffer).Process("\x1b[1;7H\x1b]66;s=2;❤\a\uFE0F");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.Equal(0, Block(buffer).Y);
        Assert.Equal(0, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
        new TerminalBuffer(8, 2).ImportState(buffer.ExportState(int.MaxValue));
    }

    [Fact]
    public void Widening_at_a_margin_bottom_scrolls_only_that_region()
    {
        var buffer = new TerminalBuffer(8, 6);
        new AnsiParser(buffer).Process("prefix\x1b[2;4r\x1b[3;7H\x1b]66;s=2;❤\a\uFE0F");
        Assert.Equal(0, buffer.Scrollback.Count);
        Assert.Equal(2, Block(buffer).Y);
        Assert.Equal(2, buffer.CursorRow);
        Assert.Equal(4, buffer.CursorCol);
        buffer.Lock.EnterReadLock();
        try { Assert.Equal("p", buffer.GetGraphemeAbsolute(0, 0)); }
        finally { buffer.Lock.ExitReadLock(); }
        new TerminalBuffer(8, 6).ImportState(buffer.ExportState(0));
    }

    [Fact]
    public void An_ordinary_mark_after_vs15_keeps_the_text_presentation_width()
    {
        var buffer = new TerminalBuffer(16, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2;😀\a\uFE0E\u0301");
        Assert.Equal(1, Block(buffer).BaseWidth);
        Assert.Equal("😀\uFE0E\u0301", Block(buffer).Text);
        Assert.Equal(2, buffer.CursorCol);
    }

    [Fact]
    public void Widening_retires_any_other_rectangle_in_its_new_footprint()
    {
        var buffer = new TerminalBuffer(16, 4);
        new AnsiParser(buffer).Process("\x1b[1;3H\x1b]66;s=2;M\a\x1b[1;1H\x1b]66;s=2;❤\a\uFE0F");
        Assert.Equal("❤\uFE0F", Block(buffer).Text);
        Assert.Equal(2, Block(buffer).BaseWidth);
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(0));
    }

    [Fact]
    public void A_stale_joiner_from_another_cell_cannot_append_a_letter_inside_a_sized_block()
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]66;s=2;M\a\x1b[1;6HA\u200D\x1b[1;2HX");
        Assert.Null(buffer.ExportState(0).TextSizing);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Complete_and_incremental_text_presentation_have_the_same_width(int scale)
    {
        var complete = new TerminalBuffer(16, 4);
        var streamed = new TerminalBuffer(16, 4);
        new AnsiParser(complete).Process($"\x1b]66;s={scale};😀\uFE0E\a");
        new AnsiParser(streamed).Process($"\x1b]66;s={scale};😀\a\uFE0E");
        Assert.Equal(1, Block(complete).BaseWidth);
        Assert.Equal(1, Block(streamed).BaseWidth);
        Assert.Equal(scale, complete.CursorCol);
        Assert.Equal(complete.CursorCol, streamed.CursorCol);
        new TerminalBuffer(16, 4).ImportState(streamed.ExportState(0));
    }

    [Theory]
    [InlineData("\uFE0E\uFE0F")]
    [InlineData("\uFE0F\uFE0E")]
    public void Selector_grouping_does_not_change_geometry(string selectors)
    {
        var grouped = new TerminalBuffer(16, 4);
        var split = new TerminalBuffer(16, 4);
        var a = new AnsiParser(grouped);
        var b = new AnsiParser(split);
        a.Process("\x1b]66;s=2;😀\a");
        b.Process("\x1b]66;s=2;😀\a");
        a.Process(selectors);
        foreach (char ch in selectors) b.Process(ch.ToString());
        Assert.Equal(Block(grouped).BaseWidth, Block(split).BaseWidth);
        Assert.Equal(Block(grouped).Text, Block(split).Text);
        Assert.Equal(grouped.CursorCol, split.CursorCol);
    }

    [Fact]
    public void A_new_joined_emoji_base_restores_its_natural_width_after_text_presentation()
    {
        var complete = new TerminalBuffer(16, 4);
        var streamed = new TerminalBuffer(16, 4);
        new AnsiParser(complete).Process("\x1b]66;s=2;👩\uFE0E\u200D👩\a");
        var parser = new AnsiParser(streamed);
        parser.Process("\x1b]66;s=2;👩\a");
        parser.Process("\uFE0E");
        parser.Process("\u200D");
        parser.Process("👩");
        Assert.Equal(2, Block(complete).BaseWidth);
        Assert.Equal(2, Block(streamed).BaseWidth);
        Assert.Equal(Block(complete).Text, Block(streamed).Text);
        Assert.Equal(4, streamed.CursorCol);
        new TerminalBuffer(16, 4).ImportState(streamed.ExportState(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_joiner_sequence_can_continue_at_the_first_column_inside_a_sized_block(int row)
    {
        var buffer = new TerminalBuffer(16, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b]66;s=2;👩\a\x1b[{row + 1};1H");
        parser.Process("\u200D");
        parser.Process("👧");
        Assert.Equal("👩\u200D👧", Block(buffer).Text);
        Assert.Equal(2, Block(buffer).BaseWidth);
        Assert.Equal(0, buffer.CursorCol);
        Assert.Equal(row, buffer.CursorRow);
        new TerminalBuffer(16, 4).ImportState(buffer.ExportState(0));
    }

}
