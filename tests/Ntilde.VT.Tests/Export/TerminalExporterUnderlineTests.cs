using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests.Export;

/// <summary>
/// #270: Export Snapshot (ANSI) keeps underline styles and colors.
/// </summary>
public sealed class TerminalExporterUnderlineTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal(int rows = 3)
    {
        var buffer = new TerminalBuffer(20, rows);
        return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null });
    }

    private static string[] Lines(TerminalBuffer buffer)
        => Array.ConvertAll(TerminalExporter.ExportToAnsi(buffer).Split('\n'), line => line.TrimEnd('\r'));

    [Fact]
    public void Styled_and_colored_underlines_survive_a_replay()
    {
        // Each change takes a different path: a style or color turning on, changing, going plain, going away,
        // and staying put while bold turns off, which resets everything else.
        var (source, parser) = NewTerminal();
        parser.Process("\x1b[4:3;58:2::1:2:3mA\x1b[4:2;58;5;9mB\x1b[4mC\x1b[58;5;4mD\x1b[4:5mE\x1b[0mF\x1b[4:4mG"
            + "\x1b[0;1;4:3mH\x1b[22mI");

        var (replay, replayParser) = NewTerminal();
        replayParser.Process(Lines(source)[0]);

        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, TermColor.FromRgb(1, 2, 3)), replay.GetUnderlineAbsolute(0, 0));
        for (int c = 1; c < 9; c++)
        {
            Assert.True(source.GetUnderlineAbsolute(c, 0) == replay.GetUnderlineAbsolute(c, 0), $"underline at {c}");
        }
    }

    [Fact]
    public void A_styled_underline_on_trailing_blanks_is_kept()
    {
        // An editor's squiggle on trailing whitespace, drawn as the renderer draws it; plain SGR 4 there is still trimmed.
        var (source, parser) = NewTerminal();
        parser.Process("AB\x1b[4:3m   \x1b[0m\r\nCD\x1b[4m   \x1b[0m");

        string[] lines = Lines(source);

        Assert.Equal("AB\x1b[4m\x1b[4:3m   \x1b[0m", lines[0]);
        Assert.Equal("CD", lines[1]);
    }

    [Fact]
    public void On_the_alternate_screen_attributes_come_from_the_rows_shown()
    {
        // Where an editor draws its squiggles. The export read attributes at the main screen's absolute rows,
        // so with scrollback present it took them from the wrong rows, or found none.
        var (source, parser) = NewTerminal();
        parser.Process("1\r\n2\r\n3\r\n4\r\n5\r\n6");
        Assert.True(source.Scrollback.Count >= 3);
        parser.Process("\x1b[?1049h\x1b[H\x1b[1mBOLD\x1b[0m \x1b[4:3mcurly\x1b[0m");

        Assert.Equal("\x1b[1mBOLD\x1b[0m \x1b[4m\x1b[4:3mcurly\x1b[0m", Lines(source)[0]);
    }
}
