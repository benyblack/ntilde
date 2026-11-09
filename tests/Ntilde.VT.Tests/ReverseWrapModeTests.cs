using Ntilde.VT.Tests.StateTransfer;

namespace Ntilde.VT.Tests;

// xterm's reverse-wraparound (CSI ? 45 h/l): with auto-wrap (?7) also set, BS at the left edge returns to the
// last column of the previous row, but only across a soft wrap. terminfo.dev's cursor.reverse-wrap fixture
// (`A` x cols, `B`, two BS) measured Ntilde ignoring the mode; the stored cursor stayed on row 2, column 1.
public class ReverseWrapModeTests
{
    private const int Cols = 20;

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Responses) Terminal(int rows = 6)
    {
        var buffer = new TerminalBuffer(cols: Cols, rows: rows);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        return (buffer, parser, responses);
    }

    private static char CharacterAt(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row).Character; }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Wrapped => new string('A', Cols) + "B";

    private static string Query(AnsiParser parser, List<string> responses)
    {
        responses.Clear();
        parser.Process("\u001b[?45$p");
        return Assert.Single(responses);
    }

    [Fact]
    public void Without_the_mode_BS_at_the_left_edge_stays_put()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[H" + Wrapped + "\b\b");

        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void With_the_mode_BS_returns_to_the_last_column_of_the_soft_wrapped_row()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?7h\u001b[?45h\u001b[H" + Wrapped);
        Assert.Equal((1, 1), (buffer.CursorRow, buffer.CursorCol));

        parser.Process("\b");
        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));

        parser.Process("\b");
        Assert.Equal((0, Cols - 1), (buffer.CursorRow, buffer.CursorCol));

        // The next write overwrites the last cell of row 1, not a new row.
        parser.Process("Z");
        Assert.Equal('Z', CharacterAt(buffer, Cols - 1, 0));
        Assert.Equal('B', CharacterAt(buffer, 0, 1));
    }

    [Fact]
    public void Reverse_wrap_continues_up_a_chain_of_soft_wraps()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[H" + new string('A', Cols * 2 + 1));
        Assert.Equal((2, 1), (buffer.CursorRow, buffer.CursorCol));

        parser.Process("\b\b");
        Assert.Equal((1, Cols - 1), (buffer.CursorRow, buffer.CursorCol));

        parser.Process(new string('\b', Cols));
        Assert.Equal((0, Cols - 1), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void A_hard_line_break_is_never_crossed()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[HAAAA\r\nB\b\b");

        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void The_top_of_the_screen_is_never_crossed()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[H\b\b");

        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Auto_wrap_must_also_be_set()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?7h\u001b[?45h\u001b[H" + Wrapped + "\u001b[?7l\b\b");

        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void The_top_margin_of_the_active_region_is_not_crossed()
    {
        var (buffer, parser, _) = Terminal();
        // Row 2 (1-based) soft-wraps into row 3; the region then starts at row 3, so BS at its left edge cannot leave it.
        parser.Process("\u001b[?45h\u001b[2;1H" + Wrapped + "\u001b[3;6r\u001b[3;1H\b");
        Assert.Equal((2, 0), (buffer.CursorRow, buffer.CursorCol));

        // Outside any region the same wrap is crossable again.
        parser.Process("\u001b[r\u001b[3;1H\b");
        Assert.Equal((1, Cols - 1), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void A_pending_wrap_is_consumed_without_moving_before_the_cursor_goes_left()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[H" + new string('A', Cols) + "\b");
        Assert.Equal((0, Cols - 1), (buffer.CursorRow, buffer.CursorCol));

        parser.Process("\b");
        Assert.Equal((0, Cols - 2), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Without_the_mode_BS_with_a_pending_wrap_keeps_its_one_cell_move()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[H" + new string('A', Cols) + "\b");

        Assert.Equal((0, Cols - 2), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void CUB_reverse_wraps_exactly_like_BS()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[H" + Wrapped + "\u001b[2D");
        Assert.Equal((0, Cols - 1), (buffer.CursorRow, buffer.CursorCol));

        // A hard break stops it, however large the count.
        parser.Process("\u001b[3;1HAB\r\n\u001b[9D");
        Assert.Equal((3, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void CUB_counts_physical_columns_over_wide_characters_in_reverse_wrap_mode()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h\u001b[H\u4e16X\u001b[2D");

        Assert.Equal((0, 1), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void CUB_without_the_mode_still_clamps_at_the_left_edge()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[H" + Wrapped + "\u001b[9D");

        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void DECRQM_reports_the_live_state()
    {
        var (_, parser, responses) = Terminal();
        Assert.Equal("\u001b[?45;2$y", Query(parser, responses));

        parser.Process("\u001b[?45h");
        Assert.Equal("\u001b[?45;1$y", Query(parser, responses));

        parser.Process("\u001b[?45l");
        Assert.Equal("\u001b[?45;2$y", Query(parser, responses));
    }

    [Theory]
    [InlineData("\u001b[!p")] // DECSTR
    [InlineData("\u001bc")]   // RIS
    public void A_reset_returns_the_mode_to_off(string reset)
    {
        var (buffer, parser, responses) = Terminal();
        parser.Process("\u001b[?45h");
        Assert.Equal("\u001b[?45;1$y", Query(parser, responses));

        parser.Process(reset);
        Assert.Equal("\u001b[?45;2$y", Query(parser, responses));

        parser.Process("\u001b[H" + Wrapped + "\b\b");
        Assert.Equal((1, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void The_mode_survives_a_state_export_and_import()
    {
        var (buffer, parser, _) = Terminal();
        parser.Process("\u001b[?45h");

        var snapshot = buffer.ExportState(maxScrollbackRows: 0, includeImages: false);
        Assert.True(snapshot.Modes.IsReverseWrapMode);

        var restored = new TerminalBuffer(cols: Cols, rows: 6);
        restored.ImportState(snapshot);
        Assert.True(restored.Modes.IsReverseWrapMode);
        Assert.True(restored.Modes.Clone().IsReverseWrapMode);

        var other = new AnsiParser(restored);
        other.Process("\u001b[H" + Wrapped + "\b\b");
        Assert.Equal((0, Cols - 1), (restored.CursorRow, restored.CursorCol));
    }
}
