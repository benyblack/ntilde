using Ntilde.VT;

namespace Ntilde.AgentHost;

/// <summary>Read actual VT geometry without blocking the UI or touching controls inside the buffer lock.</summary>
internal static class AgentPaneResizeObservation
{
    /// <summary>Try one immediate lock acquisition; compare actual dimensions against the current view and dispatch proof.</summary>
    internal static bool TryRead(TerminalBuffer buffer, int viewCols, int viewRows,
        (int Cols, int Rows) delivered, out AgentPaneResizeResult observed)
    {
        observed = default;
        if (!buffer.Lock.TryEnterReadLock(TimeSpan.Zero)) return false;
        try
        {
            int cols = buffer.Cols, rows = buffer.Rows;
            bool settled = viewCols == cols && viewRows == rows && delivered == (cols, rows);
            observed = new AgentPaneResizeResult(rows, cols, settled);
            return true;
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
