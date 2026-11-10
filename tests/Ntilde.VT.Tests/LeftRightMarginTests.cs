using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECLRMM (<c>CSI ? 69 h/l</c>) and DECSLRM (<c>CSI Pl ; Pr s</c>): left/right margins. Without DECLRMM <c>CSI s</c> stays SCOSC;
/// with it DECSLRM sets the margins and homes the cursor. The margins bound printing and autowrap, CR, CUF/CUB/BS, HT, origin-mode
/// addressing and CPR, IND/RI/NEL/LF scrolling at the region edges, SU/SD, IL/DL, ICH/DCH, DECIC/DECDC/SL/SR and DECOM rectangles.
/// </summary>
public sealed class LeftRightMarginTests
{
    private const string Esc = "\u001b";

    /// <summary>A 10x6 screen, rows seeded with ten distinct characters each, DECLRMM on and margins 3-7 (zero-based 2-6).</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser) Seeded(bool margins = true, int rows = 6)
    {
        var buffer = new TerminalBuffer(10, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        for (int row = 0; row < rows; row++)
            parser.Process($"{Esc}[{row + 1};1H" + new string(Enumerable.Range(0, 10).Select(col => (char)('A' + (row * 10 + col) % 26)).ToArray()));
        if (margins) parser.Process($"{Esc}[?69h{Esc}[3;7s");
        return (buffer, parser);
    }

    private static string[] Lines(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return Enumerable.Range(0, buffer.Rows).Select(row => new string(Enumerable.Range(0, buffer.Cols)
                .Select(col => buffer.GetCell(col, row).Character == '\0' ? ' ' : buffer.GetCell(col, row).Character).ToArray())).ToArray();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static (int Row, int Col) Cursor(TerminalBuffer buffer) => (buffer.CursorRow, buffer.CursorCol);

    private static List<string> Replies(AnsiParser parser, string input)
    {
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process(input);
        parser.OnResponse = null;
        return replies;
    }

    // ---- mode, DECSLRM and SCOSC -------------------------------------------------------------------------------------------

    [Fact]
    public void Without_DECLRMM_CSI_s_saves_the_cursor_and_sets_no_margins()
    {
        var (buffer, parser) = Seeded(margins: false);
        parser.Process($"{Esc}[2;4H{Esc}[3;7s{Esc}[1;1H{Esc}[u");
        Assert.Equal((1, 3), Cursor(buffer));
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        Assert.False(buffer.HasHorizontalMargins);
        Assert.Equal(new[] { $"{Esc}[?69;2$y" }, Replies(parser, $"{Esc}[?69$p"));
    }

    [Fact]
    public void DECSLRM_sets_the_margins_and_homes_the_cursor()
    {
        var (buffer, parser) = Seeded();
        Assert.Equal((2, 6), (buffer.LeftMargin, buffer.RightMargin));
        Assert.Equal((0, 0), Cursor(buffer));
        Assert.Equal(new[] { $"{Esc}[?69;1$y" }, Replies(parser, $"{Esc}[?69$p"));
        parser.Process($"{Esc}[3;4r{Esc}[?6h{Esc}[4;9s");
        Assert.Equal((3, 8), (buffer.LeftMargin, buffer.RightMargin));
        Assert.Equal((2, 3), Cursor(buffer)); // origin mode homes to the top-left margin corner
    }

    [Theory]
    [InlineData("s", 0, 9)]
    [InlineData("0;0s", 0, 9)]
    [InlineData("4s", 3, 9)]
    [InlineData(";5s", 0, 4)]
    [InlineData("2;99s", 1, 9)]
    public void DECSLRM_defaults_and_clamps(string parameters, int left, int right)
    {
        var (buffer, parser) = Seeded(margins: false);
        parser.Process($"{Esc}[?69h{Esc}[{parameters}");
        Assert.Equal((left, right), (buffer.LeftMargin, buffer.RightMargin));
    }

    [Theory]
    [InlineData("5;5s")]
    [InlineData("7;3s")]
    [InlineData("10;10s")]
    public void A_left_margin_not_left_of_the_right_one_is_ignored(string parameters)
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;2H{Esc}[{parameters}");
        Assert.Equal((2, 6), (buffer.LeftMargin, buffer.RightMargin));
        Assert.Equal((1, 1), Cursor(buffer)); // an ignored DECSLRM does not home the cursor
    }

    [Fact]
    public void Resetting_DECLRMM_restores_full_width_margins()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[?69l");
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        Assert.False(buffer.Modes.IsLeftRightMarginMode);
    }

    // ---- printing, wrap, CR, cursor movement -------------------------------------------------------------------------------

    [Fact]
    public void Printing_wraps_at_the_right_margin_to_the_left_margin_and_leaves_the_outside_alone()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;3H123456789A");
        var lines = Lines(buffer);
        Assert.Equal("AB12345HIJ", lines[0]);
        Assert.Equal("KL6789ARST", lines[1]);
        Assert.Equal((1, 6), Cursor(buffer));
        Assert.True(buffer.IsPendingWrap, "the fifth character filled the margin: the wrap is pending");
        buffer.Lock.EnterReadLock();
        try { Assert.False(buffer.GetRowAbsolute(buffer.Scrollback.Count)!.IsWrapped, "margin wraps are not soft wraps of the whole row"); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Without_autowrap_printing_overwrites_the_right_margin()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[?7l{Esc}[1;6Hxyz");
        Assert.Equal("ABCDExzHIJ", Lines(buffer)[0]);
        Assert.Equal((0, 6), Cursor(buffer));
    }

    [Fact]
    public void A_cursor_right_of_the_right_margin_prints_to_the_page_edge()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;9Hxyz");
        Assert.Equal("ABCDEFGHxy", Lines(buffer)[0]);
        Assert.Equal("KLzNOPQRST", Lines(buffer)[1]); // the wrap still returns to the left margin
    }

    [Fact]
    public void CR_returns_to_the_left_margin_from_inside_and_to_the_first_column_from_the_left()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;6H\r");
        Assert.Equal(2, buffer.CursorCol);
        parser.Process($"{Esc}[1;2H\r");
        Assert.Equal(0, buffer.CursorCol);
        parser.Process($"{Esc}[?6h{Esc}[1;1H\r");
        Assert.Equal(2, buffer.CursorCol); // in origin mode CR always goes to the left margin
    }

    [Fact]
    public void CUF_CUB_and_BS_stop_at_the_margins_from_inside_and_at_the_page_edges_from_outside()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;4H{Esc}[20C");
        Assert.Equal(6, buffer.CursorCol);
        parser.Process($"{Esc}[1;9H{Esc}[20C");
        Assert.Equal(9, buffer.CursorCol);
        parser.Process($"{Esc}[1;6H{Esc}[20D");
        Assert.Equal(2, buffer.CursorCol);
        parser.Process($"{Esc}[1;2H{Esc}[20D");
        Assert.Equal(0, buffer.CursorCol);
        parser.Process($"{Esc}[1;3H\b");
        Assert.Equal(2, buffer.CursorCol); // BS stops at the left margin
        parser.Process($"{Esc}[1;6H{Esc}[2a");
        Assert.Equal(6, buffer.CursorCol); // HPR stops at the right margin
    }

    [Fact]
    public void Origin_mode_addresses_columns_from_the_left_margin_and_CPR_reports_them_so()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;5r{Esc}[?6h{Esc}[1;1H");
        Assert.Equal((1, 2), Cursor(buffer));
        parser.Process($"{Esc}[2;3H");
        Assert.Equal((2, 4), Cursor(buffer));
        Assert.Equal(new[] { $"{Esc}[2;3R" }, Replies(parser, $"{Esc}[6n"));
        parser.Process($"{Esc}[1;99H");
        Assert.Equal((1, 6), Cursor(buffer)); // columns past the right margin clamp to it
        parser.Process($"{Esc}[2G");
        Assert.Equal(3, buffer.CursorCol);
        parser.Process($"{Esc}[9`");
        Assert.Equal(6, buffer.CursorCol);
        parser.Process($"{Esc}[?6l{Esc}[1;9H");
        Assert.Equal((0, 8), Cursor(buffer)); // without origin mode CUP is absolute
        Assert.Equal(new[] { $"{Esc}[1;9R" }, Replies(parser, $"{Esc}[6n"));
    }

    [Fact]
    public void HT_stops_at_the_right_margin_from_inside()
    {
        var (buffer, parser) = Seeded(margins: false);
        parser.Process($"{Esc}[?69h{Esc}[1;5s{Esc}[1;1H\t");
        Assert.Equal(4, buffer.CursorCol);
        parser.Process($"{Esc}[1;6H\t");
        Assert.Equal(8, buffer.CursorCol); // right of the right margin the next stop is reached past the margin
    }

    // ---- scrolling ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void LF_at_the_bottom_margin_scrolls_only_the_band_between_the_margins()
    {
        var (buffer, parser) = Seeded();
        long history = buffer.Scrollback.Count;
        parser.Process($"{Esc}[6;4H\n");
        var lines = Lines(buffer);
        Assert.Equal("ABMNOPQHIJ", lines[0]);
        Assert.Equal("KLWXYZARST", lines[1]);
        Assert.Equal("YZ     FGH", lines[5]);
        Assert.Equal((5, 3), Cursor(buffer));
        Assert.Equal(history, buffer.Scrollback.Count); // a band scroll never feeds scrollback
    }

    [Fact]
    public void LF_at_the_bottom_margin_outside_the_margins_neither_scrolls_nor_moves()
    {
        var (buffer, parser) = Seeded();
        string[] before = Lines(buffer);
        parser.Process($"{Esc}[6;2H\n{Esc}[6;9H{Esc}D");
        Assert.Equal(before, Lines(buffer));
        Assert.Equal((5, 8), Cursor(buffer));
    }

    [Fact]
    public void RI_and_SD_scroll_the_band_down_and_SU_scrolls_it_up_wherever_the_cursor_is()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;4H{Esc}M");
        Assert.Equal("AB     HIJ", Lines(buffer)[0]);
        Assert.Equal("KLCDEFGRST", Lines(buffer)[1]);
        var (other, otherParser) = Seeded();
        otherParser.Process($"{Esc}[1;1H{Esc}[S");
        Assert.Equal("ABMNOPQHIJ", Lines(other)[0]); // SU ignores the cursor column
        otherParser.Process($"{Esc}[T");
        Assert.Equal("AB     HIJ", Lines(other)[0]);
    }

    [Fact]
    public void NEL_returns_to_the_left_margin_then_indexes()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;6H{Esc}E");
        Assert.Equal((2, 2), Cursor(buffer));
    }

    [Fact]
    public void IL_and_DL_move_only_the_band_and_do_nothing_outside_the_margins()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[2;4H{Esc}[2L");
        var lines = Lines(buffer);
        Assert.Equal("KL     RST", lines[1]);
        Assert.Equal("UV     BCD", lines[2]);
        Assert.Equal("EFMNOPQLMN", lines[3]);
        Assert.Equal("OPWXYZAVWX", lines[4]);
        var (deleted, deleteParser) = Seeded();
        deleteParser.Process($"{Esc}[2;4H{Esc}[M");
        Assert.Equal("KLWXYZARST", Lines(deleted)[1]);
        Assert.Equal("YZ     FGH", Lines(deleted)[5]);
        var (outside, outsideParser) = Seeded();
        string[] before = Lines(outside);
        outsideParser.Process($"{Esc}[2;2H{Esc}[L{Esc}[2;9H{Esc}[M");
        Assert.Equal(before, Lines(outside));
    }

    [Fact]
    public void ICH_and_DCH_shift_only_up_to_the_right_margin()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;4H{Esc}[2@");
        Assert.Equal("ABC  DEHIJ", Lines(buffer)[0]);
        parser.Process($"{Esc}[2;4H{Esc}[2P");
        Assert.Equal("KLMPQ  RST", Lines(buffer)[1]);
        string[] before = Lines(buffer);
        parser.Process($"{Esc}[3;2H{Esc}[@{Esc}[3;9H{Esc}[P");
        Assert.Equal(before, Lines(buffer)); // outside the margins ICH and DCH do nothing
    }

    [Fact]
    public void Insert_mode_printing_pushes_text_only_to_the_right_margin()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[4h{Esc}[1;4Hx");
        Assert.Equal("ABCxDEFHIJ", Lines(buffer)[0]);
    }

    [Fact]
    public void Column_edits_act_between_the_margins()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[1;4H{Esc}[2'}}");
        Assert.Equal("ABC  DEHIJ", Lines(buffer)[0]);
        var (left, leftParser) = Seeded();
        leftParser.Process($"{Esc}[1;9H{Esc}[2 @");
        Assert.Equal(Lines(Seeded().Buffer), Lines(left)); // outside the margins SL does nothing
        leftParser.Process($"{Esc}[1;4H{Esc}[2 @");
        Assert.Equal("ABEFG  HIJ", Lines(left)[0]);
        Assert.Equal("YZCDE  FGH", Lines(left)[5]);
    }

    [Fact]
    public void Origin_mode_rectangles_offset_columns_by_the_left_margin()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[?6h{Esc}[1;1;1;2$z");
        Assert.Equal("AB  EFGHIJ", Lines(buffer)[0]);
    }

    // ---- wide glyphs and side tables ---------------------------------------------------------------------------------------

    [Fact]
    public void A_band_scroll_retires_a_wide_glyph_split_by_a_margin_and_carries_hyperlinks()
    {
        var buffer = new TerminalBuffer(10, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process($"{Esc}[2;2H中{Esc}]8;;https://x{Esc}\\L{Esc}]8;;{Esc}\\{Esc}[?69h{Esc}[3;7s{Esc}[3;4H\n");
        buffer.Lock.EnterReadLock();
        try
        {
            Assert.Equal(' ', buffer.GetCell(1, 1).Character);
            Assert.False(buffer.GetCell(1, 1).IsWide, "the lead left of the margin lost its continuation, so it was retired");
            Assert.Equal('L', buffer.GetCell(3, 0).Character);
            Assert.NotNull(buffer.GetRowAbsolute(buffer.Scrollback.Count)!.GetHyperlink(3));
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void LF_in_newline_mode_indexes_first_then_returns_to_the_left_margin()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[20h{Esc}[6;5H\n");
        Assert.Equal("ABMNOPQHIJ", Lines(buffer)[0]); // the band scrolled: the cursor was inside the margins at the bottom
        Assert.Equal((5, 2), Cursor(buffer)); // LNM's CR went to the left margin after the index
    }

    [Fact]
    public void A_legacy_replay_snapshot_restores_full_width_margins_and_the_mode_reset()
    {
        var (buffer, _) = Seeded();
        buffer.ApplySnapshot(new ReplaySnapshot { Cols = 10, Rows = 6, ScrollBottom = 5 });
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        Assert.False(buffer.Modes.IsLeftRightMarginMode);
    }

    // ---- resets, resize and state transfer ---------------------------------------------------------------------------------

    [Fact]
    public void RIS_resets_the_mode_and_margins_DECSTR_and_DECALN_reset_the_margins_only()
    {
        var (buffer, parser) = Seeded();
        parser.Process($"{Esc}[!p");
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        Assert.True(buffer.Modes.IsLeftRightMarginMode);
        parser.Process($"{Esc}[3;7s{Esc}#8");
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        parser.Process($"{Esc}[3;7s{Esc}c");
        Assert.Equal((0, 9), (buffer.LeftMargin, buffer.RightMargin));
        Assert.False(buffer.Modes.IsLeftRightMarginMode);
    }

    [Fact]
    public void A_resize_restores_full_width_margins()
    {
        var (buffer, _) = Seeded();
        buffer.Resize(12, 6);
        Assert.Equal((0, 11), (buffer.LeftMargin, buffer.RightMargin));
    }

    [Fact]
    public void State_transfer_carries_the_margins_and_the_mode_and_full_width_payloads_stay_unchanged()
    {
        var (buffer, _) = Seeded();
        var restored = new TerminalBuffer(10, 6);
        restored.ImportState(TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue))));
        Assert.Equal((2, 6), (restored.LeftMargin, restored.RightMargin));
        Assert.True(restored.Modes.IsLeftRightMarginMode);

        var (plain, _) = Seeded(margins: false);
        string json = System.Text.Encoding.UTF8.GetString(TerminalStateSerializer.ToBytes(plain.ExportState(int.MaxValue)));
        Assert.DoesNotContain("lr_left", json, StringComparison.Ordinal);
        Assert.DoesNotContain("declrmm", json, StringComparison.Ordinal);
    }
}
