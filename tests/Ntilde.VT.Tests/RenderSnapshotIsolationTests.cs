using System.Collections.Concurrent;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// CaptureRenderSnapshot keeps a row-diff baseline (the previous capture's rows) and a cache of
// built render rows, and the live renderer depends on both: it repaints only the dirty spans of
// a frame over the row picture it drew last time. Two things went wrong when a second consumer -
// the agent-host screenshot, driven from the IPC thread - captured the same buffer:
//
//   1. Logic. The agent capture advanced the baseline, so the next live frame's spans left out
//      every change made before the capture, and those cells stayed stale on screen.
//   2. A data race. Both captures wrote the baseline and cache arrays while holding only the
//      shared read lock.
//
// Isolated captures (RenderSnapshotRequest.Isolated) now neither read nor write that state.
public class RenderSnapshotIsolationTests
{
    private const int Cols = 20;
    private const int Rows = 5;

    private static TerminalBuffer NewLabelledBuffer(out AnsiParser parser)
    {
        var buffer = new TerminalBuffer(Cols, Rows);
        parser = new AnsiParser(buffer);
        for (int r = 0; r < Rows; r++)
        {
            parser.Process($"\u001b[{r + 1};1Hline {r}");
        }

        return buffer;
    }

    private static TerminalRenderSnapshot Capture(TerminalBuffer buffer, bool isolated, int scrollOffset = 0)
        => buffer.CaptureRenderSnapshot(new RenderSnapshotRequest
        {
            ViewportRows = Rows,
            ViewportCols = Cols,
            ScrollOffset = scrollOffset,
            Isolated = isolated
        }, out _);

    private static void WriteAt(AnsiParser parser, int row, int col, string text)
        => parser.Process($"\u001b[{row + 1};{col + 1}H{text}");

    private static string RowText(TerminalRenderSnapshot snapshot, int row)
    {
        var cells = snapshot.RowsData.Array[row].Cells;
        return new string(cells.Select(c => c.Character).ToArray()).TrimEnd(' ', '\0');
    }

    private static HashSet<int> DirtyColumns(TerminalRenderSnapshot snapshot, int row)
    {
        var cols = new HashSet<int>();
        for (int i = 0; i < snapshot.DirtySpans.Length; i++)
        {
            var span = snapshot.DirtySpans.Array[i];
            if (span.Row != row) continue;
            for (int c = span.ColStart; c < span.ColEnd; c++) cols.Add(c);
        }

        return cols;
    }

    private static void AssertCovers(TerminalRenderSnapshot snapshot, int row, int colStart, int colEnd)
    {
        var dirty = DirtyColumns(snapshot, row);
        for (int c = colStart; c < colEnd; c++)
        {
            Assert.True(
                dirty.Contains(c),
                $"row {row} col {c} changed since the previous live frame but is missing from its dirty spans " +
                $"(dirty cols: [{string.Join(",", dirty.Order())}]) - the renderer would keep painting it stale.");
        }
    }

    [Fact]
    public void LiveDiff_StillCoversChangesMadeBeforeAnIsolatedCapture()
    {
        var buffer = NewLabelledBuffer(out var parser);

        using (Capture(buffer, isolated: false)) { }   // live frame N: the baseline

        WriteAt(parser, row: 1, col: 0, "AAAA");        // changed before the capture...

        using (var isolated = Capture(buffer, isolated: true))
        {
            Assert.Equal("AAAA 1", RowText(isolated, 1));
            Assert.Equal("line 3", RowText(isolated, 3));
        }

        WriteAt(parser, row: 3, col: 0, "BBB");         // ...and after it

        using var live = Capture(buffer, isolated: false);  // live frame N+1

        AssertCovers(live, row: 1, colStart: 0, colEnd: 4);
        AssertCovers(live, row: 3, colStart: 0, colEnd: 3);

        // Still a diff, not a full repaint: untouched rows stay clean.
        Assert.Empty(DirtyColumns(live, 0));
        Assert.Empty(DirtyColumns(live, 2));
        Assert.Empty(DirtyColumns(live, 4));

        Assert.Equal("AAAA 1", RowText(live, 1));
        Assert.Equal("BBBe 3", RowText(live, 3));
    }

    [Fact]
    public void IsolatedCapture_ReturnsFullContent_AndMarksEveryRowDirty()
    {
        var buffer = NewLabelledBuffer(out _);

        // Two live frames with nothing in between: the second reports no changes, which is
        // exactly the state an isolated capture must not inherit.
        using (Capture(buffer, isolated: false)) { }
        using (var live = Capture(buffer, isolated: false))
        {
            Assert.Equal(0, live.DirtySpans.Length);
        }

        using var isolated = Capture(buffer, isolated: true);

        Assert.Equal(Rows, isolated.RowsData.Length);
        for (int r = 0; r < Rows; r++)
        {
            Assert.Equal($"line {r}", RowText(isolated, r));
            Assert.Equal(Enumerable.Range(0, Cols), DirtyColumns(isolated, r).Order());
        }
    }

    [Fact]
    public void IsolatedCapture_BuildsPagedScrollbackRows()
    {
        // The paged-scrollback branch has its own cached builder on the live path; the isolated
        // path builds those rows without it.
        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 12; i++)
        {
            parser.Process($"sb{i:D2}\r\n");
        }

