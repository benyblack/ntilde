using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether a multiplexer daemon is really listening, for the startup auto-apply gate
/// (PR #489 follow-up). A descriptor whose pid and process name check out is not enough - pids get
/// recycled - so the endpoint is probe-connected. A descriptor whose endpoint refuses is stale: it
/// is deleted (only if it still names that pid), and the gate no longer disables auto-update.
/// </summary>
internal static class MuxStartupProbe
{
    public static bool IsDaemonLive(string descriptorPath, TimeSpan connectTimeout, Func<string, TimeSpan, Stream>? connect = null)
    {
        if (!MuxDiscovery.TryReadLiveDescriptor(descriptorPath, out MuxEndpointDescriptor? d)) return false;

        // <root>/mux/mux-endpoint.json: only this root's endpoint is trusted (MuxDaemonLauncher).
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(descriptorPath))!)!;
        if (!MuxDaemonLauncher.IsTrustedEndpoint(d.Endpoint, root, out _)) return false;

        try
        {
            // Connected and closed without a hello: the daemon's reader sees EOF and forgets it.
            using Stream stream = (connect ?? MuxEndpointConnector.Connect)(d.Endpoint, connectTimeout);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
            return false;
        }
    }
}
