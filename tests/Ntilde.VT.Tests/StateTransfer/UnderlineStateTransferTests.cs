using Ntilde.VT;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// #270: a mux or agent attach restores underline styles and colors with the rest of the state.
/// </summary>
/// <remarks>
/// Codex review of PR #648: the import cleared every row's underline side table, and the payload carried
/// neither the table nor the pen's style and color, so an attach turned styled underlines plain and the
/// next output used the wrong pen until another SGR 4:x arrived. Each test goes through the JSON bytes, as
/// an attach does.
/// </remarks>
public class UnderlineStateTransferTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal()
    {
        var buffer = new TerminalBuffer(40, 4);
        return (buffer, new AnsiParser(buffer));
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) RoundTrip(TerminalBuffer source, AnsiParser sourceParser)
    {
        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        snapshot.Parser = sourceParser.ExportState();
        TerminalStateSnapshot onTheWire = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(snapshot));

        var (restored, restoredParser) = NewTerminal();
        TerminalStateTransfer.Restore(restored, restoredParser, onTheWire);
        return (restored, restoredParser);
    }

    private static CellUnderline? OnScreen(TerminalBuffer buffer, int col, int row)
        => buffer.GetUnderlineAbsolute(col, buffer.Scrollback.Count + row);

    [Fact]
    public void ScreenAndScrollbackUnderlines_SurviveTheWire()
    {
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3;58;5;9mscrolled\x1b[0m\r\n");
        parser.Process("x\r\nx\r\nx\r\n");
        parser.Process("\x1b[4:2;58;2;1;2;3mA\x1b[0m\x1b[4mB\x1b[0m");
        Assert.True(source.Scrollback.Count > 0);

        var (restored, _) = RoundTrip(source, parser);

        Assert.Equal(source.Scrollback.Count, restored.Scrollback.Count);
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9), restored.GetUnderlineAbsolute(0, 0));
        Assert.Equal(new CellUnderline(UnderlineStyle.Double, TermColor.FromRgb(1, 2, 3)), OnScreen(restored, 0, restored.CursorRow));
        Assert.Null(OnScreen(restored, 1, restored.CursorRow)); // a plain underline is still just the flag
    }

    [Fact]
    public void CellsSharingAnUnderline_StillShareOneInstance()
    {
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3mABC");

        var (restored, _) = RoundTrip(source, parser);

        Assert.NotNull(OnScreen(restored, 0, 0));
        Assert.Same(OnScreen(restored, 0, 0), OnScreen(restored, 2, 0));
    }

    [Fact]
    public void TheLivePen_SurvivesTheWire()
    {
        // The application set the style before the attach; the output after it must still use it.
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3;58;5;9m");

        var (restored, restoredParser) = RoundTrip(source, parser);
        restoredParser.Process("A");

        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9), OnScreen(restored, 0, 0));
    }

    [Fact]
    public void TheSavedCursorPen_SurvivesTheWire()
    {
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:2;58;2;1;2;3m\x1b7\x1b[0m");

        var (restored, restoredParser) = RoundTrip(source, parser);
        restoredParser.Process("\x1b8A");

        Assert.Equal(new CellUnderline(UnderlineStyle.Double, TermColor.FromRgb(1, 2, 3)), OnScreen(restored, 0, 0));
    }

    [Fact]
    public void AnOutOfRangePaletteIndexFromTheApplication_StillAttaches()
    {
        // The validation below refuses an underline palette index past 255; the parser must never keep one.
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3;58;5;300mA");

        var (restored, _) = RoundTrip(source, parser);

        Assert.Equal(new CellUnderline(UnderlineStyle.Curly), OnScreen(restored, 0, 0));
    }

    [Fact]
    public void APlainUnderlineEntry_ImportsAsNoEntry()
    {
        // A straight underline in the text color has no side-table entry on the live path; an attach that stored one
        // would have the exports and the renderer treat a plain underline as a styled one.
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3mA\x1b[0m\r\nx\r\nx\r\nx\r\n\x1b[4:3mB");
        Assert.True(source.Scrollback.Count > 0);
        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        foreach (TerminalUnderlineEntry entry in snapshot.Underlines)
        {
            entry.Style = (byte)UnderlineStyle.Single;
        }

        var (restored, _) = NewTerminal();
        restored.ImportState(snapshot);

        Assert.Null(restored.GetUnderlineAbsolute(0, 0)); // scrollback
        Assert.Null(OnScreen(restored, 0, restored.CursorRow));
        restored.Lock.EnterReadLock();
        try
        {
            Assert.True(restored.GetCellAbsolute(0, 0).IsUnderline);
        }
        finally
        {
            restored.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void AnUnderlineIndexNamingNoEntry_IsRejected()
    {
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3mA");
        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        snapshot.Main.UnderlineIds![0] = 7;

        var (target, _) = NewTerminal();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void AnUnknownStyle_IsRejected(byte style)
    {
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3mA");
        TerminalStateSnapshot snapshot = source.ExportState(int.MaxValue);
        snapshot.Underlines[0].Style = style;

        var (target, _) = NewTerminal();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
    }
}
