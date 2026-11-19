using System.Text;
using System.Text.Json;

namespace Ntilde.VT.Tests;

public sealed class C1WidthSwitchTests
{
    [Fact]
    public void Width_switches_are_consumed_at_every_input_split()
    {
        const string input = "start\u001b Gmiddle\u001b Fend";
        for (int split = 0; split <= input.Length; split++)
        {
            var buffer = new TerminalBuffer(40, 2);
            var parser = new AnsiParser(buffer, forceConPtyFiltering: false);

            parser.Process(input[..split]);
            parser.Process(input[split..]);

            Assert.Equal("startmiddleend", Text(buffer));
        }
    }

    [Fact]
    public void Pending_space_intermediate_survives_a_serialized_snapshot()
    {
        var sourceBuffer = new TerminalBuffer(40, 2);
        var source = new AnsiParser(sourceBuffer, forceConPtyFiltering: false);
        source.Process("A\u001b ");

        var targetBuffer = new TerminalBuffer(40, 2);
        targetBuffer.ImportState(sourceBuffer.ExportState(0));
        var target = new AnsiParser(targetBuffer, forceConPtyFiltering: false);
        target.ImportState(JsonSerializer.Deserialize<AnsiParserState>(JsonSerializer.Serialize(source.ExportState()))!);
        var replies = WireReplies(target);
        target.Process("GB\u001b[6n");

        Assert.Equal("AB", Text(targetBuffer));
        Assert.Equal(new byte[] { 0x9b, (byte)'1', (byte)';', (byte)'3', (byte)'R' }, Assert.Single(replies));
    }

    [Theory]
    [InlineData("\u001b \u0018G", "AGB")]        // CAN aborts the escape.
    [InlineData("\u001b \u001aF", "AFB")]        // SUB aborts the escape.
    [InlineData("\u001b \u001b[32m", "AB")]      // A fresh ESC starts a new sequence.
    [InlineData("\u001b \a\u007fG", "AB")]     // BEL executes and DEL is ignored during the escape.
    [InlineData("\u001b λ", "AλB")]             // Unicode text is recovered after the incomplete escape.
    public void Space_escape_recovers_at_the_following_control_or_text(string sequence, string expected)
    {
        var buffer = new TerminalBuffer(40, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        int bells = 0;
        parser.OnBell = () => bells++;

        parser.Process("A" + sequence + "B");

        Assert.Equal(expected, Text(buffer));
        Assert.Equal(sequence.Contains('\a') ? 1 : 0, bells);
    }

    [Fact]
    public void Cursor_reports_follow_the_selected_width_and_full_reset_restores_seven_bit()
    {
        var buffer = new TerminalBuffer(40, 2);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        var replies = WireReplies(parser);

        parser.Process("\u001b[6n\u001b G\u001b[6n\u001b F\u001b[6n\u001b G\u001bc\u001b[6n");

        byte[] sevenBit = "\u001b[1;1R"u8.ToArray();
        byte[] eightBit = [0x9b, .. "1;1R"u8];
        Assert.Equal(new[] { sevenBit, eightBit, sevenBit, sevenBit }, replies);
        Assert.Equal(string.Empty, Text(buffer));
    }

    [Fact]
    public void Active_width_and_extra_intermediate_survive_a_serialized_snapshot()
    {
        var source = new AnsiParser(new TerminalBuffer(40, 2), forceConPtyFiltering: false);
        source.Process("\u001b G\u001b  ");
        var target = new AnsiParser(new TerminalBuffer(40, 2), forceConPtyFiltering: false);
        target.ImportState(JsonSerializer.Deserialize<AnsiParserState>(JsonSerializer.Serialize(source.ExportState()))!);
        var replies = WireReplies(target);

        // The extra space invalidates S7C1T, but the next exact spelling switches back.
        target.Process("F\u001b[6n\u001b F\u001b[6n");

        Assert.Equal((byte[])[0x9b, .. "1;1R"u8], replies[0]);
        Assert.Equal("\u001b[1;1R"u8.ToArray(), replies[1]);
    }

    [Fact]
    public void String_replies_use_raw_c1_framing_with_utf8_payload()
    {
        var parser = new AnsiParser(new TerminalBuffer(40, 2), forceConPtyFiltering: false)
        {
            AllowTitleReport = true,
        };
        var replies = WireReplies(parser);

        parser.Process("\u001b G\u001b]2;λ\a\u001b[21t\u001bP$qm\u001b\\");

        Assert.Collection(replies,
            osc => Assert.Equal((byte[])[0x9d, .. "lλ"u8, 0x9c], osc),
            dcs => Assert.Equal((byte[])[0x90, .. "1$r0m"u8, 0x9c], dcs));
    }

    [Theory]
    [InlineData("\u001b\\", 0x9c)]
    [InlineData("\u009c", 0x9c)]
    [InlineData("\a", 0x07)]
    public void Osc_replies_emit_a_single_terminator_byte(string terminator, int expected)
    {
        var parser = new AnsiParser(new TerminalBuffer(40, 2), forceConPtyFiltering: false)
        {
            CellSizeReports = new CellSizeReportMetrics(8.25, 17.5, 2, 12.25),
        };
        var replies = WireReplies(parser);

        parser.Process("\u001b G\u001b]776;?" + terminator);

        Assert.Equal((byte[])[0x9d, .. "776;17;35;25"u8, (byte)expected], Assert.Single(replies));
    }

    private static List<byte[]> WireReplies(AnsiParser parser)
    {
        var replies = new List<byte[]>();
        parser.OnResponse = reply => replies.Add(parser.Encode8BitReply(reply) ?? Encoding.UTF8.GetBytes(reply));
        return replies;
    }

    private static string Text(TerminalBuffer buffer)
        => new string(buffer.ViewportRows[0].Cells.Select(cell => cell.Character).ToArray()).TrimEnd();
}
