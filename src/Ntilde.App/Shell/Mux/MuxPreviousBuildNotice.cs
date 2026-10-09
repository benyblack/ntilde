using System.Globalization;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Phase 5 §2 (Task 23): a daemon from another build. An update keeps a compatible daemon running (spec R9, R10), so the
/// new build can find the previous build's daemon still serving its shells; and the remote install flow replaces
/// ntilde-mux's binary with a rename, which the daemon running the old one survives. The window offers to restart such a
/// daemon, once per launch for each endpoint (<see cref="Launch"/>). A remote daemon is judged against the version
/// installed on its host, not this app's (final review I1, <see cref="DecideRemote"/>): an app update never replaces that
/// binary, so when it is as old as the running one the offer is the update, never a restart. This holds the deciding and
/// the wording, so both are testable without a window.
/// </summary>
/// <remarks>
/// Every version shown here came over a socket - from a remote host, or from a local daemon any process of the user can
/// start - so it is quoted (<see cref="RemoteOutputText.Quote"/>, ruling P2) before it reaches a toast, and so is a host.
/// </remarks>
internal static class MuxPreviousBuildNotice
{
    /// <summary>The notice's title, for the local daemon and a remote one alike, and for a restart's outcome.</summary>
    public const string Title = "Multiplexer";

    /// <summary>The local notice's action, and the version-mismatch fallback's.</summary>
    public const string LocalActionLabel = "Restart multiplexer now";

    /// <summary>The restart question's confirm button.</summary>
    public const string ConfirmButton = "Restart";

    /// <summary>What ntilde-mux reports when it cannot tell its own version (Task 20): unknown, as none is.</summary>
    private const string UnknownVersion = "0.0.0";

    /// <summary>The most digits a release or prerelease number may have: nine always fit an <see cref="int"/>.</summary>
    private const int MaxNumberDigits = 9;

    /// <summary>The remote notice's action. It names its host: a toast that merges several notices offers only the last action raised.</summary>
    public static string RemoteActionLabel(string host) => $"Restart ntilde-mux on {RemoteOutputText.Quote(host)}";

    /// <summary>
    /// Whether <paramref name="daemonVersion"/> is another build's than <paramref name="thisBuild"/>: both known and
    /// different, build metadata aside. An unknown version on either side - none, empty, or <c>0.0.0</c> - never is (the
    /// Task 20 ruling): a dev build, or a daemon that reports nothing, is not told it is out of date.
    /// </summary>
    /// <param name="daemonVersion">What the daemon reported in its welcome (<c>MuxClient.ServerVersion</c>).</param>
    /// <param name="thisBuild">
    /// The version a restart would start: this build's (<see cref="AppVersionInfo.Version"/>) for the local daemon, and for
    /// a remote one the version installed on its host (<see cref="DecideRemote"/>).
    /// </param>
    public static bool IsFromAnotherBuild(string? daemonVersion, string? thisBuild)
    {
        string daemon = Known(daemonVersion);
        string app = Known(thisBuild);
        return daemon.Length > 0 && app.Length > 0 && !string.Equals(daemon, app, StringComparison.Ordinal);
    }

    /// <summary>What a connection to a daemon is offered (final review I1): nothing, a restart, or - remote only - an update.</summary>
    internal enum NoticeOffer
    {
        None,
        Restart,
        Update,
    }

    /// <summary>
    /// Final review I1: what a remote ntilde-mux running <paramref name="running"/> is offered. A remote restart only stops
    /// the daemon; the next connect's proxy starts the binary installed on the host, which an app update never replaces. So
    /// a restart is offered only when the version the install flow recorded for the host (<paramref name="installed"/>,
    /// <c>SshMuxOptions.RemoteDaemonVersion</c>) is known and newer than the running one, by <see cref="CompareVersions"/>:
    /// the install flow replaced the binary under a running daemon. The record is per profile and per computer, so it can
    /// be stale (residual N1, ruling (a)): a running version ahead of it, or one with no order against it, is never offered
    /// a restart - it would start the same binary again. A host whose ntilde-mux is older than this app (<paramref name="app"/>)
    /// is offered the update instead. An unknown installed version (none, empty, or <c>0.0.0</c>) never gets a restart, and
    /// an unknown running one gets nothing.
    /// </summary>
    public static NoticeOffer DecideRemote(string? running, string? installed, string? app)
    {
        if (Known(running).Length == 0) return NoticeOffer.None;
        if (IsBehindInstalled(running, installed)) return NoticeOffer.Restart;
        return CompareVersions(running, app) < 0 ? NoticeOffer.Update : NoticeOffer.None;
    }

