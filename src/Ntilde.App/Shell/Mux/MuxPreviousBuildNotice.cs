using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Phase 5 §2 (Task 23): a daemon from another build. An update keeps a compatible daemon running (spec R9, R10), so the
/// new build can find the previous build's daemon still serving its shells; and the remote install flow replaces
/// ntilde-mux's binary with a rename, which the daemon running the old one survives. The window offers to restart such a
/// daemon, once per launch for each endpoint (<see cref="Offered"/>). This holds the deciding and the wording, so both are
/// testable without a window.
/// </summary>
/// <remarks>
/// Every version shown here came over a socket - from a remote host, or from a local daemon any process of the user can
/// start - so it is quoted (<see cref="RemoteOutputText.Quote"/>, ruling P2) before it reaches a toast.
/// </remarks>
internal static class MuxPreviousBuildNotice
{
    /// <summary>The notice's title, for the local daemon and a remote one alike.</summary>
    public const string Title = "Multiplexer";

    /// <summary>The local notice's action, and the version-mismatch fallback's.</summary>
    public const string LocalActionLabel = "Restart multiplexer now";

    /// <summary>The restart question's confirm button.</summary>
    public const string ConfirmButton = "Restart";

    /// <summary>What ntilde-mux reports when it cannot tell its own version (Task 20): unknown, as none is.</summary>
    private const string UnknownVersion = "0.0.0";

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

    /// <summary>The local daemon's notice: its version, and how many shells a restart closes.</summary>
    public static string LocalMessage(string daemonVersion, int shells) =>
        $"The multiplexer is from the previous build ({Shown(daemonVersion)}); restart it when convenient — this closes its {Shells(shells)}.";

    /// <summary>A remote daemon's notice: its host, its version, and how many shells a restart closes.</summary>
    public static string RemoteMessage(string host, string daemonVersion, int shells) =>
        $"ntilde-mux on {RemoteOutputText.Quote(host)} is from a previous version ({Shown(daemonVersion)}); restart it when convenient — this closes its {Shells(shells)}.";

    /// <summary>The restart question's window title; <paramref name="host"/> is null for this computer's daemon.</summary>
    public static string ConfirmTitle(string? host) => host is null ? "Restart Multiplexer" : "Restart ntilde-mux";

    /// <summary>The restart question; <paramref name="host"/> is null for this computer's daemon.</summary>
    public static string ConfirmHeading(string? host) => host is null ? "Restart the multiplexer?" : $"Restart ntilde-mux on {RemoteOutputText.Quote(host)}?";

    /// <summary>What the restart closes: <paramref name="shells"/> is the daemon's running count, -1 when it is unknown.</summary>
    public static string ConfirmMessage(string? host, int shells)
    {
        string where = host is null ? "the multiplexer" : $"ntilde-mux on {RemoteOutputText.Quote(host)}";
        return shells switch
        {
            < 0 => $"All shells running in {where} will be closed.",
            1 => $"1 shell running in {where} will be closed.",
            _ => $"{shells} shells running in {where} will be closed.",
        };
    }

    private static string Known(string? version)
    {
        string v = AppVersionInfo.WithoutBuildMetadata(version);
        return v == UnknownVersion ? string.Empty : v;
    }

    private static string Shown(string version) => RemoteOutputText.Quote(AppVersionInfo.WithoutBuildMetadata(version));

    private static string Shells(int count) => count == 1 ? "1 shell" : $"{count} shells";

    /// <summary>
    /// The endpoints whose daemon this launch has offered a restart: once per launch for each (a reconnect to the same
    /// daemon, or another window's connection to it, offers nothing more). Released after a restart, so a daemon still of
    /// another build when the next connection comes - the restart failed - is offered again. Thread-safe.
    /// </summary>
    internal sealed class Offered
    {
        private readonly HashSet<MuxEndpointId> _endpoints = [];

        /// <summary>The process's: one launch, shared by its windows.</summary>
        public static Offered Process { get; } = new();

        /// <summary>True for the first claim of <paramref name="endpoint"/> since it was last released.</summary>
        public bool TryClaim(MuxEndpointId endpoint)
        {
            lock (_endpoints) return _endpoints.Add(endpoint);
        }

        /// <summary>The next connection to <paramref name="endpoint"/> may offer it again.</summary>
        public void Release(MuxEndpointId endpoint)
        {
            lock (_endpoints) _endpoints.Remove(endpoint);
        }
    }
}
