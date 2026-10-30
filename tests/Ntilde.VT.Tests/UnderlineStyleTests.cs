using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// #270: underline styles (SGR 4:x, 21) and underline color (SGR 58/59).
/// </summary>
/// <remarks>
/// The style and color live in a row side table beside extended text and hyperlinks, not in
/// <see cref="TerminalCell"/>: its flags are full, and widening every cell (scrollback included)
/// for an attribute few cells carry is the wrong trade. A plain SGR 4 underline in the text color
/// is still just <see cref="TerminalCellFlags.Underline"/> and adds no entry; only a styled or
/// colored one does. So these tests check both halves: the cell flag, and the side entry.
/// </remarks>
public class UnderlineStyleTests
{
    private static (TerminalBuffer Buffer, AnsiParser Parser) NewTerminal(int cols = 80, int rows = 5)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer));
    }

    private static TerminalCell CellAt(TerminalBuffer buffer, int col, int row = 0)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static CellUnderline? UnderlineAt(TerminalBuffer buffer, int col, int viewportRow = 0)
        => buffer.GetUnderlineAbsolute(col, buffer.Scrollback.Count + viewportRow);

    [Theory]
    [InlineData(1, UnderlineStyle.Single)]
    [InlineData(2, UnderlineStyle.Double)]
    [InlineData(3, UnderlineStyle.Curly)]
    [InlineData(4, UnderlineStyle.Dotted)]
    [InlineData(5, UnderlineStyle.Dashed)]
    public void ColonSubparameter_SelectsTheStyle(int selector, UnderlineStyle expected)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process($"\x1b[4:{selector}mA");

        Assert.True(CellAt(buffer, 0).IsUnderline);
        Assert.Equal(expected, buffer.UnderlineStyle);
        Assert.Equal(expected, UnderlineAt(buffer, 0)?.Style ?? UnderlineStyle.Single);
    }

    [Fact]
    public void PlainUnderline_AddsNoSideEntry()
    {
        // The common case stays as cheap as before: the flag says it all.
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4mA\x1b[4:1mB");

        Assert.True(CellAt(buffer, 0).IsUnderline);
        Assert.True(CellAt(buffer, 1).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 0));
        Assert.Null(UnderlineAt(buffer, 1));
    }

    [Fact]
    public void Sgr4Colon0_TurnsTheUnderlineOff()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mA\x1b[4:0mB");

        Assert.False(CellAt(buffer, 1).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 1));
        Assert.False(buffer.IsUnderline);
    }

    [Fact]
    public void UnknownStyleSelector_FallsBackToAStraightUnderline()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:9mA");

        Assert.True(CellAt(buffer, 0).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 0));
    }

    [Fact]
    public void Sgr21_IsADoubleUnderline()
    {
        // ECMA-48's "doubly underlined", as xterm, kitty, ghostty and wezterm read it.
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[21mA");

        Assert.True(CellAt(buffer, 0).IsUnderline);
        Assert.Equal(UnderlineStyle.Double, UnderlineAt(buffer, 0)?.Style);
    }

    [Fact]
    public void PlainSgr4_AfterAStyle_IsStraightAgain()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mA\x1b[4mB");

        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 0)?.Style);
        Assert.Null(UnderlineAt(buffer, 1));
    }

    [Theory]
    [InlineData("\x1b[4;58;2;255;0;0mA")]
    [InlineData("\x1b[4;58:2::255:0:0mA")]
    [InlineData("\x1b[4;58:2:255:0:0mA")]
    public void Sgr58TrueColor_ColorsTheUnderline(string sequence)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process(sequence);

        CellUnderline? underline = UnderlineAt(buffer, 0);
        Assert.NotNull(underline);
        Assert.Equal(UnderlineStyle.Single, underline.Style);
        Assert.Equal(TermColor.FromRgb(255, 0, 0), underline.Color);
        Assert.Equal(-1, underline.ColorIndex);
        Assert.False(CellAt(buffer, 0).IsFaint); // the components did not leak as SGR codes
    }

    [Theory]
    [InlineData("\x1b[4:3;58;5;196mA")]
    [InlineData("\x1b[4:3;58:5:196mA")]
    public void Sgr58Palette_KeepsTheIndex(string sequence)
    {
        // An index, not the resolved color: like the foreground's, it follows the theme's palette.
        var (buffer, parser) = NewTerminal();

        parser.Process(sequence);

        CellUnderline? underline = UnderlineAt(buffer, 0);
        Assert.NotNull(underline);
        Assert.Equal(UnderlineStyle.Curly, underline.Style);
        Assert.Equal(196, underline.ColorIndex);
        Assert.Null(underline.Color);
    }

    [Fact]
    public void Sgr59_ResetsTheColor_AndKeepsTheStyle()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;2;1;2;3m\x1b[59mA");

        CellUnderline? underline = UnderlineAt(buffer, 0);
        Assert.NotNull(underline);
        Assert.Equal(UnderlineStyle.Curly, underline.Style);
        Assert.Null(underline.Color);
        Assert.Equal(-1, underline.ColorIndex);
    }

    [Fact]
    public void Sgr24_EndsTheUnderline_ButTheColorWaitsForTheNextOne()
    {
        // 24 is "not underlined"; 59 is what resets the color. A later SGR 4 picks the color up again.
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;2;1;2;3m\x1b[24mA\x1b[4:2mB");

        Assert.False(CellAt(buffer, 0).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 0));
        Assert.Equal(new CellUnderline(UnderlineStyle.Double, TermColor.FromRgb(1, 2, 3)), UnderlineAt(buffer, 1));
    }

    [Theory]
    [InlineData("\x1b[0m")]
    [InlineData("\x1b[m")]
    public void Sgr0_ResetsStyleAndColor(string reset)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;2;1;2;3m" + reset + "\x1b[4mA");

        Assert.True(CellAt(buffer, 0).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 0));
        Assert.Equal(UnderlineStyle.Single, buffer.UnderlineStyle);
        Assert.Null(buffer.UnderlineColor);
        Assert.Equal(-1, buffer.UnderlineColorIndex);
    }

    [Fact]
    public void CellsWrittenWithOnePen_ShareOneEntry()
    {
        // Like a hyperlink run: one instance for the run, so a long undercurled line costs references.
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mABC");

        Assert.Same(UnderlineAt(buffer, 0), UnderlineAt(buffer, 2));
    }

    [Fact]
    public void OverwritingWithPlainText_DropsTheEntry()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mA\x1b[0m\rB");

        Assert.False(CellAt(buffer, 0).IsUnderline);
        Assert.Null(UnderlineAt(buffer, 0));
    }

    [Fact]
    public void WideCharacter_CarriesTheStyleIntoItsContinuation()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3m中");

        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 0)?.Style);
        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 1)?.Style);
    }

    [Fact]
    public void InsertCharacters_MoveTheEntryWithItsCell()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mA\x1b[0mB\x1b[1G\x1b[2@");

        Assert.Null(UnderlineAt(buffer, 0));
        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 2)?.Style);
    }

    [Fact]
    public void DeleteCharacters_MoveTheEntryWithItsCell()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("AB\x1b[4:3mC\x1b[1G\x1b[2P");

        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 0)?.Style);
        Assert.Null(UnderlineAt(buffer, 2));
    }

    [Theory]
    [InlineData("\x1b[1G\x1b[K")]  // EL 0
    [InlineData("\x1b[5G\x1b[1K")] // EL 1
    [InlineData("\x1b[2K")]        // EL 2
    [InlineData("\x1b[1G\x1b[3X")] // ECH
    [InlineData("\x1b[2J")]        // ED 2
    public void Erase_DropsTheEntries(string erase)
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3mABC\x1b[0m" + erase);

        Assert.Null(UnderlineAt(buffer, 0));
        Assert.Null(UnderlineAt(buffer, 1));
        Assert.Null(UnderlineAt(buffer, 2));
    }

    [Fact]
    public void StyleSurvivesScrollingIntoScrollback()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;5;9mA\x1b[0m\r\n");
        for (int i = 0; i < 12; i++)
        {
            parser.Process($"filler {i}\r\n");
        }

        Assert.True(buffer.Scrollback.Count > 0);
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9), buffer.GetUnderlineAbsolute(0, 0));
    }

    [Fact]
    public void StyleSurvivesAReflowingResize()
    {
        // 10 columns, curly on column 8; narrowing to 5 wraps it onto the second row, column 3.
        var (buffer, parser) = NewTerminal(cols: 10, rows: 5);

        parser.Process("ABCDEFGH\x1b[4:3mI\x1b[0m");
        buffer.Resize(5, 5);

        int absRow = buffer.Scrollback.Count + 1;
        Assert.Equal(UnderlineStyle.Curly, buffer.GetUnderlineAbsolute(3, absRow)?.Style);
        Assert.Null(buffer.GetUnderlineAbsolute(2, absRow));
    }

    [Fact]
    public void StyledUnderlineOnTrailingBlanks_SurvivesAReflowingResize()
    {
        // Codex review of PR #648: reflow trims a finished row's trailing blanks, and blanks under an
        // undercurl (an editor's trailing-whitespace squiggle) read as blank. They are drawn, so they are kept.
        var (buffer, parser) = NewTerminal(cols: 12, rows: 5);

        parser.Process("AB\x1b[4:3m   \x1b[0m\r\nnext");
        buffer.Resize(10, 5);

        int absRow = buffer.Scrollback.Count;
        for (int col = 2; col <= 4; col++)
        {
            Assert.Equal(UnderlineStyle.Curly, buffer.GetUnderlineAbsolute(col, absRow)?.Style);
        }
    }

    [Fact]
    public void StyleSurvivesHeightShrinkAndGrow_UnderTheAltScreen()
    {
        // With an alt-screen application up, a height change reshapes the detached main screen: shrinking
        // pushes its top row into scrollback, growing pops it back (the main screen itself only pads).
        var (buffer, parser) = NewTerminal(cols: 20, rows: 5);

        parser.Process("\x1b[4:3mA\x1b[0m\r\n\r\n\r\n\r\nlast");
        parser.Process("\x1b[?1049h");
        buffer.Resize(20, 2);
        CellUnderline? pushed = null;
        Assert.True(buffer.Scrollback.GetUnderlineMap(0)?.TryGet(0, out pushed) == true); // in scrollback now
        Assert.Equal(UnderlineStyle.Curly, pushed!.Style);

        buffer.Resize(20, 5);
        parser.Process("\x1b[?1049l");

        Assert.Equal(0, buffer.Scrollback.Count); // popped back onto the screen
        Assert.Equal(UnderlineStyle.Curly, UnderlineAt(buffer, 0)?.Style);
    }

    [Fact]
    public void SaveAndRestoreCursor_KeepTheUnderlinePen()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;2;1;2;3m\x1b7\x1b[0m\x1b8A");

        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, TermColor.FromRgb(1, 2, 3)), UnderlineAt(buffer, 0));
    }

    [Fact]
    public void ChangingOnlyTheStyle_MarksTheCellDirty()
    {
        // The snapshot diffs render cells field by field against the previous capture. A cell whose character
        // and flags stay the same while its underline turns from curly to double has to be reported, or the
        // renderer has no reason to repaint it. Another cell on the row changes too: alone, the restyled cell
        // would be caught anyway by the fallback that repaints a row whose revision moved but whose diff is empty.
        var (buffer, parser) = NewTerminal();
        var request = new RenderSnapshotRequest { ViewportRows = buffer.Rows, ViewportCols = buffer.Cols };

        parser.Process("\x1b[4:3mA");
        using (buffer.CaptureRenderSnapshot(request, out _))
        {
            // the baseline the next capture diffs against
        }

        parser.Process("\x1b[4:2m\rA\x1b[0m\x1b[6GZ");
        using TerminalRenderSnapshot snap = buffer.CaptureRenderSnapshot(request, out _);

        bool covered = false;
        for (int i = 0; i < snap.DirtySpans.Length; i++)
        {
            DirtySpan span = snap.DirtySpans.Array[i];
            if (span.Row == 0 && span.ColStart <= 0 && span.ColEnd > 0) covered = true;
        }

        Assert.True(covered, "the restyled cell is not in a dirty span");
    }

    [Fact]
    public void RenderSnapshot_CarriesTheUnderline()
    {
        var (buffer, parser) = NewTerminal();

        parser.Process("\x1b[4:3;58;5;9mA\x1b[0m\x1b[4mB");

        using TerminalRenderSnapshot snap = buffer.CaptureRenderSnapshot(
            new RenderSnapshotRequest { ViewportRows = buffer.Rows, ViewportCols = buffer.Cols }, out _);
        RenderCellSnapshot[] cells = snap.RowsData.Array![0].Cells!;

        Assert.True(cells[0].IsUnderline);
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9), cells[0].Underline);
        Assert.True(cells[1].IsUnderline);
        Assert.Null(cells[1].Underline);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(40000)] // wrapped to a negative short
    [InlineData(65535)] // the parameter cap; wrapped to -1, read as no index at all
    public void UnderlineColor_PaletteIndexPast255_IsIgnored(int index)
    {
        // Codex review of #648: the pen kept an index no palette has, and the state transfer, which checks it,
        // then refused the terminal's own snapshot. xterm and kitty ignore it; the pen keeps the color it had.
        var (buffer, parser) = NewTerminal();

        parser.Process($"\x1b[4:3;58;5;9m\x1b[58;5;{index}mA\x1b[0;4:3;58;5;{index}mB");

        Assert.Equal(new CellUnderline(UnderlineStyle.Curly, null, 9), UnderlineAt(buffer, 0));
        Assert.Equal(new CellUnderline(UnderlineStyle.Curly), UnderlineAt(buffer, 1));
    }

    [Theory]
    [InlineData("38")]
    [InlineData("48")]
    public void TextColor_PaletteIndexPast255_IsIgnored(string code)
    {
        // The same parse serves 38 and 48, which took white under the out-of-range index.
        var (buffer, parser) = NewTerminal();

        parser.Process($"\x1b[{code};5;300mA");

        TerminalCell cell = CellAt(buffer, 0);
        Assert.True(cell.IsDefaultForeground, "foreground");
        Assert.True(cell.IsDefaultBackground, "background");
    }
}