    /// <summary>
    /// Whether a remote daemon running <paramref name="running"/> is older than the version recorded as installed on its
    /// host (<paramref name="installed"/>), both known: the only case a remote restart is offered for, and what its last look
    /// checks again (residual N1).
    /// </summary>
    public static bool IsBehindInstalled(string? running, string? installed) =>
        Known(running).Length > 0 && Known(installed).Length > 0 && CompareVersions(running, installed) < 0;

    /// <summary>
    /// Ruling R-a: how <paramref name="a"/> orders against <paramref name="b"/> - negative when it is older, positive when
    /// newer, 0 when neither - or null when either does not parse. SemVer 2.0.0 precedence: build metadata is ignored; the
    /// release parts (1 to 4 numbers) compare as numbers, a missing one counting as 0; a prerelease comes before its
    /// release; prerelease identifiers compare one by one, numbers numerically and before words, words in ASCII order, and
    /// a set that runs out first comes first. A number of more than nine digits, an empty part or identifier, or anything
    /// but ASCII letters, digits and <c>-</c> in a prerelease, does not parse.
    /// </summary>
    public static int? CompareVersions(string? a, string? b)
    {
        if (!TryParse(a, out int[] aRelease, out string[] aPre) || !TryParse(b, out int[] bRelease, out string[] bPre)) return null;

        for (int i = 0; i < Math.Max(aRelease.Length, bRelease.Length); i++)
        {
            int byPart = (i < aRelease.Length ? aRelease[i] : 0).CompareTo(i < bRelease.Length ? bRelease[i] : 0);
            if (byPart != 0) return Math.Sign(byPart);
        }

        if (aPre.Length == 0 || bPre.Length == 0) return (aPre.Length == 0).CompareTo(bPre.Length == 0); // a release comes after its prereleases
        for (int i = 0; i < Math.Min(aPre.Length, bPre.Length); i++)
        {
            int byIdentifier = CompareIdentifiers(aPre[i], bPre[i]);
            if (byIdentifier != 0) return byIdentifier;
        }

        return aPre.Length.CompareTo(bPre.Length);
    }

    /// <summary>The local daemon's notice: how its version relates to this build's, and how many shells a restart closes.</summary>
    public static string LocalMessage(string daemonVersion, string thisBuild, int shells)
    {
        string from = CompareVersions(daemonVersion, thisBuild) switch
        {
            < 0 => "the previous build",
            > 0 => "a newer build",
            _ => "a different build",
        };
        return $"The multiplexer is from {from} ({Shown(daemonVersion)}); restart it when convenient{Closes(shells)}.";
    }

    /// <summary>
    /// A remote daemon's restart notice: its host, its version - always older than the one installed on that host, which
    /// a restart starts (<see cref="DecideRemote"/>, residual N1) - and how many shells a restart closes.
    /// </summary>
    public static string RemoteMessage(string host, string daemonVersion, int shells) =>
        $"ntilde-mux on {RemoteOutputText.Quote(host)} is from a previous version ({Shown(daemonVersion)}); restart it when convenient{Closes(shells)}.";

    /// <summary>
    /// Final review I1: a remote daemon's update notice, when the ntilde-mux on its host is older than this app. The install
    /// flow it offers renames the new binary over the old one and never stops the running daemon, so its shells keep running.
    /// </summary>
    public static string RemoteUpdateMessage(string host, string running, string app) =>
        $"ntilde-mux on {RemoteOutputText.Quote(host)} is older ({Shown(running)}) than this app ({Shown(app)}); update it when convenient. "
        + "Updating replaces the binary; your shells keep running until you restart it.";

    /// <summary>The restart question's window title; <paramref name="host"/> is null for this computer's daemon.</summary>
    public static string ConfirmTitle(string? host) => host is null ? "Restart Multiplexer" : "Restart ntilde-mux";

    /// <summary>The restart question; <paramref name="host"/> is null for this computer's daemon.</summary>
    public static string ConfirmHeading(string? host) => host is null ? "Restart the multiplexer?" : $"Restart ntilde-mux on {RemoteOutputText.Quote(host)}?";

