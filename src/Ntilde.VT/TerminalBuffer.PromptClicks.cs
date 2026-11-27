namespace Ntilde.VT;

internal enum PromptClickMode
{
    Disabled,
    Absolute,
    Relative,
}

public partial class TerminalBuffer
{
    private ShellIntegrationMark? _clickPrompt;
    private ShellIntegrationMark _clickPromptEnd;
    private PromptClickMode _promptClickMode;
    private ulong _clickPromptVersion;
    private readonly List<ShellIntegrationMark> _secondaryClickPrompts = new();

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

    internal void MarkSecondaryClickPrompt(ShellIntegrationMark mark, PromptClickMode mode)
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
                int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.AbsoluteRow < mark.AbsoluteRow) + 1;
                _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);
                _secondaryClickPrompts.Add(mark);
                if (mode != PromptClickMode.Disabled)
                {
                    _promptClickMode = mode;
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

    // Called at the end of a printable run with the grid write lock held.
    private void ExtendClickPromptThroughText()
    {
        if (_lastCharCol < 0 || _isAltScreen) return;
        lock (_trackedMarkGate)
        {
            if (_promptClickMode == PromptClickMode.Disabled) return;
            ExtendClickPromptLocked(CaptureClickPosition(_lastCharRow, _lastCharCol + 1));
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

    internal void EraseClickPromptLine(int mode)
    {
        if (_promptClickMode == PromptClickMode.Disabled) return;
        bool wholeLine = mode == 2 || (mode == 0 && _cursorCol == 0)
            || (mode == 1 && _cursorCol >= CursorRowLastColumnNoLock);
        if (!wholeLine) return;

        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary
                || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow)
                || !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, _clickPromptEnd, out int endRow)
                || _cursorRow < primaryRow || _cursorRow > endRow)
            {
                return;
            }

            long erased = CaptureClickPosition(_cursorRow, 0).AbsoluteRow;
            _secondaryClickPrompts.RemoveAll(anchor => anchor.AbsoluteRow == erased);
            if (_cursorRow != endRow) return;

            // EL visits rows one at a time. Walk back over an erased tail only
            // when its last row is removed, regardless of erase order.
            _clickPromptEnd = primary;
            for (int row = endRow - 1; row >= Math.Max(0, primaryRow); row--)
            {
                int column = LastPromptTextColumn(_viewport[row]);
                if (column < 0) continue;
                _clickPromptEnd = CaptureClickPosition(row, column + 1);
                break;
            }

            // Marked empty continuation rows are also part of the editable extent.
            if (_secondaryClickPrompts.Count > 0)
                ExtendClickPromptLocked(_secondaryClickPrompts[^1]);
        }
    }

    private static int LastPromptTextColumn(TerminalRow row)
    {
        for (int column = row.WrittenColumns - 1; column >= 0; column--)
        {
            var cell = row.Cells[column];
            if (cell.Character is not (' ' or '\0') || cell.HasExtendedText)
                return column;
        }
        return -1;
    }

    internal void EraseClickPromptTail(ShellIntegrationMark cursor)
    {
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary || cursor.IsAltScreen || cursor.Generation != primary.Generation)
                return;

            if (cursor.AbsoluteRow < primary.AbsoluteRow)
            {
                ClearClickPromptLocked();
                return;
            }

            if (cursor.AbsoluteRow < _clickPromptEnd.AbsoluteRow)
                _clickPromptEnd = cursor;

            int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.AbsoluteRow <= cursor.AbsoluteRow) + 1;
            _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);
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
        _clickPromptEnd = default;
        _promptClickMode = PromptClickMode.Disabled;
        _secondaryClickPrompts.Clear();
    }

    private (ulong Version, ShellIntegrationMark Primary, ShellIntegrationMark End, ShellIntegrationMark[] Secondary)? CaptureClickPromptForReflow()
    {
        lock (_trackedMarkGate)
        {
            return _clickPrompt is { } primary
                ? (_clickPromptVersion, primary, _clickPromptEnd, _secondaryClickPrompts.ToArray())
                : null;
        }
    }

    private void RestoreClickPromptAfterReflow(ulong version, ShellIntegrationMark? primary,
        ShellIntegrationMark? end, List<ShellIntegrationMark> secondary)
    {
        lock (_trackedMarkGate)
        {
            if (_clickPromptVersion != version) return;
            if (primary is null)
            {
                ClearClickPromptLocked();
                return;
            }

            _clickPromptVersion++;
            _clickPrompt = primary;
            _clickPromptEnd = end ?? primary.Value;
            _secondaryClickPrompts.Clear();
            _secondaryClickPrompts.AddRange(secondary);
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
                    if (!ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, secondary, out int secondaryRow)
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
