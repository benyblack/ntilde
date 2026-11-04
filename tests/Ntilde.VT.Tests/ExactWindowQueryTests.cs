namespace Ntilde.VT.Tests;

public sealed class ExactWindowQueryTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(140, 40)]
    [InlineData(256, 128)]
    public void Character_geometry_reports_rows_then_columns_without_pixel_metrics(int cols, int rows)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add, CellWidth = float.NaN, CellHeight = float.PositiveInfinity };
        parser.Process("\x1b[18t");
        Assert.Equal($"\x1b[8;{rows};{cols}t", Assert.Single(responses));
    }

    [Fact]
    public void Character_geometry_uses_current_resized_buffer_and_requires_no_subscription()
    {
        var buffer = new TerminalBuffer(80, 24);
        var responses = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
        parser.Process("\x1b[18t");
        buffer.Resize(120, 31);
        parser.Process("\x1b[18t");
        Assert.Equal(new[] { "\x1b[8;24;80t", "\x1b[8;31;120t" }, responses);
        Assert.False(parser.InBandResizeReportsEnabled);
    }

    [Theory]
    [InlineData("\x1b[?18t")]
    [InlineData("\x1b[>18t")]
    [InlineData("\x1b[<18t")]
    [InlineData("\x1b[=18t")]
    [InlineData("\x1b[18$t")]
    [InlineData("\x1b[18 t")]
    [InlineData("\x1b[18;1t")]
    [InlineData("\x1b[18;0t")]
    [InlineData("\x1b[18;t")]
    [InlineData("\x1b[18:1t")]
    [InlineData("\x1b[18:t")]
    [InlineData("\x1b[18?1t")]
    [InlineData("\x1b[18 $t")]
    public void Other_spellings_cannot_alias_the_character_geometry_query(string request)
    {
        var responses = new List<string>();
        new AnsiParser(new TerminalBuffer(80, 24)) { OnResponse = responses.Add }.Process(request);
        Assert.Empty(responses);
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void Every_input_split_preserves_query_framing_and_terminal_state()
    {
        const string request = "\x1b[18t";
        for (int split = 0; split <= request.Length; split++)
        {
            var buffer = new TerminalBuffer(9, 4);
            var responses = new List<string>();
            var parser = new AnsiParser(buffer) { OnResponse = responses.Add };
            parser.Process("\x1b[?1049h\x1b[2;3r\x1b[?6h\x1b[31;4mtext");
            TerminalStateSnapshot before = buffer.ExportState(0);
            parser.Process(request[..split]);
            if (split < request.Length) Assert.Empty(responses);
            parser.Process(request[split..]);
            Assert.Equal("\x1b[8;4;9t", Assert.Single(responses));
            TerminalStateSnapshot after = buffer.ExportState(0);
            Assert.Equal(TerminalStateSerializer.ToBytes(before), TerminalStateSerializer.ToBytes(after));
        }
    }
}
