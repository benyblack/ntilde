namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// One running remote command over SSH, without a PTY (Phase 4 spec §8.2, §8.3): its stdio as raw
/// streams. The mux client speaks its protocol through <see cref="Stdout"/> and <see cref="Stdin"/>
/// to <c>ntilde-mux proxy --stdio</c> (§7.1); the installer runs one-shot commands through
/// <see cref="SshExec.RunAsync"/> (§9).
/// </summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> ends the command and never hangs, but it may block for several
/// seconds: it sends EOF, waits a grace period for the command to exit, then stops it and waits for
/// it to go. Never call it on the UI thread. A reader blocked on <see cref="Stdout"/> returns once the
/// channel is disposed.
/// </remarks>
public interface ISshExecChannel : IDisposable
{
    /// <summary>
    /// The remote command's stdout, read side. When the transport itself failed (the native backend
    /// reports connect, auth and connection loss this way), the read after the last byte throws
    /// <see cref="SshExecTransportException"/> instead of returning 0.
    /// </summary>
    Stream Stdout { get; }

    /// <summary>The remote command's stdin, write side. Every write is delivered at once; disposing it sends EOF.</summary>
    Stream Stdin { get; }

    /// <summary>The last 8 KiB of the remote command's stderr (and the transport's own diagnostics), UTF-8, lossy.</summary>
    string StderrTail { get; }

    /// <summary>
    /// The exit status once the command has ended; <see langword="null"/> when it is unknown (a signal,
    /// a lost transport, or a command the channel itself had to stop). For OpenSSH, 255 is ssh's own
    /// failure. <see cref="StderrTail"/> is complete when this resolves.
    /// </summary>
    Task<int?> Completion { get; }
}

/// <summary>
/// A way to run a command on one SSH host (Phase 4 spec §8): the OpenSSH client
/// (<see cref="OpenSshExecTransport"/>) or the native backend (<see cref="NativeSshExecTransport"/>).
/// </summary>
public interface ISshExecTransport
{
    /// <summary><c>user@host</c>, for banners and messages.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Starts <paramref name="remoteCommand"/>. It may block while it starts, so never call it on the
    /// UI thread. It may also return before connect and auth are done; both transports do. Their
    /// prompts then go to askpass or the interaction handler, and a failure surfaces through the
    /// channel: OpenSSH's as <see cref="ISshExecChannel.Completion"/> 255 with ssh's message in
    /// <see cref="ISshExecChannel.StderrTail"/>, and the native transport's as an
    /// <see cref="SshExecTransportException"/> from <see cref="ISshExecChannel.Stdout"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="ct"/> covers the start only: cancelled before or while starting, the command
    /// is stopped and <see cref="OperationCanceledException"/> thrown. Once a channel is returned,
    /// disposing it is how the caller ends the command.
    /// </remarks>
    ISshExecChannel Start(string remoteCommand, CancellationToken ct);
}

/// <summary>The outcome of a one-shot remote command (<see cref="SshExec.RunAsync"/>).</summary>
/// <param name="ExitCode">The exit status; <see langword="null"/> when unknown (see <see cref="ISshExecChannel.Completion"/>).</param>
/// <param name="Stdout">Stdout as UTF-8, at most the first <see cref="SshExec.MaxStdoutBytes"/> bytes.</param>
/// <param name="Stderr">The stderr tail (<see cref="ISshExecChannel.StderrTail"/>).</param>
public sealed record SshExecResult(int? ExitCode, string Stdout, string Stderr);
