using System.Text;

namespace Ntilde.VT.Tests;

public sealed class Vt52ModeTests
{
    private const string Enter = "\x1b[?2l";

    [Fact]
    public void Identify_and_mode_reports_use_the_current_dispatch()
    {
        var buffer = new TerminalBuffer(80, 24);
        var replies = new List<string>();
        var parser = new AnsiParser(buffer) { OnResponse = replies.Add };

        parser.Process("\x1b[?2:0l\x1b[?2$p\x1b[?2h\x1bZ" + Enter);
        Assert.True(buffer.Modes.IsVt52);
        parser.Process("\x1bZ\x1b[?2$p\u009b?2$p");
        Assert.Equal(new[] { "\x1b[?2;1$y", "\x1b[?62;4;22c", "\x1b/Z" }, replies);
        Assert.True(buffer.Modes.IsVt52);

        parser.Process("\x1b<\x1b[?2$p" + Enter + "\x1b" + "c\x1b[?2$p");
        Assert.False(buffer.Modes.IsVt52);
        Assert.Equal(new[] { "\x1b[?2;1$y", "\x1b[?2;1$y" }, replies.Skip(3));
        Assert.Equal(0, buffer.CursorCol);
    }

    [Theory]
    [InlineData("\u001bA", 2, 3)]
    [InlineData("\u001bB", 4, 3)]
    [InlineData("\u001bC", 3, 4)]
    [InlineData("\u001bD", 3, 2)]
    [InlineData("\x1bH", 0, 0)]
    public void Relative_cursor_controls_and_home_move_the_cursor(string control, int row, int col)
    {
        var buffer = new TerminalBuffer(12, 6);
        new AnsiParser(buffer).Process(Enter + "\x1bY##" + control);
        Assert.Equal((row, col), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Movement_clamps_at_edges_and_direct_address_keeps_each_invalid_axis()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        parser.Process(Enter + "\x1bH\u001bA\u001bD");
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\x1bY7o\u001bB\u001bC");
        Assert.Equal((23, 79), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\x1bY9!");
        Assert.Equal((23, 1), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\x1bY\"p");
        Assert.Equal((2, 1), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\x1bY\u0080\u0080");
        Assert.Equal((2, 1), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Reverse_line_feed_scrolls_at_top_and_erase_controls_keep_position()
    {
        var buffer = new TerminalBuffer(8, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("FIRST\r\nSECOND\r\nTHIRD" + Enter + "\x1bY!#\x1bI");
        Assert.Equal((0, 3), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\x1bI");
        Assert.Equal("", Row(buffer, 0));
        Assert.Equal("FIRST", Row(buffer, 1));
        Assert.Equal("SECOND", Row(buffer, 2));

        parser.Process("\x1bY!\"\x1bK");
        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal("FI", Row(buffer, 1));
        Assert.Equal("SECOND", Row(buffer, 2));

        parser.Process("\x1bY\"#\x1bJ");
        Assert.Equal((2, 3), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal("SEC", Row(buffer, 2));
        Assert.Equal("", Row(buffer, 3));
    }

    [Fact]
    public void Graphics_and_keypad_selection_follow_vt52_escapes()
    {
        var buffer = new TerminalBuffer(12, 6);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b(0" + Enter + "q\u001bFqx\x1bGq\x1b=");
        Assert.Equal("q─│q", Row(buffer, 0));
        Assert.True(buffer.Modes.IsApplicationKeypad);
        parser.Process("\x1b>\x1b<q");
        Assert.Equal("q─│q─", Row(buffer, 0));
        Assert.False(buffer.Modes.IsApplicationKeypad);
    }

    [Fact]
    public void A_cancel_or_new_escape_recovers_from_incomplete_cursor_address()
    {
        var buffer = new TerminalBuffer(12, 6);
        new AnsiParser(buffer).Process(Enter + "\x1bY!\x18X\x1bY\"\x1bHQ");
        Assert.Equal("Q", Row(buffer, 0));
        Assert.Equal((0, 1), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Vt52_exit_selects_seven_bit_replies_until_vt220_controls_are_selected()
    {
        var buffer = new TerminalBuffer(80, 24);
        var parser = new AnsiParser(buffer);
        var replies = new List<byte[]>();
        parser.OnResponse = reply => replies.Add(parser.Encode8BitReply(reply) ?? Encoding.UTF8.GetBytes(reply));
        parser.Process("\x1b G\x1b[6n" + Enter + "\x1b G\x1bZ\x1b<\x1b G\x1b[1;1H\x1b[6n");
        parser.Process("\x1b[62;1\"p\x1b[6n\x1b G\x1b[6n");
        parser.Process("\x1b[61\"p\x1b G\x1b[6n\x1b[62;2\"p\x1b[6n");
        Assert.Equal(new[] { "9B313B3152", "1B2F5A", "1B5B313B3152", "1B5B313B3152", "9B313B3152", "1B5B313B3152", "9B313B3152" },
            replies.Select(Convert.ToHexString));
    }

    [Fact]
    [Trait("Category", "Replay")]
    public void All_cursor_address_splits_survive_a_serialized_snapshot_and_ansi_restoration()
    {
        const string Stream = "\x1bY!\"\u001bFq\x1b=Z\x1b<\x1b G\x1b[6n";
        for (int cut = 0; cut <= Stream.Length; cut++)
        {
            var buffer = new TerminalBuffer(12, 6);
            var source = new AnsiParser(buffer);
            source.Process(Enter);
            source.Process(Stream[..cut]);
            TerminalStateSnapshot snapshot = buffer.ExportState(0);
            snapshot.Parser = source.ExportState();

            var targetBuffer = new TerminalBuffer(12, 6);
            var target = new AnsiParser(targetBuffer);
            TerminalStateTransfer.Restore(targetBuffer, target,
                TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot)));
            Assert.Equal(buffer.Modes.IsVt52, targetBuffer.Modes.Clone().IsVt52);

            var replies = new List<string>();
            target.OnResponse = reply =>
            {
                Assert.Null(target.Encode8BitReply(reply));
                replies.Add(reply);
            };
            source.Process(Stream[cut..]);
            target.Process(Stream[cut..]);
            Assert.Equal("  ─Z", Row(targetBuffer, 1));
            Assert.True(targetBuffer.Modes.IsApplicationKeypad);
            Assert.False(targetBuffer.Modes.IsVt52);
            Assert.Equal(cut == Stream.Length ? Array.Empty<string>() : new[] { "\x1b[2;5R" }, replies);
            Assert.Equal(TerminalStateSerializer.ToBytes(buffer.ExportState(0)),
                TerminalStateSerializer.ToBytes(targetBuffer.ExportState(0)));
        }
    }

    private static string Row(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return new string(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetCell(col, row).Character).ToArray()).TrimEnd();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
