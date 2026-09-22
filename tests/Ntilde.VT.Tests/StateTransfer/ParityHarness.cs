using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.VT.Tests.StateTransfer;

/// <summary>
/// One parser+buffer pair driven a byte at a time, tracking what a decoder would be holding back.
/// </summary>
/// <remarks>
/// <para>
/// Byte-at-a-time is the point, not a simplification: it makes every byte offset a legal cut
/// point and exercises mid-sequence chunking on every corpus for free. The pending-tail tracking
/// is here rather than in <c>Utf8ChunkDecoder</c> because Ntilde.VT.Tests must not reference
/// Ntilde.Pty - the decoder's own equivalence is pinned separately by
/// <c>Utf8ChunkDecoderTests</c>.
/// </para>
/// <para>
/// It tracks the tail with <see cref="Utf8.ToUtf16"/> rather than by watching whether
/// <c>Encoding.UTF8.GetDecoder().GetChars</c> emitted anything, because "emitted a char" does not
/// mean "is holding nothing back". On <c>C3 C3</c> the framework decoder emits one U+FFFD for the
/// first byte while still carrying the second, so a tail cleared on every non-empty emission
/// would record a stream position that is one byte too far - and the parity run restored from it
/// would legitimately disagree with the continuous run, a manufactured failure with no bug
/// behind it. <c>Utf8.ToUtf16</c> reports <c>bytesRead</c>, which is the thing actually needed,
/// and is exactly what the production <c>Utf8ChunkDecoder</c> is built on.
/// </para>
/// </remarks>
internal sealed class ParityRun
{
    /// <summary>Longest incomplete-but-valid UTF-8 prefix: a 4-byte sequence missing its last byte.</summary>
    private const int MaxTailLength = 3;

    private readonly byte[] _tail = new byte[MaxTailLength + 1];
    private readonly char[] _chars = new char[8];
    private int _tailLength;

    public ParityRun(int cols, int rows, bool forceConPtyFiltering)
    {
        Buffer = new TerminalBuffer(cols, rows);
        Parser = new AnsiParser(Buffer, forceConPtyFiltering) { ImageDecoder = null };
        Parser.OnResponse = r => Responses.Add(r);
    }

    public TerminalBuffer Buffer { get; }

    public AnsiParser Parser { get; }

    public List<string> Responses { get; } = new();

    /// <summary>Bytes turned into chars so far; excludes <see cref="PendingTail"/>.</summary>
    public long ConsumedBytes { get; private set; }

    /// <summary>The incomplete UTF-8 prefix the decoder is holding.</summary>
    public byte[] PendingTail => _tail.AsSpan(0, _tailLength).ToArray();

    /// <summary>
    /// Feeds <paramref name="corpus"/> from <paramref name="from"/> (inclusive) to
    /// <paramref name="to"/> (exclusive), applying every resize in <paramref name="resizes"/>
    /// whose offset falls in that range at the moment the feed reaches it.
    /// </summary>
    /// <remarks>
    /// Deliberately half-open, with no resize applied at the <paramref name="to"/> boundary. A
    /// resize sitting exactly on a cut point would otherwise fire twice in the continuous run -
    /// once as <c>to</c> in <c>Feed(0, cut)</c> and again as <c>i</c> in <c>Feed(cut, len)</c> -
    /// while the restored run fires it once. A resize at <c>corpus.Length</c> therefore never
    /// fires at all, which is harmless: nothing is parsed after the last byte.
    /// </remarks>
    public void Feed(byte[] corpus, int from, int to, (int Offset, int Cols, int Rows)[] resizes)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(resizes);

        for (int i = from; i < to; i++)
        {
            foreach ((int _, int cols, int rows) in resizes.Where(r => r.Offset == i))
            {
                Buffer.Resize(cols, rows);
            }

            _tail[_tailLength++] = corpus[i];

            Utf8.ToUtf16(
                _tail.AsSpan(0, _tailLength),
                _chars,
                out int bytesRead,
                out int charsWritten,
                replaceInvalidSequences: true,
                isFinalBlock: false);

            if (bytesRead > 0)
            {
                ConsumedBytes += bytesRead;
                for (int j = bytesRead; j < _tailLength; j++)
                {
                    _tail[j - bytesRead] = _tail[j];
                }

                _tailLength -= bytesRead;
            }

            if (_tailLength > MaxTailLength)
            {
                // An incomplete UTF-8 prefix is at most 3 bytes, so this cannot happen. If it
                // ever does, the stream offset the snapshot records is wrong and silence here
                // would turn that into an unexplainable parity failure much later.
                throw new InvalidOperationException(
                    $"UTF-8 carry of {_tailLength} bytes exceeds the {MaxTailLength}-byte maximum " +
                    $"at corpus offset {i}; the harness's tail invariant is broken.");
            }

            if (charsWritten > 0)
            {
                Parser.Process(new string(_chars, 0, charsWritten));
            }
        }
    }
}

