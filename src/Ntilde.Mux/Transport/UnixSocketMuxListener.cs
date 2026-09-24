using System.Net.Sockets;
using System.Runtime.Versioning;

namespace Ntilde.Mux.Transport;

/// <summary>A local endpoint whose permissions would let another user reach the daemon.</summary>
public sealed class MuxEndpointSecurityException : IOException
{
    public MuxEndpointSecurityException(string message) : base(message) { }
}

/// <summary>
/// Linux/macOS endpoint (spec §3): a socket file (0600) inside a directory created 0700. A directory
/// that already exists with any other mode is refused rather than "fixed": something other than us
/// made it, and a 0700 directory owned by someone else is unreadable to us, so bind fails - which
/// makes the mode check an ownership check too, without stat interop.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixSocketMuxListener : IMuxListener
{
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _socketPath;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _disposed = new();

    public UnixSocketMuxListener(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        _socketPath = Path.GetFullPath(socketPath);
        string dir = Path.GetDirectoryName(_socketPath)!;
        EnsurePrivateDirectory(dir);

        if (File.Exists(_socketPath))
        {
            if (IsAlive(_socketPath)) throw new IOException($"Another multiplexer is already listening on {_socketPath}.");
            File.Delete(_socketPath); // stale: probe-connect failed
        }

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
            File.SetUnixFileMode(_socketPath, SocketMode);
            _socket.Listen(backlog: 16);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    private static void EnsurePrivateDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir, DirMode);
            // umask can only remove bits, never add: re-assert in case the parent path pre-existed.
            File.SetUnixFileMode(dir, DirMode);
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(dir);
        if (mode != DirMode)
        {
            throw new MuxEndpointSecurityException(
                $"Refusing to serve: {dir} has mode {Convert.ToString((int)mode, 8)}, expected 700. Remove the directory or fix its permissions.");
        }
    }

    internal static bool IsAlive(string socketPath)
    {
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public Stream? Accept(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            Socket client = _socket.AcceptAsync(linked.Token).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(client, ownsSocket: true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (linked.IsCancellationRequested) return null;
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed.IsCancellationRequested) return;
        _disposed.Cancel();
        _socket.Dispose();
        try { File.Delete(_socketPath); }
        catch (IOException) { /* best effort; a stale file is probed next start */ }
        catch (UnauthorizedAccessException) { }
    }
}
