namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// Fills an inclusive zero-based text rectangle with one character carrying the live SGR pen
    /// (flags, literal/index/default colors and underline decoration). Hyperlinks, extended text and sized-text
    /// metadata are not part of an SGR rendition and are cleared. Cursor, pen, pending wrap, row wrapping and the
    /// image plane are untouched. Intersected wide or scaled units are retired whole with the erase blank, exactly
    /// as DECERA does, before the fill is written.
    /// </summary>
    internal void FillRectangularArea(char fill, int left, int top, int right, int bottom)
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
            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            EraseTextSizingRange(left, right + 1, TextSizingAbsoluteRow(top), TextSizingAbsoluteRow(bottom) + 1, blank);
            SyncPackedState();
            var decoration = UnderlineForWrite;
            for (int rowIndex = top; rowIndex <= bottom; rowIndex++)
            {
                var row = _viewport[rowIndex];
                RetireSplitWideEdges(row, left, right, blank);
                row.NoteColumnsWritten(right + 1);
                for (int col = left; col <= right; col++)
                {
                    row.Cells[col] = new TerminalCell(fill, _packedFg, _packedBg, _packedFlags);
                    row.SetExtendedText(col, null);
                    row.SetHyperlink(col, null);
                    row.SetUnderline(col, decoration);
                }
                row.TouchRevision();
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
    }

    /// <summary>
    /// Copies an inclusive zero-based source rectangle to the destination origin, clipped to the active page, as if
    /// the source were read completely before any destination cell is written (overlap cannot smear). Cells keep
    /// their character, flags, colors, extended text, hyperlink and underline decoration. A wide glyph cut by the
    /// source or by the page edge copies as a blank with its other attributes, and a destination edge that splits
    /// an existing wide glyph retires the orphaned half with the erase blank. Sized-text metadata is not copied and
    /// destination sized blocks are retired whole. Cursor, pen, pending wrap, row wrapping and the image plane are untouched.
    /// </summary>
    internal void CopyRectangularArea(int left, int top, int right, int bottom, int destinationLeft, int destinationTop)
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
            if (destinationLeft < 0 || destinationTop < 0 || destinationLeft >= Cols || destinationTop >= Rows) return;
            int width = Math.Min(right - left + 1, Cols - destinationLeft);
            int height = Math.Min(bottom - top + 1, Rows - destinationTop);

            var cells = new TerminalCell[height][];
            var text = new string?[height][];
            var links = new Links.Hyperlink?[height][];
            var underlines = new CellUnderline?[height][];
            for (int y = 0; y < height; y++)
            {
                var source = _viewport[top + y];
                cells[y] = new TerminalCell[width];
                text[y] = new string?[width];
                links[y] = new Links.Hyperlink?[width];
                underlines[y] = new CellUnderline?[width];
                for (int x = 0; x < width; x++)
                {
                    int col = left + x;
                    cells[y][x] = source.Cells[col];
                    text[y][x] = source.GetExtendedText(col);
                    links[y][x] = source.GetHyperlink(col);
                    underlines[y][x] = source.GetUnderline(col);
                }
                BlankCutWideHalves(cells[y], text[y]);
            }

            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            EraseTextSizingRange(destinationLeft, destinationLeft + width, TextSizingAbsoluteRow(destinationTop),
                TextSizingAbsoluteRow(destinationTop + height - 1) + 1, blank);
            for (int y = 0; y < height; y++)
            {
                var row = _viewport[destinationTop + y];
                RetireSplitWideEdges(row, destinationLeft, destinationLeft + width - 1, blank);
                row.NoteColumnsWritten(destinationLeft + width);
                for (int x = 0; x < width; x++)
                {
                    int col = destinationLeft + x;
                    var cell = cells[y][x];
                    cell.Flags |= (ushort)TerminalCellFlags.Dirty;
                    row.Cells[col] = cell;
                    row.SetExtendedText(col, text[y][x]);
                    row.SetHyperlink(col, links[y][x]);
                    row.SetUnderline(col, underlines[y][x]);
                }
                row.TouchRevision();
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        OnInvalidate?.Invoke();
    }

    /// <summary>Blanks a wide glyph half left outside <paramref name="left"/>..<paramref name="right"/> so no orphan survives an overwrite.</summary>
    private void RetireSplitWideEdges(TerminalRow row, int left, int right, TerminalCell blank)
    {
        if (left > 0 && row.Cells[left].IsWideContinuation && row.Cells[left - 1].IsWide) BlankCell(row, left - 1, blank);
        if (right + 1 < Cols && row.Cells[right].IsWide && row.Cells[right + 1].IsWideContinuation) BlankCell(row, right + 1, blank);
    }

    private static void BlankCell(TerminalRow row, int col, TerminalCell blank)
    {
        row.Cells[col] = blank;
        row.SetExtendedText(col, null);
        row.SetHyperlink(col, null);
        row.SetUnderline(col, null);
    }

    /// <summary>Turns a leading continuation or a trailing lead cell of a copied row into a plain blank that keeps its colors and attributes.</summary>
    private static void BlankCutWideHalves(TerminalCell[] cells, string?[] text)
    {
        const ushort wideBits = (ushort)(TerminalCellFlags.Wide | TerminalCellFlags.WideContinuation | TerminalCellFlags.HasExtendedText);
        int last = cells.Length - 1;
        if (cells[0].IsWideContinuation) Blank(0);
        if (cells[last].IsWide) Blank(last);

        void Blank(int index)
        {
            cells[index].Character = ' ';
            cells[index].Flags &= unchecked((ushort)~wideBits);
            text[index] = null;
        }
    }
}
