using System.Globalization;
using System.Text;
using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The command lines that run <c>ntilde-mux</c> on a remote host (Phase 4 spec §7.1). sshd hands them to
/// the user's login shell, which may be bash, fish, tcsh or nushell, so each is one of two shapes every
/// shell parses alike, and neither involves a PATH lookup:
/// <list type="bullet">
/// <item>the install flow's recorded absolute path, then the arguments, when the path is made only of
/// characters no shell treats specially; or, when it holds others but is <see cref="IsQuotableAbsolutePath"/>,
/// <c>sh -c 'exec "&lt;path&gt;" …'</c> with <c>$</c>, a backtick and <c>"</c> escaped for sh's double quotes;</item>
/// <item>otherwise (no recorded path, or one <see cref="RefusedRecordedPath"/>) <c>sh -c '&lt;<see cref="RemoteInstallDir.Assign"/>&gt; exec "$d/ntilde-mux" …'</c>: a
/// single-quoted script with no single quote inside, which every shell passes to sh untouched, and which sh
/// expands to the default install path (under <c>$XDG_DATA_HOME</c> when it is set and absolute).</item>
/// </list>
/// </summary>
internal static class RemoteMuxCommand
{
    /// <summary>Runs the stdio proxy the mux client speaks through (spec §8.1).</summary>
    public static string Proxy(SshMuxOptions options) => For(options, "proxy --stdio");

    /// <summary>
    /// The stdio proxy for a listing (release hardening item 7): it connects only to a daemon already running - none exits
    /// <see cref="Ntilde.Mux.Cli.MuxProxyExitCodes.NotRunning"/> - and leaves the daemon's agent link alone (item 3).
    /// </summary>
    public static string ProxyWithoutSpawn(SshMuxOptions options) => For(options, "proxy --stdio --no-spawn");

    /// <summary>Asks the installed binary for its version (the install flow, spec §9).</summary>
    public static string VersionJson(SshMuxOptions options) => For(options, "--version --json");

    /// <summary>
    /// True for an absolute path (<c>/</c> and at least one more character, no <c>//</c>, no <c>/../</c>) that can
    /// sit inside sh double quotes in a single-quoted script: it holds no <c>'</c> (ends the script), no <c>\</c> (fish
    /// rewrites a doubled one inside single quotes), no <c>!</c> (history expansion in an interactive login shell), no
    /// control, Unicode format (a bidi override spoofs what the user sees) or line/paragraph separator character, and no unpaired surrogate.
    /// </summary>
    public static bool IsQuotableAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[0] != '/') return false;
        foreach (char c in path)
        {
            if (c is '\'' or '\\' or '!') return false;
        }

        // Runes, not chars: a format character above U+FFFF is a surrogate pair to a char, and an unpaired
        // surrogate is no character at all (the wire would carry U+FFFD).
        for (int i = 0; i < path.Length; i++)
        {
            if (char.IsHighSurrogate(path[i]) && i + 1 < path.Length && char.IsLowSurrogate(path[i + 1])) i++;
            else if (char.IsSurrogate(path[i])) return false;
        }

        foreach (Rune r in path.EnumerateRunes())
        {
            if (RemoteOutputText.IsHidden(r)) return false;
        }

        return !path.Contains("//", StringComparison.Ordinal) && !path.Contains("/../", StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the profile records a path that is not used: not empty (nothing recorded) and not
    /// <see cref="IsQuotableAbsolutePath"/>, so <see cref="Proxy"/> runs the default install path instead.
    /// </summary>
    public static bool RefusedRecordedPath(string? recordedPath) =>
        !string.IsNullOrEmpty(recordedPath) && !IsQuotableAbsolutePath(recordedPath);

    /// <summary>True for <c>/</c> followed by letters, digits and <c>. _ / + -</c> only: a path that means the same in every shell, unquoted.</summary>
    private static bool IsPlainAbsolutePath(string path)
    {
        foreach (char c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '+' or '-')) return false;
        }

        return true;
    }

    private static string For(SshMuxOptions options, string arguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        string recorded = options.RemoteDaemonPath ?? string.Empty;
        if (!IsQuotableAbsolutePath(recorded))
        {
            return $"sh -c '{RemoteInstallDir.Assign}exec \"$d/ntilde-mux\" {arguments}'";
        }

        if (IsPlainAbsolutePath(recorded)) return $"{recorded} {arguments}";

        // Inside sh double quotes only $, ` and " (and \, refused above) are special. The backslash goes in the
        // script as is: the single quotes pass it through to sh, which reads it inside double quotes.
        string escaped = recorded
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"sh -c 'exec \"{escaped}\" {arguments}'";
    }
}
