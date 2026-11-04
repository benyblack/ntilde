namespace Ntilde.VT.Tests;

public sealed class TertiaryDeviceAttributeTests
{
    private const string UnassignedUnitId = "\x1bP!|00000000\x1b\\";

    [Theory]
    [InlineData("\x1b[=c")]
    [InlineData("\x1b[=0c")]
    [InlineData("\x1b[=000c")]
    [InlineData("\u009b=c")]
    [InlineData("\u009b=0c")]
    public void Default_or_zero_DA3_returns_an_unassigned_nonunique_eight_hex_digit_identifier(string request)
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }.Process(request);
        Assert.Equal(UnassignedUnitId, Assert.Single(responses));
    }

    [Theory]
    [InlineData("\x1b[=1c")]
    [InlineData("\x1b[=999999999999999999999999999999999999999999999999999999999999999c")]
    [InlineData("\x1b[=0;0c")]
    [InlineData("\x1b[=;c")]
    [InlineData("\x1b[=0:0c")]
    [InlineData("\x1b[=0:c")]
    [InlineData("\x1b[=0 c")]
    [InlineData("\x1b[=0$c")]
    [InlineData("\x1b[=0!c")]
    [InlineData("\x1b[?=0c")]
    [InlineData("\x1b[==0c")]
    [InlineData("\x1b[=0?c")]
    [InlineData("\x1b[<c")]
    public void Other_parameters_or_identities_cannot_alias_DA3_or_another_device_report(string request)
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }.Process(request);
        Assert.Empty(responses);
    }

    [Theory]
    [InlineData("\x1b[=c")]
    [InlineData("\x1b[=0c")]
    [InlineData("\u009b=0c")]
    [Trait("Category", "Replay")]
    public void Every_split_keeps_the_reply_exact_and_preserves_terminal_state(string request)
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
            Assert.Equal(UnassignedUnitId, Assert.Single(responses));
            Assert.Equal(before, TerminalStateSerializer.ToBytes(buffer.ExportState(0)));
        }
    }

    [Fact]
    public void Primary_secondary_and_tertiary_requests_have_separate_reply_families()
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(40, 6)) { OnResponse = responses.Add }
            .Process("\x1b[c\x1b[>c\x1b[=c");
        Assert.Equal(new[] { "\x1b[?62;4;22c", "\x1b[>1;10;0c", UnassignedUnitId }, responses);
    }
}
