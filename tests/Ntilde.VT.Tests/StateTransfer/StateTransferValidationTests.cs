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


    // ── the family: every required member of the envelope, rejected before mutation ──

    /// <summary>
    /// One name per required member of the envelope - the top-level nested objects and the nested
    /// DTOs' own required members. Each is non-nullable with a <c>= new()</c> or <c>= []</c>
    /// initialiser, so only an explicit <c>null</c> in an IPC payload can produce one.
    /// </summary>
    public static TheoryData<string> RequiredMembers() =>
    [
        "main", "alt", "scrollback", "tabs",
        "saved_main", "saved_alt", "screen_main", "screen_alt", "saved_main.sgr",
        "sgr", "modes",
        "kitty_kbd", "kitty_kbd.main", "kitty_kbd.alt",
        "grapheme", "links",
    ];

    private static void NullOut(TerminalStateSnapshot snapshot, string member)
    {
        switch (member)
        {
            case "main": snapshot.Main = null!; break;
            case "alt": snapshot.Alt = null!; break;
            case "scrollback": snapshot.Scrollback = null!; break;
            case "tabs": snapshot.TabStops = null!; break;
            case "saved_main": snapshot.SavedCursorMain = null!; break;
            case "saved_alt": snapshot.SavedCursorAlt = null!; break;
            case "screen_main": snapshot.ScreenCursorMain = null!; break;
            case "screen_alt": snapshot.ScreenCursorAlt = null!; break;
            case "saved_main.sgr": snapshot.SavedCursorMain.Sgr = null!; break;
            case "sgr": snapshot.Sgr = null!; break;
            case "modes": snapshot.Modes = null!; break;
            case "kitty_kbd": snapshot.KittyKeyboard = null!; break;
            case "kitty_kbd.main": snapshot.KittyKeyboard.MainStack = null!; break;
            case "kitty_kbd.alt": snapshot.KittyKeyboard.AltStack = null!; break;
            case "grapheme": snapshot.Grapheme = null!; break;
            case "links": snapshot.Hyperlinks = null!; break;
            default: throw new ArgumentOutOfRangeException(nameof(member), member, "unknown member");
        }
    }

    /// <summary>
    /// Codex finding 2, generalised from the one field it named to every required member of the
    /// envelope. A required nested object arriving <c>null</c> is malformed structure, not an
    /// absent optional, and tolerating it was wrong in two distinct ways that this one theory
    /// pins at once: <c>modes</c> and the four cursor states <em>returned early</em>, so the
    /// destination kept its own insert mode, autowrap, mouse reporting and saved cursor while
    /// every screen around them was replaced; <c>sgr</c>, <c>tabs</c> and the kitty stacks
    /// substituted defaults, so the terminal adopted attributes the sender never described.
    /// </summary>
    /// <remarks>
    /// Falsifiable: restore any one of the tolerant branches this pass removed - the
    /// <c>if (source is null) return;</c> in <c>ImportModes</c>/<c>ImportCursor</c>, the
    /// <c>sgr ??= new()</c> in <c>ImportLiveSgrNoLock</c>, the <c>incoming?.Length ?? 0</c> in
    /// <c>ImportTabStopsNoLock</c>, the <c>?? []</c> on the kitty stacks, or the
    /// <c>entries is null</c> guard in <c>RebuildHyperlinks</c> - together with dropping that
    /// member's <c>RequirePresent</c>, and the corresponding case here imports cleanly: the
    /// <c>Assert.Throws</c> fails, and so do both destination assertions, because the screen was
    /// replaced with the source's.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public void BufferImport_RejectsANullRequiredMemberWithoutTouchingTheBuffer(string member)
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        NullOut(snapshot, member);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content\u001b[4h");
        Assert.True(target.Modes.IsInsertMode);

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));

        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
        Assert.True(target.Modes.IsInsertMode);
    }

    /// <summary>
    /// The same family seen through <see cref="TerminalStateTransfer.Restore"/>, which is the gap
    /// finding 1 named: <c>Restore</c> resizes the destination before importing, so a member it
    /// does not preflight is one whose rejection arrives after the destination has already been
    /// resized and reflowed. Preflighting a <em>selection</em> of the import's checks is what
    /// this replaced; it now runs the whole routine.
    /// </summary>
    /// <remarks>
    /// Falsifiable: narrow <c>Restore</c>'s preflight back to
    /// <c>ValidateVersionAndCellLayout</c> (its previous scope) and every case here fails on the
    /// size assertions, because the geometry differs and the resize runs before the import
    /// notices the null.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RequiredMembers))]
    public void Restore_RejectsANullRequiredMemberWithoutResizingOrTouchingTheBuffer(string member)
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        NullOut(snapshot, member);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    // ── finding 1: the rest of the import's checks, hoisted ahead of the resize ──

    /// <summary>
    /// The exact case the report named: the previous preflight hoisted only the version/layout
    /// and parser checks, so a buffer-only fault - here an unreadable cell blob - was still
    /// discovered by <c>DecodeBlob</c> <em>after</em> the resize had reflowed the destination.
    /// </summary>
    /// <remarks>
    /// Falsifiable: replace <c>Restore</c>'s <c>ValidateBufferState</c> call with the old
    /// <c>ValidateVersionAndCellLayout</c> and both size assertions fail - the destination is
    /// 100x30 by the time the decode refuses the payload.
    /// </remarks>
    [Fact]
    public void Restore_RejectsAMalformedCellBlobWithoutResizingOrTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Scrollback.CellsBase64 = "!!!! not base64 !!!!";

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>The same gap for the other value the report named: the sync-output timestamp.</summary>
    /// <remarks>Falsifiable exactly as the cell-blob case above.</remarks>
    [Fact]
    public void Restore_RejectsAnUnrepresentableSyncStartWithoutResizingOrTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.LastSyncStartUtcTicks = long.MaxValue;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    // ── structure the audit turned up: enum ordinals and table indices ──────────

    /// <summary>
    /// <c>CursorStyle</c> is an enum ordinal, which the policy calls structure: it selects a caret
    /// shape rather than sizing one, and an undefined value was cast straight through to
    /// <c>ModeState.CursorStyle</c> and then out to the renderer.
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the <c>Enum.IsDefined</c> check and the import succeeds, leaving
    /// <c>target.Modes.CursorStyle</c> holding ordinal 99.
    /// </remarks>
    [Fact]
    public void BufferImport_RejectsAnUndefinedCursorStyleWithoutTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Modes.CursorStyle = 99;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");
        CursorStyle before = target.Modes.CursorStyle;

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
        Assert.Equal(before, target.Modes.CursorStyle);
    }

    /// <summary>
    /// An index into the snapshot's own hyperlink table. Out of range it used to be silently
    /// mapped to "no link", which drops an identity the payload explicitly named - the quiet half
    /// of the same failure OSC 8 grouping already depends on.
    /// </summary>
    /// <remarks>
    /// Falsifiable: remove the <c>RequireInRange</c> and the import succeeds with
    /// <c>_currentHyperlink</c> silently null.
    /// </remarks>
    [Fact]
    public void BufferImport_RejectsACurrentHyperlinkIndexNamingNoTableEntry()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.CurrentHyperlinkId = snapshot.Hyperlinks.Count;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-cell half of the same index: a <c>LinkIds</c> value naming no table entry used to
    /// be skipped, so the restored cell lost a link the payload asserted it had.
    /// </summary>
    /// <remarks>
    /// Falsifiable: remove <c>ValidateSideTables</c>' bound check and the import succeeds with
    /// the decorated cell carrying no hyperlink at all.
    /// </remarks>
    [Fact]
    public void BufferImport_RejectsACellLinkIndexNamingNoTableEntry()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("\u001b]8;id=alpha;https://example.com\u001b\\link\u001b]8;;\u001b\\");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        Assert.NotNull(snapshot.Main.LinkIds);
        Assert.NotEmpty(snapshot.Main.LinkIds!);
        snapshot.Main.LinkIds![0] = snapshot.Hyperlinks.Count;

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The table's own entries. A null entry used to throw <see cref="NullReferenceException"/>
    /// from <c>RebuildHyperlinks</c> - the first statement <em>inside</em> the write lock - and a
    /// null <c>Uri</c> was quietly substituted with the empty string, minting an identity for a
    /// link that names nothing.
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the per-entry <c>RequirePresent</c> pair; the null-entry case then
    /// throws the wrong exception type and the null-URI case does not throw at all.
    /// </remarks>
    [Fact]
    public void BufferImport_RejectsAMalformedHyperlinkTableEntry()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("\u001b]8;id=alpha;https://example.com\u001b\\link\u001b]8;;\u001b\\");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        Assert.NotEmpty(snapshot.Hyperlinks);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        snapshot.Hyperlinks[0].Uri = null!;
        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));

        snapshot.Hyperlinks[0] = null!;
        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));

        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The side tables' string values. <c>SetExtendedText</c> treats a null cluster as a
    /// <em>removal</em>, so a null here silently dropped the grapheme the payload named rather
    /// than restoring it.
    /// </summary>
    /// <remarks>Falsifiable: remove the null check in <c>ValidateSideTables</c>.</remarks>
    [Fact]
    public void BufferImport_RejectsANullExtendedTextCluster()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Main.ExtendedText = new Dictionary<int, string> { [3] = null! };

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    // ── finding 3: import must not exceed the parser's own live bounds ───────────

    /// <summary>
    /// Codex finding 3. The live parser stops adding to an OSC/APC/DCS accumulator at
    /// <c>MaxStringSequenceChars</c>; the import copied the whole transferred string, so a payload
    /// from a source with a larger ceiling (or an IPC payload simply claiming one) left this
    /// parser holding an accumulator it could never have filled, and the next terminator would
    /// dispatch it.
    /// </summary>
    /// <remarks>
    /// Falsifiable: restore the three <c>AddRange</c> calls and each assertion below reads 40
    /// characters instead of 16.
    /// </remarks>
    [Fact]
    public void ParserImport_ClampsStringAccumulatorsToThisParsersStringSequenceCap()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.OscBuffer = new string('o', 40);
        exported.ApcBuffer = new string('a', 40);
        exported.DcsBuffer = new string('d', 40);

        (AnsiParser target, _) = NewPair();
        target.MaxStringSequenceChars = 16;
        target.ImportState(exported);

        AnsiParserState reExported = target.ExportState();
        Assert.Equal(new string('o', 16), reExported.OscBuffer);
        Assert.Equal(new string('a', 16), reExported.ApcBuffer);
        Assert.Equal(new string('d', 16), reExported.DcsBuffer);
    }

    /// <summary>
    /// The same bound on the one accumulator that overflows differently. The live path does not
    /// truncate the cross-chunk kitty payload: on the chunk that would exceed the cap it latches
    /// <c>_kittyPayloadOverflow</c> and frees the buffer, so the terminator skips a payload it
    /// knows is undecodable. Truncating on import would instead hand the terminator a prefix to
    /// base64-decode - the allocation the cap exists to prevent.
    /// </summary>
    /// <remarks>
    /// Falsifiable: <c>Append(state.KittyPayload)</c> unconditionally and the overflow assertion
    /// fails with a 40-character payload held.
    /// </remarks>
    [Fact]
    public void ParserImport_TurnsAnOverCapKittyPayloadIntoTheLatchedOverflowInstead()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.KittyPayload = new string('k', 40);
        exported.KittyPayloadOverflow = false;

        (AnsiParser target, _) = NewPair();
        target.MaxStringSequenceChars = 16;
        target.ImportState(exported);

        AnsiParserState reExported = target.ExportState();
        Assert.True(reExported.KittyPayloadOverflow);
        Assert.Equal(string.Empty, reExported.KittyPayload);
    }

    /// <summary>
    /// The paired invariant: a latched overflow always accompanies an emptied buffer in the live
    /// parser, so a payload claiming both is another state it could never have reached.
    /// </summary>
    /// <remarks>
    /// Falsifiable: append whenever the payload fits the cap, regardless of the flag, and the
    /// buffer comes back holding "partial".
    /// </remarks>
    [Fact]
    public void ParserImport_EmptiesTheKittyPayloadWhenTheOverflowFlagIsLatched()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.KittyPayload = "partial";
        exported.KittyPayloadOverflow = true;

        (AnsiParser target, _) = NewPair();
        target.ImportState(exported);

        AnsiParserState reExported = target.ExportState();
        Assert.True(reExported.KittyPayloadOverflow);
        Assert.Equal(string.Empty, reExported.KittyPayload);
    }

    /// <summary>
    /// The kitty control parameters only ever receive a parsed substring or the literal "1", so a
    /// null value is another state no stream produces - and one that used to reach
    /// <c>HandleKitty</c>'s dimension parsing rather than failing at the boundary.
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the per-value <c>RequirePresent</c> loop and the import succeeds, so the
    /// throw assertion fails; the parser-state assertion is what makes it a refuse-before-mutating
    /// test rather than merely a throwing one.
    /// </remarks>
    [Fact]
    public void ParserImport_RejectsANullKittyParameterWithoutTouchingTheParser()
    {
        (AnsiParser source, _) = NewPair();
        AnsiParserState exported = source.ExportState();
        exported.KittyPendingParams = new Dictionary<string, string> { ["m"] = null! };

        (AnsiParser target, _) = NewPair();
        target.Process("\u001b]0;target title");
        string before = target.ExportState().ToDebugString();

        Assert.Throws<InvalidOperationException>(() => target.ImportState(exported));
        Assert.Equal(before, target.ExportState().ToDebugString());
    }

    // ── the family's missed member: a blob must match the shape it declares ──────

    /// <summary>
    /// Valid base64 was as far as the preflight went, so a payload carrying fewer cell bytes than
    /// <c>cols * rows * cells_sizeof</c> - or a count that is not a whole number of cells - sailed
    /// through. <c>ImportScreenNoLock</c> then blanked the destination and copied only the rows
    /// that had actually arrived, turning a truncated snapshot into a partially erased terminal
    /// instead of a refusal. A blob's length is structure, not a count: it is reinterpreted whole,
    /// against the geometry the envelope declares, so there is no defensible "take what fits".
    /// </summary>
    /// <remarks>
    /// Falsifiable: drop the <c>ValidateCellBlobLength</c> calls from <c>ValidateBufferState</c>
    /// and the import succeeds - the throw assertion fails, and the row assertion fails with it
    /// because row 0 comes back blank rather than still holding the destination's own content.
    /// </remarks>
    [Fact]
    public void BufferImport_RejectsAMainCellBlobShorterThanTheDeclaredGeometry()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);

        // One row short of what 80x24 declares.
        snapshot.Main.CellsBase64 = DropTrailingBytes(
            snapshot.Main.CellsBase64, snapshot.CellsSizeOf * snapshot.Cols);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The alignment half of the same fault: a blob that is not a whole number of cells long.
    /// <see cref="System.Runtime.InteropServices.MemoryMarshal"/>'s cast drops the partial cell
    /// silently rather than complaining.
    /// </summary>
    /// <remarks>Falsifiable exactly as the main-screen case above.</remarks>
    [Fact]
    public void BufferImport_RejectsAnAlternateCellBlobThatIsNotCellAligned()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair();
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Alt.CellsBase64 = DropTrailingBytes(snapshot.Alt.CellsBase64, 1);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair();
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    /// <summary>
    /// The scrollback blob is measured against its own declared row count rather than the screen
    /// geometry, and is the case where the old behaviour was quietest: a short blob simply
    /// restored fewer history rows than the envelope said it carried.
    /// </summary>
    /// <remarks>Falsifiable exactly as the main-screen case above.</remarks>
    [Fact]
    public void BufferImport_RejectsAScrollbackBlobShorterThanItsDeclaredRowCount()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 20, rows: 4);
        for (int i = 0; i < 30; i++)
        {
            parser.Process($"line{i}\r\n");
        }

        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        Assert.True(snapshot.ScrollbackRowCount > 1);
        snapshot.Scrollback.CellsBase64 = DropTrailingBytes(
            snapshot.Scrollback.CellsBase64, snapshot.CellsSizeOf * snapshot.Cols);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 20, rows: 4);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(() => target.ImportState(snapshot));
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
        Assert.Equal(0, target.Scrollback.Count);
    }

    /// <summary>
    /// And through the supported attach entry point, where the cost of discovering it late is a
    /// destination that has already been resized and reflowed.
    /// </summary>
    /// <remarks>Falsifiable exactly as the main-screen case above.</remarks>
    [Fact]
    public void Restore_RejectsATruncatedCellBlobWithoutResizingOrTouchingTheBuffer()
    {
        (AnsiParser parser, TerminalBuffer source) = NewPair(cols: 100, rows: 30);
        parser.Process("source content");
        TerminalStateSnapshot snapshot = SnapshotOf(source, parser);
        snapshot.Main.CellsBase64 = DropTrailingBytes(
            snapshot.Main.CellsBase64, snapshot.CellsSizeOf * snapshot.Cols);

        (AnsiParser targetParser, TerminalBuffer target) = NewPair(cols: 40, rows: 12);
        targetParser.Process("target content");

        Assert.Throws<InvalidOperationException>(
            () => TerminalStateTransfer.Restore(target, targetParser, snapshot));

        Assert.Equal(40, target.Cols);
        Assert.Equal(12, target.Rows);
        Assert.StartsWith("target content", RowText(target, 0), StringComparison.Ordinal);
    }

    private static string DropTrailingBytes(string? base64, int count)
    {
        byte[] bytes = Convert.FromBase64String(
            base64 ?? throw new InvalidOperationException("export produced no cell blob"));
        return Convert.ToBase64String(bytes, 0, bytes.Length - count);
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
