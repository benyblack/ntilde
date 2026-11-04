using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// DECRQSS <c>m</c> (current SGR) and <c>SP q</c> (DECSCUSR) reports (#844). Replies follow
/// xterm: status 1 for success, a leading 0 in the SGR report, and 1-6 cursor codes for
/// application overrides. Margins (<c>r</c>) are covered by <see cref="DecrqssMarginQueryTests"/>.
/// </summary>
public sealed class DecrqssSettingReportTests
{
    private const string Failure = "\x1bP0$r\x1b\\";
    private const string SgrRequest = "\x1bP$qm\x1b\\";
    private const string CursorRequest = "\x1bP$q q\x1b\\";

    [Theory]
    [InlineData("", "0")]
    [InlineData("\x1b[1m", "0;1")]
    [InlineData("\x1b[2m", "0;2")]
    [InlineData("\x1b[3m", "0;3")]
    [InlineData("\x1b[4m", "0;4")]
    [InlineData("\x1b[4:1m", "0;4")]
    [InlineData("\x1b[4:2m", "0;4:2")]
    [InlineData("\x1b[21m", "0;4:2")]
    [InlineData("\x1b[4:3m", "0;4:3")]
    [InlineData("\x1b[4:4m", "0;4:4")]
    [InlineData("\x1b[4:5m", "0;4:5")]
    [InlineData("\x1b[4:9m", "0;4")]
    [InlineData("\x1b[4:3;4:0m", "0")]
    [InlineData("\x1b[5m", "0;5")]
    [InlineData("\x1b[7m", "0;7")]
    [InlineData("\x1b[8m", "0;8")]
    [InlineData("\x1b[9m", "0;9")]
    [InlineData("\x1b[53m", "0;53")]
    [InlineData("\x1b[1;2;3;4;5;7;8;9;53m", "0;1;2;3;4;5;7;8;9;53")]
    [InlineData("\x1b[1;22m", "0")]
    [InlineData("\x1b[1;3;4;5;7;8;9;53m\x1b[23;24;25;27;28;29;55m", "0;1")]
    [InlineData("\x1b[31m", "0;31")]
    [InlineData("\x1b[97m", "0;97")]
    [InlineData("\x1b[44m", "0;44")]
    [InlineData("\x1b[103m", "0;103")]
    [InlineData("\x1b[38;5;3m", "0;33")]
    [InlineData("\x1b[38;5;12m", "0;94")]
    [InlineData("\x1b[38;5;16m", "0;38;5;16")]
    [InlineData("\x1b[48:5:255m", "0;48;5;255")]
    [InlineData("\x1b[38;2;1;2;3m", "0;38;2;1;2;3")]
    [InlineData("\x1b[48:2::250:128:0m", "0;48;2;250;128;0")]
    [InlineData("\x1b[58;5;9m", "0;58;5;9")]
    [InlineData("\x1b[58:2::10:20:30m", "0;58;2;10;20;30")]
    [InlineData("\x1b[58;5;9;59m", "0")]
    [InlineData("\x1b[31;44;39;49m", "0")]
    [InlineData("\x1b[1;4:3;31;48;5;200;58;2;9;8;7m", "0;1;4:3;31;48;5;200;58;2;9;8;7")]
    [InlineData("\x1b[1;31;4m\x1b[0m", "0")]
    [InlineData("\x1b[1;31;4m\x1b[m", "0")]
    public void Sgr_report_names_the_current_pen_exactly(string setup, string expected)
    {
        Assert.Equal("\x1bP1$r" + expected + "m\x1b\\", Query(setup, SgrRequest));
    }

    [Theory]
    [InlineData("\x1b[1;4:3;31;48;5;200;58;2;9;8;7m")]
    [InlineData("\x1b[2;3;5;7;8;9;53;38;2;1;2;3;104;58;5;9m")]
    [InlineData("\x1b[21;93;48;2;0;0;0m")]
    [InlineData("\x1b[4:5;58:2::255:0:128m")]
    public void Replaying_the_sgr_report_on_a_fresh_terminal_reproduces_the_pen(string setup)
    {
        var source = new TerminalBuffer(12, 4);
        var replies = new List<string>();
        var parser = new AnsiParser(source) { OnResponse = replies.Add };
        parser.Process(setup + SgrRequest);
        string reply = Assert.Single(replies);
        string sgr = reply["\x1bP1$r".Length..^"\x1b\\".Length];

        var target = new TerminalBuffer(12, 4);
        // Start from a different pen: the report's leading 0 must clear it.
        new AnsiParser(target).Process("\x1b[1;3;9;35;46;53m\x1b[" + sgr);
        Assert.Equal(TerminalStateSerializer.ToBytes(source.ExportState(0)),
            TerminalStateSerializer.ToBytes(target.ExportState(0)));
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("\x1b[1 q", "1")]
    [InlineData("\x1b[2 q", "2")]
    [InlineData("\x1b[3 q", "3")]
    [InlineData("\x1b[4 q", "4")]
    [InlineData("\x1b[5 q", "5")]
    [InlineData("\x1b[6 q", "6")]
    [InlineData("\x1b[6 q\x1b[0 q", "0")]
    [InlineData("\x1b[6 q\x1b[ q", "0")]
    [InlineData("\x1b[4 q\x1b[7 q", "4")]
    [InlineData("\x1b[4 q\x1b[!p", "0")]
    [InlineData("\x1b[4 q\u001bc", "0")]
    public void Cursor_report_names_the_current_DECSCUSR_value(string setup, string expected)
    {
        Assert.Equal("\x1bP1$r" + expected + " q\x1b\\", Query(setup, CursorRequest));
    }

