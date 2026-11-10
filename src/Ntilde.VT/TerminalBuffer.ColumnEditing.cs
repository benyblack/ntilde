namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// Inserts (<paramref name="insert"/>) or deletes <paramref name="count"/> columns on every row of the top/bottom scrolling
    /// region: at the cursor column for DECIC/DECDC (<paramref name="atCursor"/>), at the left margin for SR/SL. Nothing happens
    /// while the cursor row is outside the region or the cursor column is outside the left/right margins (DECLRMM/DECSLRM; the
    /// page edges without them). Columns pushed past the right margin are lost and deleted columns are refilled there; columns
    /// right of the right margin and left of the left margin never move. Introduced
    /// blanks carry the live colors and no visual attribute, as ICH/DCH blanks do. Shifted cells keep their character,
    /// attributes, colors and side tables (extended text, hyperlink, underline decoration, protection). A wide glyph the
    /// shift would split is retired whole with that blank, and a sized-text block the shift would split or that spans rows
    /// is retired as ICH/DCH retire it. Cursor, pen, pending wrap, scrollback and the image plane are untouched.
    /// </summary>
    internal void EditColumns(bool atCursor, bool insert, int count)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Cols < 1 || Rows < 1 || count < 1) return;
            if (_cursorRow < ScrollTop || _cursorRow > ScrollBottom) return;
            if (!CursorInHorizontalMarginsNoLock) return;
            // With pending wrap the cursor still occupies the last column (or the right margin).
            int column = atCursor ? Math.Min(_cursorCol, RightMargin) : LeftMargin;
            if (column < 0) return;
            int edge = RightMargin + 1;
            int n = Math.Min(count, edge - column);
            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            for (int rowIndex = ScrollTop; rowIndex <= ScrollBottom; rowIndex++) ShiftRowColumns(rowIndex, column, n, insert, blank, edge);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>
    /// Shifts one viewport row from <paramref name="column"/> to <paramref name="edge"/> (exclusive: the right margin plus one, or
    /// the page width): inserts <paramref name="n"/> blanks there (cells pushed past the edge are dropped) or deletes
    /// <paramref name="n"/> cells there (blanks enter at the edge). Side tables move with their cells; wide glyphs and sized blocks
    /// the shift would split are retired first. Columns at or past the edge stay where they are.
    /// </summary>
    private void ShiftRowColumns(int rowIndex, int column, int n, bool insert, TerminalCell blank, int edge)
    {
        var row = _viewport[rowIndex];
        if (edge < Cols)
        {
            EraseTextSizingRange(column, edge, TextSizingAbsoluteRow(rowIndex), TextSizingAbsoluteRow(rowIndex) + 1, blank);
            RetireWideAcross(row, edge, blank); // a glyph straddling the right margin would leave a half behind
        }
        else
        {
            EraseTextSizingForShift(rowIndex, column, insert, n);
        }
        var cells = row.Cells;
        row.WrappedColumns = null;
        row.WrittenColumns = Cols;

        // Retire, before anything moves, every wide glyph straddling a boundary the shift cuts: the shift column itself, and
        // the first column pushed off the edge (insert) or the first column kept after the deleted run (delete).
        RetireWideAcross(row, column, blank);
        RetireWideAcross(row, insert ? edge - n : column + n, blank);
        // A lead alone in the last column (ICH can leave one) has no continuation to travel with: a delete would carry it inward as an orphan.
        if (!insert && cells[edge - 1].IsWide) BlankCell(row, edge - 1, blank);

        if (insert)
        {
            for (int c = edge - 1; c >= column + n; c--) cells[c] = cells[c - n];
        }
        else
        {
            for (int c = column; c < edge - n; c++) cells[c] = cells[c + n];
        }

        if (row.HasRowMetadata) row.ShiftRowMetadata(column, insert ? n : -n, Cols, edge);

        int blankStart = insert ? column : edge - n;
        for (int c = blankStart; c < blankStart + n; c++) cells[c] = blank;
        row.TouchRevision();
    }

    /// <summary>Blanks both halves of a wide glyph whose continuation sits at <paramref name="boundary"/>, so no orphan half can survive a shift.</summary>
    private void RetireWideAcross(TerminalRow row, int boundary, TerminalCell blank)
    {
        if (boundary <= 0 || boundary >= Cols) return;
        if (!row.Cells[boundary].IsWideContinuation || !row.Cells[boundary - 1].IsWide) return;
        BlankCell(row, boundary - 1, blank);
        BlankCell(row, boundary, blank);
    }
}