    /// <summary>
    /// What the restart closes: <paramref name="shells"/> is the daemon's running count, -1 when it is unknown. With none
    /// running (ruling R-b) it says what the restart does instead.
    /// </summary>
    public static string ConfirmMessage(string? host, int shells)
    {
        string where = host is null ? "the multiplexer" : $"ntilde-mux on {RemoteOutputText.Quote(host)}";
        return shells switch
        {
            < 0 => $"All shells running in {where} will be closed.",
            0 => host is null
                ? "The multiplexer restarts with this version."
                : $"{where} is stopped; reconnecting starts the installed version.",
            1 => $"1 shell running in {where} will be closed.",
            _ => $"{shells} shells running in {where} will be closed.",
        };
    }

    /// <summary>
    /// Why a confirmed restart did not happen as asked (review items 3 and 4, round 2 M3/M4). Every outcome notice is worded
    /// from one of these by <see cref="Outcome"/> - never from what a daemon said.
    /// </summary>
    internal enum RestartOutcome
    {
        /// <summary>A restart of that daemon, from this window or another, is still asking or stopping it.</summary>
        AlreadyRestarting,

        /// <summary>
        /// At the last look the daemon is this build's after all - for a remote one, not behind the version installed on its
        /// host (residual N1): the installed version or newer.
        /// </summary>
        AlreadyThisBuild,

        /// <summary>At the last look the daemon reports no version.</summary>
        NoVersion,

        /// <summary>At the last look the version installed on a remote daemon's host is not known (final review I1).</summary>
        InstalledVersionUnknown,

        /// <summary>At the last look no daemon is advertised and none answers.</summary>
        NotRunning,

        /// <summary>At the last look a daemon is advertised but does not answer.</summary>
        Unreachable,

        /// <summary>The host has no connection to send <c>shutdown</c> over.</summary>
        NotConnected,

        /// <summary><c>shutdown</c> could not be sent, or was not answered.</summary>
        ShutdownFailed,

        /// <summary>The daemon neither exited after <c>shutdown</c> nor could be terminated.</summary>
        NotStopped,

        /// <summary><c>shutdown</c> went out, but no descriptor says which process to watch, so the stop cannot be checked.</summary>
        StopUnconfirmed,

        /// <summary>Something unexpected failed on the way (the log has it).</summary>
        Failed,
    }

    /// <summary>The words of <paramref name="outcome"/>, for this computer's daemon (<paramref name="host"/> null) or the one on <paramref name="host"/>.</summary>
    public static string Outcome(RestartOutcome outcome, string? host)
    {
        string it = host is null ? "The multiplexer" : $"ntilde-mux on {RemoteOutputText.Quote(host)}";
        string old = host is null ? "The old multiplexer" : $"The old ntilde-mux on {RemoteOutputText.Quote(host)}";
        return outcome switch
        {
            RestartOutcome.AlreadyRestarting => $"{it} is already being restarted.",
            RestartOutcome.AlreadyThisBuild => host is null
                ? "The multiplexer is already from this build; nothing to restart."
                : $"{it} is already the installed version or newer; nothing to restart.",
            RestartOutcome.NoVersion => host is null
                ? "The multiplexer does not report its build; it was not restarted."
                : $"{it} does not report its version; it was not restarted.",
            RestartOutcome.InstalledVersionUnknown => host is null
                ? "The installed multiplexer's version is not known; it was not restarted."
                : $"The version of ntilde-mux installed on {RemoteOutputText.Quote(host)} is not known; it was not restarted.",
            RestartOutcome.NotRunning => $"{it} is not running; nothing to restart.",
            RestartOutcome.Unreachable => $"{it} could not be reached; nothing was restarted.",
            RestartOutcome.NotConnected => $"{it} is not connected; it was not restarted.",
            RestartOutcome.ShutdownFailed => $"{it} could not be told to restart; it keeps running.",
            RestartOutcome.NotStopped => $"{old} could not be stopped; its shells may still be running.",
            RestartOutcome.StopUnconfirmed => $"{old} was told to stop, but whether it did could not be checked.",
            RestartOutcome.Failed => host is null
                ? "The multiplexer restart failed; see the log."
                : $"The restart of ntilde-mux on {RemoteOutputText.Quote(host)} failed; see the log.",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
        };
    }

    /// <summary>
    /// The outcome of a daemon found, at the last look, not to be another build's: this build's after all, or one that
    /// reports no version.
    /// </summary>
    public static RestartOutcome NothingToRestart(string? daemonVersion) =>
        Known(daemonVersion).Length == 0 ? RestartOutcome.NoVersion : RestartOutcome.AlreadyThisBuild;

