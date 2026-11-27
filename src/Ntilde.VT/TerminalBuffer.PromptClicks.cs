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
    private PromptClickMode _promptClickMode;
    private ulong _clickPromptVersion;
    private readonly List<ShellIntegrationMark> _secondaryClickPrompts = new();

    internal void BeginClickPrompt(ShellIntegrationMark mark, PromptClickMode mode)
    {
        lock (_trackedMarkGate)
        {
            ClearClickPromptLocked();
            if (!mark.IsAltScreen && mode != PromptClickMode.Disabled)
            {
                _clickPrompt = mark;
                _promptClickMode = mode;
            }
        }
    }

    internal void MarkSecondaryClickPrompt(ShellIntegrationMark mark)
    {
        // Keep the grid lock outermost, as when the parser updates shell marks.
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            lock (_trackedMarkGate)
            {
                if (_promptClickMode != PromptClickMode.Relative || _clickPrompt is not { } primary
                    || !ShellMarkAnchorResolver.TryResolveVisualRow(this, primary, 0, Rows, out _)
                    || mark.IsAltScreen || mark.AbsoluteRow < primary.AbsoluteRow)
                {
                    return;
                }

                // A line editor can redraw the continuation prompts from an earlier row.
                int keep = _secondaryClickPrompts.FindLastIndex(anchor => anchor.AbsoluteRow < mark.AbsoluteRow) + 1;
                _secondaryClickPrompts.RemoveRange(keep, _secondaryClickPrompts.Count - keep);
                _secondaryClickPrompts.Add(mark);
            }
        }
        finally
        {
            if (lockTaken) Lock.ExitReadLock();
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
        _promptClickMode = PromptClickMode.Disabled;
        _secondaryClickPrompts.Clear();
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
                if (_clickPrompt is not { } primary || !Modes.IsCursorVisible || IsMouseReportingActive()
                    || !ShellMarkAnchorResolver.TryResolveVisualRow(this, primary, 0, Rows, out int anchorRow)
                    || row < anchorRow || row > CursorRow)
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
                    if (!ShellMarkAnchorResolver.TryResolveVisualRow(this, secondary, 0, Rows, out int secondaryRow)
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
