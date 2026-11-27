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

    private int WireRow(int visualRow)
    {
        Assert.True(_buffer.TryGetPromptClickRow(visualRow, out int row, out _));
        return row;
    }
}
