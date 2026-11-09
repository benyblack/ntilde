namespace Ntilde.VT.Tests;

/// <summary>
/// DECSED (CSI ? Ps J) and DECSERA (CSI Pt;Pl;Pb;Pr $ {) spare characters written under DECSCA 1 (CSI 1 " q).
/// These are behavioural regressions driven only by bytes in and cells out, so they pin the contract without
/// depending on how protection is stored.
/// </summary>
public sealed class SelectiveErasureTests
{
    private const string Esc = "\u001b";

    // The pinned terminfo.dev@4.0.0 seed: protected P, then unprotected ABCDZ, on row 0.
    private const string PinnedSeed = Esc + "[1;1H" + Esc + "[1\"qP" + Esc + "[0\"qABCDZ";

    private static (TerminalBuffer Buffer, AnsiParser Parser) Create(int cols = 12, int rows = 4)
    {
        var buffer = new TerminalBuffer(cols, rows);
        return (buffer, new AnsiParser(buffer, forceConPtyFiltering: false));
    }

    private static string Row(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            return new string(Enumerable.Range(0, buffer.Cols).Select(col => buffer.GetCell(col, row).Character).ToArray())
                .Replace('\0', ' ').TrimEnd();
        }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TerminalCell CellAt(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string[] Rows(TerminalBuffer buffer) => Enumerable.Range(0, buffer.Rows).Select(row => Row(buffer, row)).ToArray();

    /// <summary>The pinned DECSED fixture: protected P survives and the whole unprotected suffix, Z included, is erased.</summary>
    [Fact]
    public void Decsed_2_keeps_the_protected_P_and_erases_the_unprotected_suffix()
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        Assert.Equal("PABCDZ", Row(buffer, 0));
        parser.Process(Esc + "[?2J");
        Assert.Equal("P", Row(buffer, 0));
        Assert.Equal((0, 6), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>The pinned DECSERA fixture: protected P and the cell outside the rectangle survive, ABCD are erased.</summary>
    [Fact]
    public void Decsera_keeps_the_protected_P_and_the_outside_Z_and_erases_ABCD()
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        parser.Process(Esc + "[1;1;1;5${");
        Assert.Equal("P    Z", Row(buffer, 0));
        Assert.Equal((0, 6), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Protection is not read-only text: ordinary erases remove protected characters.</summary>
    [Theory]
    [InlineData("\u001b[2J")]
    [InlineData("\u001b[1;1;1;5$z")]
    [InlineData("\u001b[1;1H\u001b[2K")]
    [InlineData("\u001b[1;1H\u001b[5X")]
    [InlineData("\u001b[1;1H\u001b[0J")]
    public void Ordinary_erasure_removes_protected_characters(string erase)
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        parser.Process(erase);
        Assert.NotEqual('P', CellAt(buffer, 0, 0).Character);
    }

    /// <summary>A selective erase that finds nothing to spare is exactly the ordinary erase.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Decsed_without_protected_characters_matches_ED(int mode)
    {
        var (ordinary, ordinaryParser) = Create();
        var (selective, selectiveParser) = Create();
        const string seed = "\u001b[1;1HAAAA\u001b[2;1HBBBB\u001b[3;1HCCCC\u001b[2;3H";
        ordinaryParser.Process(seed + $"\u001b[{mode}J");
        selectiveParser.Process(seed + $"\u001b[?{mode}J");
        Assert.Equal(Rows(ordinary), Rows(selective));
        Assert.Equal(TerminalStateSerializer.ToBytes(ordinary.ExportState(int.MaxValue)),
            TerminalStateSerializer.ToBytes(selective.ExportState(int.MaxValue)));
    }

    /// <summary>
    /// Protected cells sit before, at and after the cursor on several rows. Each DECSED mode erases exactly its own
    /// region, minus the protected cells inside it.
    /// </summary>
    [Theory]
    [InlineData(0, new[] { "aPb", "cPd", "eP", " P" })]
    [InlineData(1, new[] { " P", " P", " Pf", "gPh" })]
    [InlineData(2, new[] { " P", " P", " P", " P" })]
    public void Decsed_modes_erase_their_region_minus_the_protected_cells(int mode, string[] expected)
    {
        var (buffer, parser) = Create(8, 4);
        // each row is <letter> P <letter> with the middle letter protected
        parser.Process("\u001b[1;1Ha\u001b[1\"qP\u001b[0\"qb");
        parser.Process("\u001b[2;1Hc\u001b[1\"qP\u001b[0\"qd");
        parser.Process("\u001b[3;1He\u001b[1\"qP\u001b[0\"qf");
        parser.Process("\u001b[4;1Hg\u001b[1\"qP\u001b[0\"qh");
        parser.Process("\u001b[3;2H"); // cursor on the protected cell of row 3
        parser.Process($"\u001b[?{mode}J");
        Assert.Equal(expected, Rows(buffer));
        Assert.Equal((2, 1), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>Turning protection off changes what is written next; characters already written stay protected.</summary>
    [Fact]
    public void Turning_protection_off_does_not_reclassify_existing_cells()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qAB\u001b[0\"qCD\u001b[0\"q\u001b[2\"q");
        parser.Process("\u001b[?2J");
        Assert.Equal("AB", Row(buffer, 0));
    }

    /// <summary>DECSCA 0, 2 and an omitted value leave later characters erasable; 1 protects; other values and extra parameters are ignored.</summary>
    [Theory]
    [InlineData("", "\u001b[1\"q", true)]
    [InlineData("\u001b[1\"q", "\u001b[0\"q", false)]
    [InlineData("\u001b[1\"q", "\u001b[2\"q", false)]
    [InlineData("\u001b[1\"q", "\u001b[\"q", false)]
    [InlineData("\u001b[1\"q", "\u001b[3\"q", true)]
    [InlineData("\u001b[1\"q", "\u001b[99\"q", true)]
    [InlineData("\u001b[0\"q", "\u001b[3\"q", false)]
    [InlineData("\u001b[0\"q", "\u001b[1;1\"q", false)]
    [InlineData("\u001b[0\"q", "\u001b[1:1\"q", false)]
    [InlineData("\u001b[0\"q", "\u001b[?1\"q", false)]
    [InlineData("\u001b[0\"q", "\u001b[1 q", false)]
    public void Decsca_values(string before, string decsca, bool protectedAfter)
    {
        var (buffer, parser) = Create();
        parser.Process(before + decsca + "\u001b[1;1HAB\u001b[?2J");
        Assert.Equal(protectedAfter ? "AB" : string.Empty, Row(buffer, 0));
    }

    /// <summary>Protection is not an SGR attribute: SGR 0 and every SGR reset leave it set.</summary>
    [Fact]
    public void Sgr_reset_does_not_clear_protection()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"q\u001b[1;4;31m\u001b[0mA\u001b[m\u001b[22mB");
        parser.Process("\u001b[?2J");
        Assert.Equal("AB", Row(buffer, 0));
    }

    /// <summary>Overwriting a protected cell with an unprotected character unprotects it, and the reverse protects.</summary>
    [Fact]
    public void Overwrite_takes_the_protection_of_the_new_character()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qPQ\u001b[0\"qRS");
        parser.Process("\u001b[1;1HX");        // unprotected X over protected P
        parser.Process("\u001b[1\"q\u001b[1;3HY"); // protected Y over unprotected R
        parser.Process("\u001b[?2J");
        Assert.Equal(' ', CellAt(buffer, 0, 0).Character); // X erased
        Assert.Equal('Q', CellAt(buffer, 1, 0).Character); // Q still protected
        Assert.Equal('Y', CellAt(buffer, 2, 0).Character); // Y protected
        Assert.Equal(' ', CellAt(buffer, 3, 0).Character); // S erased
    }

