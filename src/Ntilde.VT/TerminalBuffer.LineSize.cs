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

    private static bool IsDoubleHeight(LineSize size) => DoubleSizeLineRows.IsDoubleHeight(size);

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

    /// <summary>
    /// The visible rows' line attributes for a viewport-only recording checkpoint, or null when every row is single
    /// width, so that a recording without double-size lines is written as before.
    /// </summary>
    public int[]? ExportViewportLineSizes()
    {
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            int[]? sizes = null;
            for (int r = 0; r < Rows; r++)
            {
                if (_viewport[r].LineSize == LineSize.SingleWidth) continue;
                sizes ??= new int[Rows];
                sizes[r] = (int)_viewport[r].LineSize;
            }
            return sizes;
        }
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
    /// Whether the row of the last written character soft-wraps into the cursor's row with the cursor at its start:
    /// the row below with the same attribute, or two rows down for a half of a re-wrapped double-height line.
    /// </summary>
    private bool LastCharRowWrapsIntoCursorRowNoLock()
    {
        if (_cursorCol != 0 || _lastCharCol < 0 || _lastCharRow < 0 || _cursorRow >= Rows || _isPendingWrap) return false;
        int distance = _cursorRow - _lastCharRow;
        if (distance != 1 && distance != 2) return false;
        TerminalRow row = _viewport[_lastCharRow];
        if (!row.IsWrapped || _viewport[_cursorRow].LineSize != row.LineSize) return false;
        if (distance == 1) return true;
        LineSize between = _viewport[_lastCharRow + 1].LineSize;
        return IsDoubleHeight(row.LineSize) && IsDoubleHeight(between) && between != row.LineSize;
    }

    /// <summary>
    /// A wrap pending at the end of a half of a re-wrapped double-height line: the half carries on two rows down, past
    /// the row of the other half (<see cref="DoubleSizeLineRows"/>), and the cursor goes there. False anywhere else.
    /// </summary>
    private bool FollowDoubleHeightWrapNoLock()
    {
        if (_cursorRow < 0 || _cursorRow + 2 >= Rows) return false;
        // Not out of the scrolling region: at its bottom margin the wrap is an ordinary line feed.
        if (_cursorRow <= ScrollBottom && _cursorRow + 2 > ScrollBottom) return false;
        TerminalRow row = _viewport[_cursorRow];
        if (!row.IsWrapped || !IsDoubleHeight(row.LineSize)) return false;
        LineSize below = _viewport[_cursorRow + 1].LineSize;
        if (!IsDoubleHeight(below) || below == row.LineSize || _viewport[_cursorRow + 2].LineSize != row.LineSize) return false;
        _cursorRow += 2;
        _cursorCol = 0;
        return true;
    }

    /// <summary>
    /// A wrap is pending only while the cursor waits at the right edge of its row. A row that has since become wider
    /// than that (a resize, or a line of the screen that was not showing returned to single width) has room again:
    /// the next character goes on the column after the cursor.
    /// </summary>
    private void DropStalePendingWrapNoLock()
    {
        if (_isPendingWrap && _cursorCol < RightEdgeForCursorNoLock) ReleasePendingWrapOnWiderRowNoLock();
    }

    /// <summary>
    /// Changes a row's attribute. On a half of a double-height line the wrap bit can mean "carries on two rows
    /// down" (see <see cref="DoubleSizeLineRows"/>); it must not outlive the attribute and turn into an
    /// ordinary wrap into the row below, which holds the other half.
    /// </summary>
    private static void ChangeLineSizeNoLock(TerminalRow row, LineSize size)
    {
        if (row.LineSize == size) return;
        if (row.LineSize != LineSize.SingleWidth) row.IsWrapped = false;
        row.LineSize = size;
    }

    /// <summary>A newly placed image takes the line attribute of the row it is placed on (see <see cref="TerminalImage.LineSize"/>).</summary>
    private void PlaceImageOnItsRowNoLock(TerminalImage image) => image.LineSize = GetLineSizeAbsolute(image.CellY);

    /// <summary>
    /// After the line attribute of visible rows changed: the images of the showing screen whose origin is on one of
    /// the rows [<paramref name="firstRow"/>, <paramref name="endRow"/>) take that row's attribute. Also called when
    /// the attribute is set to what the row already has: a scrolling region can bring an image onto a row of another
    /// attribute, and setting that row's attribute then settles the image too. Returns whether an image changed.
    /// </summary>
    private bool ImagesTakeRowLineSizesNoLock(int firstRow, int endRow)
    {
        if (_images.Count == 0) return false;
        bool changed = false;
        int top = _isAltScreen ? 0 : _scrollback.Count;
        foreach (TerminalImage image in _images)
        {
            int row = image.CellY - top;
            if (image.IsAltScreenImage != _isAltScreen || row < firstRow || row >= endRow || image.LineSize == _viewport[row].LineSize) continue;
            image.LineSize = _viewport[row].LineSize;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// DECLRMM returns every row of both screens to single width: so does every image whose origin is on one of those
    /// rows, on the screen that is not showing too. An image whose origin is in history keeps its attribute, as its
    /// row does, and so does one whose origin is on no row at all (below a screen that was made shorter).
    /// </summary>
    private void ImagesOnScreenRowsBecomeSingleWidthNoLock()
    {
        foreach (TerminalImage image in _images)
        {
            bool showing = image.IsAltScreenImage == _isAltScreen;
            int row = image.CellY - (image.IsAltScreenImage ? 0 : _scrollback.Count);
            int rows = showing ? Rows : (image.IsAltScreenImage ? _altScreen : _mainScreen)?.Length ?? 0;
            if (row >= 0 && row < rows) image.LineSize = LineSize.SingleWidth;
        }
    }

    /// <summary>An image's left and right edge in single-width columns, the unit two images on rows of different attributes share.</summary>
    private static (int Left, int Right) ImagePageColumns(TerminalImage image)
    {
        int scale = image.LineSize == LineSize.SingleWidth ? 1 : 2;
        return (image.CellX * scale, (image.CellX + image.CellWidth - image.ClipRightColumns) * scale);
    }

    private void ReturnRowToSingleWidthForSizedTextNoLock(int viewportRow)
    {
        if (viewportRow < 0 || viewportRow >= Rows || _viewport[viewportRow].LineSize == LineSize.SingleWidth) return;
        ChangeLineSizeNoLock(_viewport[viewportRow], LineSize.SingleWidth);
        ImagesTakeRowLineSizesNoLock(viewportRow, viewportRow + 1);
        if (viewportRow == _cursorRow) ReleasePendingWrapOnWiderRowNoLock();
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
            if (row.LineSize == size)
            {
                // Nothing to change on the row; an image a scrolling region brought onto it may still differ.
                if (!ImagesTakeRowLineSizesNoLock(_cursorRow, _cursorRow + 1)) return;
            }
            else
            {
                bool wasSingle = row.LineSize == LineSize.SingleWidth;
                ChangeLineSizeNoLock(row, size);
                ImagesTakeRowLineSizesNoLock(_cursorRow, _cursorRow + 1);
                if (size == LineSize.SingleWidth) ReleasePendingWrapOnWiderRowNoLock();
                else
                {
                    if (wasSingle)
                    {
                        // A sized rectangle is laid out in page columns and may span rows: one that touches this row is
                        // retired whole rather than left with one row drawn at twice the width of the others.
                        int abs = TextSizingAbsoluteRow(_cursorRow);
                        EraseTextSizingRange(0, Cols, abs, abs + 1);
                        BlankColumnsPastLineSizeNoLock(_cursorRow);
                    }
                    row.IsWrapped = false;
                    int last = CursorRowLastColumnNoLock;
                    if (_cursorCol > last)
                    {
                        _cursorCol = last;
                        _isPendingWrap = false;
                    }
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
        if (from > 0 && row.Cells[from].IsWideContinuation && row.Cells[from - 1].IsWide) from--;
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
            if (row < 0 || row >= Rows) return;
            bool imagesChanged = ImagesOnRowBecomeSingleWidthNoLock(row);
            if (_viewport[row].LineSize == LineSize.SingleWidth)
            {
                if (!imagesChanged) return;
            }
            else
            {
                ChangeLineSizeNoLock(_viewport[row], LineSize.SingleWidth);
                if (row == _cursorRow) ReleasePendingWrapOnWiderRowNoLock();
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    private bool ImagesOnRowBecomeSingleWidthNoLock(int viewportRow)
    {
        bool changed = false;
        int top = _isAltScreen ? 0 : _scrollback.Count;
        foreach (TerminalImage image in _images)
        {
            if (image.IsAltScreenImage != _isAltScreen || image.CellY - top != viewportRow || image.LineSize == LineSize.SingleWidth) continue;
            image.LineSize = LineSize.SingleWidth;
            changed = true;
        }
        return changed;
    }

    /// <summary>Clearing the alternate screen returns its rows to single width, and the images it keeps with them.</summary>
    private void AlternateScreenImagesBecomeSingleWidthNoLock()
    {
        foreach (TerminalImage image in _images)
            if (image.IsAltScreenImage) image.LineSize = LineSize.SingleWidth;
    }

    /// <summary>Returns every row of <paramref name="rows"/> to single width.</summary>
    private static void ResetLineSizesNoLock(TerminalRow[]? rows)
    {
        if (rows == null) return;
        foreach (TerminalRow row in rows)
            if (row != null) ChangeLineSizeNoLock(row, LineSize.SingleWidth);
    }
}
