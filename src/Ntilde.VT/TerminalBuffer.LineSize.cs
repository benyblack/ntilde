namespace Ntilde.VT;

/// <summary>
/// VT100 double-width and double-height lines (DECDWL, DECDHL, DECSWL). The attribute is per row. A row that is not
/// single holds half the page's columns: its cells past that are blanked when the attribute is set and are never
/// written or shown afterwards.
/// </summary>
public partial class TerminalBuffer
{
    /// <summary>The columns a row with <paramref name="size"/> holds on a page <paramref name="cols"/> wide.</summary>
    public static int ColumnsForLineSize(LineSize size, int cols) => size == LineSize.SingleWidth ? cols : Math.Max(1, cols / 2);

    /// <summary>The last column the cursor row holds: the page's last column, or the last of the left half on a double-size row.</summary>
    private int CursorRowLastColumnNoLock
        => _cursorRow >= 0 && _cursorRow < Rows && _viewport[_cursorRow].LineSize != LineSize.SingleWidth
            ? ColumnsForLineSize(_viewport[_cursorRow].LineSize, Cols) - 1
            : Cols - 1;

    /// <summary>
    /// A vertical move or a scroll keeps the column, so the cursor can sit right of a double-size row's last column.
    /// It is shown on that last column, and everything that acts at the cursor acts there: this brings the stored
    /// column back to it before such an operation. A no-op on a single-width row.
    /// </summary>
    private void SnapCursorToLineSizeNoLock()
    {
        int last = CursorRowLastColumnNoLock;
        if (last < Cols - 1 && _cursorCol > last) _cursorCol = last;
    }

    /// <summary>The line attribute of the row at an absolute index, history included. Requires the read lock.</summary>
    public LineSize GetLineSizeAbsolute(int absRow)
    {
        AssertLockHeld();
        if (absRow < 0) return LineSize.SingleWidth;
        if (_isAltScreen) return absRow < Rows ? _viewport[absRow].LineSize : LineSize.SingleWidth;
        if (absRow < _scrollback.Count) return _scrollback.GetLineSize(absRow);
        int viewportRow = absRow - _scrollback.Count;
        return viewportRow < Rows ? _viewport[viewportRow].LineSize : LineSize.SingleWidth;
    }

    /// <summary>The line attribute of the row at an absolute index, history included; takes the read lock itself.</summary>
    public LineSize LineSizeAtAbsoluteRow(int absRow)
    {
        bool lockTaken = EnterReadLockIfNeeded();
        try { return GetLineSizeAbsolute(absRow); }
        finally { ExitReadLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>The line attribute of a visible row.</summary>
    public LineSize GetLineSize(int row)
    {
        bool lockTaken = EnterReadLockIfNeeded();
        try { return row >= 0 && row < Rows ? _viewport[row].LineSize : LineSize.SingleWidth; }
        finally { ExitReadLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// The cursor row holds more columns again: a wrap that was pending at its old last column no longer is, and the
    /// next character goes on the column after it instead of the next line.
    /// </summary>
    private void ReleasePendingWrapOnWiderRowNoLock()
    {
        if (!_isPendingWrap) return;
        _isPendingWrap = false;
        if (_cursorCol < Cols - 1) _cursorCol++;
    }

    /// <summary>
    /// DECSWL, DECDWL and DECDHL: sets the cursor row's line attribute. Making a single row double loses the characters
    /// right of the centre, as on a VT100, ends any soft wrap out of the row and brings the cursor back inside it.
    /// Returning to single width restores nothing. Ignored while DECLRMM is set: left/right margins and double-size
    /// lines do not combine (DEC STD 070), and setting DECLRMM returns every line to single width.
    /// </summary>
    public void SetLineSize(LineSize size)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (_cursorRow < 0 || _cursorRow >= Rows) return;
            if (size != LineSize.SingleWidth && Modes.IsLeftRightMarginMode) return;
            TerminalRow row = _viewport[_cursorRow];
            if (row.LineSize == size) return;
            bool wasSingle = row.LineSize == LineSize.SingleWidth;
            row.LineSize = size;
            if (size == LineSize.SingleWidth) ReleasePendingWrapOnWiderRowNoLock();
            else
            {
                if (wasSingle) BlankColumnsPastLineSizeNoLock(_cursorRow);
                row.IsWrapped = false;
                int last = CursorRowLastColumnNoLock;
                if (_cursorCol > last)
                {
                    _cursorCol = last;
                    _isPendingWrap = false;
                }
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>Blanks the half of a row that a double-size line cannot show, retiring a wide glyph the cut would split.</summary>
    private void BlankColumnsPastLineSizeNoLock(int viewportRow)
    {
        TerminalRow row = _viewport[viewportRow];
        int from = ColumnsForLineSize(row.LineSize, Cols);
        if (from >= Cols) return;
        if (from > 0 && row.Cells[from].IsWideContinuation) from--;
        int abs = TextSizingAbsoluteRow(viewportRow);
        EraseTextSizingRange(from, Cols, abs, abs + 1);
        TerminalCell blank = ThemeBlankCell();
        for (int col = from; col < Cols; col++)
        {
            row.Cells[col] = blank;
            row.SetExtendedText(col, null);
            row.SetHyperlink(col, null);
            row.SetUnderline(col, null);
            row.SetProtected(col, false);
        }
        row.WrittenColumns = Math.Min(row.WrittenColumns, from);
        row.TouchRevision();
    }

    /// <summary>
    /// Returns one visible row to single width without touching its cells. ED uses it for each line it erases
    /// completely, which is how a VT100 line leaves double size other than by DECSWL.
    /// </summary>
    public void ResetLineSize(int row)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (row < 0 || row >= Rows || _viewport[row].LineSize == LineSize.SingleWidth) return;
            _viewport[row].LineSize = LineSize.SingleWidth;
            if (row == _cursorRow) ReleasePendingWrapOnWiderRowNoLock();
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>Returns every row of <paramref name="rows"/> to single width.</summary>
    private static void ResetLineSizesNoLock(TerminalRow[]? rows)
    {
        if (rows == null) return;
        foreach (TerminalRow row in rows)
            if (row != null) row.LineSize = LineSize.SingleWidth;
    }
}
