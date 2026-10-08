using System.Net.Sockets;
using System.Text;

namespace Ntilde.Mux.Daemon;

/// <summary>
/// The stable agent socket path a remote daemon's shells use, and its repointing (Unix only). The daemon outlives the
/// ssh connection that forwarded an agent, so the <c>SSH_AUTH_SOCK</c> it inherited from that connection's proxy goes
/// dead when the connection closes. tmux's answer: shells get a fixed path, a symlink in the endpoint directory, and
/// each <c>proxy --stdio</c> repoints it at its own connection's agent.
/// </summary>
public static class AgentSocketLink
{
    public const string FileName = "agent.sock";

    private static int _loggedTooLong;

    public static string PathFor(string endpointDirectory) => Path.Combine(endpointDirectory, FileName);

    /// <summary>
    /// The link's path for the daemon whose endpoint socket is <paramref name="endpoint"/>: beside it, in its 0700
    /// directory (so also beside a fallback endpoint under <c>$XDG_RUNTIME_DIR</c> or the temp directory). Null on
    /// Windows (its endpoint is a pipe name), when the endpoint has no directory, or - logged once through
    /// <paramref name="log"/> - when the link's own path would not fit a socket path (<c>agent.sock</c> is two bytes
    /// longer than <c>mux.sock</c>), since a client of the link connects through it.
    /// </summary>
    public static string? LinkPathForEndpoint(string endpoint, Action<string>? log = null)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(endpoint)) return null;
        string? directory = Path.GetDirectoryName(endpoint);
        if (string.IsNullOrEmpty(directory)) return null;

        string link = PathFor(directory);
        int budget = OperatingSystem.IsMacOS() ? 103 : 107; // sun_path minus the terminating NUL
        if (Encoding.UTF8.GetByteCount(link) <= budget) return link;

        if (Interlocked.Exchange(ref _loggedTooLong, 1) == 0)
        {
            log?.Invoke($"agent socket link skipped: '{link}' is longer than a socket path allows; SSH agent forwarding will not reach daemon shells.");
        }

        return null;
    }

    /// <summary>
    /// Points the link at target when target is a live socket; atomic (symlink to tmp + rename). Returns false (and
    /// changes nothing) otherwise. Ownership is not checked: managed code has no portable owner query, so "a socket
    /// that accepts a connection from this user" is the test - the kernel already refuses a connection to a socket
    /// this user may not write to.
    /// </summary>
    public static bool TryRepoint(string linkPath, string? target)
    {
        if (OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrEmpty(linkPath) || string.IsNullOrWhiteSpace(target) || !Path.IsPathRooted(target)) return false;
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(linkPath), StringComparison.Ordinal)) return false;
        if (!IsLiveSocket(target)) return false;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(linkPath));
        if (directory is null) return false;
        string temp = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.CreateSymbolicLink(temp, target);
            File.Move(temp, linkPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try { File.Delete(temp); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best effort: a leftover .tmp is harmless */ }
            return false;
        }
    }

    /// <summary>True when something (a file, or a symlink even if dangling) is at <paramref name="linkPath"/>.</summary>
    internal static bool LinkExists(string linkPath)
    {
        try
        {
            var info = new FileInfo(linkPath);
            return info.Exists || info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a socket that accepts a connection. Connecting proves both "is a socket" and
    /// "is alive" (a stale agent socket left by a closed connection refuses), which a stat could not.
    /// </summary>
    internal static bool IsLiveSocket(string path)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
