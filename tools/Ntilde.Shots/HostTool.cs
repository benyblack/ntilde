using System.ComponentModel;
using Ntilde.Pty;

namespace Ntilde.Shots;

/// <summary>
/// The capturing machine's command-line tools (git, docker, ffmpeg) as absolute paths, so no launch
/// hands the lookup to the OS's implicit <c>PATH</c> search (csharpsquid:S4036).
/// </summary>
/// <remarks>
/// On Unix this is <see cref="ShellHelper.ResolveSystemTool"/>: trusted system directories first,
/// then rooted <c>PATH</c> entries, which is where Homebrew's docker and ffmpeg live. Windows has no
/// trusted directory to prefer, so it walks the rooted <c>PATH</c> entries for the <c>.exe</c>.
/// A tool that is not installed throws the same <see cref="Win32Exception"/> a failed
/// <c>Process.Start</c> would, so every caller's existing "not installed" handling still applies.
/// </remarks>
internal static class HostTool
{
    public static string Resolve(string name)
    {
        string? path = OperatingSystem.IsWindows() ? ResolveOnWindows(name) : ShellHelper.ResolveSystemTool(name);
        return path ?? throw new Win32Exception(2, $"'{name}' is not installed, or not on PATH.");
    }

    private static string? ResolveOnWindows(string name)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
        {
            return null;
        }

        foreach (string directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry resolves against the working directory: the hazard S4036 is about.
            if (!Path.IsPathRooted(directory))
            {
                continue;
            }

            string candidate = Path.Combine(directory, name + ".exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
