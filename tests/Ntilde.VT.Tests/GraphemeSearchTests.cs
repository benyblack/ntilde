using System.Text.RegularExpressions;

namespace Ntilde.VT.Tests;

public sealed class GraphemeSearchTests
{
    public static TheoryData<string, int, bool, string> Cases
    {
        get
        {
            var data = new TheoryData<string, int, bool, string>();
            foreach (var (text, width) in new[] { ("e\u0301", 1), ("😀", 2), ("👩‍💻", 2), ("界", 2) })
                foreach (bool regex in new[] { false, true })
                    foreach (string phase in new[] { "viewport", "scrollback", "reflow" })
                        data.Add(text, width, regex, phase);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SearchRetainsGraphemeAndCompleteCellSpan(string grapheme, int width, bool regex, string phase)
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer, false);
        parser.Process("abc" + grapheme + "!");
        if (phase != "viewport") parser.Process("\r\nsecond\r\nthird");
        if (phase == "reflow") buffer.Resize(4, 2);

        int expectedRow = phase == "reflow" && width == 2 ? 1 : 0;
        int expectedCol = phase == "reflow" && width == 2 ? 0 : 3;
        string query = regex ? Regex.Escape(grapheme) : grapheme;
        var match = Assert.Single(buffer.FindMatches(query, regex, caseSensitive: true));
        Assert.Equal(expectedRow, match.AbsRow);
        Assert.Equal(expectedCol, match.StartCol);
        Assert.Equal(expectedCol + width - 1, match.EndCol);

        if (phase == "reflow")
        {
            buffer.Resize(20, 2);
            match = Assert.Single(buffer.FindMatches(query, regex, caseSensitive: true));
            Assert.Equal(0, match.AbsRow);
            // Existing reflow retains the wide-glyph pre-wrap padding when widened again.
            int widenedColumn = width == 2 ? 4 : 3;
            Assert.Equal(widenedColumn, match.StartCol);
            Assert.Equal(widenedColumn + width - 1, match.EndCol);
        }
    }
}
