namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The SSH connection under an exec channel failed: connect, host key, auth, a refused exec, or a
/// connection lost mid-run. The remote command did not fail. Thrown by an
/// <see cref="ISshExecChannel.Stdout"/> read once the output that arrived before the failure has been
/// read, so the connector can report it as an SSH failure (Phase 4 spec §7.1, <c>SshFailed</c>). The
/// same message is in <see cref="ISshExecChannel.StderrTail"/>.
/// </summary>
/// <remarks>
/// The native transport (<see cref="NativeSshExecTransport"/>) throws it. The OpenSSH transport cannot:
/// ssh reports its own failures as exit 255 with the reason on stderr.
/// </remarks>
public sealed class SshExecTransportException : IOException
{
    public SshExecTransportException(string message)
        : base(message)
    {
    }

    public SshExecTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