/// <summary>
/// Proves that a pane attaching mid-stream - snapshot, then parse the rest of the bytes itself -
/// reaches exactly the state it would have reached by parsing the whole stream from the start.
/// </summary>
internal static class ParityHarness
{
    /// <summary>
    /// Proves "snapshot + tail" equals "continuous" for one corpus stream.
    /// </summary>
    /// <param name="name">Corpus name, quoted in every failure message.</param>
    /// <param name="corpus">The byte stream.</param>
    /// <param name="cutPoints">Byte offsets at which to snapshot and resume.</param>
    /// <param name="resizes">(byte offset, cols, rows) applied to every run at that offset.</param>
    /// <param name="forceConPtyFiltering">
    /// Passed to every <see cref="AnsiParser"/> so the result does not depend on the host OS.
    /// </param>
    public static void AssertSnapshotTailParity(
        string name,
        byte[] corpus,
        int[] cutPoints,
        (int Offset, int Cols, int Rows)[] resizes,
        bool forceConPtyFiltering)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(cutPoints);
        ArgumentNullException.ThrowIfNull(resizes);

        foreach (int cut in cutPoints)
        {
            // A: the whole stream, continuously. Rebuilt per cut point so "responses after the
            // cut" can be isolated without replaying bookkeeping.
            var a = new ParityRun(80, 24, forceConPtyFiltering);
            a.Feed(corpus, 0, cut, resizes);
            int responsesBeforeCut = a.Responses.Count;
            a.Feed(corpus, cut, corpus.Length, resizes);
            List<string> responsesAfterCut = a.Responses.Skip(responsesBeforeCut).ToList();

            // B: the prefix only, then snapshot.
            var b = new ParityRun(80, 24, forceConPtyFiltering);
            b.Feed(corpus, 0, cut, resizes);

            TerminalStateSnapshot snapshot = b.Buffer.ExportState(int.MaxValue);
            snapshot.Parser = b.Parser.ExportState();
            snapshot.DecoderTail = b.PendingTail;
            snapshot.StreamSeq = b.ConsumedBytes;

            if (snapshot.StreamSeq + snapshot.DecoderTail.Length != cut)
            {
                Assert.Fail(
                    $"[{name} @ {cut}] snapshot claims stream position " +
                    $"{snapshot.StreamSeq}+{snapshot.DecoderTail.Length} but the cut was at {cut}.");
            }

            // C: restored from the snapshot, then fed the tail INCLUDING the pending bytes.
            // Constructed at the SNAPSHOT's dimensions, not a hardcoded 80x24: ImportState does
            // not resize, and a width mismatch degrades silently instead of throwing, so a
            // resize-interleaved run would otherwise compare two different geometries and call
            // it parity.
            var c = new ParityRun(b.Buffer.Cols, b.Buffer.Rows, forceConPtyFiltering);
            c.Buffer.ImportState(snapshot);
            c.Parser.ImportState(snapshot.Parser);
            c.Feed(corpus, (int)snapshot.StreamSeq, corpus.Length, resizes);

            AssertEquivalent(name, cut, a, responsesAfterCut, c);
        }
    }

    /// <summary>
    /// The specification of what parity means: every field of the exported state, compared. A
    /// field left out here is a field the snapshot is free to get wrong.
    /// </summary>
    private static void AssertEquivalent(
        string name, int cut, ParityRun a, List<string> responsesAfterCut, ParityRun c)
    {
        string where = $"[{name} @ cut {cut}]";

        // The rendered active screen, which is what a divergence actually looks like to a user.
        BufferSnapshot sa = BufferSnapshot.Capture(a.Buffer, includeAttributes: true);
        BufferSnapshot sc = BufferSnapshot.Capture(c.Buffer, includeAttributes: true);
        AssertLinesEqual($"{where} screen text", sa.Lines, sc.Lines);
        AssertLinesEqual($"{where} cell attributes", sa.AttributeLines!, sc.AttributeLines!);

        if (sa.CursorCol != sc.CursorCol || sa.CursorRow != sc.CursorRow
            || sa.IsAltScreen != sc.IsAltScreen)
        {
            Assert.Fail(
                $"{where} cursor/screen: A=({sa.CursorCol},{sa.CursorRow},alt={sa.IsAltScreen}) " +
                $"C=({sc.CursorCol},{sc.CursorRow},alt={sc.IsAltScreen})");
        }

        // Everything BufferSnapshot cannot see: the inactive screen, scrollback, and every
        // non-cell field. Compared through ExportState because it is the one view that covers
        // both screens without switching screens - switching would mutate what is under test.
        TerminalStateSnapshot ea = a.Buffer.ExportState(int.MaxValue);
        TerminalStateSnapshot ec = c.Buffer.ExportState(int.MaxValue);

        // Geometry first: ImportState does not resize, so a silent geometry drift would make
        // every cell comparison below compare arrays that were never comparable.
        AssertEqual($"{where} cols", ea.Cols, ec.Cols);
        AssertEqual($"{where} rows", ea.Rows, ec.Rows);
        AssertEqual($"{where} alt screen active", ea.IsAltScreenActive, ec.IsAltScreenActive);

        AssertScreenEqual($"{where} main screen", ea.Main, ec.Main);
        AssertScreenEqual($"{where} alt screen", ea.Alt, ec.Alt);
        AssertScreenEqual($"{where} scrollback", ea.Scrollback, ec.Scrollback);
        AssertEqual($"{where} scrollback row count", ea.ScrollbackRowCount, ec.ScrollbackRowCount);
        AssertEqual($"{where} scrollback truncated", ea.ScrollbackTruncated, ec.ScrollbackTruncated);
        AssertEqual($"{where} scrollback rows dropped", ea.ScrollbackRowsDropped, ec.ScrollbackRowsDropped);

        AssertEqual($"{where} cursor col", ea.CursorCol, ec.CursorCol);
        AssertEqual($"{where} cursor row", ea.CursorRow, ec.CursorRow);
        AssertEqual($"{where} pending wrap", ea.IsPendingWrap, ec.IsPendingWrap);
        AssertEqual($"{where} scroll region top", ea.ScrollTop, ec.ScrollTop);
        AssertEqual($"{where} scroll region bottom", ea.ScrollBottom, ec.ScrollBottom);
        AssertEqual($"{where} tab stops", Describe(ea.TabStops), Describe(ec.TabStops));

        AssertEqual($"{where} saved cursor (main)", Describe(ea.SavedCursorMain), Describe(ec.SavedCursorMain));
        AssertEqual($"{where} saved cursor (alt)", Describe(ea.SavedCursorAlt), Describe(ec.SavedCursorAlt));
        AssertEqual($"{where} screen cursor (main)", Describe(ea.ScreenCursorMain), Describe(ec.ScreenCursorMain));
        AssertEqual($"{where} screen cursor (alt)", Describe(ea.ScreenCursorAlt), Describe(ec.ScreenCursorAlt));
        AssertEqual($"{where} restore main cursor on alt exit",
            ea.RestoreMainCursorOnAltExit, ec.RestoreMainCursorOnAltExit);

        AssertEqual($"{where} sgr", Describe(ea.Sgr), Describe(ec.Sgr));
        AssertEqual($"{where} modes", Describe(ea.Modes), Describe(ec.Modes));
        AssertEqual($"{where} kitty keyboard", Describe(ea.KittyKeyboard), Describe(ec.KittyKeyboard));

        AssertEqual($"{where} synchronized output", ea.IsSynchronizedOutput, ec.IsSynchronizedOutput);

        // Presence only, never equality: LastSyncStartUtcTicks is a wall-clock reading and the
        // two runs take it at different instants, so comparing the values is a guaranteed flake.
        AssertEqual($"{where} synchronized-output start present",
            ea.LastSyncStartUtcTicks != 0, ec.LastSyncStartUtcTicks != 0);

        AssertEqual($"{where} grapheme continuation", Describe(ea.Grapheme), Describe(ec.Grapheme));

        // Hyperlinks compare as an ordered list of (Uri, Id) pairs, and the per-cell LinkIds maps
        // (in AssertScreenEqual) as key -> index. Together those say "the same cells point at the
        // same link identities", which is what OSC 8 grouping means. Comparing Hyperlink
        // instances would always fail - A's and C's are necessarily different objects.
        AssertEqual($"{where} hyperlink table", Describe(ea.Hyperlinks), Describe(ec.Hyperlinks));
        AssertEqual($"{where} current hyperlink", ea.CurrentHyperlinkId, ec.CurrentHyperlinkId);

        AssertEqual($"{where} parser state", a.Parser.ExportState().ToDebugString(),
            c.Parser.ExportState().ToDebugString());

        AssertLinesEqual($"{where} device replies", responsesAfterCut.ToArray(), c.Responses.ToArray());
    }

    /// <summary>All four per-screen side tables, each named separately so a failure says which.</summary>
    private static void AssertScreenEqual(string what, TerminalScreenState expected, TerminalScreenState actual)
    {
        AssertEqual($"{what} cells", expected.CellsBase64, actual.CellsBase64);
        AssertEqual($"{what} row wraps", Describe(expected.RowWraps), Describe(actual.RowWraps));
        AssertEqual($"{what} extended text",
            DescribeMap(expected.ExtendedText), DescribeMap(actual.ExtendedText));
        AssertEqual($"{what} link ids", DescribeMap(expected.LinkIds), DescribeMap(actual.LinkIds));
    }

    /// <summary>
    /// Equality with a message that says what diverged and what both sides held. xUnit's own
    /// Assert.Equal has no user-message overload, and a parity failure reading only
    /// "Assert.Equal() Failure" costs an hour of bisecting cut points by hand.
    /// </summary>
    private static void AssertEqual<T>(string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{what} diverged.\n  continuous: {expected}\n  restored:   {actual}");
        }
    }

    /// <summary>Compares row arrays and names the first differing row, with both sides.</summary>
    private static void AssertLinesEqual(string what, string[] expected, string[] actual)
    {
        if (expected.Length != actual.Length)
        {
            Assert.Fail($"{what}: row count {expected.Length} vs {actual.Length}");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"{what}: row {i} diverged.\n  continuous: {expected[i]}\n  restored:   {actual[i]}");
            }
        }
    }

    private static string Describe(bool[]? flags)
    {
        if (flags is null)
        {
            return "<null>";
        }

        var sb = new StringBuilder(flags.Length);
        foreach (bool flag in flags)
        {
            sb.Append(flag ? '1' : '0');
        }

        return sb.ToString();
    }

    private static string DescribeMap<TValue>(Dictionary<int, TValue>? map)
    {
        if (map is null)
        {
            return "<null>";
        }

        // Sorted, not in dictionary order: Dictionary enumeration order is unspecified, so an
        // unsorted rendering would make two identical maps occasionally disagree - a parity
        // failure indistinguishable from a real divergence.
        List<int> keys = map.Keys.ToList();
        keys.Sort();

        var sb = new StringBuilder();
        foreach (int key in keys)
        {
            sb.Append(key.ToString(CultureInfo.InvariantCulture))
              .Append("=>")
              .Append(map[key]?.ToString() ?? "<null>")
              .Append(';');
        }

        return sb.ToString();
    }

    private static string Describe(List<TerminalHyperlinkEntry> links)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < links.Count; i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture))
              .Append(":(")
              .Append(links[i].Uri)
              .Append(", ")
              .Append(links[i].Id ?? "<null>")
              .Append(") ");
        }

        return sb.ToString();
    }

    private static string Describe(TerminalCursorState cursor) =>
        $"row={cursor.Row} col={cursor.Col} pw={cursor.IsPendingWrap} sgr[{Describe(cursor.Sgr)}]";

    private static string Describe(TerminalSgrState sgr) =>
        $"fg={sgr.Foreground} bg={sgr.Background} fgi={sgr.FgIndex} bgi={sgr.BgIndex} " +
        $"dfg={sgr.IsDefaultForeground} dbg={sgr.IsDefaultBackground} inv={sgr.IsInverse} " +
        $"bold={sgr.IsBold} faint={sgr.IsFaint} italic={sgr.IsItalic} ul={sgr.IsUnderline} " +
        $"blink={sgr.IsBlink} strike={sgr.IsStrikethrough} hidden={sgr.IsHidden}";

    private static string Describe(TerminalModeState modes) =>
        $"x10={modes.MouseModeX10} btn={modes.MouseModeButtonEvent} any={modes.MouseModeAnyEvent} " +
        $"sgrMouse={modes.MouseModeSGR} ckm={modes.IsApplicationCursorKeys} awm={modes.IsAutoWrapMode} " +
        $"decom={modes.IsOriginMode} focus={modes.IsFocusEventReporting} bp={modes.IsBracketedPasteMode} " +
        $"cv={modes.IsCursorVisible} cblink={modes.IsCursorBlinkEnabled} cstyle={modes.CursorStyle} " +
        $"irm={modes.IsInsertMode} lnm={modes.IsLineFeedNewLineMode} srm={modes.IsEchoEnabled}";

    private static string Describe(TerminalKittyKeyboardState kitty) =>
        $"main=[{string.Join(',', kitty.MainStack)}] alt=[{string.Join(',', kitty.AltStack)}] " +
        $"altActive={kitty.IsAltScreenActive}";

    private static string Describe(TerminalGraphemeState grapheme) =>
        $"hi={DescribeCodeUnits(grapheme.HighSurrogate)} col={grapheme.LastCharCol} " +
        $"row={grapheme.LastCharRow} zwj={grapheme.IsAfterZwj}";

    /// <summary>
    /// Renders as hex code units rather than as text: the value is typically a lone surrogate,
    /// which is not representable in a test runner's XML output and would be mangled on the way
    /// into a failure message.
    /// </summary>
    private static string DescribeCodeUnits(string? text)
    {
        if (text is null)
        {
            return "<null>";
        }

        var sb = new StringBuilder();
        foreach (char ch in text)
        {
            sb.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
