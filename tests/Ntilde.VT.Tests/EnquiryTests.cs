using Ntilde.Replay;

namespace Ntilde.VT.Tests;

/// <summary>ENQ (0x05): consumed, with no answerback and no cell. See the decision in <c>docs/vt_coverage_matrix.md</c>.</summary>
public sealed class EnquiryTests
{
    private const string Enq = "\u0005";

    [Fact]
    public void ENQ_sends_no_answerback()
    {
        var responses = new List<string>();
        var parser = new AnsiParser(new TerminalBuffer(20, 4)) { OnResponse = responses.Add };
        parser.Process(Enq + "a" + Enq + Enq);
        Assert.Empty(responses);
    }

    [Fact]
    public void ENQ_writes_no_cell_and_does_not_move_the_cursor()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        parser.Process(Enq);
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("a" + Enq + "b");
        Assert.Equal((0, 2), (buffer.CursorRow, buffer.CursorCol));
        string[] lines = BufferSnapshot.Capture(buffer).Lines;
        Assert.Equal("ab", lines[0].TrimEnd());
        Assert.All(lines.Skip(1), line => Assert.Equal(string.Empty, line.TrimEnd()));
    }

    [Fact]
    public void A_stream_with_ENQ_leaves_the_same_screen_state_as_one_without()
    {
        static byte[] State(string input)
        {
            var buffer = new TerminalBuffer(12, 4);
            new AnsiParser(buffer).Process(input);
            return TerminalStateSerializer.ToBytes(buffer.ExportState(0));
        }

        Assert.Equal(State("\x1b[31mab\r\ncd"), State($"{Enq}\x1b[31ma{Enq}b\r\n{Enq}c{Enq}{Enq}d"));
    }

    [Fact]
    public void ENQ_ends_the_run_REP_repeats_as_every_control_does()
    {
        var buffer = new TerminalBuffer(20, 4);
        new AnsiParser(buffer).Process("a" + Enq + "\x1b[3b");
        Assert.Equal("a", BufferSnapshot.Capture(buffer).Lines[0].TrimEnd());
    }
}
