using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>DECRQSS for DECSLRM: <c>DCS $ q s ST</c> reports <c>DCS 1 $ r Pl ; Pr s ST</c> as xterm does.</summary>
public class DecrqssLeftRightMarginTests
{
    private const string Esc = "\x1b";
    private const string Request = "\x1bP$qs\x1b\\";
    private const string Failure = "\x1bP0$r\x1b\\";

    private static (TerminalBuffer Buffer, AnsiParser Parser, List<string> Replies) Fixture(int cols = 12, int rows = 6)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            OnResponse = reply =>
            {
                Assert.True(buffer.Lock.IsWriteLockHeld);
                replies.Add(reply);
            },
        };
        return (buffer, parser, replies);
    }

    [Fact]
    public void Full_width_is_reported_without_DECLRMM()
    {
        var (_, parser, replies) = Fixture();
        parser.Process(Request);
        Assert.Equal("\x1bP1$r1;12s\x1b\\", Assert.Single(replies));
    }

    [Fact]
    public void Set_margins_are_reported_one_based_and_the_query_does_not_mutate_state()
    {
        var (buffer, parser, replies) = Fixture();
        parser.Process($"{Esc}[31mx{Esc}[?69h{Esc}[5;10s{Esc}[2;3H");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process(Request);
        Assert.Equal("\x1bP1$r5;10s\x1b\\", Assert.Single(replies));
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Fact]
    public void Resetting_DECLRMM_reports_full_width_again()
    {
        var (_, parser, replies) = Fixture();
        parser.Process($"{Esc}[?69h{Esc}[5;10s{Esc}[?69l" + Request);
        Assert.Equal("\x1bP1$r1;12s\x1b\\", Assert.Single(replies));
    }

    [Fact]
    public void Origin_mode_still_reports_absolute_columns()
    {
        var (_, parser, replies) = Fixture();
        parser.Process($"{Esc}[?69h{Esc}[5;10s{Esc}[2;4r{Esc}[?6h" + Request);
        Assert.Equal("\x1bP1$r5;10s\x1b\\", Assert.Single(replies));
    }

    [Fact]
    public void Resize_resets_margins_and_the_report_follows_the_new_width()
    {
        var (buffer, parser, replies) = Fixture();
        parser.Process($"{Esc}[?69h{Esc}[5;10s");
        buffer.Resize(20, 6);
        parser.Process(Request);
        Assert.Equal((buffer.LeftMargin + 1, buffer.RightMargin + 1), ParseReply(Assert.Single(replies)));
        Assert.Equal(buffer.Cols, buffer.RightMargin + 1 + (buffer.Cols - buffer.RightMargin - 1));
    }

    [Fact]
    public void The_reply_round_trips_through_DECLRMM_and_DECSLRM()
    {
        var (_, parser, replies) = Fixture();
        parser.Process($"{Esc}[?69h{Esc}[3;8s" + Request);
        string reply = Assert.Single(replies);
        replies.Clear();

        var (target, targetParser, targetReplies) = Fixture();
        string parameters = reply["\x1bP1$r".Length..^"\x1b\\".Length];
        targetParser.Process($"{Esc}[?69h{Esc}[{parameters}" + Request);
        Assert.Equal((2, 7), (target.LeftMargin, target.RightMargin));
        Assert.Equal(reply, Assert.Single(targetReplies));
    }

    [Theory]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [InlineData("\u0090", "\u009c")]
    public void Standard_framings_get_a_seven_bit_reply(string start, string end)
    {
        var (_, parser, replies) = Fixture();
        parser.Process($"{Esc}[?69h{Esc}[2;4s" + start + "$qs" + end);
        Assert.Equal("\x1bP1$r2;4s\x1b\\", Assert.Single(replies));
    }

    [Theory]
    [InlineData("\x1bP$qs\a")]
    [InlineData("\u0090$qs\a")]
    [InlineData("\x1bP$q s\x1b\\")]
    [InlineData("\x1bP$qss\x1b\\")]
    [InlineData("\x1bP$qx\x1b\\")]
    public void Non_exact_or_unknown_requests_still_fail(string request)
    {
        var (_, parser, replies) = Fixture();
        parser.Process(request);
        Assert.Equal(Failure, Assert.Single(replies));
    }

    [Fact]
    public void Existing_r_m_and_cursor_style_replies_are_unchanged()
    {
        var (_, parser, replies) = Fixture();
        parser.Process($"{Esc}[2;5r\x1bP$qr\x1b\\\x1bP$qm\x1b\\\x1bP$q q\x1b\\");
        Assert.Equal(new[] { "\x1bP1$r2;5r\x1b\\", "\x1bP1$r0m\x1b\\", "\x1bP1$r0 q\x1b\\" }, replies);
    }

    private static (int Left, int Right) ParseReply(string reply)
    {
        string[] parts = reply["\x1bP1$r".Length..^"s\x1b\\".Length].Split(';');
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }
}
