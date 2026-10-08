using System.Globalization;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Phase 5 §2 (Task 23): a daemon from another build. An update keeps a compatible daemon running (spec R9, R10), so the
/// new build can find the previous build's daemon still serving its shells; and the remote install flow replaces
/// ntilde-mux's binary with a rename, which the daemon running the old one survives. The window offers to restart such a
/// daemon, once per launch for each endpoint (<see cref="Launch"/>). This holds the deciding and the wording, so both are
/// testable without a window.
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

    /// <summary>The local restart sent <c>shutdown</c>, and the daemon neither exited nor could be terminated.</summary>
    public const string NotStopped = "The old multiplexer could not be stopped; its shells may still be running.";

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
    /// <param name="thisBuild">This build's version (<see cref="AppVersionInfo.Version"/>), which is also the ntilde-mux version its install flow installs.</param>
    public static bool IsFromAnotherBuild(string? daemonVersion, string? thisBuild)
    {
        string daemon = Known(daemonVersion);
        string app = Known(thisBuild);
        return daemon.Length > 0 && app.Length > 0 && !string.Equals(daemon, app, StringComparison.Ordinal);
    }

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

    /// <summary>A remote daemon's notice: its host, how its version relates to the one this app installs, and how many shells a restart closes.</summary>
    public static string RemoteMessage(string host, string daemonVersion, string thisBuild, int shells)
    {
        string from = CompareVersions(daemonVersion, thisBuild) switch
        {
            < 0 => "a previous version",
            > 0 => "a newer version",
            _ => "a different version",
        };
        return $"ntilde-mux on {RemoteOutputText.Quote(host)} is from {from} ({Shown(daemonVersion)}); restart it when convenient{Closes(shells)}.";
    }

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
                ? "The multiplexer is stopped, and one of this version started."
                : $"{where} is stopped; reconnecting starts the installed version.",
            1 => $"1 shell running in {where} will be closed.",
            _ => $"{shells} shells running in {where} will be closed.",
        };
    }

    /// <summary>The local restart's last look reached no daemon at all: nothing to restart.</summary>
    public const string NotRunning = "The multiplexer is not running; nothing to restart.";

    /// <summary>
    /// A confirmed restart found nothing to restart at its last look: the daemon it reached is this build's after all
    /// (another window restarted it), or reports no version. <paramref name="host"/> is null for this computer's daemon.
    /// </summary>
    public static string NothingToRestart(string? host, string? daemonVersion)
    {
        if (host is null)
        {
            return Known(daemonVersion).Length == 0
                ? "The multiplexer does not report its build; it was not restarted."
                : "The multiplexer is already from this build; nothing to restart.";
        }

        string where = $"ntilde-mux on {RemoteOutputText.Quote(host)}";
        return Known(daemonVersion).Length == 0
            ? $"{where} does not report its version; it was not restarted."
            : $"{where} is already the version this app installs; nothing to restart.";
    }

    /// <summary>A second click, from this window or another, while that daemon's restart is still asking or stopping it.</summary>
    public static string AlreadyRestarting(string? host) =>
        host is null ? "The multiplexer is already being restarted." : $"ntilde-mux on {RemoteOutputText.Quote(host)} is already being restarted.";

    /// <summary>The remote restart found no connection to send <c>shutdown</c> over.</summary>
    public static string NotConnected(string host) => $"ntilde-mux on {RemoteOutputText.Quote(host)} is not connected; it was not restarted.";

    /// <summary>The remote restart's <c>shutdown</c> failed: <paramref name="reason"/> may be the server's own words, quoted.</summary>
    public static string ShutdownFailed(string host, string reason) =>
        $"ntilde-mux on {RemoteOutputText.Quote(host)} could not be told to restart ({RemoteOutputText.Quote(reason)}); it keeps running.";

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
    }
}
