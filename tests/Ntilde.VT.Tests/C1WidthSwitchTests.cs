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
        target.Process("GB");

        Assert.Equal("AB", Text(targetBuffer));
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
    public void Width_switches_leave_cursor_position_replies_in_the_existing_seven_bit_format()
    {
        var buffer = new TerminalBuffer(40, 2);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { OnResponse = replies.Add };

        parser.Process("\u001b G\u001b[6n\u001b F\u001b[6n");

        Assert.Equal(new[] { "\u001b[1;1R", "\u001b[1;1R" }, replies);
        Assert.Equal(string.Empty, Text(buffer));
    }

    private static string Text(TerminalBuffer buffer)
        => new string(buffer.ViewportRows[0].Cells.Select(cell => cell.Character).ToArray()).TrimEnd();
}
