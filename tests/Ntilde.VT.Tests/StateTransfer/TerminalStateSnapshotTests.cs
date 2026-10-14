using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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

        // What the payload is made of: both screens plus the capped scrollback, every cell of it
        // a blittable TerminalCell. Expressed in terms of Unsafe.SizeOf rather than a byte
        // constant so the bound tracks a legitimate change to the cell's layout instead of
        // failing on one.
        long cellCount = (long)snapshot.Cols * (snapshot.ScrollbackRowCount + (2 * snapshot.Rows));
        long rawCellBytes = cellCount * Unsafe.SizeOf<TerminalCell>();

        // Printed so the phase report can quote real numbers rather than estimates.
        Console.WriteLine(
            $"[mux-phase0] 80x24 + 10k scrollback: {bytes.Length} bytes serialized, " +
            $"{(double)bytes.Length / cellCount:F2} bytes/cell, " +
            $"export {exportWatch.Elapsed.TotalMilliseconds:F1} ms, " +
            $"deserialize+import {importWatch.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(10_000, snapshot.ScrollbackRowCount);

        // The bound that actually bounds something. Base64 costs a fixed 4/3, so a payload that
        // is nothing but the cells lands at ~1.33x the raw size; the 2x ceiling leaves two thirds
        // of that again for the JSON envelope and the side tables, and still fails outright on a
        // scrollback embedded twice or a screen that started carrying image pixels. The old 64 MB
        // ceiling was five times the real 12.9 MB payload and would have passed both.
        Assert.True(
            bytes.Length < rawCellBytes * 2,
            $"Snapshot is {bytes.Length} bytes for {cellCount} cells ({rawCellBytes} raw) - more " +
            "than twice the cell data it describes, so something is being embedded that should " +
            "not be (images? scrollback twice?).");
    }

    // ── the screen switch an import performs, and has to announce ────────────────

    /// <summary>
    /// An import that lands on the other screen <em>is</em> a screen switch, and is the only
    /// announcement of it there will be: the restored tail carries no enter-alt sequence, so a
    /// pane attached to an alt-screen snapshot would otherwise keep Command Assist and the Agent
    /// Output panel in main-screen state - visible over a full-screen TUI, agent status never
    /// updated - until some later, unrelated transition. <c>AGENTS.md</c> requires the assist UI
    /// to auto-hide in alternate-screen mode; <c>TerminalPane.OnBufferScreenSwitched</c> is what
    /// enforces it, and this event is what reaches it.
    /// </summary>
    /// <remarks>
    /// Falsifiable: assign <c>_isAltScreen</c> from the snapshot without raising the event, as
    /// <c>ImportState</c> used to, and <c>observed</c> comes back empty.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportState_RaisesTheScreenSwitchNotificationWhenTheImportChangesScreen(bool toAlt)
    {
        (AnsiParser source, TerminalBuffer sourceBuffer) = NewPair();
        if (toAlt)
        {
            source.Process("\u001b[?1049h");
        }

        TerminalStateSnapshot snapshot = sourceBuffer.ExportState(UnlimitedScrollback);

        // The destination starts on the other screen, so adopting the snapshot moves it.
        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        if (!toAlt)
        {
            targetParser.Process("\u001b[?1049h");
        }

        var observed = new List<bool>();
        target.OnScreenSwitched += observed.Add;
        target.ImportState(snapshot);

        Assert.Equal([toAlt], observed);
        Assert.Equal(toAlt, target.IsAltScreenActive);
    }

    /// <summary>
    /// The other half of the contract: an attach that does not move the screen must not report
    /// one. Raising unconditionally would announce a switch on every attach, which is exactly what
    /// <c>EnterAltScreen</c> and <c>SwitchToMainScreen</c> decline to do when they early-out on an
    /// already-active screen.
    /// </summary>
    /// <remarks>
    /// Falsifiable: raise the event unconditionally instead of on a changed screen, and both
    /// cases come back with one notification instead of none.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportState_DoesNotRaiseTheScreenSwitchNotificationWhenTheScreenIsUnchanged(bool onAlt)
    {
        (AnsiParser source, TerminalBuffer sourceBuffer) = NewPair();
        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        if (onAlt)
        {
            source.Process("\u001b[?1049h");
            targetParser.Process("\u001b[?1049h");
        }

        TerminalStateSnapshot snapshot = sourceBuffer.ExportState(UnlimitedScrollback);

        var observed = new List<bool>();
        target.OnScreenSwitched += observed.Add;
        target.ImportState(snapshot);

        Assert.Empty(observed);
        Assert.Equal(onAlt, target.IsAltScreenActive);
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
