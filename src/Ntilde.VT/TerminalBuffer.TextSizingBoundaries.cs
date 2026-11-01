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

    private void EraseOrdinaryWideSizingBoundaries(int x, int y, int width, int height)
    {
        bool changedHistory = false;
        int right = x + width;
        int historyCount = _isAltScreen ? 0 : _scrollback.Count;
        for (int row = Math.Max(0, y); row < Math.Min(historyCount + Rows, y + height); row++)
        {
            if (x > 0 && GetCellAbsolute(x, row).IsWideContinuation)
            {
                ClearOrdinarySizingBoundaryCell(x, row);
                if (GetCellAbsolute(x - 1, row).IsWide) ClearOrdinarySizingBoundaryCell(x - 1, row);
                changedHistory |= row < historyCount;
            }
            if (right < Cols && GetCellAbsolute(right - 1, row).IsWide)
            {
                ClearOrdinarySizingBoundaryCell(right - 1, row);
                if (GetCellAbsolute(right, row).IsWideContinuation) ClearOrdinarySizingBoundaryCell(right, row);
                changedHistory |= row < historyCount;
            }
        }
        if (changedHistory) InvalidateTextSizingHistory();
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
