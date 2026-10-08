using Ntilde.Platform.Ssh.Models;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// The command lines that run <c>ntilde-mux</c> on a remote host (Phase 4 spec §7.1). sshd hands them to
/// the user's login shell, which may be bash, fish, tcsh or nushell, so each is one of two shapes every
/// shell parses alike, and neither involves a PATH lookup:
/// <list type="bullet">
/// <item>the install flow's recorded absolute path, when it is made only of characters no shell treats
/// specially (<see cref="IsSafeAbsolutePath"/>), then the arguments;</item>
/// <item>otherwise <c>sh -c '&lt;<see cref="RemoteInstallDir.Assign"/>&gt; exec "$d/ntilde-mux" …'</c>: a
/// single-quoted script with no single quote inside, which every shell passes to sh untouched, and which sh
/// expands to the default install path (under <c>$XDG_DATA_HOME</c> when it is set and absolute).</item>
/// </list>
/// </summary>
internal static class RemoteMuxCommand
{
    /// <summary>Runs the stdio proxy the mux client speaks through (spec §8.1).</summary>
    public static string Proxy(SshMuxOptions options) => For(options, "proxy --stdio");

    /// <summary>Asks the installed binary for its version (the install flow, spec §9).</summary>
    public static string VersionJson(SshMuxOptions options) => For(options, "--version --json");

    /// <summary>
    /// True for <c>/</c> followed by letters, digits and <c>. _ / + -</c> only, with no <c>//</c> and no
    /// <c>/../</c>: a path that means the same in every shell, unquoted.
    /// </summary>
    public static bool IsSafeAbsolutePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 2 || path[0] != '/') return false;
        foreach (char c in path)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '+' or '-')) return false;
        }

        return !path.Contains("//", StringComparison.Ordinal) && !path.Contains("/../", StringComparison.Ordinal);
    }

    private static string For(SshMuxOptions options, string arguments)
    {
        ArgumentNullException.ThrowIfNull(options);
        string recorded = options.RemoteDaemonPath ?? string.Empty;
        return IsSafeAbsolutePath(recorded)
            ? $"{recorded} {arguments}"
            : $"sh -c '{RemoteInstallDir.Assign}exec \"$d/ntilde-mux\" {arguments}'";
    }
}
