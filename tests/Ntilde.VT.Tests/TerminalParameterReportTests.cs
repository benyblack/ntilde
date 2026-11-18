using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>The VT100 reports DECREQTPARM (bare <c>CSI Ps x</c>) and DECID (<c>ESC Z</c>).</summary>
public sealed class TerminalParameterReportTests
{
    private const string PrimaryDa = "\x1b[?62;4;22c";

    private static List<string> Replies(string input, out TerminalBuffer buffer)
    {
        var responses = new List<string>();
        buffer = new TerminalBuffer(20, 4);
        new AnsiParser(buffer) { OnResponse = responses.Add }.Process(input);
        return responses;
    }

    [Theory]
    [InlineData("\x1b[x", "\x1b[2;1;1;128;128;1;0x")]
    [InlineData("\x1b[0x", "\x1b[2;1;1;128;128;1;0x")]
    [InlineData("\x1b[1x", "\x1b[3;1;1;128;128;1;0x")]
    [InlineData("\u009b1x", "\x1b[3;1;1;128;128;1;0x")]
    public void DECREQTPARM_zero_and_one_are_answered_with_fixed_parameters(string request, string reply)
    {
        Assert.Equal(reply, Assert.Single(Replies(request, out _)));
    }

    [Theory]
    [InlineData("\x1b[2x")]
    [InlineData("\x1b[3x")]
    [InlineData("\x1b[99999999999999999999x")]
    [InlineData("\x1b[0;0x")]
    [InlineData("\x1b[;x")]
    [InlineData("\x1b[0:1x")]
    [InlineData("\x1b[?0x")]
    [InlineData("\x1b[>1x")]
    [InlineData("\x1b[=0x")]
    [InlineData("\x1b[0 x")]
    [InlineData("\x1b[0!x")]
    [InlineData("\x1b[0\"x")]
    public void Other_parameters_and_identities_are_not_answered(string request)
    {
        Assert.Empty(Replies(request + "ok", out TerminalBuffer buffer));
        Assert.Equal("ok", BufferSnapshot.Capture(buffer).Lines[0].TrimEnd());
    }

    [Fact]
    public void DECSACE_and_DECFRA_keep_their_identities_and_stay_silent()
    {
        var responses = new List<string>();
        var buffer = new TerminalBuffer(6, 2);
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\x1b[2*x\x1b[88;1;1;1;3$x\x1b[1*x\x1b[0$x");
        Assert.Empty(responses);
        Assert.Equal("XXX", BufferSnapshot.Capture(buffer).Lines[0].TrimEnd());
    }

    [Fact]
    public void DECID_is_answered_as_primary_DA_and_prints_nothing()
    {
        var responses = Replies("\x1bZabc\x1b[c", out TerminalBuffer buffer);
        Assert.Equal(new[] { PrimaryDa, PrimaryDa }, responses);
        Assert.Equal("abc", BufferSnapshot.Capture(buffer).Lines[0].TrimEnd());
    }

    [Theory]
    [InlineData("\x1bZ")]
    [InlineData("\x1b[1x")]
    public void Without_a_response_sink_the_request_is_consumed(string request)
    {
        var buffer = new TerminalBuffer(20, 4);
        new AnsiParser(buffer).Process(request + "ok");
        Assert.Equal("ok", BufferSnapshot.Capture(buffer).Lines[0].TrimEnd());
    }

    [Theory]
    [InlineData("\x1b[1x", "\x1b[3;1;1;128;128;1;0x")]
    [InlineData("\x1bZ", PrimaryDa)]
    public void Every_split_gives_one_exact_reply_and_leaves_terminal_state_alone(string request, string reply)
    {
        for (int split = 0; split <= request.Length; split++)
        {
            var buffer = new TerminalBuffer(12, 5);
            var responses = new List<string>();
            var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
            parser.Process("\x1b[?1049h\x1b[31;4mcontent\x1b[2;4r\x1b[?6h");
            byte[] before = TerminalStateSerializer.ToBytes(buffer.ExportState(0));
            parser.Process(request[..split]);
            if (split < request.Length) Assert.Empty(responses);
            parser.Process(request[split..]);
            Assert.Equal(reply, Assert.Single(responses));
            Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
        }
    }
}
