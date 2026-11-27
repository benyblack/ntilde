namespace Ntilde.VT;

/// <summary>A live OSC 133 P boundary in zero-based main viewport coordinates.</summary>
public readonly record struct PromptPart(string Kind, int Row, int Column);

// A row has at most one boundary per documented kind. Re-emitting a kind on the
// same line relocates it. The row object follows a scroll, but is never retained
// by the prompt or copied into scrollback, a resized grid or exported state.
internal sealed class RowPromptParts(object cycle)
{
    internal object Cycle { get; } = cycle;
    internal readonly record struct Boundary(string Kind, int Column, bool Painted = false);
    internal List<Boundary> Boundaries { get; } = new(4);

    internal void Set(string kind, int column)
    {
        Boundaries.RemoveAll(b => b.Kind == kind || b.Column == column);
        Boundaries.Add(new(kind, column));
    }

    internal void Edit(int start, int end, bool paint)
    {
        for (int i = Boundaries.Count - 1; i >= 0; i--)
        {
            var b = Boundaries[i];
            if (b.Column < start || b.Column >= end) continue;
            if (paint && !b.Painted && start == b.Column)
                Boundaries[i] = b with { Painted = true };
            else
                Boundaries.RemoveAt(i);
        }
    }
}

public partial class TerminalBuffer
{
    // Only prompts that actually use P allocate a token. Dropping it retires
    // every part at once, including after an asynchronous command-window close.
    private object? _promptPartCycle;

    internal void RecordPromptPart(string kind, ShellIntegrationMark mark)
    {
        lock (_trackedMarkGate)
        {
            if (_clickPrompt is not { } primary || mark.IsAltScreen ||
                !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int primaryRow) ||
                _cursorRow < primaryRow || (_cursorRow == primaryRow && mark.Column < primary.Column))
                return;

            var row = _viewport[_cursorRow];
            _promptPartCycle ??= new object();
            if (row.PromptParts?.Cycle != _promptPartCycle)
                row.PromptParts = new(_promptPartCycle);
            row.PromptParts.Set(kind, mark.Column);
        }
    }

    /// <summary>Copies the active viewport boundaries while holding the buffer read lock.</summary>
    public PromptPart[] ReadPromptParts()
    {
        bool lockTaken = EnterReadLockIfNeeded();
        try
        {
            lock (_trackedMarkGate)
            {
                if (_promptPartCycle == null || _clickPrompt is not { } primary ||
                    !ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int first)) return [];
                var result = new List<PromptPart>();
                for (int row = Math.Max(0, first); row < Rows; row++)
                    if (_viewport[row].PromptParts is { } parts && parts.Cycle == _promptPartCycle)
                        foreach (var boundary in parts.Boundaries)
                            result.Add(new(boundary.Kind, row, boundary.Column));
                return result.ToArray();
            }
        }
        finally { if (lockTaken) Lock.ExitReadLock(); }
    }

    internal int? RightPromptColumn(int absoluteRow)
    {
        if (GetRowAbsolute(absoluteRow)?.PromptParts is not { } parts) return null;
        lock (_trackedMarkGate)
        {
            if (parts.Cycle == _promptPartCycle && _clickPrompt is { } primary &&
                ShellMarkAnchorResolver.TryResolveLiveRowLocked(this, primary, out int first) &&
                absoluteRow >= Scrollback.Count + first)
                foreach (var boundary in parts.Boundaries)
                    if (boundary.Kind == "r") return boundary.Column;
            return null;
        }
    }
}
