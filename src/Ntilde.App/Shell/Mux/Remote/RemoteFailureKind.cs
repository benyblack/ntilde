namespace Ntilde.Shell.Mux.Remote;

/// <summary>
/// Why a remote host's <c>ntilde-mux</c> could not be reached (Phase 4 spec §7.1), as
/// <see cref="RemoteMuxFailureClassifier"/> tells them apart. The kind decides what the UI offers: the
/// install flow for <see cref="NotInstalled"/>, an update for <see cref="VersionMismatch"/> (§7.5).
/// </summary>
internal enum RemoteFailureKind
{
    /// <summary>No binary at the path: the shell said "not found" (exit 127).</summary>
    NotInstalled,

    /// <summary>The binary is there but cannot run on that host: another architecture, a libc too old or none (exit 126, loader errors).</summary>
    Unsupported,

    /// <summary>The daemon answered, but speaks no protocol version this app does.</summary>
    VersionMismatch,

    /// <summary>SSH itself failed - connect, host key, auth - so the remote command never ran: OpenSSH's exit 255, or a native transport error.</summary>
    SshFailed,

    /// <summary>Anything else: the proxy ran but never greeted, or the connection broke during the hello.</summary>
    ProxyFailed,

    /// <summary>
    /// Signing in needs an answer nobody was asked for - a password, keyboard-interactive input, a key's
    /// passphrase - because the attempt was automatic (or there was no window to ask through): the native
    /// backend ended it at the prompt, or OpenSSH in batch mode was refused. An SSH failure in kind, but not
    /// one another automatic try can fix: the reconnect loop stops on it (Phase 4 spec §7.3, by ruling), and
    /// Enter - an interactive attempt - is the way back.
    /// </summary>
    NeedsUser,
}

/// <summary>A classified remote failure: its <paramref name="Kind"/>, and the text the user should see.</summary>
internal sealed record RemoteMuxFailure(RemoteFailureKind Kind, string Reason);

/// <summary>
/// A remote host's connect attempt failed (Phase 4 spec §7.1). The App's own exception for a remote
/// endpoint: <c>Ntilde.Mux</c>'s <c>MuxUnavailableException</c> stays the local daemon's. A remote
/// <see cref="MuxConnectionHost"/>'s <see cref="MuxConnectionHost.LastFailure"/> is one of these, and the
/// session factory reads its <see cref="Failure"/>. <see cref="Exception.Message"/> is the reason.
/// </summary>
internal sealed class RemoteMuxUnavailableException(RemoteMuxFailure failure, Exception? inner = null) : IOException(failure.Reason, inner)
{
    public RemoteMuxFailure Failure { get; } = failure;
}
