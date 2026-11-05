namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// Erases an inclusive zero-based text rectangle using the existing erase colors and normal cell attributes.
    /// Cursor, pen, row wrapping and the image plane are untouched. Intersected wide/scaled text stays atomic.
    /// </summary>
    internal void EraseRectangularArea(int left, int top, int right, int bottom)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Cols < 1 || Rows < 1) return;
            left = Math.Clamp(left, 0, Cols - 1);
            right = Math.Clamp(right, 0, Cols - 1);
            top = Math.Clamp(top, 0, Rows - 1);
            bottom = Math.Clamp(bottom, 0, Rows - 1);
            if (left > right || top > bottom) return;
            int first = TextSizingAbsoluteRow(top), end = TextSizingAbsoluteRow(bottom) + 1;
            EraseTextSizingRange(left, right + 1, first, end);
            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
            for (int rowIndex = top; rowIndex <= bottom; rowIndex++)
            {
                EraseRectangularRow(_viewport[rowIndex], left, right, blank);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
    }

    /// <summary>Expands only split ordinary-wide edges, then applies one erase blank and complete side-table cleanup to the row.</summary>
    private void EraseRectangularRow(TerminalRow row, int left, int right, TerminalCell blank)
    {
        if (left > 0 && row.Cells[left].IsWideContinuation && row.Cells[left - 1].IsWide) left--;
        if (right + 1 < Cols && row.Cells[right].IsWide && row.Cells[right + 1].IsWideContinuation) right++;
        row.NoteColumnsWritten(right + 1);
        for (int col = left; col <= right; col++)
        {
            row.Cells[col] = blank;
            row.SetExtendedText(col, null);
            row.SetHyperlink(col, null);
            row.SetUnderline(col, null);
        }
        row.TouchRevision();
    }
}
