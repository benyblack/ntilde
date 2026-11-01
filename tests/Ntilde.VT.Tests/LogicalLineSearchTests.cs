using System.Text.RegularExpressions;

namespace Ntilde.VT.Tests;

public sealed class LogicalLineSearchTests
{
    public static TheoryData<bool, string> Phases
    {
        get
        {
            var data = new TheoryData<bool, string>();
            foreach (bool regex in new[] { false, true })
                foreach (string phase in new[] { "viewport", "scrollback", "reflow" }) data.Add(regex, phase);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public void SoftWrapIsOneSearchableLogicalLine(bool regex, string phase)
    {
        var buffer = new TerminalBuffer(5, 2);
        var parser = new AnsiParser(buffer, false);
        parser.Process("abcdefgh");
        if (phase == "scrollback") parser.Process("\r\ntail");
        if (phase == "reflow") buffer.Resize(4, 2);
        var match = Assert.Single(buffer.FindMatches("cdefg", regex, true));
        Assert.Equal(0, match.AbsRow);
        Assert.Equal(2, match.StartCol);
        Assert.Equal(2, match.SpanCount);
        Assert.Equal(new SearchMatchSpan(0, 2, phase == "reflow" ? 3 : 4), match.GetSpan(0));
        Assert.Equal(new SearchMatchSpan(1, 0, phase == "reflow" ? 2 : 1), match.GetSpan(1));
    }

    public static TheoryData<bool, string, bool> WideCases
    {
        get
        {
            var data = new TheoryData<bool, string, bool>();
            foreach (bool regex in new[] { false, true })
                foreach (string phase in new[] { "viewport", "scrollback", "reflow" })
                    foreach (bool authoredSpace in new[] { false, true }) data.Add(regex, phase, authoredSpace);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WideCases))]
    public void WidePrewrapDoesNotInventOrRemoveAuthoredSpaces(bool regex, string phase, bool authoredSpace)
    {
        const string emoji = "👩‍💻";
        var buffer = new TerminalBuffer(4, 2);
        var parser = new AnsiParser(buffer, false);
        parser.Process("abc" + (authoredSpace ? " " : "") + emoji + "!");
        if (phase == "scrollback") parser.Process("\r\ntail");
        if (phase == "reflow") buffer.Resize(8, 2);
        string query = "c" + (authoredSpace ? " " : "") + emoji;
        var match = Assert.Single(buffer.FindMatches(regex ? Regex.Escape(query) : query, regex, true));
        if (phase == "reflow")
        {
            Assert.Equal(1, match.SpanCount);
            Assert.Equal(new SearchMatchSpan(0, 2, authoredSpace ? 5 : 4), match.GetSpan(0));
        }
        else
        {
            Assert.Equal(2, match.SpanCount);
            Assert.Equal(new SearchMatchSpan(0, 2, authoredSpace ? 3 : 2), match.GetSpan(0));
            Assert.Equal(new SearchMatchSpan(1, 0, 1), match.GetSpan(1));
        }
        if (authoredSpace) Assert.Empty(buffer.FindMatches("c" + emoji, regex, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HardNewlineIsASearchBoundary(bool regex)
    {
        var buffer = new TerminalBuffer(5, 2);
        new AnsiParser(buffer, false).Process("abc\r\ndef");
        Assert.Empty(buffer.FindMatches("cdef", regex, true));
    }

    [Fact]
    public void LiteralOverlapsArePreservedAcrossWraps()
    {
        var buffer = new TerminalBuffer(4, 2);
        new AnsiParser(buffer, false).Process("aaaaaaa");
        Assert.Equal(5, buffer.FindMatches("aaa", false, true).Count);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("$")]
    [InlineData("\\b")]
    public void InvalidOrEmptyRegexMatchesRemainEmpty(string pattern)
    {
        var buffer = new TerminalBuffer(5, 2);
        new AnsiParser(buffer, false).Process("abcdefgh");
        Assert.Empty(buffer.FindMatches(pattern, true, true));
    }

    [Fact]
    public void BacktrackingTimeoutDiscardsLogicalLineResults()
    {
        var buffer = new TerminalBuffer(64, 2);
        new AnsiParser(buffer, false).Process(new string('a', 4096) + "!");
        Assert.Empty(buffer.FindMatches("^(a+)+$", true, true));
    }
}
