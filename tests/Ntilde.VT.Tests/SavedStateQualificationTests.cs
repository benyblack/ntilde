using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class SavedStateQualificationTests
{
    private const string Flags = "\u001b[1;2;3;4:3;5;7;8;9;53m";
    private const string Rgb = "\u001b[38;2;17;34;51;48;2;68;85;102;58;2;119;136;153m";
    private const string Palette = "\u001b[38;5;201;48;5;77;58;5;19m";
    private const string Charsets = "\u001b(B\u001b)0\u001b*0\u001b+B\u000e";
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    public static IEnumerable<object[]> CursorCases()
    {
        foreach (var (save, restore) in new[] { ("\u001b7", "\u001b[?1048l"), ("\u001b[?1048h", "\u001b8"), ("\u001b[s", "\u001b[u") })
            foreach (bool alternate in new[] { false, true })
                foreach (bool indexed in new[] { false, true })
                    yield return new object[] { save, restore, alternate, indexed };
    }

    [Theory]
    [MemberData(nameof(CursorCases))]
    public void Every_saved_pen_field_all_charset_slots_origin_and_pending_wrap_restore_in_either_bank(
        string save, string restore, bool alternate, bool indexed)
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        if (alternate) parser.Process("\u001b[?47h");
        parser.Process("\u001b[2;7r\u001b[?6h\u001b[2;20H" + Flags + (indexed ? Palette : Rgb) + Charsets + "q" + save);
        var before = buffer.ExportState(0);
        var saved = alternate ? before.SavedCursorAlt : before.SavedCursorMain;
        string pen = Json(before.Sgr);
        Assert.Equal(pen, Json(saved.Sgr));
        Assert.False(saved.Sgr.IsDefaultForeground); Assert.False(saved.Sgr.IsDefaultBackground);
        Assert.All(new[] { saved.Sgr.IsBold, saved.Sgr.IsFaint, saved.Sgr.IsItalic, saved.Sgr.IsUnderline,
            saved.Sgr.IsOverline, saved.Sgr.IsBlink, saved.Sgr.IsInverse, saved.Sgr.IsHidden, saved.Sgr.IsStrikethrough }, Assert.True);
        Assert.Equal((byte)UnderlineStyle.Curly, saved.Sgr.UnderlineStyle);
        Assert.Equal(indexed ? (short)201 : (short)-1, saved.Sgr.FgIndex);
        Assert.Equal(indexed ? (short)77 : (short)-1, saved.Sgr.BgIndex);
        Assert.Equal(indexed ? (short)19 : (short)-1, saved.Sgr.UnderlineColorIndex);
        if (!indexed)
        {
            Assert.Equal(0xff112233u, saved.Sgr.Foreground); Assert.Equal(0xff445566u, saved.Sgr.Background);
            Assert.Equal(0xff778899u, saved.Sgr.UnderlineColor);
        }
        Assert.Equal(new[] { 0, 1, 1, 0 }, saved.Charsets);
        Assert.Equal(1, saved.Gl); Assert.True(saved.OriginMode); Assert.True(saved.IsPendingWrap);
        Assert.Equal((2, 19), (saved.Row, saved.Col));
        parser.Process("\u001b[?6l\u001b[?7l\u001b[0m\u001b(0\u001b)B\u001b*B\u001b+0\u000f\u001b[1;1HD" + restore);
        Assert.Equal(pen, Json(buffer.ExportState(0).Sgr));
        Assert.Equal((saved.Row, saved.Col), (buffer.CursorRow, buffer.CursorCol));
        Assert.True(buffer.IsPendingWrap); Assert.True(buffer.Modes.IsOriginMode);
        Assert.False(buffer.Modes.IsAutoWrapMode);
        Assert.Equal(saved.Charsets, parser.ExportState().Charsets); Assert.Equal(saved.Gl, parser.ExportState().Gl);
        // Consume the saved latch only after explicitly enabling wrap; its wrap-off semantics are not assumed.
        parser.Process("\u001b[?7hq");
        Assert.Equal((3, 1), (buffer.CursorRow, buffer.CursorCol));
        Assert.StartsWith("─", BufferSnapshot.Capture(buffer).Lines[3]);
        Assert.Equal(pen, Json(buffer.ExportState(0).Sgr));
    }

    private static (TerminalBuffer Buffer, AnsiParser Parser) SeedBothBanks()
    {
        var buffer = new TerminalBuffer(20, 8);
        var parser = new AnsiParser(buffer);
        parser.Process("MAIN\u001b[2;7r\u001b[?6h\u001b[2;20H" + Flags + Rgb + Charsets + "q\u001b7" +
            "\u001b[?47h\u001b[?6l\u001b[4;20H" + Flags + Palette + "\u001b(0\u001b)B\u001b*B\u001b+0\u000fq\u001b7");
        return (buffer, parser);
    }

    private static void AssertDefaultBank(TerminalCursorState bank, TerminalSgrState defaultPen)
    {
        Assert.Equal((0, 0), (bank.Row, bank.Col)); Assert.False(bank.IsPendingWrap); Assert.False(bank.OriginMode);
        Assert.Equal(new[] { 0, 0, 0, 0 }, bank.Charsets); Assert.Equal(0, bank.Gl);
        Assert.Equal(Json(defaultPen), Json(bank.Sgr));
    }

    [Fact]
    public void Soft_reset_defaults_every_active_bank_field_and_preserves_the_inactive_bank_and_both_cells()
    {
        var (buffer, parser) = SeedBothBanks();
        var before = buffer.ExportState(0); string mainBank = Json(before.SavedCursorMain);
        var defaults = new TerminalBuffer(20, 8).ExportState(0);
        parser.Process("\u001b[!p"); var after = buffer.ExportState(0);
        Assert.True(buffer.IsAltScreenActive); Assert.Equal((3, 19), (buffer.CursorRow, buffer.CursorCol));
        Assert.False(buffer.IsPendingWrap); Assert.Equal(Json(defaults.Sgr), Json(after.Sgr));
        Assert.Equal(mainBank, Json(after.SavedCursorMain)); AssertDefaultBank(after.SavedCursorAlt, defaults.Sgr);
        Assert.Equal(before.Main.CellsBase64, after.Main.CellsBase64); Assert.Equal(before.Alt.CellsBase64, after.Alt.CellsBase64);
        Assert.Equal(new[] { 0, 0, 0, 0 }, parser.ExportState().Charsets); Assert.Equal(0, parser.ExportState().Gl);
        parser.Process("\u001b[?47l\u001b8");
        Assert.Equal(Json(before.SavedCursorMain.Sgr), Json(buffer.ExportState(0).Sgr));
        Assert.Equal(before.SavedCursorMain.Charsets, parser.ExportState().Charsets);
        parser.Process("\u001b[?47h\u001b8");
        Assert.Equal(Json(defaults.Sgr), Json(buffer.ExportState(0).Sgr)); Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
    }

    [Fact]
    public void Hard_reset_defaults_every_field_of_both_saved_banks_and_parser_charset_context()
    {
        var (buffer, parser) = SeedBothBanks(); var defaults = new TerminalBuffer(20, 8).ExportState(0);
        parser.Process("\u001bc"); var after = buffer.ExportState(0);
        Assert.False(buffer.IsAltScreenActive); Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        Assert.False(buffer.IsPendingWrap);
        AssertDefaultBank(after.SavedCursorMain, defaults.Sgr); AssertDefaultBank(after.SavedCursorAlt, defaults.Sgr);
        Assert.Equal(Json(defaults.Sgr), Json(after.Sgr));
        Assert.Equal(new[] { 0, 0, 0, 0 }, parser.ExportState().Charsets); Assert.Equal(0, parser.ExportState().Gl);
        parser.Process("\u001b8"); Assert.Equal(Json(defaults.Sgr), Json(buffer.ExportState(0).Sgr));
        parser.Process("\u001b[?47h\u001b8"); Assert.Equal(Json(defaults.Sgr), Json(buffer.ExportState(0).Sgr));
        Assert.Equal((0, 0), (buffer.CursorRow, buffer.CursorCol));
        // Cell clearing is a separate observed RIS limitation tracked by #773, not inferred from bank reset.
    }
}