        int scrollback = buffer.Scrollback.Count;
        Assert.True(scrollback >= Rows, "expected a full viewport of lines in paged scrollback");

        using var isolated = Capture(buffer, isolated: true, scrollOffset: scrollback);

        for (int r = 0; r < Rows; r++)
        {
            Assert.Equal($"sb{r:D2}", RowText(isolated, r));
        }
    }

    [Fact]
    public void IsolatedCapture_DoesNotRewriteThePaletteALiveFrameIsDrawingWith()
    {
        // The live path reuses one palette array across frames to avoid a per-frame allocation.
        // A live frame is still drawing with it after the read lock is released, so an isolated
        // capture that refilled it would recolor that frame mid-draw.
        var buffer = NewLabelledBuffer(out _);
        var oldRed = buffer.Theme.Red;

        using var live = Capture(buffer, isolated: false);
        var livePalette = live.Theme.AnsiPalette;
        Assert.Equal(oldRed, livePalette[1]);

        var newRed = TermColor.FromRgb(1, 2, 3);
        buffer.Theme = new TerminalTheme { Red = newRed };

        using (var isolated = Capture(buffer, isolated: true))
        {
            Assert.Equal(newRed, isolated.Theme.AnsiPalette[1]);
        }

        Assert.Equal(oldRed, livePalette[1]);
    }

    [Fact]
    public void ConcurrentLiveAndIsolatedCaptures_UnderAWriter_StayConsistent()
    {
        // Timing-independent by construction: each writer step rewrites every viewport row in one
        // Process call (one write-lock hold), and each capture reads under one read-lock hold, so
        // every snapshot must show ONE generation across all viewport rows, each row carrying its
        // own label. That holds for any interleaving; only a corrupted cache or baseline breaks it.
        const int generations = 400;
        const int capturesPerReader = 400;
        const int readerScrollOffset = 2;

        var buffer = new TerminalBuffer(Cols, Rows);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 12; i++)
        {
            parser.Process($"sb{i:D2}\r\n");
        }

        int scrollback = buffer.Scrollback.Count;
        Assert.True(scrollback >= readerScrollOffset, "expected lines in paged scrollback");

        static string Generation(int gen)
            => string.Concat(Enumerable.Range(0, Rows).Select(r => $"\u001b[{r + 1};1HR{r}:{gen:D6}"));

        parser.Process(Generation(0));

        var failures = new ConcurrentQueue<string>();

        void CheckSnapshot(TerminalRenderSnapshot snapshot, string who)
        {
            int? generation = null;
            for (int r = 0; r < snapshot.RowsData.Length; r++)
            {
                int absRow = snapshot.RowsData.Array[r].AbsRow;
                string text = RowText(snapshot, r);
                if (absRow < scrollback)
                {
                    if (text != $"sb{absRow:D2}")
                    {
                        failures.Enqueue($"{who}: scrollback row {absRow} read '{text}'");
                    }

                    continue;
                }

                int viewportRow = absRow - scrollback;
                string prefix = $"R{viewportRow}:";
                if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length != prefix.Length + 6)
                {
                    failures.Enqueue($"{who}: viewport row {viewportRow} read '{text}'");
                    continue;
                }

                int gen = int.Parse(text.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture);
                if (generation is { } expected && gen != expected)
                {
                    failures.Enqueue($"{who}: torn snapshot, generation {expected} and {gen} in one capture");
                }

                generation = gen;
            }
        }

        using var start = new Barrier(3);

        var writer = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int gen = 1; gen <= generations; gen++)
                {
                    parser.Process(Generation(gen));
                }
            }
            catch (Exception ex) { failures.Enqueue($"writer: {ex}"); }
        });

        var liveReader = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < capturesPerReader; i++)
                {
                    using var snapshot = Capture(buffer, isolated: false);
                    CheckSnapshot(snapshot, "live");
                }
            }
            catch (Exception ex) { failures.Enqueue($"live reader: {ex}"); }
        });

        var isolatedReader = new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < capturesPerReader; i++)
                {
                    // Alternate the scroll position so, on a shared cache, the two readers would
                    // be writing different rows into the same slots.
                    using var snapshot = Capture(buffer, isolated: true, scrollOffset: i % 2 == 0 ? 0 : readerScrollOffset);
                    CheckSnapshot(snapshot, "isolated");
                }
            }
            catch (Exception ex) { failures.Enqueue($"isolated reader: {ex}"); }
        });

        writer.Start();
        liveReader.Start();
        isolatedReader.Start();

        // A deadlock guard, not a performance bound: the whole run takes milliseconds.
        Assert.True(writer.Join(TimeSpan.FromMinutes(2)), "writer did not finish");
        Assert.True(liveReader.Join(TimeSpan.FromMinutes(2)), "live reader did not finish");
        Assert.True(isolatedReader.Join(TimeSpan.FromMinutes(2)), "isolated reader did not finish");

        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Take(10)));

        // Final content, through both modes, matches the last generation written.
        using var finalLive = Capture(buffer, isolated: false);
        using var finalIsolated = Capture(buffer, isolated: true);
        for (int r = 0; r < Rows; r++)
        {
            Assert.Equal($"R{r}:{generations:D6}", RowText(finalLive, r));
            Assert.Equal($"R{r}:{generations:D6}", RowText(finalIsolated, r));
        }
    }
}
