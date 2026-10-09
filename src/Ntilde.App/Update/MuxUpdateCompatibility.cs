using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Daemon;
using Ntilde.Shell.Mux;

namespace Ntilde.Update;

/// <summary>
/// Why applying a staged update does not keep the live local daemon (<see cref="MuxUpdateCompatibility.WhyUpdateStopsDaemon"/>):
/// the reason the update's question gives (<see cref="MuxUpdateCompatibility.SessionLossQuestion"/>).
/// </summary>
internal enum MuxUpdateStopReason
{
    /// <summary>The update keeps it - or, for the question, no reason is given (SessionPersistence off, as before Phase 5).</summary>
    None,

    /// <summary>The new build's protocol range does not overlap the daemon's: the new version cannot speak to it.</summary>
    Protocol,

    /// <summary>
    /// Its image is under the install root, which Velopack's apply kills every process under (R9): a daemon a pre-Phase-5
    /// build started from <c>current\</c>, or one whose own copy could not be staged (<c>MuxDaemonImage.Resolve</c>).
    /// </summary>
    InstallFolder,

    /// <summary>Where it runs from cannot be told - its image or its descriptor cannot be read - so it is never taken to be outside.</summary>
    UnknownImage,
}

/// <summary>Why the startup gate holds back a staged update (<see cref="MuxUpdateCompatibility.StartupApplyHoldFor"/>), for debug.log.</summary>
internal enum StartupApplyHold
{
    /// <summary>It does not: no live daemon, or one the apply leaves alone with persistence on.</summary>
    None,

    /// <summary>The live daemon's image is under the install root: the apply would kill it.</summary>
    InstallFolder,

    /// <summary>The live daemon's image, or its descriptor, cannot be read: never taken to be outside the install root.</summary>
    UnknownImage,

