namespace Ntilde.Platform.Ssh.Exec;

/// <summary>
/// The SSH connection under an exec channel failed: connect, host key, auth, a refused exec, or a
/// connection lost mid-run. The remote command did not fail. Thrown by an
/// <see cref="ISshExecChannel.Stdout"/> read once the output that arrived before the failure has been
/// read, so the connector can report it as an SSH failure (Phase 4 spec §7.1, <c>SshFailed</c>). The
/// same message is in <see cref="ISshExecChannel.StderrTail"/>. <see cref="SshExec.RunAsync"/> does
/// not throw it: it turns it into a result, as it does OpenSSH's exit 255.
/// </summary>
/// <remarks>
/// The native transport (<see cref="NativeSshExecTransport"/>) throws it. The OpenSSH transport cannot:
/// ssh reports its own failures as exit 255 with the reason on stderr.
/// </remarks>
public sealed class SshExecTransportException : IOException
{
    /// <param name="message">The message for the user, naming the host.</param>
    /// <param name="nativeMessage">The failure as the native layer worded it (<see cref="NativeMessage"/>).</param>
    public SshExecTransportException(string message, string nativeMessage)
        : base(message)
    {
        NativeMessage = nativeMessage ?? string.Empty;
    }

    /// <summary>
    /// The failure as the native layer worded it, without this transport's framing: what a classifier
    /// matches on, for example with <c>NativeSshFailureClassifier</c>.
    /// </summary>
    public string NativeMessage { get; }
}
