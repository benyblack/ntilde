using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// A direct-RGB request (<c>38/48;2;r;g;b</c>) names a literal color. The parser once
/// rewrote five exact values from Windows Terminal's Campbell scheme into palette
/// indices, so those literals followed the theme and OSC 4 instead of staying put.
/// These regressions pin the contract for those five values on both channels and in
/// the forms the parser supports: semicolon, colon with the omitted colorspace id
/// (<c>38:2::r:g:b</c>) and colon with an explicit colorspace id (<c>38:2:0:r:g:b</c>).
/// Explicit indexed colors (<c>38;5;n</c>, <c>30-37</c>, <c>90-97</c>) keep their
/// palette storage; the indexed controls below fail if literals and indices are conflated.
/// </summary>
public sealed class TruecolorLiteralStorageTests
{
    private const ushort DirtyBit = (ushort)TerminalCellFlags.Dirty;

    /// <summary>The five exact values the parser used to alias, and the index each became.</summary>
    public static readonly (byte R, byte G, byte B, int AliasedIndex)[] SnappedValues =
    [
        (0, 55, 218, 4), (58, 150, 221, 4), (12, 12, 12, 0), (204, 204, 204, 7), (242, 242, 242, 15),
    ];

    public static TheoryData<int, int, int, int, string, string> Cases()
    {
        var data = new TheoryData<int, int, int, int, string, string>();
        foreach (var (r, g, b, index) in SnappedValues)
        {
            foreach (string channel in new[] { "fg", "bg" })
            {
                int p = channel == "fg" ? 38 : 48;
                data.Add(r, g, b, index, channel, $"{p};2;{r};{g};{b}");
                data.Add(r, g, b, index, channel, $"{p}:2::{r}:{g}:{b}");
                data.Add(r, g, b, index, channel, $"{p}:2:0:{r}:{g}:{b}");
            }
        }
        return data;
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) Make(int cols = 20)
    {
        var buffer = new TerminalBuffer(cols, 4);
        return (buffer, new AnsiParser(buffer));
    }

