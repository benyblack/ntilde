using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// Where one daemon lives (Phase 4 spec §6.2): injected by the executable, so the daemon core never
/// reads the App's paths. The descriptor and endpoint are <see cref="MuxDiscovery"/>'s rules over
/// <see cref="Root"/>; the log directory defaults to <c>&lt;root&gt;/logs</c>, where the App's own
/// <c>AppPaths.LogsDirectory</c> is.
/// </summary>
/// <remarks>
/// Two roots exist (final review F3). The GUI's daemon (<c>ntilde mux</c>) serves the app-data root itself
/// (<see cref="Default"/>); the standalone <c>ntilde-mux</c> serves a root of its own beneath it
/// (<see cref="Standalone()"/>), so on a host that also runs the GUI the two never share a descriptor, socket,
/// lock or log: the GUI's daemon cannot spawn the empty-command shells a remote client asks for, and must not
/// adopt (or shut down) a remote client's sessions.
/// </remarks>
/// <param name="Root">The daemon's root: <see cref="MuxDiscovery.GetRootDirectory"/> or <see cref="StandaloneRootDirectory"/> unless overridden.</param>
public sealed record MuxPaths(string Root)
{
    /// <summary>The directory, under the app-data root, that <c>ntilde-mux</c> serves.</summary>
    public const string StandaloneDirectoryName = "ntilde-mux";

    /// <summary>
    /// <c>ntilde-mux</c>'s root, exactly, when set: what a <c>proxy</c> hands the daemon it spawns when it serves a
    /// root other than its environment's (<see cref="ProcessMuxDaemonSpawner"/>), and a test's override.
    /// <see cref="MuxDiscovery.RootOverrideEnvVar"/> moves the app-data root, and <c>ntilde-mux</c>'s with it.
    /// </summary>
    public const string StandaloneRootOverrideEnvVar = "NTILDE_MUX_ROOT";

    /// <summary>The GUI's daemon (<c>ntilde mux serve</c>): the app-data root.</summary>
    public static MuxPaths Default() => new(MuxDiscovery.GetRootDirectory());

    /// <summary>The standalone <c>ntilde-mux</c>'s own root (<see cref="StandaloneRootDirectory"/>), every verb's.</summary>
    public static MuxPaths Standalone() => Standalone(StandaloneRootDirectory());

    /// <summary><c>ntilde-mux</c>'s paths at <paramref name="root"/>: a daemon spawned for them is told the root through <see cref="StandaloneRootOverrideEnvVar"/>.</summary>
    public static MuxPaths Standalone(string root) => new(root) { IsStandalone = true };

    /// <summary>
    /// <see cref="StandaloneRootOverrideEnvVar"/> when set, otherwise <see cref="StandaloneRootUnder"/> the app-data
    /// root (<c>~/.local/share/ntilde/ntilde-mux</c> on Linux, <c>~/Library/Application Support/ntilde/ntilde-mux</c>
    /// on macOS).
    /// </summary>
    public static string StandaloneRootDirectory()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(StandaloneRootOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideRoot)) return Path.GetFullPath(overrideRoot);
        return StandaloneRootUnder(MuxDiscovery.GetRootDirectory());
    }

    /// <summary><c>ntilde-mux</c>'s root beneath the app-data root <paramref name="appDataRoot"/>.</summary>
    public static string StandaloneRootUnder(string appDataRoot) => Path.Combine(appDataRoot, StandaloneDirectoryName);

    /// <summary>
    /// <c>ntilde-mux</c>'s paths rather than the GUI daemon's: decides how a spawned daemon is told the root
    /// (<see cref="RootHandDown"/>).
    /// </summary>
    public bool IsStandalone { get; init; }

    public string DescriptorPath => MuxDiscovery.GetDescriptorPath(Root);

    public string Endpoint => MuxDiscovery.GetDefaultEndpoint(Root);

    public string LogDirectory { get; init; } = Path.Combine(Root, "logs");

    /// <summary>
    /// What a daemon spawned to serve these paths must find in its environment so that its own rule resolves
    /// <see cref="Root"/>: the variable that rule reads (<see cref="StandaloneRootOverrideEnvVar"/> for
    /// <c>ntilde-mux</c>, <see cref="MuxDiscovery.RootOverrideEnvVar"/> for the GUI's executable) and the root.
    /// Null when the inherited environment already resolves to <see cref="Root"/>: nothing is set, so the
    /// daemon's shells do not gain the variable (it would make a GUI started from one of them skip
    /// <c>AppPaths</c>' legacy-root migration).
    /// </summary>
    internal (string Variable, string Root)? RootHandDown()
    {
        string inherited = IsStandalone ? StandaloneRootDirectory() : MuxDiscovery.GetRootDirectory();
        if (SameRoot(Root, inherited)) return null;
        return (IsStandalone ? StandaloneRootOverrideEnvVar : MuxDiscovery.RootOverrideEnvVar, Path.GetFullPath(Root));
    }

    private static bool SameRoot(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