    [Fact]
    public void Decscusr_zero_returns_to_the_host_default_presentation()
    {
        var buffer = new TerminalBuffer(12, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b[6 q");
        Assert.False(buffer.Modes.CursorStyleIsDefault);
        parser.Process("\x1b[0 q");
        Assert.True(buffer.Modes.CursorStyleIsDefault);
        Assert.Equal(CursorStyle.Underline, buffer.Modes.CursorStyle);
        Assert.True(buffer.Modes.IsCursorBlinkEnabled);
    }

    [Theory]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [InlineData("\u0090", "\u009c")]
    public void Standard_framings_report_without_mutating_state(string start, string end)
    {
        var buffer = new TerminalBuffer(12, 6);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer)
        {
            OnResponse = reply =>
            {
                Assert.True(buffer.Lock.IsWriteLockHeld);
                replies.Add(reply);
            }
        };
        parser.Process("\x1b[1;4:3;31;58;5;9mtext\x1b[5 q\x1b[2;4H");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process(start + "$qm" + end + start + "$q q" + end);
        Assert.Equal(new[] { "\x1bP1$r0;1;4:3;31;58;5;9m\x1b\\", "\x1bP1$r5 q\x1b\\" }, replies);
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Theory]
    [InlineData("\x1bP$qm\a")]
    [InlineData("\x1bP$q q\a")]
    [InlineData("\u0090$qm\a")]
    [InlineData("\x1bP$qm;\x1b\\")]
    [InlineData("\x1bP$q0m\x1b\\")]
    [InlineData("\x1bP$qmm\x1b\\")]
    [InlineData("\x1bP$q m\x1b\\")]
    [InlineData("\x1bP$qm \x1b\\")]
    [InlineData("\x1bP$qq\x1b\\")]
    [InlineData("\x1bP$q  q\x1b\\")]
    [InlineData("\x1bP$q q \x1b\\")]
    [InlineData("\x1bP$q1 q\x1b\\")]
    [InlineData("\x1bP$q\"q\x1b\\")]
    public void Bel_recovery_and_inexact_settings_stay_negative(string request)
    {
        Assert.Equal(Failure, Query("\x1b[1;31m\x1b[4 q", request));
    }

    [Theory]
    [InlineData("$qm", "\x1bP1$r0;1m\x1b\\")]
    [InlineData("$q q", "\x1bP1$r4 q\x1b\\")]
    public void A_truncated_valid_prefix_stays_negative_and_an_exact_cap_length_request_succeeds(string payload, string success)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 6))
        {
            MaxStringSequenceChars = payload.Length,
            OnResponse = replies.Add,
        };
        parser.Process("\x1b[1m\x1b[4 q");
        parser.Process("\x1bP" + payload + "junk\x1b\\");
        parser.Process("\x1bP" + payload + "\x1b\\");
        Assert.Equal(new[] { Failure, success }, replies);
    }

    [Theory]
    [InlineData("$qm")]
    [InlineData("$q q")]
    [Trait("Category", "Replay")]
    public void Every_split_and_complete_JSON_state_restore_preserve_the_exact_reply(string payload)
    {
        const string Setup = "\x1b[3;4:2;38;2;7;8;9;45;58;5;200m\x1b[3 q";
        string request = "\x1bP" + payload + "\x1b\\";
        string? expected = null;
        for (int cut = 0; cut <= request.Length; cut++)
        {
            var buffer = new TerminalBuffer(12, 6);
            var replies = new List<string>();
            var source = new AnsiParser(buffer) { OnResponse = replies.Add };
            source.Process(Setup);
            source.Process(request[..cut]);
            TerminalStateSnapshot snapshot = buffer.ExportState(0);
            snapshot.Parser = source.ExportState();
            var targetBuffer = new TerminalBuffer(12, 6);
            var resumed = new List<string>();
            var target = new AnsiParser(targetBuffer) { OnResponse = resumed.Add };
            TerminalStateTransfer.Restore(targetBuffer, target,
                TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            int alreadyReplied = replies.Count; // the complete request answers before the snapshot
            source.Process(request[cut..]);
            target.Process(request[cut..]);
            string reply = Assert.Single(replies);
            expected ??= reply;
            Assert.Equal(expected, reply);
            Assert.Equal(replies.Skip(alreadyReplied), resumed);
        }
        Assert.Equal(payload == "$qm" ? "\x1bP1$r0;3;4:2;38;2;7;8;9;45;58;5;200m\x1b\\" : "\x1bP1$r3 q\x1b\\", expected);
    }

    [Fact]
    public void Reports_follow_the_active_screen_pen_and_saved_cursor_restore()
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 6)) { OnResponse = replies.Add };
        parser.Process("\x1b[1;31m\u001b7\x1b[0;4;42m");
        Expect("0;4;42");
        parser.Process("\u001b8");
        Expect("0;1;31");
        parser.Process("\x1b[?1049h\x1b[0;3m");
        Expect("0;3");
        parser.Process("\x1b[?1049l");
        Expect("0;1;31");

        void Expect(string sgr)
        {
            parser.Process(SgrRequest);
            Assert.Equal("\x1bP1$r" + sgr + "m\x1b\\", Assert.Single(replies));
            replies.Clear();
        }
    }

    private static string Query(string setup, string request)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 4)) { OnResponse = replies.Add };
        parser.Process(setup);
        parser.Process(request);
        return Assert.Single(replies);
    }
}
