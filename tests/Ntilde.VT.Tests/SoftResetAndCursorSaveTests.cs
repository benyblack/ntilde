namespace Ntilde.VT.Tests;

public sealed class SoftResetAndCursorSaveTests
{
    private static char Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row).Character; }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Soft_reset_preserves_content_current_position_active_screen_and_reporting(bool alternate)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer) { SixelCellSizeOverride = new SixelCellSize(12, 24) };
        parser.Process("history\r\n" + string.Concat(Enumerable.Repeat("line\r\n", 10)));
        if (alternate) parser.Process("\u001b[?1049h");
        parser.Process("\u001b[2;3H\u001b[31;1;4:3;58:5:2mkept\u001b[?1048h" +
            "\u001b[2;7r\u001b[?6h\u001b[3;4H\u001b[?7l\u001b[?1h\u001b[4h\u001b[?25l" +
            "\u001b[?1003;1004;2004;2048;80h\u001b[>3u");
        var before = buffer.ExportState(int.MaxValue);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        parser.Process("\u001b[!p");
        var after = buffer.ExportState(int.MaxValue);
        Assert.Equal((before.CursorRow, before.CursorCol), (after.CursorRow, after.CursorCol));
        Assert.Equal(alternate, buffer.IsAltScreenActive);
        Assert.Equal(before.Main.CellsBase64, after.Main.CellsBase64);
        Assert.Equal(before.Alt.CellsBase64, after.Alt.CellsBase64);
        Assert.Equal(before.ScrollbackRowCount, after.ScrollbackRowCount);
        Assert.Equal((0, 7), (buffer.ScrollTop, buffer.ScrollBottom));
        Assert.False(buffer.Modes.IsOriginMode);
        Assert.True(buffer.Modes.IsAutoWrapMode);
        Assert.False(buffer.Modes.IsApplicationCursorKeys);
        Assert.False(buffer.Modes.IsInsertMode);
        Assert.True(buffer.Modes.IsCursorVisible);
        Assert.False(buffer.IsBold);
        Assert.False(buffer.IsUnderline);
        Assert.Null(buffer.UnderlineColor);
        Assert.True(buffer.IsDefaultForeground);
        Assert.True(buffer.IsDefaultBackground);
        Assert.True(buffer.Modes.MouseModeAnyEvent);
        Assert.True(buffer.Modes.IsFocusEventReporting);
        Assert.True(buffer.Modes.IsBracketedPasteMode);
        Assert.True(buffer.Modes.IsSixelDisplayMode);
        Assert.True(parser.InBandResizeReportsEnabled);
        Assert.Equal(before.KittyKeyboard.MainStack, after.KittyKeyboard.MainStack);
        Assert.Equal(before.KittyKeyboard.AltStack, after.KittyKeyboard.AltStack);
        Assert.Equal(new SixelCellSize(12, 24), parser.SixelCellSizeOverride);
        Assert.Empty(responses);
        parser.Process("\u001b[?1048l");
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsDefaultForeground);
        Assert.False(buffer.IsBold);
    }

    [Fact]
    public void Soft_reset_resets_charsets_and_preserves_custom_tabs()
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[3g\u001b[1;6H\u001bH\u001b(0\u001b)0\u000e\u001b[!pq\u001b[2;1H\tX");
        Assert.Equal('q', Cell(buffer, 5, 0));
        Assert.Equal('X', Cell(buffer, 5, 1));
        Assert.Equal(new[] { 0, 0, 0, 0 }, parser.ExportState().Charsets);
        Assert.Equal(0, parser.ExportState().Gl);
    }

    [Theory]
    [InlineData("\u001b[?6h", 6, 2)]
    [InlineData("\u001b[?7l", 7, 1)]
    [InlineData("\u001b[?1h", 1, 2)]
    [InlineData("\u001b[?25l", 25, 1)]
    public void Soft_reset_is_visible_to_mode_queries(string setup, int mode, int expected)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        parser.Process(setup + "\u001b[!p" + $"\u001b[?{mode}$p");
        Assert.Equal($"\u001b[?{mode};{expected}$y", Assert.Single(responses));
    }

    [Theory]
    [InlineData("\u001b[? !p")]
    [InlineData("\u001b[1!p")]
    [InlineData("\u001b[!!p")]
    [InlineData("\u001b[p")]
    [InlineData("\u001b[>!p")]
    [InlineData("\u001b[?1048!l")]
    public void Other_CSI_identities_cannot_soft_reset_or_restore_a_cursor(string sequence)
    {
        var buffer = new TerminalBuffer(20, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;3H\u001b[?1048h\u001b[4;5H\u001b[1m\u001b[?7l");
        parser.Process(sequence);
        Assert.Equal((3, 4), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsBold);
        Assert.False(buffer.Modes.IsAutoWrapMode);
    }

    [Theory]
    [InlineData("\u001b7", "\u001b[?1048l")]
    [InlineData("\u001b[?1048h", "\u001b8")]
    [InlineData("\u001b[?1048h", "\u001b[?1048l")]
    public void Mode_1048_shares_DECSC_banks_and_restores_rendition_without_switching_screens(string save, string restore)
    {
        var buffer = new TerminalBuffer(8, 5);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;8H\u001b[31;1;4:3;58:5:2mX" + save);
        Assert.True(buffer.IsPendingWrap);
        parser.Process("\u001b[1;1H\u001b[0m" + restore);
        Assert.False(buffer.IsAltScreenActive);
        Assert.Equal((1, 7), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsPendingWrap);
        Assert.True(buffer.IsBold);
        Assert.Equal(UnderlineStyle.Curly, buffer.UnderlineStyle);
        Assert.Equal((short)2, buffer.UnderlineColorIndex);
        parser.Process("Y");
        Assert.Equal('X', Cell(buffer, 7, 1));
        Assert.Equal('Y', Cell(buffer, 0, 2));
    }

    [Fact]
    public void Main_and_alternate_banks_are_independent_and_soft_reset_touches_only_the_active_bank()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;3H\u001b[31m\u001b[?1048h\u001b[?47h" +
            "\u001b[4;5H\u001b[32m\u001b[?1048h\u001b[1;1H\u001b[?1048l");
        Assert.True(buffer.IsAltScreenActive);
        Assert.Equal((3, 4), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("\u001b[!p\u001b[?47l\u001b[1;1H\u001b[?1048l");
        Assert.Equal((1, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.Equal((short)1, buffer.CurrentFgIndex);
        parser.Process("\u001b[?47h\u001b[?1048l");
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsDefaultForeground);
    }

    [Fact]
    public void Mode_1048_is_an_action_not_a_persistent_boolean_toggle()
    {
        var parser = new AnsiParser(new TerminalBuffer(20, 5));
        var responses = new List<string>();
        parser.OnResponse = responses.Add;
        parser.Process("\u001b[?1048h\u001b[?1048$p\u001b[?1048l\u001b[?1048$p");
        Assert.Equal(new[] { "\u001b[?1048;0$y", "\u001b[?1048;0$y" }, responses);
    }

    [Theory]
    [InlineData("\u001b7", "\u001b[?1048l")]
    [InlineData("\u001b[?1048h", "\u001b8")]
    public void Saved_cursor_restores_origin_wrap_and_character_set_context(string save, string restore)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[2;7r\u001b[?6h\u001b[2;3H\u001b[?7l\u001b(0" + save);
        parser.Process("\u001b[?6l\u001b[?7h\u001b(B\u001b[1;1H" + restore);
        Assert.True(buffer.Modes.IsOriginMode);
        Assert.False(buffer.Modes.IsAutoWrapMode);
        Assert.Equal((2, 2), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("q");
        Assert.Equal('─', Cell(buffer, 2, 2));
    }

    [Fact]
    public void Mode_1049_uses_the_same_complete_main_cursor_bank()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("\u001b[?6h\u001b[3;4H\u001b[?7l\u001b(0\u001b[?1049h" +
            "\u001b[?6l\u001b[?7h\u001b(B\u001b[?1049l");
        Assert.False(buffer.IsAltScreenActive);
        Assert.True(buffer.Modes.IsOriginMode);
        Assert.False(buffer.Modes.IsAutoWrapMode);
        Assert.Equal((2, 3), (buffer.CursorRow, buffer.CursorCol));
        parser.Process("q");
        Assert.Equal('─', Cell(buffer, 3, 2));
    }

}
