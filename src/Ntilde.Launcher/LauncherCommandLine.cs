namespace Ntilde.Launcher;

/// <summary>
/// The command line <c>ntilde.com</c> gives <c>Ntilde.exe</c> (Phase 4 spec §11.1): the target, quoted, then
/// the launcher's own raw command line after its argv[0]. Never re-parsed and re-quoted, so the arguments
/// reach <c>Ntilde.exe</c> byte-identical, whatever quoting or escaping the caller used. Pure, so it is
/// tested on every OS.
/// </summary>
public static class LauncherCommandLine
{
    /// <summary>Names the event a GUI launch sets so the launcher returns at once (spec §11.3).</summary>
    public const string ReleaseEventVariable = "NTILDE_LAUNCHER_RELEASE";

    /// <summary>The exit code when <c>Ntilde.exe</c> is missing: cmd's own for a command it cannot find.</summary>
    public const int TargetMissingExitCode = 9009;

    /// <summary>The raw command line after argv[0] (CRT rules), leading whitespace trimmed; "" when none.</summary>
    /// <remarks>
    /// The MSVC CRT ends argv[0] at the first space or tab outside double quotes. Inside argv[0] a quote
    /// only toggles that state - it cannot be escaped, since a quote cannot appear in a file name - so
    /// <c>"C:\a"b\ntilde.com x</c> has the program name <c>C:\ab\ntilde.com</c>. A line that opens a quote
    /// and never closes it is all argv[0]. The blanks separating argv[0] from the first argument are dropped;
    /// everything after them, blanks included, is kept as is.
    /// </remarks>
    public static string Tail(string rawCommandLine)
    {
        ArgumentNullException.ThrowIfNull(rawCommandLine);

        bool inQuotes = false;
        int i = 0;
        for (; i < rawCommandLine.Length; i++)
        {
            char c = rawCommandLine[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && IsBlank(c)) break;
        }

        while (i < rawCommandLine.Length && IsBlank(rawCommandLine[i])) i++;
        return rawCommandLine[i..];
    }

    /// <summary><c>"&lt;targetExePath&gt;"</c>, then a space and <paramref name="tail"/> when there is one.</summary>
    /// <remarks>
    /// A Windows path cannot contain a quote, so quoting it whole is exact for any path, a space in it
    /// (<c>C:\Program Files\...</c>) included, and the child's argv[0] ends exactly where it does.
    /// </remarks>
    public static string Build(string targetExePath, string tail)
    {
        ArgumentNullException.ThrowIfNull(targetExePath);
        ArgumentNullException.ThrowIfNull(tail);

        string quoted = "\"" + targetExePath + "\"";
        return tail.Length > 0 ? quoted + " " + tail : quoted;
    }

    private static bool IsBlank(char c) => c is ' ' or '\t';
}
