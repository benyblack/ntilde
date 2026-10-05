using Ntilde.Mux.Contracts;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// Where one daemon lives (Phase 4 spec §6.2): injected by the executable, so the daemon core never
/// reads the App's paths. The descriptor and endpoint are <see cref="MuxDiscovery"/>'s rules over
/// <see cref="Root"/>; the log directory defaults to <c>&lt;root&gt;/logs</c>, where the App's own
/// <c>AppPaths.LogsDirectory</c> is.
/// </summary>
/// <param name="Root">The app-data root: <see cref="MuxDiscovery.GetRootDirectory"/> unless overridden.</param>
public sealed record MuxPaths(string Root)
{
    public static MuxPaths Default() => new(MuxDiscovery.GetRootDirectory());

    public string DescriptorPath => MuxDiscovery.GetDescriptorPath(Root);

    public string Endpoint => MuxDiscovery.GetDefaultEndpoint(Root);

    public string LogDirectory { get; init; } = Path.Combine(Root, "logs");
}