    /// <summary>SessionPersistence is off: any live daemon holds it, as before Phase 5.</summary>
    PersistenceOff,
}

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
    /// Whether the update keeps the daemon, and if not why: <see cref="MuxUpdateStopReason.None"/> keeps it. It is kept
    /// when the ranges overlap (a null new range counts as overlapping) and the daemon's image is outside the install root
    /// (a daemon inside it would be killed). With no install root - not a Windows Velopack install - the apply kills
    /// nothing and the ranges alone decide; with one, an unknown image is never taken to be outside it. The protocol
    /// decides first - a daemon the new build cannot speak to is not kept wherever it runs - then where it runs from
    /// (<see cref="WhereApplyLeaves"/>).
    /// </summary>
    public static MuxUpdateStopReason WhyUpdateStopsDaemon((int Min, int Max) daemon, (int Min, int Max)? newBuild, string? daemonImagePath, string? installRoot)
    {
        if (newBuild is { } build && MuxProtocol.NegotiateVersion(daemon.Min, daemon.Max, build.Min, build.Max) is null) return MuxUpdateStopReason.Protocol;
        return WhereApplyLeaves(daemonImagePath, installRoot) switch
        {
            ApplyKills.No => MuxUpdateStopReason.None,
            ApplyKills.Yes => MuxUpdateStopReason.InstallFolder,
            _ => MuxUpdateStopReason.UnknownImage,
        };
    }

    /// <summary>
    /// The in-app update's question for <paramref name="running"/> sessions: "N multiplexed session(s) will be closed by the
    /// update", with <paramref name="reason"/> in brackets - none for <see cref="MuxUpdateStopReason.None"/> (persistence
    /// off: worded as before Phase 5, giving no reason that may not be true).
    /// </summary>
    public static string SessionLossQuestion(int running, MuxUpdateStopReason reason)
    {
        string sessionsClosed = running == 1
            ? "1 multiplexed session will be closed by the update"
            : $"{running} multiplexed sessions will be closed by the update";
        string why = reason switch
        {
            MuxUpdateStopReason.Protocol => running == 1 ? " (the new version cannot keep it)" : " (the new version cannot keep them)",
            MuxUpdateStopReason.InstallFolder => " (the multiplexer is running from the install folder, so the update has to stop it)",
            MuxUpdateStopReason.UnknownImage => " (the multiplexer could not be checked, so the update has to stop it)",
            _ => "",
        };
        return sessionsClosed + why + ".";
    }

    /// <summary>
    /// The startup gate (Program.ShouldAutoApplyUpdateOnStartup): whether a live daemon stops Velopack applying a staged
    /// update as the app starts, and why - any reason but <see cref="StartupApplyHold.None"/> holds it, and Program.Main
    /// logs the reason. One the apply would kill always does: its image inside <paramref name="installRoot"/> or not known
    /// to be outside it (a descriptor gone since the probe included). Any other holds it only with SessionPersistence off,
    /// which keeps the gate exactly as it was before Phase 5 (any live daemon). The protocol is not asked: the new build
    /// meets a mismatch at launch. Only a live daemon costs more than the probe: its image is read only under an install
    /// root, and the setting only when the image does not already decide.
    /// </summary>
    /// <param name="daemonLive">Whether a daemon answers its endpoint (the 200 ms probe).</param>
    /// <param name="readDescriptor">The descriptor, read after the probe said live; null when it cannot be read.</param>
    /// <param name="daemonImagePath">The descriptor's daemon's image (<see cref="DaemonImagePath"/>).</param>
    /// <param name="persistenceOff">Whether the persisted SessionPersistence is off (false when it cannot be read).</param>
    public static StartupApplyHold StartupApplyHoldFor(string? installRoot, Func<bool> daemonLive, Func<MuxEndpointDescriptor?> readDescriptor,
        Func<MuxEndpointDescriptor, string?> daemonImagePath, Func<bool> persistenceOff)
    {
        ArgumentNullException.ThrowIfNull(daemonLive);
        ArgumentNullException.ThrowIfNull(readDescriptor);
        ArgumentNullException.ThrowIfNull(daemonImagePath);
        ArgumentNullException.ThrowIfNull(persistenceOff);
        if (!daemonLive()) return StartupApplyHold.None;
        if (installRoot is not null)
        {
            ApplyKills kills = readDescriptor() is { } descriptor ? WhereApplyLeaves(daemonImagePath(descriptor), installRoot) : ApplyKills.Unknown;
            if (kills == ApplyKills.Yes) return StartupApplyHold.InstallFolder;
            if (kills == ApplyKills.Unknown) return StartupApplyHold.UnknownImage;
        }

        return persistenceOff() ? StartupApplyHold.PersistenceOff : StartupApplyHold.None;
    }

    /// <summary>
    /// The debug.log line for a startup gate that held (Program.Main logs it once the log is up); null for
    /// <see cref="StartupApplyHold.None"/>. Velopack is not asked whether an update is staged, so the line says "if any".
    /// </summary>
    public static string? DescribeStartupApplyHold(StartupApplyHold hold)
    {
        string? why = hold switch
        {
            StartupApplyHold.InstallFolder => "the multiplexer is running from the install folder, so applying it would stop every shell",
            StartupApplyHold.UnknownImage => "where the multiplexer is running from could not be checked, so applying it might stop every shell",
            StartupApplyHold.PersistenceOff => "session persistence is off and the multiplexer is running, as before Phase 5",
            _ => null,
        };
        return why is null ? null : $"[Update] a staged update, if any, was not applied at startup: {why}; the in-app update asks first";
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

    /// <summary>Whether the apply kills a process with a given image (<see cref="WhereApplyLeaves"/>).</summary>
    private enum ApplyKills
    {
        /// <summary>No install root to kill under, or the image is known and outside it.</summary>
        No,

        /// <summary>The image is under the install root.</summary>
        Yes,

        /// <summary>The image is unknown or unreadable: never taken to be outside.</summary>
        Unknown,
    }

    /// <summary>
    /// Whether the apply leaves a process with this image alone: there is no install root to kill under, or the image is
    /// known and outside it. An unknown or unreadable path counts as inside (<see cref="ApplyKills.Unknown"/>).
    /// </summary>
    private static ApplyKills WhereApplyLeaves(string? imagePath, string? installRoot)
    {
        if (installRoot is null) return ApplyKills.No;
        if (string.IsNullOrEmpty(imagePath)) return ApplyKills.Unknown;
        try
        {
            return MuxDaemonImage.IsSameOrUnder(imagePath, installRoot) ? ApplyKills.Yes : ApplyKills.No;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return ApplyKills.Unknown;
        }
    }

    // One line, up to the first "-->": a marker split across lines is no marker.
    [GeneratedRegex(@"<!--[ \t]*ntilde-mux-protocol[ \t]*:(?<range>[^\r\n]*?)-->", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    // ASCII digits only ([0-9], not \d, which matches every Unicode digit); nine at most, so int.Parse cannot overflow.
    [GeneratedRegex(@"^[ \t]*(?<min>[0-9]{1,9})[ \t]*-[ \t]*(?<max>[0-9]{1,9})[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();
}
