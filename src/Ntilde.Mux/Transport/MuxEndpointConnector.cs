using System.IO.Pipes;
using System.Net.Sockets;

namespace Ntilde.Mux.Transport;

/// <summary>Client end of <see cref="NamedPipeMuxListener"/> / <see cref="UnixSocketMuxListener"/>.</summary>
public static class MuxEndpointConnector
{
    /// <param name="endpoint">Pipe name on Windows, absolute socket path elsewhere.</param>
    public static Stream Connect(string endpoint, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        if (OperatingSystem.IsWindows())
        {
            // CurrentUserOnly on the client too: it verifies the server pipe is owned by this user,
            // so a pipe squatted by another account is rejected rather than trusted.
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                pipe.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cts.Token).AsTask().GetAwaiter().GetResult();
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw new TimeoutException($"Timed out connecting to {endpoint}.");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new IOException($"Could not connect to {endpoint}: {ex.Message}", ex);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
