namespace Ntilde.VT;

public partial class TerminalBuffer
{
    // A relocated glyph vacates layout padding, rather than authoring spaces on its old row.
    private void MarkTextSizingRelocationWrap(int x, int y, int oldWidth)
    {
        if (y < 0) return;
        if (!_isAltScreen && y < _scrollback.Count)
        {
            _scrollback.MarkTextSizingWrap(y, x, x + oldWidth);
            InvalidateTextSizingHistory();
            return;
        }
        var row = GetRowAbsolute(y);
        if (row == null) return;
        if (row.WrittenColumns <= x + oldWidth) row.WrittenColumns = Math.Min(row.WrittenColumns, x);
        row.SetSoftWrap(x);
        row.TouchRevision();
    }

    // Preserve possibly authored blanks before the old rectangle, but stop charging its vacated tail.
    private void TrimVacatedTextSizingExtents(int x, int y, int width, int height)
    {
        int historyCount = _isAltScreen ? 0 : _scrollback.Count;
        for (int row = Math.Max(0, y); row < Math.Min(historyCount + Rows, y + height); row++)
        {
            if (row < historyCount) _scrollback.TrimTextSizingWrittenColumns(row, x, x + width);
            else
            {
                var target = _viewport[row - historyCount];
                if (target.WrittenColumns <= x + width) target.WrittenColumns = Math.Min(target.WrittenColumns, x);
                target.TouchRevision();
            }
        }
    }

    private void InsertTextSizingRectangle(int width, int height)
    {
        int top = _cursorRow;
        for (int dy = 0; dy < height; dy++)
        {
            _cursorRow = top + dy;
            // Retire pairs split by insertion or by the source slice's clipping edge.
            int movingWidth = Math.Max(0, Cols - width - _cursorCol);
            EraseOrdinaryWideSizingBoundaries(_cursorCol, TextSizingAbsoluteRow(_cursorRow), movingWidth, 1);
            InsertCharactersInternal(width);
        }
        _cursorRow = top;
    }

    private void EraseOrdinaryWideSizingBoundaries(int x, int y, int width, int height)
    {
        bool changedHistory = false;
        int historyCount = _isAltScreen ? 0 : _scrollback.Count;
        for (int row = Math.Max(0, y); row < Math.Min(historyCount + Rows, y + height); row++)
        {
            // Evaluate each boundary even when the first one changed a glyph.
            bool leftChanged = ClearOrdinaryWideSplitAt(x, row);
            bool rightChanged = ClearOrdinaryWideSplitAt(x + width, row);
            changedHistory |= (leftChanged || rightChanged) && row < historyCount;
        }
        if (changedHistory) InvalidateTextSizingHistory();
    }

    private bool ClearOrdinaryWideSplitAt(int boundary, int absoluteRow)
    {
        if (boundary <= 0 || boundary >= Cols) return false;
        if (!GetCellAbsolute(boundary - 1, absoluteRow).IsWide
            || !GetCellAbsolute(boundary, absoluteRow).IsWideContinuation) return false;
        ClearOrdinarySizingBoundaryCell(boundary - 1, absoluteRow);
        ClearOrdinarySizingBoundaryCell(boundary, absoluteRow);
        return true;
    }

    private void ClearOrdinarySizingBoundaryCell(int col, int absoluteRow)
    {
        if (!_isAltScreen && absoluteRow < _scrollback.Count)
        {
            _scrollback.ClearCellForTextSizing(absoluteRow, col);
            return;
        }
        var row = GetRowAbsolute(absoluteRow);
        if (row == null) return;
        ClearSizedCell(row, col);
        row.TouchRevision();
    }
}
