using System.Net.Sockets;
using Ntilde.Mux.Contracts;
using Ntilde.Mux.Transport;

namespace Ntilde.Mux.Cli;

/// <summary>How <c>ntilde-mux proxy --stdio</c> ends (Phase 4 spec §8.1).</summary>
public static class MuxProxyExitCodes
{
    /// <summary>The client closed the proxy's stdin: the normal end. The daemon keeps its sessions.</summary>
    public const int StdinClosed = 0;

    /// <summary>The daemon could not be reached or spawned; <c>mux: …</c> on stderr says why.</summary>
    public const int DaemonUnavailable = 1;

    /// <summary>The command line was not <c>proxy --stdio</c>.</summary>
    public const int Usage = 2;

    /// <summary>The pump to stdout ended first: the daemon closed the connection, or stdout could no longer be written.</summary>
    public const int DaemonClosed = 3;
}

/// <summary>
/// <c>ntilde-mux proxy --stdio</c> (Phase 4 spec §8.1), which sshd runs as an exec channel: connect to
/// this host's daemon (spawning it on demand), greet with <see cref="MuxProxyPreamble"/>, then pump
/// bytes stdin → daemon and daemon → stdout until either side closes. A dumb pump: it never parses a
/// frame, so the hello and everything after it are the client's and the daemon's own. Its stdout
/// carries the preamble and frames only; whatever it has to say goes to stderr.
/// </summary>
public static class MuxProxyCommand
{
    /// <summary>Each pump's buffer.</summary>
    private const int BufferBytes = 64 * 1024;

    /// <summary>How long a stdin-EOF exit waits for the daemon → stdout pump to finish.</summary>
    private static readonly TimeSpan OutPumpJoinTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Why a pump ended; the first one to end decides the exit.</summary>
    private enum PumpEnd
    {
        /// <summary>Stdin reached EOF (or was closed): the client is done.</summary>
        StdinClosed,

        /// <summary>Writing to the daemon failed: it is gone, though its last bytes may still be on their way out.</summary>
        DaemonWriteFailed,

        /// <summary>The daemon → stdout pump ended: the daemon closed, or stdout could not be written.</summary>
        OutEnded,
    }

    /// <summary>
    /// Runs the proxy to its end and returns its <see cref="MuxProxyExitCodes">exit code</see>. When it
    /// returns, the daemon connection and <paramref name="stdout"/> are closed, as the process's exit
    /// would close them. <paramref name="stdin"/> is not: a read of it may still be blocked on its pump
    /// thread (a background thread the process exit ends), and only its EOF can end that read.
    /// </summary>
    /// <param name="stdin">The raw stdin: frames from the client.</param>
    /// <param name="stdout">The raw stdout: the preamble, then frames from the daemon.</param>
    /// <param name="stderr">Diagnostics, never frames.</param>
    /// <param name="connectDaemon">
    /// The daemon's endpoint, connected and not yet greeted (<c>MuxDaemonLauncher.EnsureEndpointStreamAsync</c>):
    /// the client's own hello goes through.
    /// </param>
    public static int Run(Stream stdin, Stream stdout, TextWriter stderr, Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> connectDaemon)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(connectDaemon);

        Stream daemon;
        MuxEndpointDescriptor descriptor;
        try
        {
            (daemon, descriptor) = connectDaemon(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (MuxCli.IsReportableFailure(ex))
        {
            stderr.WriteLine($"mux: {ex.Message}");
            CloseQuietly(stdout);
            return MuxProxyExitCodes.DaemonUnavailable;
        }

        try
        {
            stdout.Write(MuxProxyPreamble.Format(descriptor.Pid));
            stdout.Flush();
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            // Nobody reads stdout any more: the same end as the out pump failing to write.
            CloseQuietly(daemon);
            CloseQuietly(stdout);
            return MuxProxyExitCodes.DaemonClosed;
        }

        // Dedicated threads, never the pool: each spends its life blocked in a read. Background, so a
        // read still blocked on stdin after the out pump ended cannot keep the process alive.
        var firstEnd = new TaskCompletionSource<PumpEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inPump = new Thread(() => firstEnd.TrySetResult(PumpIn(stdin, daemon))) { IsBackground = true, Name = "MuxProxyIn" };
        var outPump = new Thread(() =>
        {
            PumpOut(daemon, stdout);
            firstEnd.TrySetResult(PumpEnd.OutEnded);
        })
        { IsBackground = true, Name = "MuxProxyOut" };
        inPump.Start();
        outPump.Start();

        PumpEnd end = firstEnd.Task.GetAwaiter().GetResult();
        if (end == PumpEnd.StdinClosed)
        {
            // The daemon reads our EOF before the close, so it ends the connection cleanly and keeps
            // the sessions. A named pipe has no half-close: closing it is the EOF.
            ShutdownSend(daemon);
            CloseQuietly(daemon);
            outPump.Join(OutPumpJoinTimeout);
            CloseQuietly(stdout);
            return MuxProxyExitCodes.StdinClosed;
        }

        // The daemon side ended. The in pump is not joined: it may be blocked on stdin, and the
        // process exits. A failed write to the daemon gives the out pump a moment to forward what the
        // daemon sent before it went.
        if (end == PumpEnd.DaemonWriteFailed) outPump.Join(OutPumpJoinTimeout);
        CloseQuietly(stdout);
        CloseQuietly(daemon);
        return MuxProxyExitCodes.DaemonClosed;
    }

    /// <summary>stdin → daemon, flushing every write.</summary>
    private static PumpEnd PumpIn(Stream stdin, Stream daemon)
    {
        byte[] buffer = new byte[BufferBytes];
        while (true)
        {
            int n;
            try
            {
                n = stdin.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (IsClosed(ex))
            {
                return PumpEnd.StdinClosed;
            }

            if (n <= 0) return PumpEnd.StdinClosed;

            try
            {
                daemon.Write(buffer, 0, n);
                daemon.Flush();
            }
            catch (Exception ex) when (IsClosed(ex))
            {
                return PumpEnd.DaemonWriteFailed;
            }
        }
    }

    /// <summary>daemon → stdout, flushing every write, until the daemon's EOF or either side fails.</summary>
    private static void PumpOut(Stream daemon, Stream stdout)
    {
        byte[] buffer = new byte[BufferBytes];
        try
        {
            int n;
            while ((n = daemon.Read(buffer, 0, buffer.Length)) > 0)
            {
                stdout.Write(buffer, 0, n);
                stdout.Flush();
            }
        }
        catch (Exception ex) when (IsClosed(ex))
        {
            // That side closed: the end this pump reports either way.
        }
    }

    /// <summary>A Unix socket's half-close; other transports (a named pipe) have none.</summary>
    private static void ShutdownSend(Stream daemon)
    {
        if (daemon is not NetworkStream network) return;
        try
        {
            network.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Already gone: the close that follows is all that is left to do.
        }
    }

    /// <summary>A stream's other end went away, or the stream was closed under a pump.</summary>
    private static bool IsClosed(Exception ex) => ex is IOException or ObjectDisposedException;

    private static void CloseQuietly(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception ex) when (IsClosed(ex)) { /* flushing into a closed pipe: closing it is all that was left */ }
    }
}
