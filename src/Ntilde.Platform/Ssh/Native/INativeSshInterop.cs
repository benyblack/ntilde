namespace Ntilde.Platform.Ssh.Native;

public interface INativeSshInterop
{
    NovaSshSafeHandle Connect(NativeSshConnectionOptions options);

    /// <summary>
    /// Starts an exec session (Phase 4 spec §8.3): the connect, jump chain, host-key and auth prompts
    /// of <see cref="Connect"/>, then <paramref name="command"/> runs on the target with no PTY. Returns
    /// at once with the handle; everything after (prompts, stdout as <see cref="NativeSshEventKind.Data"/>,
    /// stderr as <see cref="NativeSshEventKind.ExtendedData"/>, the exit status, a failure) arrives as
    /// poll events ending with <see cref="NativeSshEventKind.Closed"/>. <see cref="Write"/> feeds the
    /// command's stdin and <see cref="SendEof"/> ends it. Release the handle with <see cref="Close"/>.
    /// </summary>
    /// <remarks>
    /// Default implementation throws, like <see cref="RequestRemoteForward"/>: a test double that
    /// never expects an exec session should fail a test that starts one.
    /// </remarks>
    NovaSshSafeHandle Exec(NativeSshConnectionOptions options, string command) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement exec sessions.");

    /// <summary>
    /// Starts a shared connection: the connect, jump chain, host-key and auth prompts of <see cref="Connect"/>,
    /// with no channel of its own. Shells join it with <see cref="OpenShell"/> and never log in again. Its poll
    /// events are the prompts, <see cref="NativeSshEventKind.Connected"/>, the forward-channel events, and Error
    /// and Closed; the port-forward calls take its handle, so the forwards outlive every shell. Release the
    /// handle with <see cref="Close"/>.
    /// </summary>
    /// <remarks>Default implementation throws, like <see cref="Exec"/>.</remarks>
    NovaSshSafeHandle ConnectShared(NativeSshConnectionOptions options) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement shared connections.");

    /// <summary>
    /// Opens a PTY shell on <paramref name="connection"/> (from <see cref="ConnectShared"/>), with the terminal
    /// and shell settings of <paramref name="options"/>. Returns at once: a shell opened while the connection is
    /// still connecting waits for it. Its events are a <see cref="Connect"/> session's from Connected on, and a
    /// connection that fails or is lost ends it with Error and Closed. Closing it never closes the connection.
    /// Release the handle with <see cref="Close"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection is closed or no longer running.</exception>
    /// <remarks>Default implementation throws, like <see cref="Exec"/>.</remarks>
    NovaSshSafeHandle OpenShell(NovaSshSafeHandle connection, NativeSshConnectionOptions options) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement shared connections.");

    /// <summary>
    /// Sends EOF on the session's main channel: for an exec session, the end of the command's stdin.
    /// Idempotent; writes queued before it are sent first, and writes after it are dropped. A session
    /// that has already ended ignores it.
    /// </summary>
    /// <remarks>Default implementation throws; see <see cref="Exec"/>.</remarks>
    void SendEof(NovaSshSafeHandle sessionHandle) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement exec sessions.");

    // Blocking FFI/network call. App/UI-facing services must offload this work before awaiting it.
    IReadOnlyList<NativeRemotePathEntry> ListRemoteDirectory(
        NativeSshConnectionOptions connectionOptions,
        string remotePath,
        CancellationToken cancellationToken);
    void RunSftpTransfer(
        NativeSshConnectionOptions connectionOptions,
        NativeSftpTransferOptions transferOptions,
        Action<NativeSftpTransferProgress>? progress,
        CancellationToken cancellationToken);
    NativeSshEvent? PollEvent(NovaSshSafeHandle sessionHandle);

    /// <summary>
    /// Blocks until the session has an event to poll, it closes, or <paramref name="timeout"/> passes
    /// (the native side caps it at one second). Lets a poll thread wait instead of sleeping between
    /// <see cref="PollEvent"/> calls. Returns false only when the wait ended with nothing to poll.
    /// </summary>
    /// <remarks>
    /// Default implementation sleeps at most 10 ms and returns true: what the poll loops did before
    /// the native wait existed, so a test double behaves as it always has. Only the real FFI can
    /// wake early, and only it overrides.
    /// </remarks>
    bool WaitForEvent(NovaSshSafeHandle sessionHandle, TimeSpan timeout)
    {
        TimeSpan sleep = timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout; // Infinite must not sleep forever
        Thread.Sleep(sleep > TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : sleep);
        return true;
    }
    void Write(NovaSshSafeHandle sessionHandle, ReadOnlySpan<byte> data);
    void Resize(NovaSshSafeHandle sessionHandle, int cols, int rows);
    int OpenDirectTcpIp(NovaSshSafeHandle sessionHandle, NativePortForwardOpenOptions options);
    void WriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Queues forward-channel data toward the remote, reporting whether it was accepted.
    /// <see langword="false"/> means the channel already has more queued than its budget allows: not
    /// an error, and nothing was consumed. The caller should retry, which is what stops it reading its
    /// local socket and lets TCP flow control throttle the local peer — the alternative to either
    /// growing the queue without limit or closing a channel for being merely slow.
    /// </summary>
    /// <remarks>
    /// Default implementation delegates to <see cref="WriteChannel"/> and reports acceptance, so an
    /// interop that has no queue of its own (test doubles, in-memory fakes) needs no changes. Only the
    /// real FFI can answer this meaningfully, and only it overrides.
    /// </remarks>
    bool TryWriteChannel(NovaSshSafeHandle sessionHandle, int channelId, ReadOnlySpan<byte> data)
    {
        WriteChannel(sessionHandle, channelId, data);
        return true;
    }
    /// <summary>
    /// Asks the server to open a remote-forward listener (a tcpip-forward global request) and
    /// returns the port it bound. Connections arriving on that listener surface as
    /// <see cref="NativeSshEventKind.ForwardChannelIncoming"/> events. Blocking FFI/network call —
    /// it waits for the server's verdict — so callers offload it, never hold a UI thread on it.
    /// </summary>
    /// <remarks>
    /// Default implementation throws: a test double that never expects remote forwards should fail
    /// a test that reaches for one, and the double that does expect them overrides.
    /// </remarks>
    int RequestRemoteForward(NovaSshSafeHandle sessionHandle, string bindAddress, int port) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not implement remote forwards.");

    void SendChannelEof(NovaSshSafeHandle sessionHandle, int channelId);
    void CloseChannel(NovaSshSafeHandle sessionHandle, int channelId);
    void SubmitResponse(NovaSshSafeHandle sessionHandle, NativeSshResponseKind responseKind, ReadOnlySpan<byte> data);
    void Close(NovaSshSafeHandle sessionHandle);
}
