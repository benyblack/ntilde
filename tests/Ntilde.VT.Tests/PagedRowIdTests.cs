namespace Ntilde.VT.Tests;

/// <summary>
/// A history row's snapshot id: never 0 (the id of a row that is not there, which the draw path
/// skips), never a live row's id, and the same id for the same line wherever it sits in the view.
/// </summary>
public sealed class PagedRowIdTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Oldest_history_row_does_not_carry_the_missing_row_id(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 4);
        new AnsiParser(buffer).Process("MMMM\r\n\r\n\r\n\r\n");
        Assert.Equal(1, buffer.Scrollback.Count);
        Assert.Equal(0, buffer.Scrollback.TotalRowsEvicted);

        using var snapshot = buffer.CaptureRenderSnapshot(Request(scrollOffset: 1, isolated), out _);
        var rows = snapshot.RowsData.Array!;
        Assert.Equal('M', rows[0].Cells[0].Character);
        Assert.NotEqual(0, rows[0].RowId);
        for (int r = 1; r < 4; r++)
        {
            Assert.NotEqual(rows[0].RowId, rows[r].RowId);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void History_rows_keep_distinct_ids_that_follow_them_through_the_view(bool isolated)
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("a\r\nb\r\nc\r\nd\r\ne\r\nf\r\n");
        Assert.Equal(3, buffer.Scrollback.Count);

        long[] before;
        using (var snapshot = buffer.CaptureRenderSnapshot(Request(scrollOffset: 3, isolated), out _))
        {
            before = snapshot.RowsData.Array!.Take(3).Select(row => row.RowId).ToArray();
        }

        Assert.Equal(3, before.Distinct().Count());
        Assert.DoesNotContain(0, before);

        // One more line of history: the same three lines sit one row higher in the scrolled view.
        parser.Process("g\r\n");
        using var after = buffer.CaptureRenderSnapshot(Request(scrollOffset: 4, isolated), out _);
        Assert.Equal(before, after.RowsData.Array!.Take(3).Select(row => row.RowId).ToArray());
    }

    [Fact]
    public void Clearing_history_changes_the_cache_identity_of_the_row_that_takes_its_place()
    {
        var buffer = new TerminalBuffer(20, 4);
        var parser = new AnsiParser(buffer);
        parser.Process("AAAA\r\n\r\n\r\n\r\n");
        long oldId;
        uint oldRevision;
        using (var before = buffer.CaptureRenderSnapshot(Request(scrollOffset: 1, isolated: false), out _))
        {
            var row = before.RowsData.Array![0];
            Assert.Equal('A', row.Cells[0].Character);
            (oldId, oldRevision) = (row.RowId, row.Revision);
        }

        // CSI 3J resets the history's row counters, so the next line into history sits at the
        // position, and takes the id, the cleared line had.
        parser.Process("\x1b[3J\x1b[2J\x1b[HBBBB\r\n\r\n\r\n\r\n");
        Assert.Equal(1, buffer.Scrollback.Count);
        using var after = buffer.CaptureRenderSnapshot(Request(scrollOffset: 1, isolated: false), out _);
        var replaced = after.RowsData.Array![0];
        Assert.Equal('B', replaced.Cells[0].Character);
        Assert.True(replaced.RowId != oldId || replaced.Revision != oldRevision,
            "a row picture cached for the cleared line would be served for the new one");
    }

    private static RenderSnapshotRequest Request(int scrollOffset, bool isolated)
        => new() { ViewportRows = 4, ViewportCols = 20, ScrollOffset = scrollOffset, Isolated = isolated };
}
