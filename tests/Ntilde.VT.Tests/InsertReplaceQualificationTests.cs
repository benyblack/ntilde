namespace Ntilde.VT.Tests;

public sealed class InsertReplaceQualificationTests
{
    private const string Esc = "\u001b";

    private static string Line(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return new string(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetCell(col, row).Character).ToArray()); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TerminalCell Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Pinned_fixture_and_transition_use_standard_mode_four()
    {
        var buffer = new TerminalBuffer(8, 2);
        var parser = new AnsiParser(buffer);
        parser.Process($"{Esc}[4l{Esc}[1;1H{Esc}[2KABC");
        Assert.Equal("ABC     ", Line(buffer, 0));
        parser.Process($"{Esc}[1;1H");
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        parser.Process($"{Esc}[4hX");
        Assert.Equal("XABC    ", Line(buffer, 0));
        Assert.Equal((0, 1), (buffer.CursorRow, buffer.CursorCol));
        parser.Process($"{Esc}[4lY");
        Assert.Equal("XYBC    ", Line(buffer, 0));
        Assert.Equal((0, 2), (buffer.CursorRow, buffer.CursorCol));
        Assert.False(buffer.Modes.IsInsertMode);
        Assert.Equal("        ", Line(buffer, 1));
    }

    [Fact]
    public void Multi_character_insert_clips_at_edge_and_preserves_shifted_attributes()
    {
        var buffer = new TerminalBuffer(8, 2);
        var parser = new AnsiParser(buffer);
        parser.Process($"{Esc}[?7l{Esc}[1;1HABC{Esc}[1mDE{Esc}[0mFGH{Esc}[2;1HKEEP{Esc}[1;4H");
        var d = Cell(buffer, 3, 0);
        var e = Cell(buffer, 4, 0);
        parser.Process($"{Esc}[4h{Esc}[44mxy{Esc}[4l");
        Assert.Equal("ABCxyDEF", Line(buffer, 0));
        Assert.Equal("KEEP    ", Line(buffer, 1));
        Assert.Equal((0, 5), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(Cell(buffer, 5, 0).IsBold);
        Assert.True(Cell(buffer, 6, 0).IsBold);
        Assert.Equal((d.Flags, d.Fg, d.Bg), (Cell(buffer, 5, 0).Flags, Cell(buffer, 5, 0).Fg, Cell(buffer, 5, 0).Bg));
        Assert.Equal((e.Flags, e.Fg, e.Bg), (Cell(buffer, 6, 0).Flags, Cell(buffer, 6, 0).Fg, Cell(buffer, 6, 0).Bg));
        Assert.Equal(4, Cell(buffer, 3, 0).BgIndex);
        Assert.Equal(4, Cell(buffer, 4, 0).BgIndex);
        Assert.False(buffer.Modes.IsInsertMode);
    }
}
