using System.Reflection;
using Ntilde.VT;

namespace Ntilde.Tests.Shell.Mux;

internal static class MuxTestText
{
    public static string VisibleText(TerminalBuffer buffer)
    {
        var field = typeof(TerminalBuffer).GetField("_viewport", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var viewport = (TerminalRow[])field.GetValue(buffer)!;
        return string.Join("\n", viewport.Select(row => new string(row.Cells.Select(c => c.Character == '\0' ? ' ' : c.Character).ToArray()).TrimEnd())).TrimEnd();
    }
}
