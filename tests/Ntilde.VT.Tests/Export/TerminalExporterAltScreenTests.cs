using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests.Export;

/// <summary>
/// The text exports on the alternate screen, over scrollback: they read cells with GetCell, which counted the
/// alternate screen's rows from the main screen's scrollback, so their wide-character check looked at the
/// wrong row, or found no cell at all, while GetGrapheme read the right one. GetCell itself is fixed too.
/// </summary>
public sealed class TerminalExporterAltScreenTests
{
    private const string WideThenX = "\u754CX"; // a two-column character, then X

    private static TerminalBuffer AltScreenOverAScreenfulOfScrollback(string content)
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        parser.Process("1\r\n2\r\n3\r\n4\r\n5\r\n6");
        Assert.True(buffer.Scrollback.Count >= buffer.Rows);
        parser.Process("\x1b[?1049h\x1b[H" + content);
        return buffer;
    }

    [Fact]
    public void PlainText_SkipsTheSecondHalfOfAWideCharacter()
    {
        TerminalBuffer buffer = AltScreenOverAScreenfulOfScrollback(WideThenX);

        Assert.Equal(WideThenX, TerminalExporter.ExportToPlainText(buffer));
    }

    [Fact]
    public void VisibleRowTexts_SkipTheSecondHalfOfAWideCharacter()
    {
        TerminalBuffer buffer = AltScreenOverAScreenfulOfScrollback(WideThenX);

        Assert.Equal(WideThenX, TerminalExporter.GetVisibleRowTexts(buffer)[0]);
        Assert.Equal(WideThenX, TerminalExporter.GetLastNonEmptyRowText(buffer));
    }

    [Fact]
    public void GetCell_ReadsTheAlternateScreensOwnRow()
    {
        TerminalBuffer buffer = AltScreenOverAScreenfulOfScrollback(WideThenX);

        buffer.Lock.EnterReadLock();
        try
        {
            Assert.True(buffer.GetCell(0, 0).IsWide, "the wide character");
            Assert.True(buffer.GetCell(1, 0).IsWideContinuation, "its second half");
            Assert.Equal('X', buffer.GetCell(2, 0).Character);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
