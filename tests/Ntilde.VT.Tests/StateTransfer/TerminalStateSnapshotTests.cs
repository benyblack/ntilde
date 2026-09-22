using System;
using System.Diagnostics;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

public class TerminalStateSnapshotTests
{
    private const int UnlimitedScrollback = int.MaxValue;

    private static (AnsiParser Parser, TerminalBuffer Buffer) NewPair(int cols = 80, int rows = 24)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        return (parser, buffer);
    }

    [Fact]
    public void ExportImport_RestoresTheInactiveScreen()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("main screen text");
        a.Process("\u001b[?1049h");   // into the alt screen
        a.Process("alt screen text");

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (AnsiParser c, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        // Both are on the alt screen; leaving it must reveal the main screen's text.
        a.Process("\u001b[?1049l");
        c.Process("\u001b[?1049l");

        Assert.Equal(RowText(bufA, 0), RowText(bufC, 0));
        Assert.StartsWith("main screen text", RowText(bufC, 0));
    }

    [Fact]
    public void ExportImport_RestoresScrollback()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 20, rows: 4);
        for (int i = 0; i < 30; i++)
        {
            a.Process($"line{i}\r\n");
        }

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);
        Assert.False(snapshot.ScrollbackTruncated);

        (_, TerminalBuffer bufC) = NewPair(cols: 20, rows: 4);
        bufC.ImportState(snapshot);

        bufA.Lock.EnterReadLock();
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(bufA.Scrollback.Count, bufC.Scrollback.Count);
            Assert.Equal(bufA.TotalLines, bufC.TotalLines);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
            bufA.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void ExportState_CapsScrollbackAndSaysSo()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 20, rows: 4);
        for (int i = 0; i < 100; i++)
        {
            a.Process($"line{i}\r\n");
        }

        TerminalStateSnapshot snapshot = bufA.ExportState(maxScrollbackRows: 10);

        Assert.True(snapshot.ScrollbackTruncated);
        Assert.Equal(10, snapshot.ScrollbackRowCount);
        Assert.True(snapshot.ScrollbackRowsDropped > 0);

        // The rows kept are the NEWEST ones - the ones a user scrolling up sees first.
        (_, TerminalBuffer bufC) = NewPair(cols: 20, rows: 4);
        bufC.ImportState(snapshot);
        bufC.Lock.EnterReadLock();
        try
        {
            Assert.Equal(10, bufC.Scrollback.Count);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
        }
    }

    [Fact]
    public void ExportImport_RestoresTabStopsSavedCursorSyncAndKittyStack()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b[3g");            // clear all tab stops
        a.Process("\u001b[10G\u001bH");    // set one at column 10
        a.Process("\u001b[5;7H\u001b7");   // move, then DECSC
        a.Process("\u001b[?2026h");        // begin synchronized output
        a.Process("\u001b[>1u");           // kitty keyboard push

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (_, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        Assert.Equal(snapshot.TabStops, bufC.ExportState(UnlimitedScrollback).TabStops);
        Assert.Equal(snapshot.SavedCursorMain.Row, bufC.ExportState(UnlimitedScrollback).SavedCursorMain.Row);
        Assert.Equal(snapshot.SavedCursorMain.Col, bufC.ExportState(UnlimitedScrollback).SavedCursorMain.Col);
        Assert.True(bufC.ExportState(UnlimitedScrollback).IsSynchronizedOutput);
        Assert.Equal(1, bufC.Modes.KittyKeyboard.Flags);
        Assert.Equal(1, bufC.Modes.KittyKeyboard.StackDepth);
    }

    [Fact]
    public void ExportImport_RestoresHyperlinkIdentityIncludingGrouping()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair();
        a.Process("\u001b]8;id=x;https://example.com\u0007AB\u001b]8;;\u0007 ");
        a.Process("\u001b]8;id=x;https://example.com\u0007CD\u001b]8;;\u0007");

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (_, TerminalBuffer bufC) = NewPair();
        bufC.ImportState(snapshot);

        bufC.Lock.EnterReadLock();
        try
        {
            Ntilde.VT.Links.Hyperlink? first = bufC.ViewportRows[0].GetHyperlink(0);
            Ntilde.VT.Links.Hyperlink? later = bufC.ViewportRows[0].GetHyperlink(3);

            Assert.NotNull(first);
            Assert.Equal("https://example.com", first!.Uri);
            // Same explicit id and URI: one anchor, so one instance - reference equality is the
            // identity test OSC 8 defines, and an import that minted two instances would silently
            // stop the two runs underlining together.
            Assert.Same(first, later);
        }
        finally
        {
            bufC.Lock.ExitReadLock();
        }
    }

    /// <summary>
    /// A resize replaces <c>_viewport</c> but leaves the stashed <c>_mainScreen</c> pointing at the
    /// pre-resize array - it is only reconciled on the next screen switch
    /// (<c>TerminalBuffer.ResizeAndReflow.cs</c>, the "Normal Main Screen Resize" branch). So the
    /// export has to resolve the active screen through <c>_viewport</c>; reading <c>_mainScreen</c>
    /// directly ships a screen that is stale in both content and shape.
    /// </summary>
    [Fact]
    public void ExportImport_AfterAResize_CarriesTheScreenTheViewportActuallyHolds()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 40, rows: 8);

        // 50 characters: wraps across two rows at 40 columns, and reflows back onto one at 60.
        // That makes the stale array observably different from the live one rather than merely
        // a different object holding the same text.
        a.Process(new string('x', 39) + "ABCDEFGHIJK");
        bufA.Resize(60, 10);

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);
        Assert.Equal(60, snapshot.Cols);
        Assert.Equal(10, snapshot.Rows);

        (_, TerminalBuffer bufC) = NewPair(cols: 60, rows: 10);
        bufC.ImportState(snapshot);

        Assert.Equal(RowText(bufA, 0), RowText(bufC, 0));
        Assert.Equal(RowText(bufA, 1), RowText(bufC, 1));
        Assert.Equal(new string('x', 39) + "ABCDEFGHIJK", RowText(bufC, 0));
    }

    /// <summary>
    /// <c>EnterAltScreen</c> discards a wrong-shaped <c>_altScreen</c> and allocates a blank one
    /// rather than resizing it, so an export must not ship content the source buffer would itself
    /// have thrown away. Shipping it would be worse than losing it: the import writes into a
    /// correctly-shaped array, which then passes the very shape check that would have discarded it,
    /// and the restored buffer shows content on an alt screen the source shows blank.
    /// </summary>
    /// <remarks>
    /// Uses <c>?47h</c> rather than <c>?1049h</c> deliberately: 1049 clears on entry and would mask
    /// the divergence entirely. 47 and 1047 do not.
    /// </remarks>
    [Fact]
    public void ExportState_BlanksAnAltScreenLeftAtAStaleShapeByAResize()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 40, rows: 8);
        a.Process("\u001b[?47h");            // legacy alt screen: does NOT clear on entry
        a.Process("alt screen leftovers");
        a.Process("\u001b[?47l");            // back to main; _altScreen keeps the content

        // A main-screen resize touches neither stashed screen, so _altScreen is now genuinely
        // stale-shaped: 8 rows of 40 columns against a 10x60 buffer.
        bufA.Resize(60, 10);

        TerminalStateSnapshot snapshot = bufA.ExportState(UnlimitedScrollback);

        (AnsiParser c, TerminalBuffer bufC) = NewPair(cols: 60, rows: 10);
        bufC.ImportState(snapshot);

        a.Process("\u001b[?47h");
        c.Process("\u001b[?47h");

        Assert.Equal("", RowText(bufA, 0));
        Assert.Equal(RowText(bufA, 0), RowText(bufC, 0));
    }

    /// <summary>
    /// Export → bytes → import into a fresh buffer → export again must produce the same bytes.
    /// The import in the middle is the point: a serializer round trip alone only proves the JSON
    /// survives, while this proves the buffer reconstructed from it is the same buffer. Anything
    /// the import drops, reorders or recomputes differently shows up as a byte difference.
    /// </summary>
    [Fact]
    public void ExportImportExport_IsByteIdentical()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 40, rows: 8);
        a.Process("hello \u001b[31mworld\u001b[0m 你好 \U0001F600\r\n");
        a.Process("\u001b]8;id=k;https://example.com\u0007link\u001b]8;;\u0007\r\n");
        a.Process("\u001b[3g\u001b[1;9H\u001bH\u001b[4;4H\u001b7");
        a.Process("\u001b[?1049hinside alt\u001b[>1u");
        for (int i = 0; i < 20; i++)
        {
            a.Process($"scroll {i}\r\n");
        }

        TerminalStateSnapshot first = bufA.ExportState(UnlimitedScrollback);
        first.Parser = a.ExportState();
        first.DecoderTail = [0xF0, 0x9F];
        first.StreamSeq = 12345;

        byte[] bytes = TerminalStateSerializer.ToBytes(first);

        TerminalStateSnapshot restored = TerminalStateSerializer.FromBytes(bytes);
        (AnsiParser c, TerminalBuffer bufC) = NewPair(cols: 40, rows: 8);
        bufC.ImportState(restored);
        c.ImportState(restored.Parser);

        TerminalStateSnapshot second = bufC.ExportState(UnlimitedScrollback);
        second.Parser = c.ExportState();
        second.DecoderTail = restored.DecoderTail;
        second.StreamSeq = restored.StreamSeq;

        Assert.Equal(bytes, TerminalStateSerializer.ToBytes(second));
    }

    /// <summary>
    /// Reports the size and cost of the shape the multiplexer will actually send on attach.
    /// Not a threshold assertion - the numbers go in the phase report - but it does fail if the
    /// snapshot grows past a bound no plausible implementation should reach, which is how a
    /// regression that starts embedding images or scrollback twice would surface.
    /// </summary>
    [Fact]
    public void Measure_SerializedSizeAndTiming_For80x24With10kScrollback()
    {
        (AnsiParser a, TerminalBuffer bufA) = NewPair(cols: 80, rows: 24);
        bufA.MaxHistory = 20000;

        // Comfortably more than the 10k we want in scrollback: only rows that scroll OFF the
        // viewport reach it, so the 24-row viewport keeps the newest ones back. Emitting exactly
        // 10,000 lines leaves 9,977 rows in scrollback (10,000 lines + the cursor's empty row,
        // minus the 24 on screen) and the cap below never binds - the measurement is meant to be
        // of a full 10k-row scrollback, and of a capped export. The margin is 100 rather than the
        // 24 that would just barely do it, so that a future one-row shift in row accounting costs
        // a row of slack instead of flipping this straight back to a hard failure.
        for (int i = 0; i < 10_000 + 100; i++)
        {
            a.Process($"scrollback line {i} with some filler text to make it realistic\r\n");
        }

        var exportWatch = Stopwatch.StartNew();
        TerminalStateSnapshot snapshot = bufA.ExportState(maxScrollbackRows: 10_000);
        exportWatch.Stop();

        byte[] bytes = TerminalStateSerializer.ToBytes(snapshot);

        var importWatch = Stopwatch.StartNew();
        TerminalStateSnapshot restored = TerminalStateSerializer.FromBytes(bytes);
        (_, TerminalBuffer bufC) = NewPair(cols: 80, rows: 24);
        bufC.ImportState(restored);
        importWatch.Stop();

        // Printed so the phase report can quote real numbers rather than estimates.
        Console.WriteLine(
            $"[mux-phase0] 80x24 + 10k scrollback: {bytes.Length} bytes serialized, " +
            $"export {exportWatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"deserialize+import {importWatch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(10_000, snapshot.ScrollbackRowCount);
        Assert.True(
            bytes.Length < 64 * 1024 * 1024,
            $"Snapshot ballooned to {bytes.Length} bytes for 80x24 + 10k scrollback; something is " +
            "being embedded that should not be (images? scrollback twice?).");
    }

    private static string RowText(TerminalBuffer buffer, int row)
    {
        buffer.Lock.EnterReadLock();
        try
        {
            TerminalCell[] cells = buffer.ViewportRows[row].Cells;
            var chars = new char[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                chars[i] = cells[i].Character == '\0' ? ' ' : cells[i].Character;
            }

            return new string(chars).TrimEnd();
        }
        finally
        {
            buffer.Lock.ExitReadLock();
        }
    }
}
