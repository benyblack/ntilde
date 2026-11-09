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
            var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground,
                fgIdx: CurrentFgIndex, bgIdx: CurrentBgIndex);
            EraseTextSizingRange(left, right + 1, first, end, blank);
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
            row.SetProtected(col, false);
        }
        row.TouchRevision();
    }

    /// <summary>Selects the DECSACE extent under the write lock; the setting is stream state, not a mode the screen renders.</summary>
    internal void SetAttributeChangeExtent(bool rectangular)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try { Modes.IsRectangularAttributeExtent = rectangular; }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// DECCARA (<paramref name="reverse"/> false: set or clear) and DECRARA (true: toggle) over an inclusive zero-based
    /// rectangle, or over the stream from (top, left) to (bottom, right) unless DECSACE selected the rectangle.
    /// Only bold, underline, blink and inverse can change; text, colors, side tables, cursor, pen and protection are untouched.
    /// Both halves of an intersected wide glyph change together. Reversed borders change nothing. In the stream extent cells past a
    /// row's written extent (unoccupied) are skipped; the rectangle changes and occupies them.
    /// </summary>
    internal void ChangeRectangularAttributes(int left, int top, int right, int bottom, ReadOnlySpan<int> selectors, bool reverse)
    {
        if (!HasDefinedSelector(selectors, reverse)) return;
        bool changed = false;
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (Cols < 1 || Rows < 1) return;
            left = Math.Clamp(left, 0, Cols - 1);
            right = Math.Clamp(right, 0, Cols - 1);
            top = Math.Clamp(top, 0, Rows - 1);
            bottom = Math.Clamp(bottom, 0, Rows - 1);
            if (left > right || top > bottom) return;
            bool rectangle = Modes.IsRectangularAttributeExtent;
            for (int rowIndex = top; rowIndex <= bottom; rowIndex++)
            {
                int first = rectangle || rowIndex == top ? left : 0;
                int last = rectangle || rowIndex == bottom ? right : Cols - 1;
                var row = _viewport[rowIndex];
                // DEC STD 070 (as xterm reads it): the stream leaves unoccupied cells alone, the rectangle fills them. A cell past
                // the row's written extent is unoccupied; typed spaces inside it are not.
                if (!rectangle) last = Math.Min(last, row.WrittenColumns - 1);
                if (first <= last) changed |= ChangeRowAttributes(row, first, last, selectors, reverse, rectangle);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        if (changed) OnInvalidate?.Invoke();
    }

    /// <summary>Whether any selector is in the table for the operation (DECCARA: 0, 1, 4, 5, 7, 22, 24, 25, 27; DECRARA: 0, 1, 4, 5, 7).</summary>
    private static bool HasDefinedSelector(ReadOnlySpan<int> selectors, bool reverse)
    {
        foreach (int selector in selectors)
        {
            if (selector is 0 or 1 or 4 or 5 or 7) return true;
            if (!reverse && selector is 22 or 24 or 25 or 27) return true;
        }
        return false;
    }

    /// <summary>
    /// Applies the selector list to one cell's flags in order: DECCARA sets (1, 4, 5, 7) and clears (22, 24, 25, 27), DECRARA toggles
    /// (1, 4, 5, 7); selector 0 clears or toggles all four. Selectors outside the table are ignored. <paramref name="dropUnderlineDetail"/>
    /// is set whenever the underline goes off at any step, even if a later selector turns it back on.
    /// </summary>
    private static void ApplySelectors(ReadOnlySpan<int> selectors, bool reverse, ref ushort flags, ref bool dropUnderlineDetail)
    {
        const ushort Bold = (ushort)TerminalCellFlags.Bold, Underline = (ushort)TerminalCellFlags.Underline,
            Blink = (ushort)TerminalCellFlags.Blink, Inverse = (ushort)TerminalCellFlags.Inverse;
        const ushort All = Bold | Underline | Blink | Inverse;
        foreach (int selector in selectors)
        {
            ushort mask = selector switch
            {
                0 => All,
                1 => Bold,
                4 => Underline,
                5 => Blink,
                7 => Inverse,
                22 when !reverse => Bold,
                24 when !reverse => Underline,
                25 when !reverse => Blink,
                27 when !reverse => Inverse,
                _ => 0,
            };
            if (mask == 0) continue;
            ushort before = flags;
            bool clears = selector is 0 or 22 or 24 or 25 or 27;
            if (reverse) flags ^= mask;
            else if (clears) flags &= unchecked((ushort)~mask);
            else flags |= mask;
            if ((before & Underline) != 0 && (flags & Underline) == 0) dropUnderlineDetail = true;
        }
    }

    /// <summary>
    /// Applies the selectors to one row span, widened so a wide glyph is never split; the rectangle also occupies the span even when no
    /// attribute differs. Returns whether any cell changed.
    /// </summary>
    private bool ChangeRowAttributes(TerminalRow row, int first, int last, ReadOnlySpan<int> selectors, bool reverse, bool rectangle)
    {
        if (first > 0 && row.Cells[first].IsWideContinuation && row.Cells[first - 1].IsWide) first--;
        if (last + 1 < Cols && row.Cells[last].IsWide && row.Cells[last + 1].IsWideContinuation) last++;
        bool changed = false;
        for (int col = first; col <= last; col++)
        {
            var cell = row.Cells[col];
            ushort flags = cell.Flags;
            bool dropDetail = false;
            ApplySelectors(selectors, reverse, ref flags, ref dropDetail);
            bool detailChanged = false;
            if (dropDetail && row.GetUnderline(col) is { } detail)
            {
                // The underline's style and color go with it, wherever the list leaves the underline; an independent overline stays.
                var replacement = detail.Overline ? CellUnderline.OverlineOnly : null;
                if (!Equals(detail, replacement))
                {
                    row.SetUnderline(col, replacement);
                    detailChanged = true;
                }
            }
            if (flags == cell.Flags && !detailChanged) continue;
            cell.Flags = (ushort)(flags | (ushort)TerminalCellFlags.Dirty);
            row.Cells[col] = cell;
            changed = true;
        }
        if (rectangle || changed) row.NoteColumnsWritten(last + 1);
        if (changed) row.TouchRevision();
        return changed;
    }
}
