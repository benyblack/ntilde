using System.Globalization;
using System.Text;
using Ntilde.VT;
using Ntilde.VT.Export;

namespace Ntilde.Mux.TextClient;

/// <summary>
/// Turns the model's buffer into output for a foreign terminal (Phase 3 spec §6.2). No I/O: the
/// caller writes what <see cref="Render"/> returns. The renderer is its buffer's only
/// CaptureRenderSnapshot consumer, so the snapshot's dirty spans describe exactly what changed since
/// the previous frame.
/// </summary>
/// <remarks>
/// Everything returned is composed here - cursor moves, erases, mode toggles from a fixed set, SGR and
/// cell text from the snapshot - never bytes copied from the session's stream. A device query the
/// shell emits (DA, CPR, XTGETTCAP) is consumed by the model's parser and so cannot reach the outer
/// terminal through this class.
/// </remarks>
public sealed class TextClientRenderer
{
    /// <summary>Written once on attach: the outer terminal's alternate screen, cleared.</summary>
    public const string EnterSequence = "\x1b[?1049h\x1b[H\x1b[2J";

    /// <summary>Written on every exit path: plain SGR, cursor shown, modes off, back to the outer main screen.</summary>
    public const string LeaveSequence = "\x1b[0m\x1b[?25h\x1b[?1l\x1b[?2004l\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1049l";

    private const string DetachHint = "Ctrl+\\ d to detach";

    private readonly TerminalBuffer _buffer;
    private bool _haveFrame;
    private int _lastConsoleCols, _lastConsoleRows, _lastSessionCols, _lastSessionRows, _lastTop;
    private bool _lastAltScreen;
    private string? _lastStatus;
    private (int Row, int Col) _lastCursor = (-1, -1);
    private bool? _appCursor, _bracketed, _cursorVisible, _mouse1000, _mouse1002, _mouse1003, _mouse1006;

    public TextClientRenderer(TerminalBuffer buffer) => _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    /// <summary>Read-only attach: a permanent status line says so.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The next <see cref="Render"/> repaints everything (console resized, or anything else invalidated the outer screen).</summary>
    /// <remarks>Also forgets every emitted mode, so the frame re-states them all after the outer terminal was reset.</remarks>
    public void Invalidate()
    {
        _haveFrame = false;
        _appCursor = _bracketed = _cursorVisible = _mouse1000 = _mouse1002 = _mouse1003 = _mouse1006 = null;
        _lastCursor = (-1, -1);
    }

    /// <summary>The bytes that bring the outer terminal up to date; empty when nothing changed.</summary>
    public string Render(int consoleCols, int consoleRows)
    {
        consoleCols = Math.Max(1, consoleCols);
        consoleRows = Math.Max(1, consoleRows);
        using TerminalRenderSnapshot snap = _buffer.CaptureRenderSnapshot(
            new RenderSnapshotRequest { ViewportRows = _buffer.Rows, ViewportCols = _buffer.Cols }, out _);
        int sessionRows = snap.ViewportRows;
        int sessionCols = snap.ViewportCols;
        string? status = BuildStatus(sessionCols, sessionRows, consoleCols, consoleRows);
        // A one-row console with a status line shows only the status line: it is what explains the
        // missing session, and a session row would have to share its single row.
        int visibleRows = consoleRows - (status is null ? 0 : 1);
        int visibleCols = consoleCols;
        int top = visibleRows == 0 ? 0 : Math.Clamp(snap.CursorRow - visibleRows + 1, 0, Math.Max(0, sessionRows - visibleRows));
        bool alt = _buffer.IsAltScreenActive;

        bool full = !_haveFrame
            || consoleCols != _lastConsoleCols || consoleRows != _lastConsoleRows
            || sessionCols != _lastSessionCols || sessionRows != _lastSessionRows
            || top != _lastTop || alt != _lastAltScreen || !string.Equals(status, _lastStatus, StringComparison.Ordinal);

        var sb = new StringBuilder();
        bool painted = false;
        if (full)
        {
            sb.Append("\x1b[?25l\x1b[H\x1b[0m\x1b[2J");
            _cursorVisible = false;
            int rows = Math.Min(visibleRows, sessionRows - top);
            for (int r = 0; r < rows; r++) AppendRow(sb, snap, top + r, r, visibleCols, clearFirst: false);
            if (status is not null)
            {
                sb.Append(Cup(consoleRows, 1)).Append("\x1b[0;7m").Append(status).Append(AnsiCellWriter.Reset);
            }

            painted = true;
        }
        else if (snap.DirtySpans.Array is { } spans)
        {
            var dirtyRows = new SortedSet<int>();
            for (int i = 0; i < snap.DirtySpans.Length; i++)
            {
                int row = spans[i].Row;
                if (row >= top && row < top + visibleRows) dirtyRows.Add(row);
            }

            if (dirtyRows.Count > 0 && _cursorVisible == true)
            {
                // No cursor flickering along the repainted rows; it is shown again at its place below.
                sb.Append("\x1b[?25l");
                _cursorVisible = false;
            }

            foreach (int row in dirtyRows) AppendRow(sb, snap, row, row - top, visibleCols, clearFirst: true);
            painted = dirtyRows.Count > 0;
        }

        AppendModes(sb, unclipped: top == 0 && sessionCols <= visibleCols && sessionRows <= visibleRows);

        int cursorRow = snap.CursorRow - top;
        int cursorCol = Math.Min(snap.CursorCol, visibleCols - 1);
        bool cursorShown = _buffer.Modes.IsCursorVisible && cursorRow >= 0 && cursorRow < visibleRows && snap.CursorCol < visibleCols;
        if (cursorShown && (painted || (cursorRow, cursorCol) != _lastCursor)) sb.Append(Cup(cursorRow + 1, cursorCol + 1));
        _lastCursor = cursorShown ? (cursorRow, cursorCol) : (-1, -1);
        if (_cursorVisible != cursorShown)
        {
            sb.Append(cursorShown ? "\x1b[?25h" : "\x1b[?25l");
            _cursorVisible = cursorShown;
        }

        _haveFrame = true;
        (_lastConsoleCols, _lastConsoleRows, _lastSessionCols, _lastSessionRows, _lastTop) = (consoleCols, consoleRows, sessionCols, sessionRows, top);
        _lastAltScreen = alt;
        _lastStatus = status;
        return sb.ToString();
    }

