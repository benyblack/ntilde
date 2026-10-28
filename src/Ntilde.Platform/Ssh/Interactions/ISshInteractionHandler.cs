namespace Ntilde.Platform.Ssh.Interactions;

public interface ISshInteractionHandler
{
    Task<SshInteractionResponse> HandleAsync(SshInteractionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Sign-in is over: every hop authenticated and the session's channel is open, so no prompt follows and every answer
    /// this handler gave was taken. A failure after it - the link dropping - is never an answer refused. The native exec
    /// transport calls it once, on its poll thread, when its session reports
    /// <see cref="Ntilde.Platform.Ssh.Native.NativeSshEventKind.Connected"/>; nothing else does.
    /// </summary>
    /// <remarks>Default implementation does nothing, so a handler that only answers prompts needs no changes.</remarks>
    void Authenticated()
    {
    }

    /// <summary>
    /// A prompt of the shared connection this handler's tab joined was refused. Only the tab that opened a shared connection
    /// answers its prompts, so this handler was never asked; but the refusal ends the connection, and this tab's shell with
    /// it. <see cref="Ntilde.Platform.Ssh.Native.NativeSshSharedConnection"/> calls it on its poll thread, before the
    /// refusal is sent, for every tab that holds the connection except the one that refused.
    /// </summary>
    /// <remarks>Default implementation does nothing: a handler that does not report why a connection ended needs no changes.</remarks>
    void RefusedOnSharedConnection(SshInteractionRequest request)
    {
    }
}
