using Ntilde.AgentHost.Contracts;
using Ntilde.VT;

namespace Ntilde.AgentHost;

internal static class CaptureSelectionValidation
{
    internal static bool WellFormed(CaptureSelection? value) => value is null ||
        (value.StartRow >= 0 && value.StartCol >= 0 && value.EndRow >= value.StartRow && value.EndCol >= 0 &&
            (value.EndRow != value.StartRow || value.EndCol >= value.StartCol));

    internal static bool FitsGrid(CaptureSelection? value, int rows, int cols) => value is null ||
        (WellFormed(value) && value.EndRow < rows && value.StartCol < cols && value.EndCol < cols);

    internal static bool FitsBuffer(CaptureSelection? value, TerminalBuffer buffer)
    {
        if (value is null) return true;
        buffer.Lock.EnterReadLock();
        try { return FitsGrid(value, buffer.Rows, buffer.Cols); }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
