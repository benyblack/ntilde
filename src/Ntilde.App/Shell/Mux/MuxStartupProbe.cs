using System.Linq;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Shell.Mux;

/// <summary>
/// Whether a multiplexer daemon is really listening, for the startup auto-apply gate
/// (PR #489 follow-up). A descriptor whose pid and process name check out is not enough - pids get
/// recycled - so the endpoint is probe-connected. A descriptor whose endpoint genuinely refuses
/// (missing/connection-refused) is stale: it is deleted (only if it still names that pid), and the
/// gate no longer disables auto-update.
///
/// A connect timeout is not automatically a refusal: on Windows, a live daemon with every pipe
/// instance busy makes a connect wait and then time out exactly like a pipe nobody ever created -
/// the client API alone cannot tell the two apart. So a Windows timeout takes a second look without
/// spending a connect: it enumerates \\.\pipe\ (FindFirstFile, not <see cref="File.Exists"/>, which
/// can open - and so consume - a pipe instance on some Windows versions) for the pipe's name.
/// Absent means nobody is, or ever was, listening: a genuine refusal, so the descriptor is deleted.
/// Present means busy: still live, kept. Off Windows, and if the enumeration itself fails, a timeout
/// stays live - a false "not live" would auto-apply an update underneath a running daemon, which
/// spec §9 forbids, so the safer wrong answer is "live". Likewise
/// <see cref="UnauthorizedAccessException"/> means something else owns that endpoint, which is
/// still live from this probe's point of view.
/// </summary>
internal static class MuxStartupProbe
{
    public static bool IsDaemonLive(string descriptorPath, TimeSpan connectTimeout,
        Func<string, TimeSpan, Stream>? connect = null, Func<string, bool>? pipeExists = null)
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
        catch (TimeoutException)
        {
            // Windows only (see the class remarks): a pipe that does not exist was never a "busy"
            // daemon to begin with - that is a genuine refusal, unlike every other timeout here.
            if (OperatingSystem.IsWindows() && !(pipeExists ?? DefaultPipeExists)(d.Endpoint))
            {
                MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
                return false;
            }
            return true;
        }
        // Something else owns the endpoint: also not a refusal.
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        // The endpoint is missing or nobody is listening: a genuine refusal, so the descriptor is stale.
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            MuxDiscovery.DeleteDescriptorIfOwned(descriptorPath, d.Pid);
            return false;
        }
    }

    /// <summary>
    /// Whether a named pipe with this name currently exists, via FindFirstFile
    /// (<see cref="Directory.EnumerateFiles(string)"/> on \\.\pipe\) rather than a connect or
    /// <see cref="File.Exists"/>. Enumeration failing counts as "exists" (live): the conservative
    /// answer, since this check only ever downgrades a live daemon to a dead descriptor when it is
    /// certain, never the reverse.
    /// </summary>
    internal static bool DefaultPipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(path => string.Equals(Path.GetFileName(path), pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
