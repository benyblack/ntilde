namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// True when any cell of the active screen was written under DECSCA 1. While none is, DECSED and DECSERA
    /// are exactly ED and DECERA, and the callers keep using those paths.
    /// </summary>
    public bool HasProtectedCharacters()
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try { return AnyViewportRowProtected(0, _viewport.Length - 1); }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>Whether any viewport row in the inclusive range holds a protected cell. The caller holds the buffer lock.</summary>
    private bool AnyViewportRowProtected(int first, int last)
    {
        for (int r = first; r <= last; r++)
            if (_viewport[r].HasProtectedCells) return true;
        return false;
    }

    /// <summary>Whether the active screen's cell is protected; false outside the screen.</summary>
    public bool IsCellProtected(int col, int row)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            return row >= 0 && row < _viewport.Length && col >= 0 && col < Cols && _viewport[row].IsProtected(col);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// DECSED: the ED region of <paramref name="mode"/> (0 cursor to end, 1 start to cursor, 2 everything)
    /// erased except protected characters. Returns false, having changed nothing, when no character is
    /// protected, so the caller runs ordinary ED. Like DECERA it works on stored text only: the image
    /// plane, the cursor, the pen and saved lines are untouched.
    /// </summary>
    internal bool TrySelectiveEraseInDisplay(int mode)
    {
        if (mode is < 0 or > 2) return false;
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Cols < 1 || Rows < 1 || _cursorRow < 0 || _cursorRow >= Rows || !AnyViewportRowProtected(0, _viewport.Length - 1)) return false;
            SelectiveEraseDisplayNoLock(mode);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
        return true;
    }

    /// <summary>The ED region of <paramref name="mode"/> relative to the cursor, one row span at a time; the caller holds the lock.</summary>
    private void SelectiveEraseDisplayNoLock(int mode)
    {
        int cursorCol = Math.Clamp(_cursorCol, 0, Cols);
        int firstRow = mode == 0 ? _cursorRow : 0;
        int lastRow = mode == 1 ? _cursorRow : Rows - 1;
        var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
        for (int r = firstRow; r <= lastRow; r++)
        {
            bool cursorRow = r == _cursorRow;
            int from = mode == 0 && cursorRow ? cursorCol : 0;
            int to = mode == 1 && cursorRow ? Math.Min(Cols, cursorCol + 1) : Cols;
            EraseUnprotectedSpanNoLock(r, from, to, blank);
        }
    }

    /// <summary>
    /// DECSERA: DECERA's inclusive zero-based rectangle with the same clipping, edge and blank rules, minus
    /// the protected characters. With nothing protected it is DECERA itself.
    /// </summary>
    internal void SelectiveEraseRectangularArea(int left, int top, int right, int bottom)
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
            if (!AnyViewportRowProtected(top, bottom))
            {
                EraseRectangularArea(left, top, right, bottom);
                return;
            }

            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            for (int r = top; r <= bottom; r++)
            {
                var row = _viewport[r];
                int from = left, to = right + 1;
                // A glyph split by an edge is one unit; whether it goes is decided by its own protection.
                if (from > 0 && row.Cells[from].IsWideContinuation && row.Cells[from - 1].IsWide) from--;
                if (to < Cols && row.Cells[to - 1].IsWide && row.Cells[to].IsWideContinuation) to++;
                EraseUnprotectedSpanNoLock(r, from, to, blank);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
    }

    /// <summary>
    /// Blanks every unprotected cell of [<paramref name="from"/>, <paramref name="to"/>) in one viewport row with
    /// the same per-cell cleanup the ordinary erases do. A row with no protected cell is erased exactly as ED
    /// erases it, so selective and ordinary erase agree wherever nothing is protected. Scaled (OSC 66) units are
    /// retired only through unprotected cells: they are written, and protected, as a whole.
    /// </summary>
    private void EraseUnprotectedSpanNoLock(int rowIndex, int from, int to, TerminalCell blank)
    {
        var row = _viewport[rowIndex];
        from = Math.Max(0, from);
        to = Math.Min(Cols, to);
        if (from >= to) return;
        if (from == 0 && to == Cols && !row.HasProtectedCells)
        {
            ClearRowInternal(rowIndex);
            return;
        }

        int absolute = TextSizingAbsoluteRow(rowIndex);
        int col = from;
        while (col < to)
        {
            if (row.IsProtected(col)) { col++; continue; }
            int start = col;
            while (col < to && !row.IsProtected(col)) col++;
            EraseTextSizingRange(start, col, absolute, absolute + 1, blank);
            row.NoteColumnsWritten(col);
            for (int c = start; c < col; c++)
            {
                row.Cells[c] = blank;
                row.SetExtendedText(c, null);
                row.SetHyperlink(c, null);
                row.SetUnderline(c, null);
            }
        }
        row.TouchRevision();
    }
}
