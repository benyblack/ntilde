using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Shell.Mux;

namespace Ntilde.Update;

/// <summary>
/// Whether applying a staged update keeps the running local multiplexer daemon (Phase 5 R10). The daemon survives when
/// the new build speaks its protocol and the apply does not kill it. Velopack's Windows apply kills every process whose
/// image is under the install root (R9), so a daemon a pre-Phase-5 build started from <c>current\</c> dies whatever the
/// GUI does, while one running from its own copy (<see cref="MuxDaemonImage"/>) survives. macOS and Linux updaters kill
/// nothing.
/// </summary>
/// <remarks>
/// The old GUI cannot know the new build's protocol range, so the release states it in Velopack's release notes as the
/// marker line <c>&lt;!-- ntilde-mux-protocol: &lt;min&gt;-&lt;max&gt; --&gt;</c> (release.yml, from
/// <c>scripts/ci/mux-protocol-range.sh</c>). A missing marker counts as compatible: the minimum has been 1 since Phase 0,
/// and the new GUI still meets a mismatch at launch.
/// </remarks>
internal static partial class MuxUpdateCompatibility
{
    /// <summary>
    /// The range in the notes' marker line, or null when there is none or it is malformed. The first marker decides: a
    /// later one never overrides it, and a malformed first one means no range. Whitespace around and inside the marker
    /// is ignored; the marker must sit on one line, and both bounds are ASCII integers from 1, the lower one first.
    /// </summary>
    public static (int Min, int Max)? ParseProtocolRange(string? releaseNotes)
    {
        if (string.IsNullOrEmpty(releaseNotes)) return null;
        Match marker = MarkerPattern().Match(releaseNotes);
        if (!marker.Success) return null;
        Match range = RangePattern().Match(marker.Groups["range"].Value);
        if (!range.Success) return null;
        int min = int.Parse(range.Groups["min"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        int max = int.Parse(range.Groups["max"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return min >= 1 && min <= max ? (min, max) : null;
    }

    /// <summary>
    /// Whether the update keeps the daemon: the ranges overlap (a null new range counts as overlapping) and the daemon's
    /// image is outside the install root (a daemon inside it would be killed). With no install root - not a Windows
    /// Velopack install - the apply kills nothing and the ranges alone decide; with one, an unknown image is never taken
    /// to be outside it.
    /// </summary>
    public static bool KeepsDaemon((int Min, int Max) daemon, (int Min, int Max)? newBuild, string? daemonImagePath, string? installRoot) =>
        (newBuild is not { } build || MuxProtocol.NegotiateVersion(daemon.Min, daemon.Max, build.Min, build.Max) is not null)
        && SurvivesApply(daemonImagePath, installRoot);

    /// <summary>
    /// The startup gate (Program.ShouldAutoApplyUpdateOnStartup): whether a live daemon stops Velopack applying a staged
    /// update as the app starts - only one the apply would kill, its image inside <paramref name="installRoot"/> or not
    /// known to be outside it (a descriptor gone since the probe included). Without an install root nothing is killed,
    /// so nothing blocks and nothing is probed. The protocol is not asked: the new build meets a mismatch at launch.
    /// </summary>
    /// <param name="daemonLive">Whether a daemon answers its endpoint (the 200 ms probe).</param>
    /// <param name="readDescriptor">The descriptor, read after the probe said live; null when it cannot be read.</param>
    /// <param name="daemonImagePath">The descriptor's daemon's image (<see cref="DaemonImagePath"/>).</param>
    public static bool BlocksStartupApply(string? installRoot, Func<bool> daemonLive, Func<MuxEndpointDescriptor?> readDescriptor,
        Func<MuxEndpointDescriptor, string?> daemonImagePath)
    {
        ArgumentNullException.ThrowIfNull(daemonLive);
        ArgumentNullException.ThrowIfNull(readDescriptor);
        ArgumentNullException.ThrowIfNull(daemonImagePath);
        if (installRoot is null || !daemonLive()) return false;
        return readDescriptor() is not { } descriptor || !SurvivesApply(daemonImagePath(descriptor), installRoot);
    }

    /// <summary>
    /// The executable the daemon <paramref name="descriptor"/> names runs from, read from the very process once it is
    /// shown to be that daemon - the name and start time the descriptor recorded (<see cref="MuxDaemonStop.IsDescribedDaemon"/>),
    /// so a recycled pid is never taken for it. Null when that cannot be told: the pid is gone, is this process, is
    /// another process now, or the OS refuses (access denied, a 32/64-bit mismatch). One process lookup; never throws.
    /// </summary>
    public static string? DaemonImagePath(MuxEndpointDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Pid <= 0 || descriptor.Pid == Environment.ProcessId) return null;
        try
        {
            using Process process = Process.GetProcessById(descriptor.Pid);
            if (!MuxDaemonStop.IsDescribedDaemon(process, descriptor)) return null;
            string? image = process.MainModule?.FileName;
            return string.IsNullOrEmpty(image) ? null : image;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the apply leaves a process with this image alone: there is no install root to kill under, or the image is
    /// known and outside it. An unknown or unreadable path is inside.
    /// </summary>
    private static bool SurvivesApply(string? imagePath, string? installRoot)
    {
        if (installRoot is null) return true;
        if (string.IsNullOrEmpty(imagePath)) return false;
        try
        {
            return !MuxDaemonImage.IsSameOrUnder(imagePath, installRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    // One line, up to the first "-->": a marker split across lines is no marker.
    [GeneratedRegex(@"<!--[ \t]*ntilde-mux-protocol[ \t]*:(?<range>[^\r\n]*?)-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    // ASCII digits only ([0-9], not \d, which matches every Unicode digit); nine at most, so int.Parse cannot overflow.
    [GeneratedRegex(@"^[ \t]*(?<min>[0-9]{1,9})[ \t]*-[ \t]*(?<max>[0-9]{1,9})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();
}
