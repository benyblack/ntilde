using System;
using System.Text;
using Ntilde.VT;

namespace Ntilde.VT.Tests;

// RAM audit, 2026-10-06: widening a window with 100k lines of scrollback in each of three panes
// took Ntilde to 1.24 GB. The reflow materialised the whole scrollback four times over - a
// TerminalRow copy of every old row, one (cell, grapheme, link) entry per cell in a pooled scratch
// array sized scrollback x the wider width (rounded up to a power of two, and kept by the pool
// afterwards), a TerminalRow for every re-wrapped row, and the rebuilt pages.
public class ReflowMemoryTests
{
    private const int HistoryLines = 10_000;

    [Fact]
    public void Widening_a_full_scrollback_allocates_about_one_new_scrollback()
    {
        var buffer = new TerminalBuffer(80, 24) { MaxHistory = HistoryLines };
        var parser = new AnsiParser(buffer);
        var text = new StringBuilder();
        for (int i = 0; i < HistoryLines + 100; i++) text.Append("line ").Append(i).Append(' ').Append('x', 60).Append("\r\n");
        parser.Process(text.ToString());
        Assert.True((buffer.TotalLines - buffer.Rows) >= HistoryLines - 64, $"Precondition: scrollback should be full, has {(buffer.TotalLines - buffer.Rows)} rows.");

        long before = GC.GetAllocatedBytesForCurrentThread();
        buffer.Resize(120, 24);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The rebuilt scrollback itself is the unavoidable part: rows x 120 cols x 12-byte cells.
        long newScrollbackBytes = (long)(buffer.TotalLines - buffer.Rows) * 120 * 12;
        Assert.True(
            allocated < newScrollbackBytes * 13 / 10 + 2 * 1024 * 1024,
            $"Reflow allocated {allocated / (1024 * 1024)} MB for a {newScrollbackBytes / (1024 * 1024)} MB scrollback.");
    }
}
