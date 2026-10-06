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

    /// <summary>
    /// The daemon side of the connection ended and the daemon's process is gone (it stopped, or was killed),
    /// its sessions with it. The GUI's <c>DaemonStopped</c>: it does not reconnect on its own.
    /// </summary>
    public const int DaemonClosed = 3;

    /// <summary>
    /// The daemon side of the connection ended, but the daemon's process still runs, with its sessions (codex D1):
    /// it dropped this one connection - a newer one with the same client instance id replaced it, or the client
    /// fell too far behind (<c>client_too_slow</c>). Also how a proxy whose stdout can no longer be written ends,
    /// since nobody reads the code then. The GUI counts it, like every code but 3, as a lost link.
    /// </summary>
    public const int ConnectionClosed = 4;
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

    /// <summary>
    /// How long, once the daemon side ended, the proxy waits for the daemon's process to exit before it concludes
    /// that the daemon runs on (codex D1). A daemon that stops - <c>kill-server</c>, its idle exit, an update's
    /// <c>shutdown</c> - closes its connections first and exits a moment later. Only the exit code waits: the
    /// channel's stdout and stderr have ended before it (<c>endStdio</c>), so the client has seen the end already.
    /// It fits inside the 2 s the GUI's exec channels give a command to exit once their stdin closed, and under the
    /// GUI's 2 s wait for the exit status (<c>RemoteMuxHostFactory.DisconnectExitWait</c>), so the code reaches it.
    /// </summary>
    private static readonly TimeSpan DaemonExitWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>How often <see cref="DaemonExitWait"/> looks.</summary>
    private static readonly TimeSpan DaemonExitPoll = TimeSpan.FromMilliseconds(50);

    /// <summary>Why a pump ended; the first one to end decides the exit.</summary>
    private enum PumpEnd
    {
        /// <summary>Stdin reached EOF (or was closed): the client is done.</summary>
        StdinClosed,

        /// <summary>Writing to the daemon failed: it closed its side, though its last bytes may still be on their way out.</summary>
        DaemonWriteFailed,

        /// <summary>The daemon → stdout pump read the daemon's end: its EOF, or a failed read.</summary>
        DaemonEnded,

        /// <summary>The daemon → stdout pump could not write stdout: nobody reads it any more.</summary>
        StdoutFailed,
    }

    /// <summary>
    /// Runs the proxy to its end and returns its <see cref="MuxProxyExitCodes">exit code</see>. When it
    /// returns, the daemon connection and <paramref name="stdout"/> are closed, as the process's exit
    /// would close them. <paramref name="stdin"/> is not: a read of it may still be blocked on its pump
    /// thread (a background thread the process exit ends), and only its EOF can end that read.
    /// </summary>
    /// <remarks>
    /// When the daemon side ends, <paramref name="stdout"/> is closed and then <paramref name="endStdio"/> ends the
    /// process's own stdout and stderr, which closing the stream does not (its descriptor is a copy, and sshd waits for
    /// stderr too): the client sees the end without delay. Only then does the exit code wait for the answer to whether
    /// the daemon's process is gone (<see cref="MuxProxyExitCodes.DaemonClosed"/>) or runs on
    /// (<see cref="MuxProxyExitCodes.ConnectionClosed"/>), for up to <see cref="DaemonExitWait"/>; nothing is written
    /// to stdout or stderr after.
    /// </remarks>
    /// <param name="stdin">The raw stdin: frames from the client.</param>
    /// <param name="stdout">The raw stdout: the preamble, then frames from the daemon.</param>
    /// <param name="stderr">Diagnostics, never frames.</param>
    /// <param name="connectDaemon">
    /// The daemon's endpoint, connected and not yet greeted (<c>MuxDaemonLauncher.EnsureEndpointStreamAsync</c>):
    /// the client's own hello goes through.
    /// </param>
    /// <param name="isDaemonAlive">
    /// Whether the daemon the descriptor names still runs. Null, as the verb passes it, is
    /// <see cref="MuxDiscovery.IsProcessAlive"/> with the descriptor's pid, process name and start token, so a
    /// process that took a dead daemon's pid does not count. Tests whose daemon runs in their own process pass
    /// their own.
    /// </param>
    /// <param name="endStdio">
    /// Ends the process's own stdout and stderr once the daemon side ended, before the wait for the daemon's process
    /// (residual R1): the verb passes <see cref="UnixChannelStdio.EndStdoutAndStderr"/> on Unix, where the proxy runs.
    /// Null for none: a proxy run inside another process (tests) must not end that process's descriptors.
    /// </param>
    public static int Run(
        Stream stdin,
        Stream stdout,
        TextWriter stderr,
        Func<CancellationToken, Task<(Stream Stream, MuxEndpointDescriptor Descriptor)>> connectDaemon,
        Func<MuxEndpointDescriptor, bool>? isDaemonAlive = null,
        Action? endStdio = null)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(connectDaemon);
        isDaemonAlive ??= static d => MuxDiscovery.IsProcessAlive(d.Pid, d.ProcessName, d.StartTime);

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
            return MuxProxyExitCodes.ConnectionClosed;
        }

        // Dedicated threads, never the pool: each spends its life blocked in a read. Background, so a
        // read still blocked on stdin after the out pump ended cannot keep the process alive.
        var firstEnd = new TaskCompletionSource<PumpEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outEnd = new TaskCompletionSource<PumpEnd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inPump = new Thread(() => firstEnd.TrySetResult(PumpIn(stdin, daemon))) { IsBackground = true, Name = "MuxProxyIn" };
        var outPump = new Thread(() =>
        {
            PumpEnd end = PumpOut(daemon, stdout);
            outEnd.TrySetResult(end);
            firstEnd.TrySetResult(end);
        })
        { IsBackground = true, Name = "MuxProxyOut" };
        inPump.Start();
        outPump.Start();

        PumpEnd first = firstEnd.Task.GetAwaiter().GetResult();
        if (first == PumpEnd.StdinClosed)
        {
            // The daemon reads our EOF before the close, so it ends the connection cleanly and keeps
            // the sessions. A named pipe has no half-close: closing it is the EOF.
            ShutdownSend(daemon);
            CloseQuietly(daemon);
            outPump.Join(OutPumpJoinTimeout);
            CloseQuietly(stdout);
            return MuxProxyExitCodes.StdinClosed;
        }

        // The daemon side ended, or stdout did. The in pump is not joined: it may be blocked on stdin, and the
        // process exits. A failed write to the daemon gives the out pump a moment to forward what the daemon
        // sent before it went.
        if (first == PumpEnd.DaemonWriteFailed) outPump.Join(OutPumpJoinTimeout);
        bool stdoutFailed = outEnd.Task is { IsCompletedSuccessfully: true, Result: PumpEnd.StdoutFailed };
        CloseQuietly(stdout);
        CloseQuietly(daemon);

        // A stdout nobody reads says nothing about the daemon, and nobody reads the code either: never 3.
        if (stdoutFailed) return MuxProxyExitCodes.ConnectionClosed;

        // Closing the stream left the channel open (a copy of fd 1, and sshd waits for stderr too): end both for
        // real now, so the client sees the end before the wait below, not when the process exits after it.
        endStdio?.Invoke();
        return DaemonRunsOn(descriptor, isDaemonAlive) ? MuxProxyExitCodes.ConnectionClosed : MuxProxyExitCodes.DaemonClosed;
    }

    /// <summary>
    /// Whether the daemon's process still runs once its side of the connection ended: asked every
    /// <see cref="DaemonExitPoll"/> for up to <see cref="DaemonExitWait"/>, since a daemon that stops closes its
    /// connections before it exits. True only when it still runs at the end of the wait.
    /// </summary>
    private static bool DaemonRunsOn(MuxEndpointDescriptor descriptor, Func<MuxEndpointDescriptor, bool> isDaemonAlive)
    {
        long deadline = Environment.TickCount64 + (long)DaemonExitWait.TotalMilliseconds;
        while (isDaemonAlive(descriptor))
        {
            if (Environment.TickCount64 >= deadline) return true;
            Thread.Sleep(DaemonExitPoll);
        }

        return false;
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

    /// <summary>daemon → stdout, flushing every write, until the daemon's EOF or either side fails; says which side ended it.</summary>
    private static PumpEnd PumpOut(Stream daemon, Stream stdout)
    {
        byte[] buffer = new byte[BufferBytes];
        while (true)
        {
            int n;
            try
            {
                n = daemon.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex) when (IsClosed(ex))
            {
                return PumpEnd.DaemonEnded;
            }

            if (n <= 0) return PumpEnd.DaemonEnded;

            try
            {
                stdout.Write(buffer, 0, n);
                stdout.Flush();
            }
            catch (Exception ex) when (IsClosed(ex))
            {
                return PumpEnd.StdoutFailed;
            }
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
