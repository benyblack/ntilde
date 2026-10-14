namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether a daemon session runs the program a restoring pane expects (PR #489 follow-up). Compared
/// by file name without extension, because the pane re-resolves its command on every launch (a path
/// may differ, the program should not). An empty daemon command (unknown) matches.
/// </summary>
internal static class MuxCommandMatch
{
    public static bool SameExecutable(string? daemonCommand, string? requestCommand)
    {
        if (string.IsNullOrWhiteSpace(daemonCommand) || string.IsNullOrWhiteSpace(requestCommand)) return true;
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(ExecutableName(daemonCommand), ExecutableName(requestCommand), comparison);
    }

    private static string ExecutableName(string command)
    {
        // Both separators on every OS: a Windows path must not survive whole on Linux (Path.GetFileName would keep it).
        string file = command.Trim().Trim('"').Replace('\\', '/');
        int slash = file.LastIndexOf('/');
        if (slash >= 0) file = file[(slash + 1)..];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }
}
