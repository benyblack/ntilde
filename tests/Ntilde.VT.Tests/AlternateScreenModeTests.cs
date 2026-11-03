using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class AlternateScreenModeTests
{
    [Fact]
    public void Mode_1047_entry_preserves_the_retained_alternate_cells_cursor_and_pen()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("MAIN\u001b[?47h\u001b[3;5H\u001b[31;53mA\u001b[?47l");
        parser.Process("\u001b[?1047h");
        Assert.True(buffer.IsAltScreenActive);
        Assert.StartsWith("    A", BufferSnapshot.Capture(buffer).Lines[2]);
        Assert.Equal((2, 5), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal((short)1, buffer.CurrentFgIndex);
        Assert.True(buffer.IsOverline);
    }

    [Fact]
    public void Mode_1047_exit_clears_alternate_text_and_row_metadata_without_changing_main()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("MAIN\u001b[?47h\u001b[2;3H\u001b[53;4:3;58:5:9m" +
            "\u001b]8;;https://example.com/owned\u0007X\u001b]8;;\u0007\u001b[?1047l");
        Assert.False(buffer.IsAltScreenActive);
        Assert.StartsWith("MAIN", BufferSnapshot.Capture(buffer).Lines[0]);
        Assert.Equal(0, buffer.Scrollback.Count);
        parser.Process("\u001b[?47h");
        Assert.All(BufferSnapshot.Capture(buffer).Lines, line => Assert.True(string.IsNullOrWhiteSpace(line)));
        Assert.Null(buffer.GetUnderlineAbsolute(2, 1));
        Assert.Null(buffer.GetHyperlinkAbsolute(2, 1));
        Assert.All(buffer.ExportState(0).Alt.RowWrittenColumns!, value => Assert.Equal(0, value));
        Assert.Equal((1, 3), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsOverline);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1047)]
    [InlineData(1049)]
    public void Repeated_entries_preserve_the_current_alternate_contents_and_position(int mode)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process($"MAIN\u001b[?{mode}h\u001b[3;4H\u001b[32;1mALT");
        string before = BufferSnapshot.Capture(buffer).ToFormattedString();
        parser.Process($"\u001b[?{mode}h");
        Assert.Equal(before, BufferSnapshot.Capture(buffer).ToFormattedString());
        Assert.Equal((2, 6), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsBold);
        Assert.Equal((short)2, buffer.CurrentFgIndex);
    }

    [Fact]
    public void Mode_1047_reset_while_main_is_active_does_not_clear_the_stashed_alternate()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("MAIN\u001b[?47hALT\u001b[?47l\u001b[?1047l\u001b[?47h");
        Assert.StartsWith("ALT", BufferSnapshot.Capture(buffer).Lines[0]);
    }

    [Fact]
    public void State_transfer_after_1047_exit_keeps_the_cleared_alternate_and_its_live_cursor()
    {
        var source = new TerminalBuffer(20, 8);
        new AnsiParser(source).Process("MAIN\u001b[?47h\u001b[3;4H\u001b[31mALT\u001b[?1047l");
        var target = new TerminalBuffer(20, 8);
        target.ImportState(TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(source.ExportState(0))));
        new AnsiParser(target).Process("\u001b[?47h");
        Assert.All(BufferSnapshot.Capture(target).Lines, line => Assert.True(string.IsNullOrWhiteSpace(line)));
        Assert.Equal((2, 6), (target.CursorRow, target.CursorCol));
        Assert.Equal((short)1, target.CurrentFgIndex);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1047)]
    [InlineData(1049)]
    public void Exit_after_1049_restores_the_armed_main_cursor_rendition_origin_and_charset(int exitMode)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("MAIN\u001b[?6h\u001b[3;4H\u001b[31;1;53m\u001b(0\u001b[?1049h" +
            "\u001b[?6l\u001b(B\u001b[32mALT" + $"\u001b[?{exitMode}l");
        Assert.False(buffer.IsAltScreenActive);
        Assert.True(buffer.Modes.IsOriginMode);
        Assert.Equal((2, 3), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsBold); Assert.True(buffer.IsOverline);
        Assert.Equal((short)1, buffer.CurrentFgIndex);
        parser.Process("q");
        Assert.StartsWith("   ─", BufferSnapshot.Capture(buffer).Lines[2]);
        Assert.StartsWith("MAIN", BufferSnapshot.Capture(buffer).Lines[0]);
    }
}
