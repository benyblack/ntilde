namespace Ntilde.VT.Tests;

/// <summary>
/// Background color erase for line scrolling: the lines a scroll (SU, and IND/NEL/LF at the bottom margin), SD, IL or DL brings in hold the
/// active SGR background, as xterm fills them (and as ED, EL, ECH and Reverse Index already do); with the default background they stay default cells.
/// </summary>
public sealed class ScrollBackgroundColorEraseTests
{
    private const string Esc = "\u001b";

    public static TheoryData<string, bool, bool> Cases
    {
        get
        {
            var cases = new TheoryData<string, bool, bool>();
            foreach (string operation in new[] { "SU", "SD", "IL", "DL", "LF", "IND", "NEL" })
                foreach (bool verticalMargins in new[] { false, true })
                    foreach (bool horizontalMargins in new[] { false, true })
                        cases.Add(operation, verticalMargins, horizontalMargins);
            return cases;
        }
    }

    /// <summary>A 10x6 screen, every cell a distinct letter; optionally DECLRMM margins 3-7 (zero-based 2-6) and DECSTBM rows 2-5 (zero-based 1-4).</summary>
    private static (TerminalBuffer Buffer, AnsiParser Parser) Seeded(bool verticalMargins, bool horizontalMargins)
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        Seed(parser);
        if (horizontalMargins) parser.Process($"{Esc}[?69h{Esc}[3;7s");
        if (verticalMargins) parser.Process($"{Esc}[2;5r");
        return (buffer, parser);
    }

    private static void Seed(AnsiParser parser)
    {
        for (int row = 0; row < 6; row++)
            parser.Process($"{Esc}[{row + 1};1H" + new string(Enumerable.Range(0, 10).Select(col => (char)('A' + (row * 10 + col) % 26)).ToArray()));
    }

    /// <summary>The input that performs <paramref name="operation"/>, with the cursor where it scrolls, and where the lines it moves start and its new line lands.</summary>
    private static (string Input, bool MovesUp, int From, int Blank) Plan(string operation, int top, int bottom) => operation switch
    {
        "SU" => ($"{Esc}[3;4H{Esc}[S", true, top, bottom),
        "SD" => ($"{Esc}[3;4H{Esc}[T", false, top, top),
        "IL" => ($"{Esc}[3;4H{Esc}[L", false, 2, 2),
        "DL" => ($"{Esc}[3;4H{Esc}[M", true, 2, bottom),
        "LF" => ($"{Esc}[{bottom + 1};4H\n", true, top, bottom),
        "IND" => ($"{Esc}[{bottom + 1};4H{Esc}D", true, top, bottom),
        "NEL" => ($"{Esc}[{bottom + 1};4H{Esc}E", true, top, bottom),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static TerminalCell Cell(TerminalBuffer buffer, int row, int col) => buffer.ViewportRows[row].Cells[col];

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_line_a_scroll_brings_in_holds_the_active_background(string operation, bool verticalMargins, bool horizontalMargins)
    {
        var (buffer, parser) = Seeded(verticalMargins, horizontalMargins);
        int top = verticalMargins ? 1 : 0, bottom = verticalMargins ? 4 : 5;
        int first = horizontalMargins ? 2 : 0, last = horizontalMargins ? 6 : 9;
        var (input, movesUp, from, blank) = Plan(operation, top, bottom);
        var before = Enumerable.Range(0, 6).Select(row => buffer.ViewportRows[row].Cells.ToArray()).ToArray();

        // A palette background: the new line is blank cells of that index, and the lines the scroll moved keep their cells.
        parser.Process($"{Esc}[44m{input}");
        for (int col = first; col <= last; col++)
        {
            var cell = Cell(buffer, blank, col);
            Assert.Equal(' ', cell.Character);
            Assert.False(cell.IsDefaultBackground);
            Assert.Equal(4, cell.BgIndex);
        }
        for (int row = 0; row < 6; row++)
        {
            if (row == blank) continue;
            for (int col = 0; col < 10; col++)
            {
                bool moved = col >= first && col <= last && row >= from && row <= bottom;
                int source = !moved ? row : movesUp ? row + 1 : row - 1;
                Assert.Equal(before[source][col].Character, Cell(buffer, row, col).Character);
            }
        }
        for (int col = 0; col < 10; col++)
        {
            if (col >= first && col <= last) continue;
            Assert.Equal(before[blank][col].Character, Cell(buffer, blank, col).Character); // outside the left/right margins nothing is filled
        }

        // A true-colour background replaces it, with no palette index.
        parser.Process($"{Esc}[48;2;18;52;86m{input}");
        for (int col = first; col <= last; col++)
        {
            var cell = Cell(buffer, blank, col);
            Assert.False(cell.IsDefaultBackground);
            Assert.Equal(-1, cell.BgIndex);
            Assert.Equal(new TermColor(18, 52, 86), cell.Background);
        }

        // Back to the default background, the new line is the default cell again.
        parser.Process($"{Esc}[49m{input}");
        for (int col = first; col <= last; col++)
        {
            var cell = Cell(buffer, blank, col);
            Assert.Equal(' ', cell.Character);
            Assert.True(cell.IsDefaultBackground);
        }
    }

    [Fact]
    public void Autowrap_at_the_bottom_right_scrolls_in_a_line_with_the_active_background()
    {
        var (buffer, parser) = Seeded(verticalMargins: false, horizontalMargins: false);
        parser.Process($"{Esc}[44m{Esc}[6;10HXY");
        Assert.Equal('Y', Cell(buffer, 5, 0).Character);
        for (int col = 1; col < 10; col++)
        {
            var cell = Cell(buffer, 5, col);
            Assert.Equal(' ', cell.Character);
            Assert.False(cell.IsDefaultBackground);
            Assert.Equal(4, cell.BgIndex);
        }
    }

    [Fact]
    public void Autowrap_at_the_right_margin_scrolls_the_band_in_with_the_active_background()
    {
        var (buffer, parser) = Seeded(verticalMargins: true, horizontalMargins: true);
        parser.Process($"{Esc}[44m{Esc}[5;7HXY");
        Assert.Equal('Y', Cell(buffer, 4, 2).Character);
        for (int col = 3; col <= 6; col++)
        {
            var cell = Cell(buffer, 4, col);
            Assert.Equal(' ', cell.Character);
            Assert.False(cell.IsDefaultBackground);
            Assert.Equal(4, cell.BgIndex);
        }
    }

    [Fact]
    public void The_alternate_screen_fills_scrolled_in_lines_with_the_active_background()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        parser.Process($"{Esc}[?1049h");
        Seed(parser);
        parser.Process($"{Esc}[44m{Esc}[6;1H\n");
        AssertRowBackground(buffer, 5);
        Seed(parser);
        parser.Process($"{Esc}[2;1H{Esc}[L");
        AssertRowBackground(buffer, 1);
        Seed(parser);
        parser.Process($"{Esc}[2;1H{Esc}[M");
        AssertRowBackground(buffer, 5);
    }

    private static void AssertRowBackground(TerminalBuffer buffer, int row)
    {
        for (int col = 0; col < 10; col++)
        {
            var cell = Cell(buffer, row, col);
            Assert.Equal(' ', cell.Character);
            Assert.False(cell.IsDefaultBackground);
            Assert.Equal(4, cell.BgIndex);
        }
    }

    [Fact]
    public void Scrolling_with_the_default_background_still_brings_in_default_cells()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        Seed(parser);
        parser.Process($"{Esc}[6;1H\n");
        for (int col = 0; col < 10; col++)
        {
            var cell = Cell(buffer, 5, col);
            Assert.Equal(' ', cell.Character);
            Assert.True(cell.IsDefaultBackground);
            Assert.True(cell.IsDefaultForeground);
        }
    }

    [Fact]
    public void A_foreground_colour_alone_does_not_change_the_default_blank_a_scroll_brings_in()
    {
        var buffer = new TerminalBuffer(10, 6);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false);
        Seed(parser);
        parser.Process($"{Esc}[31;1;4m{Esc}[6;1H\n");
        var reference = Cell(new TerminalBuffer(10, 6), 5, 0);
        for (int col = 0; col < 10; col++)
        {
            var cell = Cell(buffer, 5, col);
            Assert.Equal(reference.Flags & ~(int)TerminalCellFlags.Dirty, cell.Flags & ~(int)TerminalCellFlags.Dirty); // still unused padding, not a styled blank
            Assert.True(cell.IsDefaultForeground);
            Assert.True(cell.IsDefaultBackground);
        }

        // With a background the foreground is still the default one: a space shows none.
        parser.Process($"{Esc}[44m\n");
        for (int col = 0; col < 10; col++)
        {
            var cell = Cell(buffer, 5, col);
            Assert.True(cell.IsDefaultForeground);
            Assert.Equal(4, cell.BgIndex);
        }
    }
}
