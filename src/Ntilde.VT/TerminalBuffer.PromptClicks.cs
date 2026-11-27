namespace Ntilde.VT;

internal enum PromptClickMode
{
    Disabled,
    Absolute,
    Relative,
}

public partial class TerminalBuffer
{
    private readonly record struct SecondaryClickPrompt(ShellIntegrationMark Mark, bool HasWrittenText = false);

    private ShellIntegrationMark? _clickPrompt;
    private ShellIntegrationMark _clickPromptEnd;
    private PromptClickMode _promptClickMode;
    private ulong _clickPromptVersion;
    private readonly List<SecondaryClickPrompt> _secondaryClickPrompts = new();

    internal void BeginClickPrompt(ShellIntegrationMark mark, PromptClickMode mode)
    {
        lock (_trackedMarkGate)
        {
            ClearClickPromptLocked();
            if (!mark.IsAltScreen)
            {
                _clickPrompt = mark;
                _clickPromptEnd = mark;
                _promptClickMode = mode;
            }
        }
    }

    internal void MarkSecondaryClickPrompt(ShellIntegrationMark mark, PromptClickMode? mode)
    {
        // Keep the grid lock outermost, as when the parser updates shell marks.
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            lock (_trackedMarkGate)
            {
                if (_clickPrompt is not { } primary
                    || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out _)
                    || mark.IsAltScreen || mark.AbsoluteRow < primary.AbsoluteRow)
                {
                    return;
                }

                // A line editor can redraw the continuation prompts from an earlier row.
                int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.Mark.AbsoluteRow < mark.AbsoluteRow) + 1;
                _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);
                _secondaryClickPrompts.Add(new(mark));
                if (mode is { } requested)
                {
                    if (requested == PromptClickMode.Disabled || _promptClickMode == PromptClickMode.Disabled)
                        _clickPromptEnd = mark;
                    _promptClickMode = requested;
                }

                ExtendClickPromptLocked(mark);
                _clickPromptVersion++;
            }
        }
        finally
        {
            if (lockTaken) Lock.ExitReadLock();
        }
    }

    // Called after writing text, with the grid write lock held.
    private void ExtendClickPromptThroughRows(long firstRow, int row, int column)
    {
        if (_promptClickMode == PromptClickMode.Disabled || _isAltScreen) return;
        lock (_trackedMarkGate)
        {
            var end = CaptureClickPosition(row, column);
            if (end.Generation != _clickPromptEnd.Generation) return;
            ExtendClickPromptLocked(end);

            long firstWritten = Math.Min(firstRow, end.AbsoluteRow);
            for (int index = _secondaryClickPrompts.Count - 1; index >= 0; index--)
            {
                var anchor = _secondaryClickPrompts[index];
                if (anchor.Mark.AbsoluteRow < firstWritten) break;
                if (!anchor.HasWrittenText && anchor.Mark.AbsoluteRow <= end.AbsoluteRow)
                    _secondaryClickPrompts[index] = anchor with { HasWrittenText = true };
            }
        }
    }

    private ShellIntegrationMark CaptureClickPosition(int viewportRow, int column)
    {
        int row = Scrollback.Count + viewportRow;
        return new ShellIntegrationMark(row, column, Scrollback.TotalRowsEvicted + row,
            IsAltScreen: false, Scrollback.Generation);
    }

    private void ExtendClickPromptLocked(ShellIntegrationMark end)
    {
        // Shell line editors can move back above a continuation line and edit it
        // in place. Keep the extent of previously written input through that move.
        if (end.Generation == _clickPromptEnd.Generation
            && (end.AbsoluteRow > _clickPromptEnd.AbsoluteRow
                || (end.AbsoluteRow == _clickPromptEnd.AbsoluteRow && end.Column > _clickPromptEnd.Column)))
        {
            _clickPromptEnd = end;
        }
    }

    private void UpdateClickPromptAfterRowEdit(int row)
    {
        if (_promptClickMode == PromptClickMode.Disabled) return;
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary
                || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow)
                || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, _clickPromptEnd, out int endRow)
                || row < primaryRow || row > endRow)
            {
                return;
            }

            int columns = _viewport[row].PromptTextColumns;
            var edited = CaptureClickPosition(row, columns);
            // Editors can mark a continuation before clearing old cells and then
            // drawing it. Only retire the mark after its own text has been erased.
            if (columns == 0)
                _secondaryClickPrompts.RemoveAll(anchor => anchor.HasWrittenText && anchor.Mark.AbsoluteRow == edited.AbsoluteRow);
            if (row != endRow) return;
            if (columns == 0)
                RestoreClickPromptEndBeforeRow(primary, primaryRow, endRow);
            else
            {
                _clickPromptEnd = primary;
                ExtendClickPromptLocked(edited);
            }
        }
    }

    private void RestoreClickPromptEndBeforeRow(ShellIntegrationMark primary, int primaryRow, int erasedRow)
    {
        // EL visits rows one at a time. Walk back over an erased tail only
        // when its last row is removed, regardless of erase order.
        _clickPromptEnd = primary;
        for (int row = erasedRow - 1; row >= Math.Max(0, primaryRow); row--)
        {
            int columns = _viewport[row].PromptTextColumns;
            if (columns == 0) continue;
            _clickPromptEnd = CaptureClickPosition(row, columns);
            break;
        }

        // Marked empty continuation rows are also part of the editable extent.
        if (_secondaryClickPrompts.Count > 0)
            ExtendClickPromptLocked(_secondaryClickPrompts[^1].Mark);
    }

    internal void EraseClickPromptTail(ShellIntegrationMark cursor)
    {
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary
                || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow))
                return;

            if (cursor.AbsoluteRow < primary.AbsoluteRow)
            {
                ClearClickPromptLocked();
                return;
            }

            int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.Mark.AbsoluteRow < cursor.AbsoluteRow
                || (anchor.Mark.AbsoluteRow == cursor.AbsoluteRow
                    && (!anchor.HasWrittenText || anchor.Mark.Column < cursor.Column))) + 1;
            _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);

            if (cursor.Column == 0 && cursor.AbsoluteRow <= _clickPromptEnd.AbsoluteRow)
                RestoreClickPromptEndBeforeRow(primary, primaryRow, _cursorRow);
            else
                LimitClickPromptEnd(cursor);
        }
    }

    private void LimitClickPromptEnd(ShellIntegrationMark end)
    {
        if (end.AbsoluteRow < _clickPromptEnd.AbsoluteRow
            || (end.AbsoluteRow == _clickPromptEnd.AbsoluteRow && end.Column < _clickPromptEnd.Column))
        {
            _clickPromptEnd = end;
        }
    }

    // The grid write lock is held while rows are moved or selectively erased.
    internal void InvalidateClickPromptInRows(int top, int bottom)
    {
        if (!_clickPrompt.HasValue || _isAltScreen) return;
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is { } primary
                && ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow)
                && ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, _clickPromptEnd, out int endRow)
                && primaryRow <= bottom && endRow >= top)
            {
                ClearClickPromptLocked();
            }
        }
    }

    private void TrimClickPromptAfterHeightShrink()
    {
        if (!_clickPrompt.HasValue) return;
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary) return;
            if (!ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow))
            {
                ClearClickPromptLocked();
                return;
            }

            long lastRow = CaptureClickPosition(Rows - 1, 0).AbsoluteRow;
            if (_clickPromptEnd.AbsoluteRow <= lastRow) return;
            int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.Mark.AbsoluteRow <= lastRow) + 1;
            _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);
            RestoreClickPromptEndBeforeRow(primary, primaryRow, Rows);
            _clickPromptVersion++;
        }
    }

    internal void ClearClickPrompt()
    {
        lock (_trackedMarkGate) ClearClickPromptLocked();
    }

    private void ClearClickPromptLocked()
    {
        _clickPromptVersion++;
        _clickPrompt = null;
        _promptPartCycle = null;
        _clickPromptEnd = default;
        _promptClickMode = PromptClickMode.Disabled;
        _secondaryClickPrompts.Clear();
    }

    private (ulong Version, ShellIntegrationMark Primary, ShellIntegrationMark End, SecondaryClickPrompt[] Secondary)? CaptureClickPromptForReflow()
    {
        lock (_trackedMarkGate)
        {
            return _clickPrompt is { } primary
                ? (_clickPromptVersion, primary, _clickPromptEnd, _secondaryClickPrompts.ToArray())
                : null;
        }
    }

    private void RestoreClickPromptAfterReflow(ulong version, ShellIntegrationMark? primary,
        ShellIntegrationMark? end, List<SecondaryClickPrompt> secondary)
    {
        lock (_trackedMarkGate)
        {
            if (_clickPromptVersion != version) return;
            if (primary is not { } start)
            {
                ClearClickPromptLocked();
                return;
            }

            _clickPromptVersion++;
            _clickPrompt = start;
            _clickPromptEnd = start;
            if (end is { } retainedEnd) ExtendClickPromptLocked(retainedEnd);
            _secondaryClickPrompts.Clear();
            foreach (var anchor in secondary)
            {
                if (anchor.Mark.AbsoluteRow < start.AbsoluteRow) continue;
                _secondaryClickPrompts.Add(anchor);
                ExtendClickPromptLocked(anchor.Mark);
            }
        }
    }

    /// <summary>Resolves a live, zero-based viewport row to Kitty's one-based SGR wire row.</summary>
    public bool TryGetPromptClickRow(int row, out int wireRow, out ulong promptVersion)
    {
        wireRow = 0;
        promptVersion = 0;
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            lock (_trackedMarkGate)
            {
                if (_promptClickMode == PromptClickMode.Disabled || _clickPrompt is not { } primary
                    || !Modes.IsCursorVisible || IsMouseReportingActive()
                    || (uint)row >= (uint)Rows
                    || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int anchorRow)
                    || row < anchorRow
                    || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, _clickPromptEnd, out int endRow)
                    || row > Math.Max(endRow, _cursorRow))
                {
                    return false;
                }

                promptVersion = _clickPromptVersion;
                if (_promptClickMode == PromptClickMode.Absolute)
                {
                    wireRow = row + 1;
                    return true;
                }

                foreach (var secondary in _secondaryClickPrompts)
                {
                    if (!ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, secondary.Mark, out int secondaryRow)
                        || secondaryRow > row)
                    {
                        break;
                    }

                    anchorRow = secondaryRow;
                }

                wireRow = row - anchorRow + 1;
                return true;
            }
        }
        finally
        {
            if (lockTaken) Lock.ExitReadLock();
        }
    }
}
