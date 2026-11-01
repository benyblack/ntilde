using System.Runtime.InteropServices;

namespace Ntilde.VT.Tests;

public sealed class LogicalLineSearchStateTests
{
    private const string Emoji = "👩‍💻";

    private static TerminalBuffer Source(bool scrollback = false)
    {
        var buffer = new TerminalBuffer(4, 2);
        var parser = new AnsiParser(buffer, false);
        parser.Process("abc" + Emoji + "!");
        if (scrollback) parser.Process("\r\ntail");
        return buffer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StateRoundTripPreservesWrapPaddingInViewportAndScrollback(bool scrollback)
    {
        var source = Source(scrollback);
        var state = TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(source.ExportState(100)));
        var restored = new TerminalBuffer(4, 2);
        restored.ImportState(state);
        var match = Assert.Single(restored.FindMatches("c" + Emoji, false, true));
        Assert.Equal(2, match.SpanCount);
        Assert.Equal(new SearchMatchSpan(0, 2, 2), match.GetSpan(0));
        Assert.Equal(new SearchMatchSpan(1, 0, 1), match.GetSpan(1));
        restored.Resize(8, 2);
        Assert.Single(restored.FindMatches("c" + Emoji, false, true));
    }

    [Fact]
    public void MissingOptionalMetadataKeepsLegacySpacesInsteadOfGuessing()
    {
        var state = Source().ExportState(0);
        state.Main.RowWrapColumns = null;
        var restored = new TerminalBuffer(4, 2);
        restored.ImportState(state);
        Assert.Empty(restored.FindMatches("c" + Emoji, false, true));
        Assert.Single(restored.FindMatches("c " + Emoji, false, true));
    }

    [Fact]
    public void LegacyReplayRoundTripCarriesOptionalPaddingAndResetsAbsentWrapFlags()
    {
        var state = Source().ExportState(0);
        var replay = new ReplaySnapshot
        {
            Cols = 4,
            Rows = 2,
            ScrollBottom = 1,
            IsAutoWrapMode = true,
            CellsBase64 = state.Main.CellsBase64,
            ExtendedText = state.Main.ExtendedText,
            RowWraps = state.Main.RowWraps,
            RowWrapColumns = state.Main.RowWrapColumns
        };
        var restored = new TerminalBuffer(4, 2);
        restored.ApplySnapshot(replay);
        Assert.Single(restored.FindMatches("c" + Emoji, false, true));
        replay.RowWrapColumns = null;
        replay.RowWraps = null;
        restored.ApplySnapshot(replay);
        Assert.Empty(restored.FindMatches("c " + Emoji, false, true));
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("tooWide")]
    [InlineData("hidesText")]
    [InlineData("hardLine")]
    [InlineData("shortArray")]
    [InlineData("extendedTail")]
    [InlineData("styledTail")]
    public void MalformedWrapMetadataIsRejectedBeforeDestinationChanges(string kind)
    {
        var state = Source().ExportState(0);
        switch (kind)
        {
            case "negative": state.Main.RowWrapColumns![0] = -2; break;
            case "tooWide": state.Main.RowWrapColumns![0] = 5; break;
            case "hidesText": state.Main.RowWrapColumns![0] = 0; break;
            case "hardLine": state.Main.RowWraps![0] = false; break;
            case "shortArray": state.Main.RowWrapColumns = [3]; break;
            case "extendedTail": (state.Main.ExtendedText ??= [])[3] = "hidden"; break;
            case "styledTail":
                byte[] bytes = Convert.FromBase64String(state.Main.CellsBase64!);
                MemoryMarshal.Cast<byte, TerminalCell>(bytes.AsSpan())[3].IsDefaultBackground = false;
                state.Main.CellsBase64 = Convert.ToBase64String(bytes);
                break;
        }
        var destination = new TerminalBuffer(4, 2);
        new AnsiParser(destination, false).Process("keep");
        byte[] before = TerminalStateSerializer.ToBytes(destination.ExportState(0));
        Assert.Throws<InvalidOperationException>(() => destination.ImportState(state));
        Assert.Equal(before, TerminalStateSerializer.ToBytes(destination.ExportState(0)));
    }

    [Theory]
    [InlineData(" ", "c ")]
    [InlineData("X", "cX")]
    public void WritingIntoPaddingMakesItRealContent(string text, string queryPrefix)
    {
        var buffer = Source();
        new AnsiParser(buffer, false).Process("\u001b[1;4H" + text);
        Assert.Single(buffer.FindMatches(queryPrefix + Emoji, false, true));
        Assert.Empty(buffer.FindMatches("c" + Emoji, false, true));
    }

    [Fact]
    public void EditingEarlierColumnsKeepsUntouchedPaddingExcluded()
    {
        var buffer = Source();
        new AnsiParser(buffer, false).Process("\u001b[1;1Hx");
        Assert.Single(buffer.FindMatches("c" + Emoji, false, true));
    }

    [Fact]
    public void AlternateScreenSearchDoesNotBorrowMainHistory()
    {
        var buffer = new TerminalBuffer(5, 2);
        var parser = new AnsiParser(buffer, false);
        parser.Process("abcdefgh\r\ntail\u001b[?1049hHELLO");
        Assert.Empty(buffer.FindMatches("abcdefgh", false, true));
        Assert.Single(buffer.FindMatches("HELLO", false, true));
    }

    [Theory]
    [InlineData("ſ", "S")]
    [InlineData("ς", "Σ")]
    [InlineData("K", "K")]
    [InlineData("ı", "I")]
    [InlineData("\U00010428", "\U00010400")]
    [InlineData("\U00016ebb", "\U00016ea0")]
    public void LongNeedlesUseExactFrameworkOrdinalIgnoreCase(string suffix, string querySuffix)
    {
        string text = new string('x', 300) + suffix + new string('x', 300) + suffix;
        string query = new string('X', 257) + querySuffix;
        int expected = 0;
        for (int index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); index >= 0;
            index = text.IndexOf(query, index + 1, StringComparison.OrdinalIgnoreCase)) expected++;
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer, false).Process(text);
        Assert.Equal(expected, buffer.FindMatches(query, false, false).Count);
    }

    [Fact]
    public void LongPartialSurrogateNeedleKeepsFrameworkBehavior()
    {
        string text = new string('a', 257) + "👩";
        string query = new string('a', 257) + "\ud83d";
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer, false).Process(text);
        int expected = text.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Assert.Equal(expected, buffer.FindMatches(query, false, false).Count);
    }

    [Fact]
    public void SingleRowAndDefaultResultKeepOriginalPropertiesWithoutSpanArrays()
    {
        SearchMatch match = default;
        Assert.Equal(1, match.SpanCount);
        Assert.Equal(new SearchMatchSpan(0, 0, 0), match.GetSpan(0));
        match = new SearchMatch(2, 3, 4);
        Assert.Equal(new SearchMatchSpan(2, 3, 4), match.GetSpan(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => match.GetSpan(1));
    }
}
