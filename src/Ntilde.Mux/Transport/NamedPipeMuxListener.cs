using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Ntilde.Mux.Transport;

/// <summary>
/// Windows endpoint (spec §3). <see cref="PipeOptions.CurrentUserOnly"/> restricts the pipe's ACL to
/// the creating user: the protocol has no authentication and <c>spawn</c> runs arbitrary commands.
/// <see cref="PipeOptions.Asynchronous"/> because a pipe opened for synchronous I/O can deadlock its
/// Dispose against a read blocked on another thread (PR #474 open question).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeMuxListener : IMuxListener
{
    // CreateNamedPipe with nInBufferSize/nOutBufferSize left at 0 ("system default") can hand back a
    // pipe that only transfers bytes while a read is concurrently in flight - a write issued before the
    // peer starts reading then blocks forever. Confirmed by hanging a real client/server round trip
    // (WriteAsync stuck, dotnet-dump showed both ends correctly async/thread-pool-bound, so it was not a
    // binding bug) until an explicit buffer size was set. 64 KiB comfortably covers a VT chunk.
    private const int PipeBufferSize = 64 * 1024;

    private readonly string _pipeName;
    private readonly CancellationTokenSource _disposed = new();
    private readonly object _gate = new();
    private NamedPipeServerStream? _pending;

    public NamedPipeMuxListener(string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        // Create the first instance now so a client that connects before the accept loop runs finds
        // the pipe (and so a name collision fails the constructor, not the accept thread).
        _pending = CreateInstance();
    }

    private NamedPipeServerStream CreateInstance() =>
        new(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: PipeBufferSize, outBufferSize: PipeBufferSize);

    public Stream? Accept(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        NamedPipeServerStream server;
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return null;
            server = _pending ?? CreateInstance();
            _pending = null;
        }

        try
        {
            server.WaitForConnectionAsync(linked.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            server.Dispose();
            if (linked.IsCancellationRequested) return null;
            throw;
        }

        // Pre-create the next instance so there is never a moment with no listening instance
        // (a client connecting then would see "pipe not found" rather than wait).
        lock (_gate)
        {
            if (!_disposed.IsCancellationRequested) _pending ??= CreateInstance();
        }

        return server;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed.IsCancellationRequested) return;
            _disposed.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}
