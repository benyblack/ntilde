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
/// The specification of what "same terminal state" means - every field of the exported state,
/// compared. A field left out here is a field the snapshot is free to get wrong. Shared by the
/// Phase 0 parity suite and the Phase 1 mux suite.
/// </summary>
internal static class TerminalStateAssert
{
    /// <summary>
    /// The specification of what parity means: every field of the exported state, compared. A
    /// field left out here is a field the snapshot is free to get wrong.
    /// </summary>
    public static void AssertEquivalent(
        string where,
        TerminalBuffer expectedBuffer,
        AnsiParser expectedParser,
        TerminalBuffer actualBuffer,
        AnsiParser actualParser)
    {
        // The rendered active screen, which is what a divergence actually looks like to a user.
        BufferSnapshot sa = BufferSnapshot.Capture(expectedBuffer, includeAttributes: true);
        BufferSnapshot sc = BufferSnapshot.Capture(actualBuffer, includeAttributes: true);
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
        TerminalStateSnapshot ea = expectedBuffer.ExportState(int.MaxValue);
        TerminalStateSnapshot ec = actualBuffer.ExportState(int.MaxValue);

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

        AssertEqual($"{where} parser state", expectedParser.ExportState().ToDebugString(),
            actualParser.ExportState().ToDebugString());
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
    public static void AssertEqual<T>(string what, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Assert.Fail($"{what} diverged.\n  expected: {expected}\n  actual:   {actual}");
        }
    }

    /// <summary>Compares row arrays and names the first differing row, with both sides.</summary>
    public static void AssertLinesEqual(string what, string[] expected, string[] actual)
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
                    $"{what}: row {i} diverged.\n  expected: {expected[i]}\n  actual:   {actual[i]}");
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
