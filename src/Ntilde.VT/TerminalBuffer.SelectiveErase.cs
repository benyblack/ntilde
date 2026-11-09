namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// True when any cell of the active screen was written under DECSCA 1. While none is, DECSED and DECSERA
    /// are exactly ED and DECERA, and the callers keep using those paths.
    /// </summary>
    public bool HasProtectedCharacters()
    {
        bool lockTaken = EnterReadLockIfNeeded();
        try { return AnyViewportRowProtected(0, _viewport.Length - 1); }
        finally { ExitReadLockIfNeeded(Lock, lockTaken); }
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
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            return row >= 0 && row < _viewport.Length && col >= 0 && col < Cols && _viewport[row].IsProtected(col);
        }
        finally { ExitReadLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// DECSED: the ED region of <paramref name="mode"/> (0 cursor to end, 1 start to cursor, 2 everything)
    /// erased except protected characters. Returns false, having changed nothing, when no character is
    /// protected, so the caller runs ordinary ED. Like DECERA it works on stored text only: the cursor, the pen
    /// and saved lines are untouched (mode 2 retires images exactly as ED 2 does).
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
        if (mode == 2) RetireImagesOverVisibleScreen(); // as ED 2 does: only ED 2 touches the image plane, and images are never protected
        for (int r = firstRow; r <= lastRow; r++)
        {
            bool cursorRow = r == _cursorRow;
            int from = mode == 0 && cursorRow ? cursorCol : 0;
            int to = mode == 1 && cursorRow ? Math.Min(Cols, cursorCol + 1) : Cols;
            EraseUnprotectedSpanNoLock(r, from, to, blank, edShortcut: true);
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
            // A scaled block crossing the rectangle can hold protection outside it, so only a screen with no protection at all is DECERA's.
            if (!AnyViewportRowProtected(0, _viewport.Length - 1))
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
                EraseUnprotectedSpanNoLock(r, from, to, blank, edShortcut: false);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
    }

    /// <summary>
    /// Whether the cell is protected, or belongs to a scaled (OSC 66) block with any protected cell on the screen. A block is written
    /// protected as a whole, but rows that went through history and came back lose protection, so a block can hold mixed protection;
    /// it is retired whole or not at all, and one protected cell spares all of it.
    /// </summary>
    private bool IsProtectedOrInProtectedBlock(int rowIndex, int col)
    {
        var row = _viewport[rowIndex];
        if (row.IsProtected(col)) return true;
        if (row.GetTextSizing(col) is not { } piece) return false;
        int top = rowIndex - piece.RowOffset, left = col - piece.ColumnOffset;
        for (int dy = 0; dy < piece.Block.Height; dy++)
        {
            int r = top + dy;
            if (r < 0 || r >= _viewport.Length || !_viewport[r].HasProtectedCells) continue;
            for (int dx = 0; dx < piece.Block.Width; dx++)
                if (left + dx >= 0 && left + dx < Cols && _viewport[r].IsProtected(left + dx)) return true;
        }
        return false;
    }

    /// <summary>
    /// Blanks every unprotected cell of [<paramref name="from"/>, <paramref name="to"/>) in one viewport row with
    /// the same per-cell cleanup the ordinary erases do. With <paramref name="edShortcut"/> (DECSED) a whole row with no
    /// protected cell is erased exactly as ED erases it; a rectangle (DECSERA) always keeps DECERA's own blank and cleanup. Scaled (OSC 66) units are
    /// retired only through unprotected cells: they are written, and protected, as a whole.
    /// </summary>
    private void EraseUnprotectedSpanNoLock(int rowIndex, int from, int to, TerminalCell blank, bool edShortcut)
    {
        var row = _viewport[rowIndex];
        from = Math.Max(0, from);
        to = Math.Min(Cols, to);
        if (from >= to) return;
        if (edShortcut && from == 0 && to == Cols && !row.HasProtectedCells && row.GetTextSizingMap() is not { Count: > 0 })
        {
            ClearRowInternal(rowIndex);
            return;
        }

        int absolute = TextSizingAbsoluteRow(rowIndex);
        int col = from;
        while (col < to)
        {
            if (IsProtectedOrInProtectedBlock(rowIndex, col)) { col++; continue; }
            int start = col;
            while (col < to && !IsProtectedOrInProtectedBlock(rowIndex, col)) col++;
            EraseTextSizingRange(start, col, absolute, absolute + 1, blank);
            // DECERA counts what it erases as authored; ED does too, but a selective display erase over default-colored blanks adds
            // nothing the row did not already hold, and must not extend the extent reflow uses to tell typed spaces from wide padding.
            BlankRun(row, start, col, blank, noteExtent: !edShortcut || !blank.IsDefaultBackground);
        }
        if (edShortcut && blank.IsDefaultBackground && to == Cols) TrimErasedTailExtent(rowIndex, from);
        row.TouchRevision();
    }

    /// <summary>
    /// A selective display erase of a row's tail over default blanks trims the authored extent back to what survives (the cells before
    /// <paramref name="from"/> and the protected cells), so a later wide pre-wrap still recognizes the erased tail as padding.
    /// </summary>
    private void TrimErasedTailExtent(int rowIndex, int from)
    {
        var row = _viewport[rowIndex];
        if (row.WrittenColumns <= from) return;
        int kept = from;
        // Cells the erase spared (protected, or part of a scaled block with a protected cell) are still authored content.
        for (int c = from; c < row.Cells.Length; c++) if (IsProtectedOrInProtectedBlock(rowIndex, c)) kept = c + 1;
        row.WrittenColumns = Math.Min(row.WrittenColumns, kept);
    }

    /// <summary>Blanks [<paramref name="start"/>, <paramref name="end"/>) of one row with the ordinary erases' per-cell metadata cleanup.</summary>
    private static void BlankRun(TerminalRow row, int start, int end, TerminalCell blank, bool noteExtent)
    {
        if (noteExtent) row.NoteColumnsWritten(end);
        for (int c = start; c < end; c++)
        {
            row.Cells[c] = blank;
            row.SetExtendedText(c, null);
            row.SetHyperlink(c, null);
            row.SetUnderline(c, null);
            // A stale record (an orphaned wide anchor's) must not outlive the cell it described: once the blank is narrow it would read protected.
            row.SetProtected(c, false);
        }
    }
}
