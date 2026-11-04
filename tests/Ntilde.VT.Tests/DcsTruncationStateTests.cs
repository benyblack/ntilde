using System.Text.Json.Nodes;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

public class DcsTruncationStateTests
{
    private static AnsiParser NewParser(int cap, List<string>? replies = null) =>
        new(new TerminalBuffer(12, 5), forceConPtyFiltering: false)
        {
            MaxStringSequenceChars = cap,
            OnResponse = replies is null ? null : replies.Add,
        };

    private static bool? Truncated(AnsiParserState state) => state.DcsTruncated;

    private static AnsiParserState JsonRoundTrip(AnsiParser parser)
    {
        var snapshot = new TerminalBuffer(12, 5).ExportState(0);
        snapshot.Parser = parser.ExportState();
        return TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)).Parser;
    }

    [Fact]
    public void Default_cap_latches_loss_only_after_the_first_discarded_character()
    {
        var replies = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(12, 5)) { OnResponse = replies.Add };
        int cap = parser.MaxStringSequenceChars;
        parser.Process("\x1bP$qr");
        string chunk = new('x', 16_384);
        for (int remaining = cap - 3; remaining > 0; remaining -= chunk.Length)
            parser.Process(chunk[..Math.Min(remaining, chunk.Length)]);

        Assert.Equal(false, Truncated(parser.ExportState()));
        parser.Process("x");
        AnsiParserState state = parser.ExportState();
        Assert.Equal(cap, state.DcsBuffer.Length);
        Assert.Equal(true, Truncated(state));
        parser.Process("\x1b\\");
        Assert.Equal(new[] { "\x1bP0$r\x1b\\" }, replies);
        Assert.Equal(false, Truncated(parser.ExportState()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void A_discard_latches_loss_even_if_the_host_later_raises_the_cap(int cap)
    {
        var parser = NewParser(cap);
        parser.Process("\x1bP" + new string('x', Math.Max(0, cap)));
        Assert.Equal(false, Truncated(parser.ExportState()));
        parser.Process("y");
        Assert.Equal(true, Truncated(parser.ExportState()));
        parser.MaxStringSequenceChars = 20;
        parser.Process("z");
        Assert.Equal(true, Truncated(parser.ExportState()));
    }

    [Theory]
    [InlineData("\x1bP", "\a")]
    [InlineData("\x1bP", "\u009c")]
    [InlineData("\x1bP", "\x1b\\")]
    [InlineData("\u0090", "\a")]
    [InlineData("\u0090", "\u009c")]
    [InlineData("\u0090", "\x1b\\")]
    [Trait("Category", "Replay")]
    public void Every_split_and_generated_json_restore_preserves_loss_and_negative_reply(string start, string end)
    {
        string stream = start + "$qrjunk" + end;
        for (int cut = 0; cut <= stream.Length; cut++)
        {
            var replies = new List<string>();
            var source = NewParser(3, replies);
            source.Process(stream[..cut]);
            AnsiParserState transferred = JsonRoundTrip(source);
            Assert.NotNull(Truncated(transferred));
            var resumedReplies = new List<string>();
            var resumed = NewParser(3, resumedReplies);
            resumed.ImportState(transferred);
            Assert.Equal(source.ExportState().ToDebugString(), resumed.ExportState().ToDebugString());

            int alreadyReplied = replies.Count;
            source.Process(stream[cut..]);
            resumed.Process(stream[cut..]);
            Assert.Equal(new[] { "\x1bP0$r\x1b\\" }, replies);
            Assert.Equal(replies.Skip(alreadyReplied), resumedReplies);
            Assert.Equal(false, Truncated(resumed.ExportState()));
            Assert.Equal(source.ExportState().ToDebugString(), resumed.ExportState().ToDebugString());
        }
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(3, true)]
    [InlineData(7, false)]
    public void Import_clipping_marks_a_retained_query_prefix_incomplete(int cap, bool expectedLoss)
    {
        var source = NewParser(20);
        source.Process("\x1bP$qrjunk");
        AnsiParserState state = JsonRoundTrip(source);
        var replies = new List<string>();
        var target = NewParser(cap, replies);
        target.ImportState(state);
        Assert.Equal(expectedLoss, Truncated(target.ExportState()));
        Assert.Equal("$qrjunk"[..Math.Min(7, Math.Max(0, cap))], target.ExportState().DcsBuffer);
        target.Process("\x1b\\");
        Assert.Equal(cap >= 2 ? new[] { "\x1bP0$r\x1b\\" } : [], replies);
        Assert.Equal(false, Truncated(target.ExportState()));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("\x1bP$qr", true)]
    [InlineData("\x1bP$qr\x1b", true)]
    public void Legacy_omission_cannot_prove_an_active_DCS_prefix_complete(string prefix, bool expectedLoss)
    {
        var source = NewParser(3);
        source.Process(prefix);
        var snapshot = new TerminalBuffer(12, 5).ExportState(0);
        snapshot.Parser = source.ExportState();
        JsonNode json = JsonNode.Parse(TerminalStateSerializer.ToBytes(snapshot))!;
        json["parser"]!.AsObject().Remove("dcs_trunc");
        AnsiParserState legacy = TerminalStateSerializer.FromBytes(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())).Parser;
        var target = NewParser(3);
        target.ImportState(legacy);
        Assert.Equal(expectedLoss, Truncated(target.ExportState()));
        target.Process(prefix.EndsWith('\x1b') ? "\\" : "\x1b\\");
        target.Process("\x1bP$qr");
        Assert.Equal(false, Truncated(target.ExportState()));
    }

    [Theory]
    [InlineData("$qrjunk", "\x1bP0$r\x1b\\")]
    [InlineData("+q544ejunk", "\x1bP0+r\x1b\\")]
    public void Truncated_queries_stay_negative_and_a_new_DCS_clears_the_marker(string payload, string failure)
    {
        var replies = new List<string>();
        var parser = NewParser(3, replies);
        parser.Process("\x1bP" + payload);
        Assert.Equal(true, Truncated(parser.ExportState()));
        parser.Process("\x1b\\");
        Assert.Equal(new[] { failure }, replies);
        Assert.Equal(false, Truncated(parser.ExportState()));
        parser.Process("\x1bP$q");
        Assert.Equal(false, Truncated(parser.ExportState()));
    }

    [Fact]
    public void Malformed_escape_abandonment_does_not_carry_loss_into_the_next_DCS()
    {
        var parser = NewParser(3);
        parser.Process("\x1bP$qrjunk\x1b[6n");
        Assert.Equal(false, Truncated(parser.ExportState()));
        parser.Process("\x1bP$qr");
        Assert.Equal(false, Truncated(parser.ExportState()));
    }
}
