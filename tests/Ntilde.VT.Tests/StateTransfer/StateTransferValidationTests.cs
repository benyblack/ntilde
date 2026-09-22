using System;
using System.Collections.Generic;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// The import paths' shared validation policy: clamp geometry and counts, reject malformed
/// structure at the boundary, and never half-apply.
/// </summary>
/// <remarks>
/// None of this is reachable in Phase 0 - there is no untrusted input yet, and every snapshot in
/// the product is produced by <c>ExportState</c> a few microseconds earlier. It is pinned anyway
/// because Phase 1 is where a snapshot starts arriving over IPC, and a seam whose validation is
/// asserted is one Phase 1 can build on rather than re-derive.
/// </remarks>
public class StateTransferValidationTests
{
    private static (AnsiParser Parser, TerminalBuffer Buffer) NewPair(int cols = 80, int rows = 24)
    {
        var buffer = new TerminalBuffer(cols, rows);
        var parser = new AnsiParser(buffer, forceConPtyFiltering: false) { ImageDecoder = null };
        return (parser, buffer);
    }

    private static TerminalStateSnapshot SnapshotOf(TerminalBuffer buffer, AnsiParser parser)
    {
        TerminalStateSnapshot snapshot = buffer.ExportState(int.MaxValue);
        snapshot.Parser = parser.ExportState();
        return snapshot;
    }

    // ── parser state: structure is rejected ──────────────────────────────────────

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    [InlineData(int.MaxValue)]
    public void ParserImport_RejectsAStateMachinePositionThatIsNotAState(int state)
    {
        (AnsiParser source, TerminalBuffer sourceBuffer) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.State = state;

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
        GC.KeepAlive(sourceBuffer);
    }

