using Ntilde.VT.Export;
using Ntilde.VT.Links;

namespace Ntilde.VT.Tests;

public sealed class Osc66StateTests
{
    private static CellTextSizing? Piece(TerminalBuffer buffer, int col, int row)
    {
        buffer.Lock.EnterReadLock();
        try { return buffer.GetTextSizingAbsolute(col, row); }
        finally { buffer.Lock.ExitReadLock(); }
    }

    private static TerminalStateSnapshot Wire(TerminalStateSnapshot state)
        => TerminalStateSerializer.FromBytes(TerminalStateSerializer.ToBytes(state));

    [Fact]
    public void Both_screens_and_clipped_history_blocks_survive_json_and_later_edits()
    {
        var source = new TerminalBuffer(12, 3);
        var parser = new AnsiParser(source);
        parser.Process("\x1b]66;s=2:w=2;main\a\x1b[3;1H\n\x1b[?47h\x1b]66;s=2:w=2;alt\a");
        var state = Wire(source.ExportState(int.MaxValue));
        Assert.Equal(2, state.TextSizing!.Count);
        var target = new TerminalBuffer(12, 3);
        target.ImportState(state);
        Assert.Equal("alt", Piece(target, 0, 0)!.Block.Text);
        new AnsiParser(target).Process("\x1b[?47l");
        Assert.Equal("main", Piece(target, 0, 0)!.Block.Text);
        Assert.Same(Piece(target, 0, 0)!.Block, Piece(target, 3, 1)!.Block);
        new AnsiParser(target).Process("\x1b[1;1H\x1b[X");
        Assert.Null(Piece(target, 0, 0));
        Assert.Null(Piece(target, 3, 1));
    }

    [Fact]
    public void Zero_history_export_keeps_the_visible_fragment_with_negative_anchor()
    {
        var source = new TerminalBuffer(12, 3);
        new AnsiParser(source).Process("\x1b]66;s=2;A\a\x1b[3;1H\n");
        var state = Wire(source.ExportState(0));
        Assert.Equal(-1, Assert.Single(state.TextSizing!).Y);
        var target = new TerminalBuffer(12, 3);
        target.ImportState(state);
        Assert.Equal(1, Piece(target, 0, 0)!.RowOffset);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("position")]
    [InlineData("width")]
    [InlineData("overlap")]
    [InlineData("options")]
    public void Invalid_metadata_is_rejected_before_replacing_existing_content(string mutation)
    {
        var source = new TerminalBuffer(12, 3);
        new AnsiParser(source).Process("\x1b]66;s=2;A\a");
        var state = Wire(source.ExportState(0));
        var entry = Assert.Single(state.TextSizing!);
        switch (mutation)
        {
            case "text": entry.Text = "changed"; break;
            case "position": entry.X = 12; break;
            case "width": entry.BaseWidth = 0; break;
            case "overlap": state.TextSizing!.Add(entry); break;
            case "options": entry.Options = entry.Options with { Scale = 8 }; break;
        }
        var target = new TerminalBuffer(12, 3);
        target.Write("keep");
        Assert.Throws<InvalidOperationException>(() => target.ImportState(state));
        Assert.Equal("keep", TerminalExporter.ExportToPlainText(target));
    }

    [Fact]
    public void Cropped_import_removes_the_whole_block_in_history_and_the_viewport()
    {
        var source = new TerminalBuffer(8, 3);
        new AnsiParser(source).Process("\x1b]66;s=2:w=2;AB\a\x1b[3;1H\n\n\n");
        var target = new TerminalBuffer(2, 3);
        target.ImportState(Wire(source.ExportState(int.MaxValue)));
        Assert.Null(target.ExportState(int.MaxValue).TextSizing);
        target.Lock.EnterReadLock();
        try { Assert.Equal(" ", target.GetGraphemeAbsolute(0, 0)); }
        finally { target.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Metadata_is_optional_for_ordinary_and_legacy_state()
    {
        var source = new TerminalBuffer(12, 3);
        source.Write("ordinary");
        var state = Wire(source.ExportState(0));
        Assert.Null(state.TextSizing);
        var target = new TerminalBuffer(12, 3);
        target.ImportState(state);
        Assert.Equal("ordinary", TerminalExporter.ExportToPlainText(target));
    }

    [Fact]
    public void Text_extraction_contains_the_payload_once_and_skips_occupied_padding()
    {
        var source = new TerminalBuffer(12, 3);
        new AnsiParser(source).Process("\x1b]66;s=2:w=2;AB\a!");
        Assert.Equal("AB!", TerminalExporter.ExportToPlainText(source));
        var (text, columns) = RowTextExtractor.Extract(source, 0);
        Assert.StartsWith("AB!", text);
        Assert.Equal(new[] { 0, 0, 4 }, columns.Take(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_skips_padding_and_maps_payload_matches_to_the_full_top_row_width(bool regex)
    {
        var source = new TerminalBuffer(12, 3);
        new AnsiParser(source).Process("\x1b]66;s=2:w=2;AB\a!");
        var match = Assert.Single(source.FindMatches("AB!", regex, true));
        Assert.Equal(new SearchMatchSpan(0, 0, 4), match.GetSpan(0));
        var sizedOnly = Assert.Single(source.FindMatches("B", regex, true));
        Assert.Equal(new SearchMatchSpan(0, 0, 3), sizedOnly.GetSpan(0));
    }

    [Fact]
    public void Combining_replacement_dirties_lower_rows_and_old_snapshots_stay_immutable()
    {
        var source = new TerminalBuffer(12, 3);
        var parser = new AnsiParser(source);
        parser.Process("\x1b]66;s=2;A\a");
        var request = new RenderSnapshotRequest { ViewportRows = 3, ViewportCols = 12 };
        using var old = source.CaptureRenderSnapshot(request, out _);
        parser.Process("\u0301");
        using var current = source.CaptureRenderSnapshot(request, out _);
        Assert.Equal("A", old.RowsData.Array[1].Cells[0].TextSizing!.Block.Text);
        Assert.Equal("A\u0301", current.RowsData.Array[1].Cells[0].TextSizing!.Block.Text);
        Assert.Contains(current.DirtySpans.Array.Take(current.DirtySpans.Length), x => x.Row == 1 && x.ColStart == 0 && x.ColEnd >= 2);
    }
}
