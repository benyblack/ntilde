using System.Text.Json.Nodes;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public class DecrqssMarginQueryTests
{
    private const string Failure = "\x1bP0$r\x1b\\";
    private const string Request = "\x1bP$qr\x1b\\";

    [Theory]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [InlineData("\u0090", "\u009c")]
    public void Standard_framing_reports_absolute_current_margins_without_mutation(string start, string end)
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
        parser.Process("\x1b[31;4mcontent\x1b[2;5r\x1b[?6h\x1b[3;4H");
        byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        parser.Process(start + "$qr" + end);
        Assert.Equal("\x1bP1$r2;5r\x1b\\", Assert.Single(replies));
        Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
    }

    [Fact]
    public void A_single_row_terminal_reports_its_actual_full_screen_region()
    {
        var replies = new List<string>();
        new AnsiParser(new TerminalBuffer(12, 1)) { OnResponse = replies.Add }.Process(Request);
        Assert.Equal("\x1bP1$r1;1r\x1b\\", Assert.Single(replies));
    }

    [Theory]
    [InlineData("\x1bP")]
    [InlineData("\u0090")]
    public void Permissive_BEL_recovery_retains_the_bounded_negative_reply(string start)
    {
        AssertReply(start + "$qr\a", Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("s")]
    [InlineData("r;")]
    [InlineData("0r")]
    [InlineData("r:0")]
    [InlineData(" r")]
    [InlineData("r ")]
    [InlineData("rr")]
    [InlineData("r\0")]
    [InlineData("r\x18")]
    [InlineData("r\x1a")]
    [InlineData("r\r")]
    public void Other_settings_and_extra_payload_bytes_cannot_alias_the_exact_r_request(string setting)
    {
        AssertReply("\x1bP$q" + setting + "\x1b\\", Failure);
    }

    [Theory]
    [InlineData("0$qr")]
    [InlineData("?$qr")]
    [InlineData("$Qr")]
    public void Different_DCS_headers_stay_silent(string payload)
    {
        AssertReply("\x1bP" + payload + "\x1b\\", null);
    }

    [Fact]
    public void Exact_cap_length_succeeds_but_a_truncated_valid_prefix_stays_negative()
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 6))
        {
            MaxStringSequenceChars = 3,
            OnResponse = replies.Add,
        };
        parser.Process("\x1bP$qrjunk\x1b\\");
        Assert.Equal(Failure, Assert.Single(replies));
        parser.Process(Request);
        Assert.Equal(new[] { Failure, "\x1bP1$r1;6r\x1b\\" }, replies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_clipping_or_unknown_legacy_completeness_cannot_create_a_positive_reply(bool legacy)
    {
        var buffer = new TerminalBuffer(12, 6);
        var source = new AnsiParser(buffer);
        source.Process("\x1bP$qr" + (legacy ? "" : "junk"));
        TerminalStateSnapshot snapshot = buffer.ExportState(0);
        snapshot.Parser = source.ExportState();
        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);
        if (legacy)
        {
            JsonNode json = JsonNode.Parse(bytes)!;
            json["parser"]!.AsObject().Remove("dcs_trunc");
            bytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        }
        var replies = new List<string>();
        var targetBuffer = new TerminalBuffer(12, 6);
        var target = new AnsiParser(targetBuffer) { MaxStringSequenceChars = 3, OnResponse = replies.Add };
        TerminalStateTransfer.Restore(targetBuffer, target, TerminalStateSerializer.FromBytes(bytes));
        Assert.Equal(true, target.ExportState().DcsTruncated);
        target.Process("\x1b\\");
        Assert.Equal(Failure, Assert.Single(replies));
    }

    [Theory]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [InlineData("\u0090", "\u009c")]
    [Trait("Category", "Replay")]
    public void Every_split_and_complete_JSON_state_restore_preserves_the_exact_reply(string start, string end)
    {
        string request = start + "$qr" + end;
        for (int cut = 0; cut <= request.Length; cut++)
        {
            var buffer = new TerminalBuffer(12, 6);
            var replies = new List<string>();
            var source = new AnsiParser(buffer) { MaxStringSequenceChars = 3, OnResponse = replies.Add };
            source.Process("\x1b[2;5r\x1b[?6h\x1b[3;4H");
            source.Process(request[..cut]);
            TerminalStateSnapshot snapshot = buffer.ExportState(0);
            snapshot.Parser = source.ExportState();
            var targetBuffer = new TerminalBuffer(12, 6);
            var resumedReplies = new List<string>();
            var target = new AnsiParser(targetBuffer) { MaxStringSequenceChars = 3, OnResponse = resumedReplies.Add };
            TerminalStateTransfer.Restore(targetBuffer, target,
                TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            int alreadyReplied = replies.Count;
            source.Process(request[cut..]);
            target.Process(request[cut..]);
            Assert.Equal("\x1bP1$r2;5r\x1b\\", Assert.Single(replies));
            Assert.Equal(replies.Skip(alreadyReplied), resumedReplies);
            Assert.Equal(source.ExportState().ToDebugString(), target.ExportState().ToDebugString());
            Assert.Equal(TerminalStateSerializer.ToBytes(buffer.ExportState(0)),
                TerminalStateSerializer.ToBytes(targetBuffer.ExportState(0)));
        }
    }

    [Fact]
    public void Reports_follow_resize_resets_and_screen_switches_instead_of_reusing_old_margins()
    {
        var buffer = new TerminalBuffer(12, 6);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = replies.Add };
        Query("1;6");
        parser.Process("\x1b[2;5r");
        Query("2;5");
        parser.Process("\x1b[?1049h");
        Query("1;6");
        parser.Process("\x1b[3;4r");
        Query("3;4");
        parser.Process("\x1b[?1049l");
        Query("1;6");
        buffer.Resize(15, 9);
        Query("1;9");
        parser.Process("\x1b[2;8r\x1b[!p");
        Query("1;9");
        parser.Process("\x1b[3;7r\u001bc");
        Query("1;9");

        void Query(string margins)
        {
            parser.Process(Request);
            Assert.Equal("\x1bP1$r" + margins + "r\x1b\\", Assert.Single(replies));
            replies.Clear();
        }
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(2, 1)]
    [InlineData(0, 6)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Invalid_host_supplied_margins_are_rejected_without_arithmetic_or_synthesis(int top, int bottom)
    {
        var buffer = new TerminalBuffer(12, 6) { ScrollTop = top, ScrollBottom = bottom };
        var replies = new List<string>();
        new AnsiParser(buffer) { OnResponse = replies.Add }.Process(Request);
        Assert.Equal(Failure, Assert.Single(replies));
        Assert.Equal((top, bottom), (buffer.ScrollTop, buffer.ScrollBottom));
    }

    private static void AssertReply(string request, string? expected)
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 6)) { OnResponse = replies.Add };
        parser.Process(request);
        if (expected is null) Assert.Empty(replies);
        else Assert.Equal(expected, Assert.Single(replies));
    }
}