    /// <summary>
    /// <c>_gl</c> indexes a 4-element array on the next printable character, so an unchecked value
    /// throws <see cref="IndexOutOfRangeException"/> arbitrarily far from the import that set it.
    /// </summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ParserImport_RejectsAnOutOfRangeGlSlot(int gl)
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.Gl = gl;

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(4)]
    public void ParserImport_RejectsAnOutOfRangePendingCharsetSlot(int slot)
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.PendingCharsetSlot = slot;

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
    }

    [Fact]
    public void ParserImport_RejectsACharsetOrdinalThatIsNotACharset()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.Charsets[2] = 7;

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
    }

    [Fact]
    public void ParserImport_RejectsAWrongLengthCharsetArray()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.Charsets = [0, 0];

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
    }

    [Fact]
    public void ParserImport_RejectsMissingAccumulators()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.KittyPendingParams = null!;

        (AnsiParser target, _) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
    }

    // ── parser state: counts are clamped ─────────────────────────────────────────

    /// <summary>
    /// A CSI run longer than the parser's own accumulation cap is truncated exactly where the
    /// accumulation path would have truncated it - and latched as truncated, so the surviving
    /// prefix is not mistaken for a complete, classifiable sequence.
    /// </summary>
    [Fact]
    public void ParserImport_ClampsAnOverLongCsiRunAndLatchesTheTruncation()
    {
        const int MaxCsiParamChars = 65536;

        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.CsiParams = new string('1', MaxCsiParamChars + 4096);
        exported.CsiTruncated = false;

        (AnsiParser target, _) = NewPair();
        target.ImportState(exported);

        AnsiParserState reExported = target.ExportState();
        Assert.Equal(MaxCsiParamChars, reExported.CsiParams.Length);
        Assert.True(reExported.CsiTruncated);
    }

    // ── kitty keyboard: both dimensions clamped ──────────────────────────────────

    /// <summary>
    /// The stack's length was already clamped; its flag <em>values</em> were not, so an import
    /// could reach a flag word no <c>CSI &gt; flags u</c> could produce.
    /// </summary>
    /// <param name="corrupted">The tampered stack entry.</param>
    /// <param name="expected">
    /// What <c>Mask</c> makes of it: the supported bits survive, everything else is dropped, and a
    /// negative word - which no CSI parameter can produce, since they are unsigned digit runs -
    /// becomes 0, exactly as <see cref="KittyKeyboardState.Push"/> would have made it.
    /// </param>
    [Theory]
    [InlineData(0x7FFFFFFF, KittyKeyboardState.FlagDisambiguateEscapeCodes)]
    [InlineData(0b11110, 0)]
    [InlineData(-559038737, 0)]
    public void BufferImport_MasksKittyKeyboardFlagsToTheSupportedBits(int corrupted, int expected)
    {
        (AnsiParser parser, TerminalBuffer buffer) = NewPair();

        // Push a real entry so the stack has a slot, then corrupt the exported value.
        parser.Process("\u001b[>1u");
        TerminalStateSnapshot snapshot = SnapshotOf(buffer, parser);
        Assert.NotEmpty(snapshot.KittyKeyboard.MainStack!);
        snapshot.KittyKeyboard.MainStack![0] = corrupted;

        (_, TerminalBuffer target) = NewPair();
        target.ImportState(snapshot);

        // Nothing outside the honoured set may survive: an echoed bit tells crossterm/Codex that
        // key-release events are coming when they are not.
        Assert.Equal(0, target.Modes.KittyKeyboard.Flags & ~KittyKeyboardState.SupportedFlags);
        Assert.Equal(expected, target.Modes.KittyKeyboard.Flags);
    }

    // ── buffer state: structure is rejected, and nothing is half-applied ─────────

    [Fact]
    public void BufferImport_RejectsAMalformedCellBlobWithoutTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Main.CellsBase64 = "!!!! not base64 !!!!";

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));

        // The whole point of validating before the write lock: a refused import leaves the
        // terminal the user was looking at exactly as it was.
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void BufferImport_RejectsAnUnrepresentableSyncStartWithoutTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.LastSyncStartUtcTicks = -1;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, -1)]
    public void BufferImport_RejectsUnusableGeometry(int cols, int rows)
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Cols = cols;
        snapshot.Rows = rows;

        (_, TerminalBuffer target) = NewPair();
        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
    }

    // ── buffer state: "wholesale" is literal ─────────────────────────────────────

    /// <summary>
    /// The OSC 133 marks are deliberately excluded from the payload (their scrollback generation
    /// is a process-local epoch), which makes leaving the destination's own marks in place the
    /// wrong answer: they would point into a screen this call just replaced.
    /// </summary>
    [Fact]
    public void BufferImport_ClearsTheTrackedShellIntegrationMarks()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("\u001b]133;A\u0007prompt$ \u001b]133;B\u0007");
        Assert.NotNull(target.CommandStartMark);
        Assert.True(target.IsAcceptingCommandInput);

        target.ImportState(snapshot);

        Assert.Null(target.CommandStartMark);
        Assert.Null(target.CommandOutputStartMark);
        Assert.False(target.IsAcceptingCommandInput);
    }

    /// <summary>
    /// Side-table entries are keyed <c>row * cols + col</c> in the SOURCE geometry, and a row the
    /// cell copy skipped has no restored content to decorate. Reachable now that
    /// <see cref="TerminalStateTransfer.Restore"/> is the only caller that guarantees matching
    /// geometry - a direct <c>ImportState</c> into a taller buffer does not.
    /// </summary>
    [Fact]
    public void BufferImport_DoesNotApplySideTablesToRowsWhoseCellsWereNotCopied()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 80, rows: 10);
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);

        // A key for row 15 - beyond the 10 rows the snapshot actually carries, so the cell copy
        // stops long before it.
        snapshot.Main.ExtendedText = new Dictionary<int, string> { [(15 * 80) + 3] = "\U0001F600" };

        (_, TerminalBuffer target) = NewPair(cols: 80, rows: 24);
        target.ImportState(snapshot);

        target.Lock.EnterReadLock();
        try
        {
            Assert.Null(target.ViewportRows[15].GetExtendedText(3));
        }
        finally
        {
            target.Lock.ExitReadLock();
        }
    }

    // ── Restore owns the geometry precondition ───────────────────────────────────

    /// <summary>
    /// A geometry mismatch used to degrade silently: the cells are cropped and padded to the
    /// destination's shape while the side tables are applied at the source's coordinates. Restore
    /// resizes instead, so the caller cannot get this wrong.
    /// </summary>
    [Fact]
    public void Restore_ResizesTheBufferToTheSnapshotsGeometry()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("wide and tall");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        TerminalStateTransfer.Restore(target, targetParser, snapshot);

        Assert.Equal(100, target.Cols);
        Assert.Equal(30, target.Rows);
        Assert.StartsWith("wide and tall", RowText(target, 0), StringComparison.Ordinal);
    }

    // ── Restore validates the whole envelope before resizing or importing ───────

    /// <summary>
    /// Codex finding 1: a geometry mismatch used to make <see cref="TerminalStateTransfer.Restore"/>
    /// resize the destination before <c>TerminalBuffer.ImportState</c> got a chance to reject a bad
    /// version. A caller catching the resulting exception was left with a resized, reflowed
    /// destination. This is falsifiable: reverting the hoist in <c>Restore</c> (validating only
    /// inside <c>ImportState</c>, after the resize) makes the resize run unconditionally whenever
    /// the geometry differs, so the buffer's <c>Cols</c>/<c>Rows</c> would already be the snapshot's
    /// by the time the version check throws, and the size assertions below would fail.
    /// </summary>
    [Fact]
    public void Restore_RejectsABadVersionWithoutResizingOrTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Version = TerminalStateSnapshot.CurrentVersion + 1;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex finding 1's second half: a malformed <em>parser</em> position used to be rejected only
    /// after the buffer half of the transfer had already resized and imported - the exact case the
    /// report called out. Falsifiable the same way: move the parser validation back to run only
    /// inside <c>AnsiParser.ImportState</c> (i.e. after <c>buffer.ImportState</c> in <c>Restore</c>)
    /// and the buffer will have already been resized and its content replaced with the source's by
    /// the time the throw happens, failing every assertion below.
    /// </summary>
    [Fact]
    public void Restore_RejectsABadParserStateWithoutResizingOrTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Parser.State = -1;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    // ── buffer state: required nested objects, rejected before the write lock ───

    /// <summary>
    /// Codex finding 2: <c>KittyKeyboard</c> is read as a non-nullable object inside the write lock
    /// (<c>snapshot.KittyKeyboard.MainStack</c>, directly, unlike the null-tolerant
    /// Main/Alt/Scrollback/Sgr/Modes/saved-cursor/TabStops/Hyperlinks paths below it), so a
    /// deserialized payload carrying an explicit <c>"kitty_kbd": null</c> used to throw a
    /// <see cref="NullReferenceException"/> only after the screens, scrollback and cursors had
    /// already been replaced. Falsifiable: remove the <c>RequirePresent</c> call added for this and
    /// the main screen is overwritten with "source content" before the (wrongly-typed) exception
    /// fires, so both the exception-type assertion and the content assertion fail.
    /// </summary>
    [Fact]
    public void BufferImport_RejectsNullKittyKeyboardStateWithoutTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.KittyKeyboard = null!;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Same gap, the other field Codex named: <c>snapshot.Grapheme.HighSurrogate</c> dereferences
    /// <c>Grapheme</c> itself directly, so an explicit <c>"grapheme": null</c> used to throw mid
    /// import for the same reason. Falsifiable the same way as the kitty-keyboard case above.
    /// </summary>
    [Fact]
    public void BufferImport_RejectsNullGraphemeStateWithoutTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Grapheme = null!;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
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
