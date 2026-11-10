using System.Text;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>Checksum weight of a bold cell (xterm's DECRQCRA attribute weights).</summary>
    internal const int ChecksumBold = 0x80;
    /// <summary>Checksum weight of a blinking cell.</summary>
    internal const int ChecksumBlink = 0x40;
    /// <summary>Checksum weight of a reverse-video cell.</summary>
    internal const int ChecksumInverse = 0x20;
    /// <summary>Checksum weight of an underlined cell (any underline style).</summary>
    internal const int ChecksumUnderline = 0x10;

    /// <summary>
    /// The DECRQCRA checksum of the zero-based inclusive page rectangle: the negated 16-bit sum, over every cell, of its
    /// character's Unicode scalar value plus the weights of its bold, blink, reverse and underline attributes. A blank cell
    /// (never written or erased) counts as a space. A wide glyph counts once, at its lead cell; its continuation cell adds
    /// nothing. A cell with combining marks counts its base character only. Colors, faint, italic, hidden, strikethrough,
    /// overline, hyperlinks and DECSCA protection add nothing. Coordinates are clamped to the page; an inverted rectangle
    /// sums nothing (checksum 0).
    /// </summary>
    internal ushort RectangleChecksum(int left, int top, int right, int bottom)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Cols < 1 || Rows < 1) return 0;
            left = Math.Clamp(left, 0, Cols - 1);
            right = Math.Clamp(right, 0, Cols - 1);
            top = Math.Clamp(top, 0, Rows - 1);
            bottom = Math.Clamp(bottom, 0, Rows - 1);
            // Wrapping int arithmetic keeps the sum exact modulo 2^16, which is all the reply carries.
            int total = 0;
            if (left <= right && top <= bottom)
            {
                for (int rowIndex = top; rowIndex <= bottom; rowIndex++)
                {
                    TerminalRow row = _viewport[rowIndex];
                    for (int col = left; col <= right; col++) total = unchecked(total + CellChecksum(row, col));
                }
            }
            return unchecked((ushort)(-total));
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>One cell's checksum contribution; see <see cref="RectangleChecksum"/>.</summary>
    private static int CellChecksum(TerminalRow row, int col)
    {
        TerminalCell cell = row.Cells[col];
        if (cell.IsWideContinuation) return 0;
        int value = cell.Character is '\0' ? ' ' : cell.Character;
        if (cell.HasExtendedText && row.GetExtendedText(col) is { Length: > 0 } text
            && Rune.DecodeFromUtf16(text, out Rune rune, out _) == System.Buffers.OperationStatus.Done)
        {
            value = rune.Value;
        }
        if (cell.IsBold) value += ChecksumBold;
        if (cell.IsBlink) value += ChecksumBlink;
        if (cell.IsInverse) value += ChecksumInverse;
        if (cell.IsUnderline) value += ChecksumUnderline;
        return value;
    }
}
