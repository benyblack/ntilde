using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests;

public class PromptClickTests
{
    private readonly TerminalBuffer _buffer = new(80, 24);
    private readonly AnsiParser _parser;

    public PromptClickTests() => _parser = new AnsiParser(_buffer);

    [Theory]
    [InlineData(1, 5, 7, 9, 10)]
    [InlineData(2, 1, 3, 1, 2)]
    public void PromptRowsAreAbsoluteOrRelativeToTheApplicableAnchor(
        int mode, int primaryTop, int beforeSecondary, int secondaryTop, int afterSecondary)
    {
        _parser.Process($"\x1b[5;1H\x1b]133;A;redraw=0;click_events={mode}\x1b\\PS1> ");
        _parser.Process("\x1b[9;1H\x1b]133;A;k=s\aPS2> \r\ncontinued");

        Assert.False(_buffer.TryGetPromptClickRow(3, out _, out _));
        Assert.Equal(primaryTop, WireRow(4));
        Assert.Equal(beforeSecondary, WireRow(6));
        Assert.Equal(secondaryTop, WireRow(8));
        Assert.Equal(afterSecondary, WireRow(9));
        Assert.False(_buffer.TryGetPromptClickRow(10, out _, out _));
    }

    [Theory]
    [InlineData("\x1b]133;C\a")]
    [InlineData("\x1b]133;D;0\a")]
    [InlineData("\x1b]133;A\a")]
    [InlineData("\x1b]133;A;click_events=0\a")]
    [InlineData("\x1b]633;A;click_events=2\a")]
    [InlineData("\x1b[?1049h\x1b[?1049l")]
    [InlineData("\x1b[?25l")]
    [InlineData("\x1b[?1000h")]
    [InlineData("\x1b[3J")]
    [InlineData("\x1b" + "c")]
    public void ClicksAreSuppressedWhenThePromptIsNoLongerEligible(string sequence)
    {
        _parser.Process("\x1b]133;A;click_events=2\aPS1> ");
        Assert.Equal(1, WireRow(0));

        _parser.Process(sequence);
        _parser.Process("\x1b[H");
        Assert.False(_buffer.TryGetPromptClickRow(0, out _, out _));
    }

    [Fact]
    public void NewOptedInPromptReplacesThePreviousContinuationAnchor()
    {
        _parser.Process("\x1b[3;1H\x1b]133;A;click_events=2\a\x1b[7;1H\x1b]133;A;k=s\a");
        Assert.Equal(1, WireRow(6));

        _parser.Process("\x1b[5;1H\x1b]133;A;click_events=2\a\x1b[7;1H");
        Assert.Equal(3, WireRow(6));
    }

    [Fact]
    public void SecondaryMarkerWithoutAnOptedInPrimaryDoesNotEnableClicks()
    {
        _parser.Process("\x1b]133;A;k=s\aPS2> ");
        Assert.False(_buffer.TryGetPromptClickRow(0, out _, out _));
    }

    [Fact]
    public void SecondaryPromptCanRequestRelativeReports()
    {
        _parser.Process("\x1b[3;1H\x1b]133;A\a\x1b[5;1H\x1b]133;A;k=s;click_events=2\aPS2> ");
        Assert.Equal(2, WireRow(3));
        Assert.Equal(1, WireRow(4));
    }

    [Theory]
    [InlineData(1, 1, 2, 4)]
    [InlineData(2, 2, 1, 1)]
    public void RetainedHistoryAnchorsKeepVisiblePromptRowsClickable(int mode, int atTop, int atSecondary, int atFreshSecondary)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        parser.Process($"\x1b]133;A;click_events={mode}\aPS1>\r\nfirst\r\n\x1b]133;A;k=s\aPS2>\r\nthird\r\nfourth");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.True(buffer.TryGetPromptClickRow(0, out int top, out _));
        Assert.Equal(atTop, top);
        Assert.True(buffer.TryGetPromptClickRow(1, out int secondary, out _));
        Assert.Equal(atSecondary, secondary);