    /// <summary>
    /// The status row: shown when read-only or when the session grid exceeds the console. "Resize to
    /// fit" appears only in the second case; the detach hint is always part of it and is never the
    /// part cut to fit the console width: the text before it is shortened with an ellipsis instead.
    /// Every character is one column wide, so a length is a column count.
    /// </summary>
    private string? BuildStatus(int sessionCols, int sessionRows, int consoleCols, int consoleRows)
    {
        const string Separator = " · ";
        bool tooBig = sessionCols > consoleCols || sessionRows > consoleRows;
        if (!tooBig && !ReadOnly) return null;
        string head = (ReadOnly, tooBig) switch
        {
            (true, false) => "read-only",
            (true, true) => "read-only" + Separator + SizeMismatch(),
            _ => SizeMismatch(),
        };

        if (head.Length + Separator.Length + DetachHint.Length <= consoleCols) return head + Separator + DetachHint;
        int room = consoleCols - Separator.Length - DetachHint.Length;
        if (room < 2) return DetachHint.Length <= consoleCols ? DetachHint : DetachHint[..consoleCols];
        return head[..(room - 1)] + "…" + Separator + DetachHint;

        string SizeMismatch() => string.Create(CultureInfo.InvariantCulture,
            $"session is {sessionCols}x{sessionRows}, this terminal is {consoleCols}x{consoleRows} — resize to fit");
    }

    /// <summary>
    /// One session row at one screen row. An incremental repaint erases the line FIRST (<c>CSI 2K</c>):
    /// an <c>EL</c> after the text would erase the last cell when the row fills to the pending-wrap column.
    /// </summary>
    private static void AppendRow(StringBuilder sb, in TerminalRenderSnapshot snap, int sessionRow, int screenRow, int visibleCols, bool clearFirst)
    {
        sb.Append(Cup(screenRow + 1, 1));
        if (clearFirst) sb.Append("\x1b[0m\x1b[2K");
        RenderRowSnapshot row = snap.RowsData.Array![sessionRow];
        if (row.Cells is { Length: > 0 } cells) AnsiCellWriter.AppendRow(sb, cells, Math.Min(visibleCols, row.Cols));
    }

    /// <summary>Only on change against what was last EMITTED (unknown at first, so the first frame states each one).</summary>
    private void AppendModes(StringBuilder sb, bool unclipped)
    {
        ModeState m = _buffer.Modes;
        Toggle(sb, ref _appCursor, m.IsApplicationCursorKeys, 1);
        Toggle(sb, ref _bracketed, m.IsBracketedPasteMode, 2004);
        // Mirrored only while the window is unclipped: the outer terminal's mouse reports then carry
        // the inner coordinates and go through as ordinary input (spec §6.6).
        Toggle(sb, ref _mouse1000, unclipped && m.MouseModeX10, 1000);
        Toggle(sb, ref _mouse1002, unclipped && m.MouseModeButtonEvent, 1002);
        Toggle(sb, ref _mouse1003, unclipped && m.MouseModeAnyEvent, 1003);
        Toggle(sb, ref _mouse1006, unclipped && m.MouseModeSGR, 1006);
    }

    private static void Toggle(StringBuilder sb, ref bool? last, bool want, int mode)
    {
        if (last == want) return;
        sb.Append("\x1b[?").Append(mode.ToString(CultureInfo.InvariantCulture)).Append(want ? 'h' : 'l');
        last = want;
    }

    private static string Cup(int row, int col) => string.Create(CultureInfo.InvariantCulture, $"\x1b[{row};{col}H");
}
