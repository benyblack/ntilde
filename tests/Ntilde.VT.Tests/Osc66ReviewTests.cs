using System.Runtime.InteropServices;
using System.Text;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests;

public sealed class Osc66ReviewTests
{
    private static CellTextSizing? Piece(TerminalBuffer buffer, int col = 0, int row = 0)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetTextSizingAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static string Grapheme(TerminalBuffer buffer, int col)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetGraphemeAbsolute(col, 0); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Decaln_erases_sizing_before_filling_the_screen()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2;A\a\x1b#8");
        Assert.Null(Piece(buffer));
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        { ViewportRows = 4, ViewportCols = 12 }, out _);
        for (int row = 0; row < 4; row++)
            Assert.All(snapshot.RowsData.Array[row].Cells, c => { Assert.Equal('E', c.Character); Assert.Null(c.TextSizing); });
    }

    [Fact]
    public void A_cursor_move_away_from_the_block_end_cannot_modify_its_text()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2;A\a\x1b[1;6H\u0301");
        Assert.Equal("A", Piece(buffer)!.Block.Text);
        Assert.Equal("A", Grapheme(buffer, 0));
    }

    [Fact]
    public void Automatic_width_zero_width_text_attaches_to_the_preceding_ordinary_cell()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("A\x1b]66;s=2;\u0301\a");
        Assert.Equal("A\u0301", Grapheme(buffer, 0));
        Assert.Equal(1, buffer.CursorCol);
        Assert.Null(Piece(buffer));
    }

    [Fact]
    public void Split_regional_indicators_update_both_the_immutable_block_and_cell_text()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2;🇸\a🇪");
        Assert.Equal("🇸🇪", Piece(buffer)!.Block.Text);
        Assert.Equal("🇸🇪", Grapheme(buffer, 0));
        Assert.Same(Piece(buffer)!.Block, Piece(buffer, 3, 1)!.Block);
    }

    [Fact]
    public void Alternate_screen_import_cannot_invent_a_fragment_above_row_zero()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("\x1b[?47h\x1b]66;s=2;A\a");
        var state = buffer.ExportState(0);
        var entry = Assert.Single(state.TextSizing!);
        entry.Y = -1;
        byte[] bytes = Convert.FromBase64String(state.Alt.CellsBase64!);
        MemoryMarshal.Cast<byte, TerminalCell>(bytes.AsSpan())[0] = TerminalCell.Default;
        state.Alt.CellsBase64 = Convert.ToBase64String(bytes);
        var target = new TerminalBuffer(12, 4);
        Assert.Throws<InvalidOperationException>(() => target.ImportState(state));
        var legacy = new ReplaySnapshot
        {
            Cols = 12,
            Rows = 4,
            IsAltScreen = true,
            CellsBase64 = state.Alt.CellsBase64,
            TextSizing = [new TerminalTextSizingState
                { Text = entry.Text, Options = entry.Options, BaseWidth = entry.BaseWidth, X = entry.X, Y = entry.Y }]
        };
        Assert.Throws<InvalidOperationException>(() => target.ApplySnapshot(legacy));
    }

    [Fact]
    public void Text_client_fallback_cannot_attach_a_leading_mark_to_the_previous_cell()
    {
        var buffer = new TerminalBuffer(12, 4);
        new AnsiParser(buffer).Process("P\x1b]66;w=2;\u0301X\a!");
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        { ViewportRows = 4, ViewportCols = 12 }, out _);
        var output = new StringBuilder();
        AnsiCellWriter.AppendRow(output, snapshot.RowsData.Array[0].Cells, 12);
        var outer = new TerminalBuffer(12, 4);
        new AnsiParser(outer).Process(output.ToString());
        Assert.Equal("P", Grapheme(outer, 0));
        Assert.Equal("X", Grapheme(outer, 1));
        Assert.Equal("!", Grapheme(outer, 3));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Detected_link_spans_cover_the_full_sized_payload_width(int row)
    {
        const string url = "https://example.test";
        var buffer = new TerminalBuffer(20, 4);
        new AnsiParser(buffer).Process("\x1b]66;s=2:w=5;" + url + "\a");
        var (text, map) = Ntilde.VT.Links.RowTextExtractor.Extract(buffer, row, out int[] ends);
        Assert.StartsWith(url, text);
        Assert.Equal((0, 9), Ntilde.VT.Links.RowTextExtractor.SpanToColumns(new(0, url.Length, url), map, ends));
        var ordinary = new TerminalBuffer(20, 4);
        ordinary.Write("ordinary");
        var (_, ordinaryMap) = Ntilde.VT.Links.RowTextExtractor.Extract(ordinary, 0, out int[] ordinaryEnds);
        Assert.Same(ordinaryMap, ordinaryEnds);
    }

}