    /// <summary>Copies one cell while respecting the same buffer read boundary as rendering and snapshots.</summary>
    private static TerminalCell Cell(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetCell(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TermColor Resolve(TerminalBuffer buffer, TerminalCell cell, string channel)
    {
        bool fg = channel == "fg";
        short index = fg ? cell.FgIndex : cell.BgIndex;
        if (index >= 0) return buffer.GetPaletteColor(index);
        return fg ? cell.Foreground : cell.Background;
    }

    private static void AssertLiteral(TerminalCell cell, string channel, int r, int g, int b)
    {
        bool fg = channel == "fg";
        Assert.Equal('X', cell.Character);
        Assert.False(fg ? cell.IsPaletteForeground : cell.IsPaletteBackground);
        Assert.Equal((short)-1, fg ? cell.FgIndex : cell.BgIndex);
        Assert.False(fg ? cell.IsDefaultForeground : cell.IsDefaultBackground);
        TermColor stored = fg ? cell.Foreground : cell.Background;
        Assert.Equal((r, g, b), (stored.R, stored.G, stored.B));
        // The opposite channel stays on its default marker and no unrelated attribute is set.
        ushort other = (ushort)(fg ? TerminalCellFlags.DefaultBackground : TerminalCellFlags.DefaultForeground);
        Assert.Equal(other, (ushort)(cell.Flags & ~DirtyBit));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Direct_rgb_is_stored_as_the_requested_literal(int r, int g, int b, int index, string channel, string request)
    {
        _ = index;
        var (buffer, parser) = Make();
        parser.Process($"\x1b[{request}mX");
        AssertLiteral(Cell(buffer, 0, 0), channel, r, g, b);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Direct_rgb_resolves_to_the_requested_color_before_and_after_osc4_changes_the_aliased_entry(
        int r, int g, int b, int index, string channel, string request)
    {
        var (buffer, parser) = Make();
        parser.Process($"\x1b[{request}mX");
        TerminalCell cell = Cell(buffer, 0, 0);
        Assert.Equal((r, g, b), RgbOf(Resolve(buffer, cell, channel)));

        // Re-theme the entry the literal used to alias.
        parser.Process($"\x1b]4;{index};rgb:ff/00/ff\x07");
        Assert.Equal((255, 0, 255), RgbOf(buffer.GetPaletteColor(index)));
        Assert.Equal((r, g, b), RgbOf(Resolve(buffer, Cell(buffer, 0, 0), channel)));
        AssertLiteral(Cell(buffer, 0, 0), channel, r, g, b);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Indexed_positive_control_follows_the_palette_while_the_literal_stays(
        int r, int g, int b, int index, string channel, string request)
    {
        var (buffer, parser) = Make();
        int p = channel == "fg" ? 38 : 48;
        // Cell 0: explicit indexed color; cell 1: the literal under test.
        parser.Process($"\x1b[{p};5;{index}mI\x1b[0m\x1b[{request}mX");
        TerminalCell indexed = Cell(buffer, 0, 0);
        Assert.Equal((short)index, channel == "fg" ? indexed.FgIndex : indexed.BgIndex);
        TermColor before = Resolve(buffer, indexed, channel);

        parser.Process($"\x1b]4;{index};rgb:ff/00/ff\x07");
        TermColor after = Resolve(buffer, Cell(buffer, 0, 0), channel);
        Assert.Equal((255, 0, 255), RgbOf(after));
        Assert.NotEqual(RgbOf(before), RgbOf(after));
        AssertLiteral(Cell(buffer, 1, 0), channel, r, g, b);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sgr_report_names_the_literal_not_a_standard_color(int r, int g, int b, int index, string channel, string request)
    {
        _ = index;
        var (_, parser) = Make();
        var replies = new List<string>();
        parser.OnResponse = replies.Add;
        parser.Process($"\x1b[{request}m\x1bP$qm\x1b\\");
        int p = channel == "fg" ? 38 : 48;
        Assert.Equal($"\x1bP1$r0;{p};2;{r};{g};{b}m\x1b\\", Assert.Single(replies));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Replacing_a_literal_with_an_indexed_or_default_color_clears_the_literal_exactly(
        int r, int g, int b, int index, string channel, string request)
    {
        _ = (r, g, b);
        var (buffer, parser) = Make();
        int p = channel == "fg" ? 38 : 48;
        parser.Process($"\x1b[{request}m\x1b[{p};5;{index}mP\x1b[{(channel == "fg" ? 39 : 49)}mD");
        TerminalCell indexed = Cell(buffer, 0, 0);
        Assert.Equal((short)index, channel == "fg" ? indexed.FgIndex : indexed.BgIndex);
        TerminalCell reset = Cell(buffer, 1, 0);
        Assert.True(channel == "fg" ? reset.IsDefaultForeground : reset.IsDefaultBackground);
        Assert.Equal((short)-1, channel == "fg" ? reset.FgIndex : reset.BgIndex);
    }

    [Theory]
    [InlineData(0, 55, 217)]
    [InlineData(12, 12, 13)]
    [InlineData(204, 204, 205)]
    [InlineData(242, 242, 241)]
    [InlineData(58, 150, 220)]
    public void Near_miss_values_were_always_literal(int r, int g, int b)
    {
        var (buffer, parser) = Make();
        parser.Process($"\x1b[38;2;{r};{g};{b}mX");
        AssertLiteral(Cell(buffer, 0, 0), "fg", r, g, b);
    }

    [Theory]
    [InlineData("30", 0)]
    [InlineData("34", 4)]
    [InlineData("37", 7)]
    [InlineData("90", 8)]
    [InlineData("97", 15)]
    [InlineData("38;5;4", 4)]
    [InlineData("38;5;7", 7)]
    [InlineData("38;5;15", 15)]
    [InlineData("38:5:0", 0)]
    public void Explicit_indexed_foreground_colors_keep_their_palette_storage(string request, int index)
    {
        var (buffer, parser) = Make();
        parser.Process($"\x1b[{request}mX");
        TerminalCell cell = Cell(buffer, 0, 0);
        Assert.True(cell.IsPaletteForeground);
        Assert.Equal((short)index, cell.FgIndex);
        Assert.True(cell.IsDefaultBackground);
    }

    [Theory]
    [InlineData("40", 0)]
    [InlineData("44", 4)]
    [InlineData("47", 7)]
    [InlineData("100", 8)]
    [InlineData("107", 15)]
    [InlineData("48;5;4", 4)]
    [InlineData("48;5;7", 7)]
    [InlineData("48;5;15", 15)]
    [InlineData("48:5:0", 0)]
    public void Explicit_indexed_background_colors_keep_their_palette_storage(string request, int index)
    {
        var (buffer, parser) = Make();
        parser.Process($"\x1b[{request}mX");
        TerminalCell cell = Cell(buffer, 0, 0);
        Assert.True(cell.IsPaletteBackground);
        Assert.Equal((short)index, cell.BgIndex);
        Assert.True(cell.IsDefaultForeground);
    }

    private static (int R, int G, int B) RgbOf(TermColor color) => (color.R, color.G, color.B);
}
