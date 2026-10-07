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

/// <summary>
/// Why a <see cref="RemoteFailureKind.NeedsUser"/> failure needs the user, in the app's own terms. It decides the line a
/// pane writes under its Enter banner (<c>TerminalPane.RemoteNeedsUserLine</c>), so that line never repeats what ssh or
/// rusty_ssh said - that stays in the failure's reason, for the log. Meaningless for the other kinds.
/// </summary>
internal enum RemoteNeedsUserCause
{
    /// <summary>
    /// Signing in needs an answer an automatic attempt does not give - a password with none saved, a key's passphrase,
    /// keyboard-interactive input - or OpenSSH was refused with none of them offered. Enter signs in.
    /// </summary>
    SignIn,

    /// <summary>The profile's saved password was offered by an automatic attempt, and refused. Enter signs in.</summary>
    SavedPasswordRefused,

    /// <summary>
    /// The native SSH backend is switched off (codex4 F): the reason is the app's own message, which says what to do,
    /// and Enter alone does not fix it.
    /// </summary>
    NativeSshDisabled,
}

/// <summary>
/// A classified remote failure: its <paramref name="Kind"/>, the reason (for the log, and the notices of failures past
/// SSH), and, for a <see cref="RemoteFailureKind.NeedsUser"/> one, its <paramref name="Cause"/>.
/// </summary>
internal sealed record RemoteMuxFailure(RemoteFailureKind Kind, string Reason, RemoteNeedsUserCause Cause = RemoteNeedsUserCause.SignIn)
{
    /// <summary>
    /// SSH said the server refused the sign-in: OpenSSH's <c>Permission denied</c> or <c>Too many authentication
    /// failures</c> (exit 255), or the native layer's authentication failure - whoever started the attempt. It is the
    /// evidence that a password the attempt sent was refused (<see cref="RemoteMuxConnector"/>); a failure without it - a
    /// link that dropped - says nothing about the password.
    /// </summary>
    public bool SignInRefused { get; init; }
}

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
