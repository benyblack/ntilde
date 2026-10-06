using System.Globalization;
using System.Text.RegularExpressions;
using Ntilde.Platform.Ssh.Exec;

namespace Ntilde.Shell.Mux.Remote;

/// <summary>What <see cref="RemoteHostProbe.Parse"/> made of a host: <see cref="RemoteHostFacts"/> or <see cref="RemoteHostRefusal"/>.</summary>
internal abstract record RemoteHostProbeOutcome;

/// <summary>A host <c>ntilde-mux</c> runs on: the release asset it takes and its <c>$HOME</c>.</summary>
/// <param name="Rid"><c>linux-x64</c>, <c>linux-arm64</c> or <c>osx-arm64</c>.</param>
/// <param name="Home">The remote <c>$HOME</c>, absolute.</param>
internal sealed record RemoteHostFacts(string Rid, string Home) : RemoteHostProbeOutcome;

/// <summary>A host the install flow will not install on, and the reason the dialog shows.</summary>
internal sealed record RemoteHostRefusal(string Reason) : RemoteHostProbeOutcome;

/// <summary>
/// The install flow's first step (Phase 4 spec §9 step 1): one exec that prints <c>uname -sm</c>, the C
/// library's first line and <c>HOME=</c>, and a pure parser that maps it to the release asset the host
/// takes, or refuses the host with the exact reason.
/// </summary>
/// <remarks>
/// <para>
/// The binary is NativeAOT, built on Ubuntu 22.04 against glibc (spec §10.3), so it needs a glibc at
/// least that new (<see cref="MinimumGlibc"/>) and cannot run on musl. macOS has no glibc and skips the
/// check. Only <c>linux-x64</c>, <c>linux-arm64</c> and <c>osx-arm64</c> are published.
/// </para>
/// <para>
/// The libc line is <c>ldd --version</c>'s first line (<c>ldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35</c>,
/// <c>ldd (GNU libc) 2.39</c>, musl's <c>musl libc (x86_64)</c>, or the shell's error when there is no
/// ldd), or <c>getconf GNU_LIBC_VERSION</c>'s (<c>glibc 2.36</c>) when ldd printed nothing.
/// </para>
/// </remarks>
internal static partial class RemoteHostProbe
{
    /// <summary>
    /// The probe. sshd hands it to the login shell, which may be fish, tcsh or nushell, so it is one
    /// single-quoted <c>sh -c</c> script with no single quote inside. ldd's own output goes to stderr on
    /// musl, hence <c>2&gt;&amp;1</c>.
    /// </summary>
    public const string Command = "sh -c 'uname -sm; (ldd --version 2>&1 || getconf GNU_LIBC_VERSION 2>&1) | head -n 1; printf \"HOME=%s\\n\" \"$HOME\"'";

    /// <summary>Ubuntu 22.04's glibc, the release build's (spec §10.3): the oldest the binary loads on.</summary>
    public static readonly Version MinimumGlibc = new(2, 35);

    private const string HomePrefix = "HOME=";
    private const int MaxQuotedLength = 200;

    /// <summary>
    /// Maps the probe's result to the host's facts, or a refusal. A failed probe (ssh's exit 255, a
    /// native transport failure) is a refusal that carries ssh's reason.
    /// </summary>
    /// <remarks>
    /// The lines are read back from the last <c>HOME=</c> line, the libc line and the <c>uname</c> line
    /// just above it, so anything a login shell's rc files print first does not matter.
    /// </remarks>
    public static RemoteHostProbeOutcome Parse(SshExecResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.ExitCode != 0)
        {
            string exit = result.ExitCode is { } code ? $"exit {code.ToString(CultureInfo.InvariantCulture)}" : "no exit status";
            string tail = LastLines(result.Stderr);
            return new RemoteHostRefusal(tail.Length == 0
                ? $"Probing the host failed ({exit})"
                : $"Probing the host failed ({exit}): {tail}");
        }

        string[] lines = result.Stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int home = Array.FindLastIndex(lines, l => l.StartsWith(HomePrefix, StringComparison.Ordinal));
        if (home < 1)
        {
            return Unrecognized(result.Stdout);
        }

        string unameLine = lines[home >= 2 ? home - 2 : home - 1].Trim();
        string libcLine = home >= 2 ? lines[home - 1].Trim() : string.Empty;
        string[] uname = unameLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (uname.Length != 2)
        {
            return Unrecognized(result.Stdout);
        }

        string homeDirectory = lines[home].Substring(HomePrefix.Length);
        if (homeDirectory.Length == 0)
        {
            return new RemoteHostRefusal("The remote HOME is not set");
        }

        if (homeDirectory[0] != '/')
        {
            return new RemoteHostRefusal($"The remote HOME is not an absolute path: {Quote(homeDirectory)}");
        }

        string kernel = uname[0];
        string machine = uname[1];
        switch (kernel)
        {
            case "Darwin":
                return machine switch
                {
                    "arm64" => new RemoteHostFacts("osx-arm64", homeDirectory),
                    "x86_64" => new RemoteHostRefusal("Intel Macs are not supported"),
                    _ => new RemoteHostRefusal($"macOS on {Quote(machine)} is not supported"),
                };

            case "Linux":
            {
                string? rid = machine switch
                {
                    "x86_64" => "linux-x64",
                    "aarch64" or "arm64" => "linux-arm64",
                    _ => null,
                };
                if (rid is null)
                {
                    return new RemoteHostRefusal($"Linux on {Quote(machine)} is not supported");
                }

                return CheckGlibc(libcLine) ?? (RemoteHostProbeOutcome)new RemoteHostFacts(rid, homeDirectory);
            }

            default:
                return new RemoteHostRefusal($"{Quote(kernel)} is not supported");
        }
    }

    /// <summary>A refusal for a C library the binary cannot load on; <see langword="null"/> for a new enough glibc.</summary>
    private static RemoteHostRefusal? CheckGlibc(string libcLine)
    {
        if (libcLine.Contains("musl", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteHostRefusal("musl libc is not supported");
        }

        // The last N.N on the line: "ldd (Ubuntu GLIBC 2.35-0ubuntu3.8) 2.35" ends with the version itself.
        Match? last = VersionPattern().Matches(libcLine).LastOrDefault();
        if (last is null
            || !int.TryParse(last.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int major)
            || !int.TryParse(last.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            return new RemoteHostRefusal(libcLine.Length == 0
                ? "Could not tell the C library version: the host printed none"
                : $"Could not tell the C library version from: {Quote(libcLine)}");
        }

        var glibc = new Version(major, minor);
        return glibc < MinimumGlibc
            ? new RemoteHostRefusal($"glibc {glibc.ToString(2)} is older than {MinimumGlibc.ToString(2)}")
            : null;
    }

    private static RemoteHostRefusal Unrecognized(string stdout)
    {
        string text = LastLines(stdout);
        return new RemoteHostRefusal(text.Length == 0
            ? "The host probe's output was not recognized"
            : $"The host probe's output was not recognized: {text}");
    }

    /// <summary>The last few non-blank lines of <paramref name="text"/>, joined with " / ", at most <see cref="MaxQuotedLength"/> characters.</summary>
    internal static string LastLines(string? text, int count = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string[] lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return Quote(string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - count))));
    }

    /// <summary>Host-supplied text, cut to <see cref="MaxQuotedLength"/> characters, with control characters dropped.</summary>
    private static string Quote(string text)
    {
        string clean = new(text.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaxQuotedLength ? clean : string.Concat(clean.AsSpan(0, MaxQuotedLength), "\u2026");
    }

    [GeneratedRegex("([0-9]+)\\.([0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
