using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECRQCRA (<c>CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y</c>) contract: silent unless the host turns on
/// <see cref="AnsiParser.AllowRectangleChecksumReport"/>; when on, the reply is <c>DCS Pi ! ~ XXXX ST</c>, the negated 16-bit sum of
/// each cell's character (a blank is a space, a wide glyph counts once, combining marks add nothing) plus the bold 0x80, blink
/// 0x40, reverse 0x20 and underline 0x10 weights. The request never changes terminal state.
/// </summary>
public sealed class RectangleChecksumTests
{
    private const string Esc = "\u001b";

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Create(int cols = 8, int rows = 4, bool allow = true)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { AllowRectangleChecksumReport = allow };
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        return (buffer, parser, replies);
    }

    /// <summary>The expected reply for request <paramref name="id"/> over cells summing to <paramref name="sum"/>.</summary>
    private static string Reply(int id, int sum) => $"{Esc}P{id}!~{unchecked((ushort)(-sum)):X4}{Esc}\\";

    private static string Ask(AnsiParser parser, List<string> replies, string parameters)
    {
        replies.Clear();
        parser.Process($"{Esc}[{parameters}*y");
        return Assert.Single(replies);
    }

    private static byte[] State(TerminalBuffer buffer) => TerminalStateSerializer.ToBytes(buffer.ExportState(int.MaxValue));

    [Fact]
    public void The_report_is_off_by_default_and_the_request_stays_silent()
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        Assert.False(parser.AllowRectangleChecksumReport);
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process("ABCDE");
        byte[] before = State(buffer);
        parser.Process($"{Esc}[1;1;1;1;1;1*y{Esc}[1;1;1;1;1;5*y{Esc}[*y");
        Assert.Empty(replies);
        Assert.Equal(before, State(buffer));
    }

    [Fact]
    public void Turning_the_gate_off_again_silences_the_next_request()
    {
        var (_, parser, replies) = Create();
        parser.Process("A");
        Assert.Equal(Reply(1, 'A'), Ask(parser, replies, "1;1;1;1;1;1"));
        parser.AllowRectangleChecksumReport = false;
        replies.Clear();
        parser.Process($"{Esc}[1;1;1;1;1;1*y");
        Assert.Empty(replies);
    }

    [Fact]
    public void Pinned_spellings_answer_with_the_checksum_of_the_seeded_cells()
    {
        var (_, parser, replies) = Create();
        parser.Process($"ABCDE{Esc}[1;1H");
        Assert.Equal(Reply(1, 'A' + 'B' + 'C' + 'D' + 'E'), Ask(parser, replies, "1;1;1;1;1;5"));
        Assert.Equal($"{Esc}P1!~FEB1{Esc}\\", Ask(parser, replies, "1;1;1;1;1;5"));
        Assert.Equal(Reply(1, 'A'), Ask(parser, replies, "1;1;1;1;1;1"));
        Assert.Equal($"{Esc}P1!~FFBF{Esc}\\", Ask(parser, replies, "1;1;1;1;1;1"));
    }

    [Fact]
    public void Blank_cells_count_as_spaces_whether_never_written_or_erased()
    {
        var (_, parser, replies) = Create();
        Assert.Equal(Reply(0, 32 * ' '), Ask(parser, replies, ""));
        parser.Process($"XYZ{Esc}[2J");
        Assert.Equal(Reply(0, 32 * ' '), Ask(parser, replies, ""));
        parser.Process($"{Esc}[1;1HQ{Esc}[1;1H{Esc}[K");
        Assert.Equal(Reply(5, 8 * ' '), Ask(parser, replies, "5;1;1;1;1;8"));
    }

    [Theory]
    [InlineData("1", 0x80)]
    [InlineData("5", 0x40)]
    [InlineData("7", 0x20)]
    [InlineData("4", 0x10)]
    [InlineData("4:3", 0x10)]
    [InlineData("1;5;7;4", 0xF0)]
    [InlineData("2", 0)]
    [InlineData("3", 0)]
    [InlineData("8", 0)]
    [InlineData("9", 0)]
    [InlineData("53", 0)]
    [InlineData("31;44", 0)]
    [InlineData("38;2;1;2;3;48;5;200", 0)]
    public void Bold_blink_reverse_and_underline_add_their_weights_and_nothing_else_does(string sgr, int weight)
    {
        var (_, parser, replies) = Create();
        parser.Process($"{Esc}[{sgr}mA{Esc}[0m");
        Assert.Equal(Reply(1, 'A' + weight), Ask(parser, replies, "1;1;1;1;1;1"));
    }

    [Fact]
    public void A_space_written_under_an_attribute_carries_its_weight()
    {
        var (_, parser, replies) = Create();
        parser.Process($"{Esc}[7m {Esc}[0m");
        Assert.Equal(Reply(1, ' ' + 0x20), Ask(parser, replies, "1;1;1;1;1;1"));
    }

    [Fact]
    public void A_wide_glyph_counts_once_at_its_lead_cell()
    {
        var (_, parser, replies) = Create();
        parser.Process("中A");
        Assert.Equal(Reply(1, 0x4E2D + 'A'), Ask(parser, replies, "1;1;1;1;1;3"));
        Assert.Equal(Reply(1, 0x4E2D), Ask(parser, replies, "1;1;1;1;1;1"));
        Assert.Equal(Reply(1, 0), Ask(parser, replies, "1;1;1;2;1;2"));
        Assert.Equal($"{Esc}P1!~0000{Esc}\\", Ask(parser, replies, "1;1;1;2;1;2"));
    }

    [Fact]
    public void A_supplementary_glyph_counts_its_full_scalar_value()
    {
        var (_, parser, replies) = Create();
        parser.Process("\U0001F600");
        Assert.Equal(Reply(1, 0x1F600), Ask(parser, replies, "1;1;1;1;1;2"));
    }

    [Fact]
    public void Combining_marks_add_nothing_to_their_base_character()
    {
        var (_, parser, replies) = Create();
        parser.Process("éx");
        Assert.Equal(Reply(1, 'e' + 'x'), Ask(parser, replies, "1;1;1;1;1;2"));
    }

    [Fact]
    public void Omitted_and_zero_borders_take_the_page_defaults_and_oversized_borders_clamp()
    {
        var (_, parser, replies) = Create();
        parser.Process($"{Esc}[4;8HZ{Esc}[1;1HA");
        int page = 'A' + 'Z' + 30 * ' ';
        Assert.Equal(Reply(0, page), Ask(parser, replies, ""));
        Assert.Equal(Reply(3, page), Ask(parser, replies, "3"));
        Assert.Equal(Reply(3, page), Ask(parser, replies, "3;0;0;0;0;0"));
        Assert.Equal(Reply(3, page), Ask(parser, replies, "3;1;1;1;999;999"));
        Assert.Equal(Reply(3, 'Z'), Ask(parser, replies, "3;1;4;8"));
        Assert.Equal(Reply(3, 'Z'), Ask(parser, replies, "3;1;999;999;999;999"));
    }

    [Fact]
    public void The_page_parameter_is_not_interpreted()
    {
        var (_, parser, replies) = Create();
        parser.Process("AB");
        Assert.Equal(Reply(9, 'A' + 'B'), Ask(parser, replies, "9;1;1;1;1;2"));
        Assert.Equal(Reply(9, 'A' + 'B'), Ask(parser, replies, "9;7;1;1;1;2"));
        Assert.Equal(Reply(9, 'A' + 'B'), Ask(parser, replies, "9;0;1;1;1;2"));
    }

    [Fact]
    public void The_request_id_is_echoed_and_an_omitted_id_is_zero()
    {
        var (_, parser, replies) = Create();
        parser.Process("A");
        Assert.Equal(Reply(0, 'A'), Ask(parser, replies, ";;1;1;1;1"));
        Assert.Equal(Reply(65535, 'A'), Ask(parser, replies, "65535;;1;1;1;1"));
    }

    [Fact]
    public void An_inverted_rectangle_sums_nothing()
    {
        var (_, parser, replies) = Create();
        parser.Process("ABCD");
        Assert.Equal($"{Esc}P1!~0000{Esc}\\", Ask(parser, replies, "1;1;1;4;1;1"));
        Assert.Equal($"{Esc}P1!~0000{Esc}\\", Ask(parser, replies, "1;1;3;1;2;8"));
    }

    [Fact]
    public void Origin_mode_offsets_rows_by_the_top_margin_and_the_page_still_clips()
    {
        var (_, parser, replies) = Create();
        parser.Process($"{Esc}[1;1HA{Esc}[2;1HB{Esc}[3;1HC{Esc}[2;3r{Esc}[?6h");
        Assert.Equal(Reply(1, 'B'), Ask(parser, replies, "1;1;1;1;1;1"));
        Assert.Equal(Reply(1, 'B' + 'C' + 22 * ' '), Ask(parser, replies, "1;1;1;1;9;8"));
        parser.Process($"{Esc}[?6l");
        Assert.Equal(Reply(1, 'A'), Ask(parser, replies, "1;1;1;1;1;1"));
    }

    [Fact]
    public void The_alternate_screen_reports_its_own_cells()
    {
        var (_, parser, replies) = Create();
        parser.Process($"A{Esc}[?1049h{Esc}[1;1HQ");
        Assert.Equal(Reply(1, 'Q'), Ask(parser, replies, "1;1;1;1;1;1"));
        parser.Process($"{Esc}[?1049l");
        Assert.Equal(Reply(1, 'A'), Ask(parser, replies, "1;1;1;1;1;1"));
    }

    [Theory]
    [InlineData("\u001b[?1;1;1;1;1;1*y")]
    [InlineData("\u001b[>1;1;1;1;1;1*y")]
    [InlineData("\u001b[1;1;1;1;1;1;1*y")]
    [InlineData("\u001b[1;1;1:2;1;1;1*y")]
    [InlineData("\u001b[1;1;1;1;1;1$y")]
    [InlineData("\u001b[1;1;1;1;1;1 *y")]
    [InlineData("\u001b[1;1;1;1;1;1y")]
    public void Only_the_exact_identity_is_answered(string request)
    {
        var (buffer, parser, replies) = Create();
        parser.Process("A");
        byte[] before = State(buffer);
        parser.Process(request);
        Assert.Empty(replies);
        Assert.Equal(before, State(buffer));
    }

    [Fact]
    public void The_request_changes_no_terminal_state()
    {
        var (buffer, parser, replies) = Create();
        parser.Process($"{Esc}[31;1mABCDEFGH{Esc}[2;3r{Esc}[?6h");
        byte[] before = State(buffer);
        parser.Process($"{Esc}[1;1;1;1;4;8*y{Esc}[7;1;2;2;3;3*y");
        Assert.Equal(2, replies.Count);
        Assert.Equal(before, State(buffer));
    }

    [Fact]
    public void A_full_page_of_large_scalars_wraps_modulo_two_to_the_sixteenth()
    {
        var (_, parser, replies) = Create(cols: 200, rows: 100);
        for (int row = 1; row <= 100; row++) parser.Process($"{Esc}[{row};1H" + string.Concat(Enumerable.Repeat("\U0001F600", 100)));
        long sum = 100L * 100 * 0x1F600;
        Assert.Equal(Reply(1, unchecked((int)sum)), Ask(parser, replies, "1"));
    }
}
