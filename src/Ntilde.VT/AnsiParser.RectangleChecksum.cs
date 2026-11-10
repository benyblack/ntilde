using System.Globalization;

namespace Ntilde.VT;

public partial class AnsiParser
{
    /// <summary>
    /// Host-settable gate for DECRQCRA. Off by default, like xterm's window-ops gate: a checksum of a one-cell rectangle names the
    /// character in that cell, so a program answered cell by cell can read back anything on the screen, including output it did
    /// not write (an earlier command's printed token). While off the request is consumed and never answered.
    /// </summary>
    public bool AllowRectangleChecksumReport { get; set; }

    /// <summary>
    /// DECRQCRA (<c>CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y</c>): replies <c>DCS Pi ! ~ XXXX ST</c>, the 16-bit checksum of the
    /// rectangle as four uppercase hex digits. Only the exact identity is admitted (no leader, the single <c>*</c> intermediate,
    /// at most six plain parameters). An omitted <c>Pi</c> is 0; <c>Pg</c> is not interpreted (one page). The rectangle uses the
    /// DECERA defaults, origin translation and page clipping; a rectangle whose top is below its bottom or whose left is right
    /// of its right sums nothing. See <see cref="TerminalBuffer.RectangleChecksum"/> for the per-cell rule.
    /// </summary>
    private void RequestRectangleChecksum(in CsiSequence csi)
    {
        if (csi.Leader != '\0' || !csi.Intermediates.SequenceEqual("*") || csi.Args.Length > 6 || csi.Separators.Contains(':')) return;
        if (!AllowRectangleChecksumReport || OnResponse is null || _buffer.Rows < 1 || _buffer.Cols < 1) return;
        ReadOnlySpan<int> args = csi.Args;
        int id = args.Length > 0 ? Math.Max(0, args[0]) : 0;
        ReadOnlySpan<int> rect = args.Length > 2 ? args[2..] : [];
        int origin = _buffer.Modes.IsOriginMode ? _buffer.ScrollTop : 0;
        int top = RectangleCoordinate(rect, 0, 1, _buffer.Rows, origin);
        int left = RectangleCoordinate(rect, 1, 1, _buffer.Cols, 0);
        int bottom = RectangleCoordinate(rect, 2, _buffer.Rows, _buffer.Rows, origin);
        int right = RectangleCoordinate(rect, 3, _buffer.Cols, _buffer.Cols, 0);
        ushort checksum = _buffer.RectangleChecksum(left, top, right, bottom);
        OnResponse(string.Create(CultureInfo.InvariantCulture, $"\x1bP{id}!~{checksum:X4}\x1b\\"));
    }
}
