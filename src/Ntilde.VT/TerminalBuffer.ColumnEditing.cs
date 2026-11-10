namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// Inserts (<paramref name="insert"/>) or deletes <paramref name="count"/> columns on every row of the top/bottom scrolling
    /// region: at the cursor column for DECIC/DECDC (<paramref name="atCursor"/>), at the left margin for SR/SL. Nothing happens
    /// while the cursor row is outside the region. Left/right margins (DECLRMM) are not implemented, so the horizontal margins
    /// are the page edges; columns pushed past the right edge are lost and deleted columns are refilled there. Introduced
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
            // With pending wrap the cursor still occupies the last column.
            int column = atCursor ? Math.Min(_cursorCol, Cols - 1) : 0;
            if (column < 0) return;
            int n = Math.Min(count, Cols - column);
            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            for (int rowIndex = ScrollTop; rowIndex <= ScrollBottom; rowIndex++) ShiftRowColumns(rowIndex, column, n, insert, blank);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>
    /// Shifts one viewport row from <paramref name="column"/> to the right edge: inserts <paramref name="n"/> blanks there
    /// (cells pushed past the edge are dropped) or deletes <paramref name="n"/> cells there (blanks enter at the edge). Side
    /// tables move with their cells; wide glyphs and sized blocks the shift would split are retired first.
    /// </summary>
    private void ShiftRowColumns(int rowIndex, int column, int n, bool insert, TerminalCell blank)
    {
        EraseTextSizingForShift(rowIndex, column, insert, n);
        var row = _viewport[rowIndex];
        var cells = row.Cells;
        row.WrappedColumns = null;
        row.WrittenColumns = Cols;

        // Retire, before anything moves, every wide glyph straddling a boundary the shift cuts: the shift column itself, and
        // the first column pushed off the edge (insert) or the first column kept after the deleted run (delete).
        RetireWideAcross(row, column, blank);
        RetireWideAcross(row, insert ? Cols - n : column + n, blank);
        // A lead alone in the last column (ICH can leave one) has no continuation to travel with: a delete would carry it inward as an orphan.
        if (!insert && cells[Cols - 1].IsWide) BlankCell(row, Cols - 1, blank);

        if (insert)
        {
            for (int c = Cols - 1; c >= column + n; c--) cells[c] = cells[c - n];
        }
        else
        {
            for (int c = column; c < Cols - n; c++) cells[c] = cells[c + n];
        }

        if (row.HasRowMetadata) row.ShiftRowMetadata(column, insert ? n : -n, Cols);

        int blankStart = insert ? column : Cols - n;
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
