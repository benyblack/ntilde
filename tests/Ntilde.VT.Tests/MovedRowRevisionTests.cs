using Ntilde.VT;

namespace Ntilde.VT.Tests;

/// <summary>
/// #657: rows that a scroll, an insert or a delete moves keep their revisions; a move is not an edit. The row-picture
/// cache, keyed by (row Id, revision), then replays a moved row's picture instead of drawing every row afresh each frame
/// output scrolls. What keys by viewport index still sees the move, through the row Id.
/// </summary>
public class MovedRowRevisionTests
{
    private const int Cols = 20;
    private const int Rows = 6;

    private static (TerminalBuffer Buffer, AnsiParser Parser) TerminalWithLines()
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < Rows; i++)
        {
            parser.Process($"line {i}" + (i < Rows - 1 ? "\r\n" : string.Empty));
        }

        // Inside the screen, so each operation moves rows within it rather than into scrollback.
        parser.Process("\x1b[3;1H");
        return (buffer, parser);
    }

    private static TerminalRow[] ViewportRows(TerminalBuffer buffer)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            var rows = new TerminalRow[Rows];
            for (int r = 0; r < Rows; r++)
            {
                rows[r] = buffer.GetRowAbsolute(buffer.Scrollback.Count + r)!;
            }

            return rows;
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }

    // Scroll up (SU), scroll down (SD), insert line (IL), delete line (DL): each moves rows without editing them.
    public static TheoryData<string> Moves => new() { "\x1b[S", "\x1b[T", "\x1b[L", "\x1b[M" };

    [Theory]
    [MemberData(nameof(Moves))]
    public void AMovedRow_KeepsItsRevision(string move)
    {
        var (buffer, parser) = TerminalWithLines();
        TerminalRow[] before = ViewportRows(buffer);
        uint[] revisions = Array.ConvertAll(before, row => row.Revision);

        parser.Process(move);

        TerminalRow[] after = ViewportRows(buffer);
        int moved = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (Array.IndexOf(after, before[i]) is int index and >= 0 && index != i)
            {
                moved++;
                Assert.Equal(revisions[i], before[i].Revision);
            }
        }

        Assert.True(moved > 0, "setup: the operation moved no row");
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void EveryIndexAMovedRowArrivesAt_IsReportedDirty(string move)
    {
        // A moved row's revision no longer changes, so the snapshot must see the move through the row Id: a consumer
        // that repaints only dirty rows would otherwise leave the old row's text where the moved one now is.
        var (buffer, parser) = TerminalWithLines();
        var request = new RenderSnapshotRequest { ViewportRows = Rows, ViewportCols = Cols };
        buffer.CaptureRenderSnapshot(request, out _).Dispose();
        TerminalRow[] before = ViewportRows(buffer);

        parser.Process(move);
        TerminalRow[] after = ViewportRows(buffer);
        using TerminalRenderSnapshot snap = buffer.CaptureRenderSnapshot(request, out _);

        var dirtyRows = new HashSet<int>();
        for (int i = 0; i < snap.DirtySpans.Length; i++)
        {
            dirtyRows.Add(snap.DirtySpans.Array![i].Row);
        }

        for (int r = 0; r < Rows; r++)
        {
            if (!ReferenceEquals(before[r], after[r]))
            {
                Assert.True(dirtyRows.Contains(r), $"row {r} now shows another row but is not reported dirty");
            }
        }
    }
}