        parser.Process("\r\n\x1b]133;A;k=s\aPS2> fifth");
        Assert.Equal(2, buffer.Scrollback.Count);
        Assert.True(buffer.TryGetPromptClickRow(3, out int freshSecondary, out _));
        Assert.Equal(atFreshSecondary, freshSecondary);

        parser.Process("\r\nsixth");
        Assert.Equal(3, buffer.Scrollback.Count);
        Assert.True(buffer.TryGetPromptClickRow(0, out int afterHistoricalSecondary, out _));
        Assert.Equal(atTop, afterHistoricalSecondary);
    }

    [Theory]
    [InlineData(1, 4, 5)]
    [InlineData(2, 2, 1)]
    public void WidthReflowKeepsBothAnchorsWithTheirContent(int mode, int beforeSecondary, int atSecondary)
    {
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process($"0123456789abcdef\r\n\x1b]133;A;click_events={mode}\aPS1>\r\nfirst\r\n\x1b]133;A;k=s\aPS2>\r\ncontinued");
        Assert.True(buffer.TryGetPromptClickRow(3, out _, out ulong oldVersion));

        buffer.Resize(10, 12);
        Assert.False(buffer.TryGetPromptClickRow(1, out _, out _));
        Assert.True(buffer.TryGetPromptClickRow(3, out int primary, out ulong newVersion));
        Assert.Equal(beforeSecondary, primary);
        Assert.NotEqual(oldVersion, newVersion);
        Assert.True(buffer.TryGetPromptClickRow(4, out int secondary, out _));
        Assert.Equal(atSecondary, secondary);

        buffer.Resize(20, 12);
        Assert.True(buffer.TryGetPromptClickRow(2, out primary, out _));
        Assert.Equal(mode == 1 ? 3 : 2, primary);
        Assert.True(buffer.TryGetPromptClickRow(3, out secondary, out _));
        Assert.Equal(mode == 1 ? 4 : 1, secondary);
    }

    [Theory]
    [InlineData(1, 5, 6)]
    [InlineData(2, 2, 2)]
    public void MovingTheEditorCursorUpKeepsLowerWrittenInputClickable(int mode, int wireRow, int reflowedWireRow)
    {
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process($"0123456789abcdef\r\n\x1b]133;A;click_events={mode}\aPS1>\r\nfirst\r\n\x1b]133;A;k=s\aPS2>\r\ncontinued\x1b[2;1H");
        Assert.Equal(1, buffer.CursorRow);
        Assert.True(buffer.TryGetPromptClickRow(4, out int beforeResize, out _));
        Assert.Equal(wireRow, beforeResize);
        Assert.False(buffer.TryGetPromptClickRow(5, out _, out _));

        buffer.Resize(10, 12);
        Assert.Equal(2, buffer.CursorRow);
        Assert.True(buffer.TryGetPromptClickRow(5, out int afterResize, out _));
        Assert.Equal(reflowedWireRow, afterResize);
        Assert.False(buffer.TryGetPromptClickRow(6, out _, out _));

        parser.Process("\x1b[J");
        Assert.False(buffer.TryGetPromptClickRow(5, out _, out _));
        parser.Process("\r\nnew limit");
        Assert.True(buffer.TryGetPromptClickRow(3, out _, out _));
        Assert.False(buffer.TryGetPromptClickRow(4, out _, out _));
    }

    [Theory]
    [InlineData("\x1b[2;1H\x1b[2K\x1b[3;1H\x1b[2K")]
    [InlineData("\x1b[3;1H\x1b[K\x1b[2;1H\x1b[K")]
    [InlineData("\x1b[2;80H\x1b[1K\x1b[3;80H\x1b[1K")]
    public void FullLineErasureShrinksThePromptTailInEitherOrder(string erase)
    {
        _parser.Process("\x1b]133;A;click_events=2\aPS1>\r\n\x1b]133;A;k=s\alower\r\nlast\x1b[H");
        Assert.Equal(2, WireRow(2));

        _parser.Process(erase + "\x1b[H");
        Assert.Equal(1, WireRow(0));
        Assert.False(_buffer.TryGetPromptClickRow(1, out _, out _));
        Assert.False(_buffer.TryGetPromptClickRow(2, out _, out _));
    }

    [Fact]
    public void ReflowKeepsTheRetainedInputWhenATrailingBlankRowIsTrimmed()
    {
        var buffer = new TerminalBuffer(20, 12);
        var parser = new AnsiParser(buffer);
        parser.Process("\x1b]133;A;click_events=2\a" + new string('a', 20) + " \x1b[H");

        buffer.Resize(10, 12);
        Assert.True(buffer.TryGetPromptClickRow(1, out int row, out _));
        Assert.Equal(2, row);
        Assert.False(buffer.TryGetPromptClickRow(2, out _, out _));
    }

    [Theory]
    [InlineData("K")]
    [InlineData("J")]
    public void PartialTailErasureDoesNotReflowOldPromptPadding(string erase)
    {
        var parser = new AnsiParser(_buffer);
        parser.Process("\x1b[3;1HOUT\x1b[H\x1b]133;A;click_events=2\aPS1>\r\n" + new string('a', 70));
        parser.Process("\x1b[2;6H\x1b[" + erase + "\x1b[H");

        _buffer.Resize(10, 24);
        Assert.Equal(2, WireRow(1));
        Assert.False(_buffer.TryGetPromptClickRow(2, out _, out _));
        if (erase == "K")
        {
            _buffer.Lock.EnterReadLock();
            try { Assert.Equal('O', _buffer.GetCell(0, 2).Character); }
            finally { _buffer.Lock.ExitReadLock(); }
        }
    }

    [Fact]
    public void PromptClickAnchorsLeaveReflowContentIdenticalAfterSnapshotImport()
    {
        var live = new TerminalBuffer(20, 12);
        new AnsiParser(live).Process("\x1b]133;A;click_events=2\a" + new string('a', 20) + " \x1b[H");
        var restored = new TerminalBuffer(20, 12);
        restored.ImportState(live.ExportState(0));

        foreach (TerminalBuffer buffer in new[] { live, restored })
        {
            buffer.Resize(10, 12);
            new AnsiParser(buffer).Process("\x1b[3;1HZ");
            buffer.Resize(40, 12);
        }

        var actual = live.ExportState(0).Main;
        var expected = restored.ExportState(0).Main;
        Assert.Equal(expected.CellsBase64, actual.CellsBase64);
        Assert.Equal(expected.RowWraps, actual.RowWraps);
    }

    [Fact]
    public void RepeatedWidthChangesKeepTheExclusiveEndOfRetainedText()
    {
        var buffer = new TerminalBuffer(20, 12);
        new AnsiParser(buffer).Process("\x1b]133;A;click_events=2\a" + new string('a', 20) + "\x1b[H");

        foreach (int width in new[] { 10, 20, 19 })
            buffer.Resize(width, 12);

        Assert.True(buffer.TryGetPromptClickRow(1, out int row, out _));
        Assert.Equal(2, row);
        Assert.False(buffer.TryGetPromptClickRow(2, out _, out _));
    }

    [Theory]
    [InlineData("\x1b[3;1H\x1b[2M")]
    [InlineData("\x1b[3;1H\x1b[2L")]
    [InlineData("\x1b[2;8r\x1b[S")]
    [InlineData("\x1b[T")]
    public void RowEditsThatIntersectThePromptRequireFreshAnchors(string edit)
    {
        _parser.Process("\x1b]133;A;click_events=2\aPS1>\x1b[5;1H\x1b]133;A;k=s\aPS2>");
        Assert.Equal(1, WireRow(4));

        _parser.Process(edit);
        Assert.False(_buffer.TryGetPromptClickRow(2, out _, out _));
        _parser.Process("\x1b[4;1H\x1b]133;A;click_events=2\aPS1>");
        Assert.Equal(1, WireRow(3));
    }

    private int WireRow(int visualRow)
    {
        Assert.True(_buffer.TryGetPromptClickRow(visualRow, out int row, out _));
        return row;
    }
}
