namespace Ntilde.VT.Tests;

public sealed class LogicalLineSearchHighlightTests
{
    [Fact]
    public void EachVisibleSpanIsHighlightedEvenWhenTheFirstIsOutsideTheViewport()
    {
        var buffer = new TerminalBuffer(5, 2);
        new AnsiParser(buffer, false).Process("abcdefgh\r\ntail");
        var matches = buffer.FindMatches("cdefg", false, true);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportCols = 5,
            ViewportRows = 2,
            SearchMatches = matches,
            ActiveSearchIndex = 0,
            Isolated = true
        }, out _);
        Assert.Equal(1, snapshot.SearchHighlights.Length);
        var span = snapshot.SearchHighlights.Array[0];
        Assert.Equal((1, 0, 1, true), (span.AbsRow, span.StartCol, span.EndCol, span.IsActive));
    }

    [Fact]
    public void ActiveIndexNamesALogicalResultRatherThanAPhysicalSpan()
    {
        var buffer = new TerminalBuffer(5, 4);
        new AnsiParser(buffer, false).Process("abcdefgh\r\nabcdefgh");
        var matches = buffer.FindMatches("abcdefgh", false, true);
        Assert.Equal(2, matches.Count);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportCols = 5,
            ViewportRows = 4,
            SearchMatches = matches,
            ActiveSearchIndex = 1,
            Isolated = true
        }, out _);
        Assert.Equal(4, snapshot.SearchHighlights.Length);
        for (int i = 0; i < 4; i++)
        {
            var span = snapshot.SearchHighlights.Array[i];
            Assert.Equal(i, span.AbsRow);
            Assert.Equal(i >= 2, span.IsActive);
        }
    }

    [Fact]
    public void DenseOverlappingResultsShareRowsAndKeepVisibleHighlightsBounded()
    {
        var buffer = new TerminalBuffer(20, 3);
        new AnsiParser(buffer, false).Process(new string('a', 20_000));
        _ = buffer.FindMatches("warmup", false, true);
        string query = new('a', 10_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var matches = buffer.FindMatches(query, false, true);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(10_001, matches.Count);
        // A separate span array per overlap takes tens of MB for this small transcript.
        Assert.InRange(allocated, 0, 8L * 1024 * 1024);
        Assert.Equal(500, matches[^1].SpanCount);
        using var snapshot = buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportCols = 20,
            ViewportRows = 3,
            SearchMatches = matches,
            ActiveSearchIndex = matches.Count - 1,
            ScrollOffset = buffer.TotalLines - buffer.Rows - 500,
            Isolated = true
        }, out _);
        Assert.InRange(snapshot.SearchHighlights.Length, 3, 6);
        var spans = snapshot.SearchHighlights.Array.Take(snapshot.SearchHighlights.Length).ToArray();
        Assert.Equal(3, spans.Count(span => span.IsActive));
        Assert.All(spans, span =>
        {
            Assert.InRange(span.AbsRow, 500, 502);
            Assert.InRange(span.StartCol, 0, 19);
            Assert.InRange(span.EndCol, span.StartCol, 19);
        });
    }
}