    /// <summary>ICH, DCH and insert mode move cells together with their protection.</summary>
    [Fact]
    public void Insert_and_delete_characters_carry_protection_with_the_cells()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qPQ\u001b[0\"qRS\u001b[1;1H\u001b[2@");
        Assert.Equal("  PQRS", Row(buffer, 0));
        parser.Process("\u001b[?2J");
        Assert.Equal("  PQ", Row(buffer, 0));

        parser.Process("\u001b[2J\u001b[1;1H\u001b[0\"qxy\u001b[1\"qPQ\u001b[0\"qRS\u001b[1;1H\u001b[2P");
        Assert.Equal("PQRS", Row(buffer, 0));
        parser.Process("\u001b[?2J");
        Assert.Equal("PQ", Row(buffer, 0));

        parser.Process("\u001b[2J\u001b[1;1H\u001b[1\"qPQ\u001b[0\"qRS\u001b[1;1H\u001b[4hxy\u001b[4l");
        Assert.Equal("xyPQRS", Row(buffer, 0));
        parser.Process("\u001b[?2J");
        Assert.Equal("  PQ", Row(buffer, 0));
    }

    /// <summary>Lines carry their protection when the screen scrolls; the line that appears is unprotected.</summary>
    [Fact]
    public void Scrolling_moves_protected_rows_and_adds_unprotected_ones()
    {
        var (buffer, parser) = Create(8, 3);
        parser.Process("\u001b[1;1Hone\u001b[2;1H\u001b[1\"qtwo\u001b[0\"q\u001b[3;1Hthree\n");
        Assert.Equal(new[] { "two", "three", "" }, Rows(buffer));
        parser.Process("\u001b[3;1Hfresh\u001b[?2J");
        Assert.Equal(new[] { "two", "", "" }, Rows(buffer));
    }

    /// <summary>Each screen bank has its own cells; DECSED on the alternate screen leaves the main screen alone.</summary>
    [Fact]
    public void The_alternate_screen_has_its_own_protected_cells()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qMAIN\u001b[0\"qmain");
        parser.Process("\u001b[?1049h\u001b[1;1H\u001b[0\"qalt\u001b[?2J");
        Assert.Equal(string.Empty, Row(buffer, 0));
        parser.Process("\u001b[?1049l");
        Assert.Equal("MAINmain", Row(buffer, 0));
        parser.Process("\u001b[?2J");
        Assert.Equal("MAIN", Row(buffer, 0));
    }

    /// <summary>Incorrect, reversed, omitted and oversized DECSERA borders.</summary>
    [Theory]
    [InlineData("[2;1;1;5${", "PABCDZ")]          // bottom above top: ignored
    [InlineData("[1;5;1;2${", "PABCDZ")]          // right left of left: ignored
    [InlineData("[1;2;1;3${", "P  CDZ")]         // interior rectangle (columns 2-3)
    [InlineData("[1;2;1;99${", "P")]              // oversized right clamps to the page
    [InlineData("[${", "P")]                      // omitted borders cover the page
    [InlineData("[0;0;0;0${", "P")]               // zero borders default
    [InlineData("[1;1;1;1${", "PABCDZ")]          // only the protected cell
    [InlineData("[1;6;1;6${", "PABCD")]           // only Z
    public void Decsera_borders(string sequence, string expected)
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        parser.Process(Esc + sequence);
        Assert.Equal(expected, Row(buffer, 0));
    }

    /// <summary>Other spellings never reach DECSERA: a leader, a different intermediate, extra parameters or subparameters are ignored.</summary>
    [Theory]
    [InlineData("[?1;1;1;5${")]
    [InlineData("[1;1;1;5{")]
    [InlineData("[1;1;1;5 {")]
    [InlineData("[1;1;1;5;1${")]
    [InlineData("[1:1;1;1;5${")]
    public void Decsera_requires_its_exact_identity(string sequence)
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        parser.Process(Esc + sequence);
        Assert.Equal("PABCDZ", Row(buffer, 0));
    }

    /// <summary>DECSERA rectangle edges respect origin mode like DECERA.</summary>
    [Fact]
    public void Decsera_follows_origin_mode()
    {
        var (buffer, parser) = Create(8, 5);
        parser.Process("\u001b[2;4r\u001b[?6h\u001b[1;1HAAAA\u001b[2;1H\u001b[1\"qBBBB\u001b[0\"q\u001b[3;1HCCCC");
        parser.Process("\u001b[1;1;3;4${"); // rows 1-3 of the region, that is screen rows 2-4
        Assert.Equal(new[] { string.Empty, string.Empty, "BBBB", string.Empty, string.Empty }, Rows(buffer));
    }

    /// <summary>A protected wide glyph is one unit: neither half is erased, however the rectangle cuts it.</summary>
    [Theory]
    [InlineData("[1;1;1;1${")]
    [InlineData("[1;2;1;2${")]
    [InlineData("[1;1;1;2${")]
    public void Protected_wide_glyphs_are_never_split(string sequence)
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"q界\u001b[0\"qXY");
        parser.Process(Esc + sequence);
        Assert.True(CellAt(buffer, 0, 0).IsWide);
        Assert.True(CellAt(buffer, 1, 0).IsWideContinuation);
        Assert.Equal('界', CellAt(buffer, 0, 0).Character);
    }

    /// <summary>Unprotected wide glyphs are erased whole by a selective erase that cuts them, while a protected neighbour stays.</summary>
    [Fact]
    public void Unprotected_wide_glyphs_are_erased_whole_next_to_protected_text()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qP\u001b[0\"q界X");
        parser.Process("\u001b[1;3;1;3${");
        Assert.Equal('P', CellAt(buffer, 0, 0).Character);
        Assert.False(CellAt(buffer, 1, 0).IsWide);
        Assert.False(CellAt(buffer, 2, 0).IsWideContinuation);
        Assert.Equal('X', CellAt(buffer, 3, 0).Character);
    }

    /// <summary>Erasure of unprotected cells clears their side metadata and applies the erase colors, never the pen's rendition.</summary>
    [Fact]
    public void Selective_erasure_clears_metadata_and_keeps_pen_state()
    {
        var (buffer, parser) = Create();
        parser.Process("\u001b[1;1H\u001b[1\"qP\u001b[0\"q\u001b]8;;https://example.invalid/x\u001b\\\u001b[1;4;31;44mABC\u001b]8;;\u001b\\");
        parser.Process("\u001b[32;45;3m");
        parser.Process("\u001b[1;1;1;4${");
        buffer.Lock.EnterReadLock();
        try
        {
            var row = buffer.GetRowAbsolute(buffer.Scrollback.Count)!;
            Assert.Equal('P', row.Cells[0].Character);
            for (int col = 1; col <= 3; col++)
            {
                Assert.Equal(' ', row.Cells[col].Character);
                Assert.False(row.Cells[col].IsBold);
                Assert.False(row.Cells[col].IsUnderline);
                Assert.Null(row.GetHyperlink(col));
                Assert.Null(row.GetUnderline(col));
            }
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.True(buffer.IsItalic);
        Assert.Equal((0, 4), (buffer.CursorRow, buffer.CursorCol));
    }

    /// <summary>
    /// A full-width DECSERA over a protected row and an entirely unprotected row blanks both with DECERA's erase blank, including the
    /// retained palette index, so a later palette change reaches every erased cell.
    /// </summary>
    [Fact]
    public void Decsera_blanks_whole_unprotected_rows_like_DECERA()
    {
        var (buffer, parser) = Create(8, 3);
        parser.Process("\u001b[1;1H\u001b[1\"qP\u001b[0\"qabc\u001b[2;1Hxyz\u001b[41m");
        parser.Process("\u001b[1;1;2;8${");
        foreach (int row in new[] { 0, 1 })
        {
            var cell = CellAt(buffer, 5, row);
            Assert.Equal(1, cell.BgIndex);
            Assert.Equal(' ', cell.Character);
        }
        parser.Process("\u001b]4;1;#abcdef\a");
        Assert.Equal(CellAt(buffer, 5, 0).Bg, CellAt(buffer, 5, 1).Bg);
        Assert.Equal('P', CellAt(buffer, 0, 0).Character);
    }

    /// <summary>
    /// A scaled block is retired whole or not at all: when rows that went through history lost their protection and the block holds mixed
    /// protection, one protected cell spares all of it, and a rectangle that covers only part of it cannot retire it either.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scaled_blocks_with_any_protected_cell_are_spared_whole(bool rectangle)
    {
        var (buffer, parser) = Create(8, 4);
        parser.Process("\u001b[1;1H\u001b[1\"q\u001b]66;s=2;A\a\u001b[0\"q");
        parser.Process("\u001b[?47h");
        buffer.Resize(8, 3);
        buffer.Resize(8, 4);
        parser.Process("\u001b[?47l");
        bool topProtected, lowerProtected;
        buffer.Lock.EnterReadLock();
        try
        {
            topProtected = buffer.IsCellProtected(0, 0);
            lowerProtected = buffer.IsCellProtected(0, 1);
        }
        finally { buffer.Lock.ExitReadLock(); }
        Assert.NotEqual(topProtected, lowerProtected); // the round trip leaves mixed protection, which is the case under test
        parser.Process(rectangle ? "\u001b[1;1;1;2${" : "\u001b[?2J");
        Assert.Equal('A', CellAt(buffer, 0, 0).Character);
        Assert.NotNull(buffer.ExportState(0).TextSizing); // the block was spared, not retired
    }

    /// <summary>Whatever the input is split into, the selective erase gives the same screen.</summary>
    [Fact]
    public void Selective_erasure_is_equivalent_across_every_input_split()
    {
        const string command = "\u001b[1;1;1;5${\u001b[?2J";
        for (int split = 0; split <= command.Length; split++)
        {
            var (buffer, parser) = Create();
            parser.Process(PinnedSeed);
            parser.Process(command[..split]);
            parser.Process(command[split..]);
            Assert.Equal("P", Row(buffer, 0));
        }
    }

    /// <summary>The undefined private mode 3 still must not delete history, even with protected text on the screen.</summary>
    [Fact]
    public void Decsed_mode_3_is_ignored_with_protected_text_present()
    {
        var (buffer, parser) = Create(8, 3);
        for (int line = 0; line < 6; line++) parser.Process($"old{line}\r\n");
        parser.Process("\u001b[1;1H\u001b[1\"qP\u001b[0\"qABC");
        int history = buffer.Scrollback.Count;
        parser.Process("\u001b[?3J");
        Assert.Equal(history, buffer.Scrollback.Count);
        Assert.Equal("PABC", Row(buffer, 0));
    }

    /// <summary>DECSED never touches saved lines.</summary>
    [Fact]
    public void Decsed_2_preserves_scrollback()
    {
        var (buffer, parser) = Create(8, 3);
        for (int line = 0; line < 6; line++) parser.Process($"old{line}\r\n");
        int history = buffer.Scrollback.Count;
        parser.Process("\u001b[1;1H\u001b[1\"qP\u001b[0\"qABC\u001b[?2J");
        Assert.Equal(history, buffer.Scrollback.Count);
        Assert.Equal("P", Row(buffer, 0));
    }

    /// <summary>
    /// DECSEL is not yet protection-aware and keeps its EL alias. This pins the documented gap so that making it
    /// selective is a deliberate change with its own contract.
    /// </summary>
    [Fact]
    public void Decsel_is_still_an_EL_alias()
    {
        var (buffer, parser) = Create();
        parser.Process(PinnedSeed);
        parser.Process("\u001b[1;1H\u001b[?2K");
        Assert.Equal(string.Empty, Row(buffer, 0));
    }
}