    /// <summary>
    /// The outcome of a remote daemon found, at the last look, not behind the version installed on its host (final review
    /// I1, residual N1): it reports no version, no installed version is known, or it is the installed version or newer.
    /// </summary>
    public static RestartOutcome NothingToRestartRemote(string? running, string? installed) =>
        Known(running).Length == 0 ? RestartOutcome.NoVersion
        : Known(installed).Length == 0 ? RestartOutcome.InstalledVersionUnknown
        : RestartOutcome.AlreadyThisBuild;

    /// <summary>
    /// A multiplexer notice as the window raised it (tests: <c>MainWindow.MuxNoticeRaisedForTest</c>): the endpoint it is
    /// about, the outcome it reports (null for the offer itself), the key it merges by, its words and its action.
    /// </summary>
    internal readonly record struct Raised(MuxEndpointId Endpoint, RestartOutcome? Outcome, string Key, string Message, Controls.PersistenceNoticeAction? Action);

    private static string Known(string? version)
    {
        string v = AppVersionInfo.WithoutBuildMetadata(version);
        return v == UnknownVersion ? string.Empty : v;
    }

    private static string Shown(string version) => RemoteOutputText.Quote(AppVersionInfo.WithoutBuildMetadata(version));

    /// <summary>Ruling R-b: the clause, or nothing when no shell runs.</summary>
    private static string Closes(int shells) => shells switch
    {
        0 => string.Empty,
        1 => " \u2014 this closes its 1 shell",
        _ => $" \u2014 this closes its {shells} shells",
    };

    private static bool TryParse(string? version, out int[] release, out string[] prerelease)
    {
        release = [];
        prerelease = [];
        string v = AppVersionInfo.WithoutBuildMetadata(version);
        int dash = v.IndexOf('-', StringComparison.Ordinal);
        string core = dash < 0 ? v : v[..dash];
        string[] parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryNumber(parts[i], out numbers[i])) return false;
        }

        if (dash >= 0)
        {
            string[] identifiers = v[(dash + 1)..].Split('.');
            foreach (string identifier in identifiers)
            {
                if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                if (identifier.All(char.IsAsciiDigit) && identifier.Length > MaxNumberDigits) return false;
            }

            prerelease = identifiers;
        }

        release = numbers;
        return true;
    }

    private static bool TryNumber(string text, out int value)
    {
        value = 0;
        return text.Length is > 0 and <= MaxNumberDigits
            && text.All(char.IsAsciiDigit)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static int CompareIdentifiers(string a, string b)
    {
        bool aNumber = TryNumber(a, out int an);
        bool bNumber = TryNumber(b, out int bn);
        if (aNumber && bNumber) return an.CompareTo(bn);
        if (aNumber != bNumber) return aNumber ? -1 : 1; // numbers come before words
        return Math.Sign(string.CompareOrdinal(a, b));
    }

    /// <summary>
    /// What this launch has done about daemons of another build, for each endpoint (local, or one SSH profile): whether it
    /// has offered a restart - once per launch (a reconnect to the same daemon, or another window's connection to it, offers
    /// nothing more), released after a restart or any way one did not happen, so the next connection may offer it again -
    /// and whether a restart runs now, so a second click, from any window, sends nothing. Thread-safe.
    /// </summary>
    internal sealed class Launch
    {
        private readonly HashSet<MuxEndpointId> _offered = [];
        private readonly HashSet<MuxEndpointId> _restarting = [];

        /// <summary>The process's: one launch, shared by its windows.</summary>
        public static Launch Process { get; } = new();

        /// <summary>True for the first offer for <paramref name="endpoint"/> since it was last released.</summary>
        public bool TryOffer(MuxEndpointId endpoint)
        {
            lock (_offered) return _offered.Add(endpoint);
        }

        /// <summary>The next connection to <paramref name="endpoint"/> may offer it again.</summary>
        public void ReleaseOffer(MuxEndpointId endpoint)
        {
            lock (_offered) _offered.Remove(endpoint);
        }

        /// <summary>True when no restart of <paramref name="endpoint"/>'s daemon runs: this one does now, until <see cref="EndRestart"/>.</summary>
        public bool TryBeginRestart(MuxEndpointId endpoint)
        {
            lock (_restarting) return _restarting.Add(endpoint);
        }

        public void EndRestart(MuxEndpointId endpoint)
        {
            lock (_restarting) _restarting.Remove(endpoint);
        }

        /// <summary>Whether a restart of <paramref name="endpoint"/>'s daemon runs now ("Attach to session…" opens nothing on it).</summary>
        public bool IsRestarting(MuxEndpointId endpoint)
        {
            lock (_restarting) return _restarting.Contains(endpoint);
        }
    }
}
