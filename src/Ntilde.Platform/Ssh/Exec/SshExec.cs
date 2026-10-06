using System.Globalization;
using System.Text;

namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// One-shot remote commands over an <see cref="ISshExecTransport"/> (Phase 4 spec §9): the install
/// flow's probe and upload, which feed stdin, read stdout to the end and want the exit status.
/// </summary>
public static class SshExec
{
    /// <summary>Stdin is written in chunks of this size, with a progress report after each.</summary>
    public const int StdinChunkBytes = 64 * 1024;

    /// <summary>The most stdout a result keeps; anything past it is read and discarded.</summary>
    public const int MaxStdoutBytes = 1024 * 1024;

    private const int StdoutBufferBytes = 16 * 1024;

    /// <summary>
    /// Runs <paramref name="command"/>: starts it (off the calling thread, since
    /// <see cref="ISshExecTransport.Start"/> may block), writes <paramref name="stdin"/> in
    /// <see cref="StdinChunkBytes"/> chunks, sends EOF, reads stdout to the end and returns the exit
    /// status with the stderr tail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stdout is read while stdin is written, so a command that echoes its input cannot deadlock on a
    /// full pipe. A command that stops reading its stdin (it exited, or failed first) is not an error
    /// here: the write ends, and its exit status and stderr say what happened.
    /// </para>
    /// <para>
    /// A failed SSH connection is a result, not an exception, whichever transport ran it. OpenSSH
    /// reports one as exit 255 with ssh's message on stderr. The native transport reports one as an
    /// <see cref="SshExecTransportException"/> from stdout; that becomes a null exit code, the stdout
    /// read before it, and the native message in <see cref="SshExecResult.Stderr"/>.
    /// </para>
    /// </remarks>
    /// <param name="stdinProgress">Receives the total bytes written after each chunk; none for empty stdin.</param>
    /// <param name="timeout">The whole run, start included (<see cref="Timeout.InfiniteTimeSpan"/> for no limit).</param>
    /// <exception cref="TimeoutException">The run outlasted <paramref name="timeout"/>; the channel was disposed first.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; the channel was disposed first.</exception>
    public static async Task<SshExecResult> RunAsync(
        ISshExecTransport transport,
        string command,
        ReadOnlyMemory<byte> stdin,
        IProgress<long>? stdinProgress,
        TimeSpan timeout,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(command);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be positive or infinite.");
        }

        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        CancellationToken token = linked.Token;
        bool TimedOut() => deadline.IsCancellationRequested && !ct.IsCancellationRequested;

        ISshExecChannel channel;
        try
        {
            channel = await Task.Run(() => transport.Start(command, token), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (TimedOut())
        {
            throw TimeoutFor(transport, timeout);
        }

        Task<StdoutCapture>? stdoutTask = null;
        bool completed = false;
        try
        {
            stdoutTask = Task.Run(() => ReadCappedAsync(channel.Stdout, token), CancellationToken.None);
            await WriteStdinAsync(channel.Stdin, stdin, stdinProgress, token).ConfigureAwait(false);
            StdoutCapture stdout = await stdoutTask.WaitAsync(token).ConfigureAwait(false);
            int? exitCode = await channel.Completion.WaitAsync(token).ConfigureAwait(false);
            completed = true;

            string stderr = channel.StderrTail;
            if (stdout.TransportFailure is { } failure)
            {
                // As OpenSSH's 255 reads: no exit status of the command's own, and the reason on stderr.
                exitCode = null;
                if (!stderr.Contains(failure.NativeMessage, StringComparison.Ordinal))
                {
                    stderr = $"{stderr}{failure.NativeMessage}\n";
                }
            }

            return new SshExecResult(exitCode, Encoding.UTF8.GetString(stdout.Bytes), stderr);
        }
        catch (OperationCanceledException) when (TimedOut())
        {
            throw TimeoutFor(transport, timeout);
        }
        finally
        {
            if (completed)
            {
                // The command has exited: disposing only releases the pipes.
                channel.Dispose();
            }
            else
            {
                // Disposing a channel whose command still runs can wait out its grace period, so it
                // runs on the pool, never on the thread that cancelled. A read it abandoned faults
                // or ends when the channel closes; observed, so that is not an unobserved exception.
                await Task.Run(channel.Dispose, CancellationToken.None).ConfigureAwait(false);
                Observe(stdoutTask);
            }
        }
    }

    private static async Task WriteStdinAsync(Stream stdin, ReadOnlyMemory<byte> data, IProgress<long>? progress, CancellationToken token)
    {
        Task? pending = null;
        try
        {
            int written = 0;
            while (written < data.Length)
            {
                int count = Math.Min(StdinChunkBytes, data.Length - written);

                // Bounded by the token even when the stream ignores it (a synchronous pipe's WriteAsync
                // does); such a write is abandoned to the channel's disposal.
                pending = stdin.WriteAsync(data.Slice(written, count), token).AsTask();
                await pending.WaitAsync(token).ConfigureAwait(false);
                pending = null;
                written += count;
                progress?.Report(written);
            }
        }
        catch (IOException)
        {
            // The command stopped reading its stdin. Its exit status and stderr carry the reason.
        }
        catch (OperationCanceledException)
        {
            Observe(pending);
            throw;
        }

        CloseQuietly(stdin);
    }

    /// <summary>Stdout up to <see cref="MaxStdoutBytes"/>, and the transport failure that ended it, if one did.</summary>
    private readonly record struct StdoutCapture(byte[] Bytes, SshExecTransportException? TransportFailure);

    private static async Task<StdoutCapture> ReadCappedAsync(Stream stdout, CancellationToken token)
    {
        using var kept = new MemoryStream();
        byte[] buffer = new byte[StdoutBufferBytes];
        while (true)
        {
            int read;
            try
            {
                read = await stdout.ReadAsync(buffer, token).ConfigureAwait(false);
            }
            catch (SshExecTransportException ex)
            {
                // The connection failed after (or before) this much output: a result, as for OpenSSH.
                return new StdoutCapture(kept.ToArray(), ex);
            }

            if (read == 0)
            {
                return new StdoutCapture(kept.ToArray(), null);
            }

            // Past the cap the rest is still read, so the command never blocks on a full pipe.
            int room = MaxStdoutBytes - (int)kept.Length;
            if (room > 0)
            {
                kept.Write(buffer, 0, Math.Min(room, read));
            }
        }
    }

    private static TimeoutException TimeoutFor(ISshExecTransport transport, TimeSpan timeout) =>
        new(string.Create(CultureInfo.InvariantCulture,
            $"The remote command on {transport.DisplayName} did not finish within {timeout.TotalSeconds:0.###} s."));

    /// <summary>Stdin's EOF. A channel already gone fails the final flush, which changes nothing.</summary>
    private static void CloseQuietly(Stream stream)
    {
        try { stream.Dispose(); }
        catch (IOException) { /* the command is gone: there is nobody left to send EOF to */ }
    }

    private static void Observe(Task? task) =>
        _ = task?.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
