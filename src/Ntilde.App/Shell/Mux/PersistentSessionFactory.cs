using Ntilde.Pty;
using Ntilde.Shell.Mux.Remote;

namespace Ntilde.Shell.Mux;

internal enum PersistentSessionOutcome
{
    NotPersistent,
    Spawned,
    Reattached,
    PreviousLost,

    /// <summary>A new pane's daemon could not be used: <see cref="PersistentSessionResult.Session"/> is a plain local fallback.</summary>
    Unavailable,

    /// <summary>
    /// A pane asked to reopen a daemon session (ExistingMuxSessionId) and the daemon could not be
    /// used. No session is created (<see cref="PersistentSessionResult.Session"/> is null): a local
    /// fallback shell would hide the fact that the user's shell is still running in the daemon,
    /// and the pane would forget its id. The pane keeps the id and offers a retry instead.
    /// Also a new remote tab whose SSH connect failed, or failed unclassified: it has no id to keep, and a
    /// plain SSH stand-in would only fail again (a controller ruling over Phase 4 spec §7.5).
    /// </summary>
    DaemonUnreachable,

    /// <summary>
    /// A restore's session is held by another client (another window or instance): a fresh shell was
    /// started, and the pane says so. On v2 the pane produces this after the attach itself refused
    /// (session_attached); on v1 the factory's listSessions check does.
    /// </summary>
    AttachedElsewhere,

    /// <summary>
    /// A deliberate share ("Attach to session…") named a session that is gone or faulted. No session
    /// is created (<see cref="PersistentSessionResult.Session"/> is null): a fresh shell is not what
    /// the user chose, so the pane asks the window to close it instead.
    /// </summary>
    ShareEnded,
}

/// <param name="Session">The session to show; null only for <see cref="PersistentSessionOutcome.DaemonUnreachable"/> and <see cref="PersistentSessionOutcome.ShareEnded"/>.</param>
/// <param name="Endpoint">
/// The <see cref="MuxEndpointId"/> (as its string) of the daemon the session - or, for DaemonUnreachable
/// and ShareEnded, the named session - lives on; persisted as PaneNode.MuxEndpoint. Null when not persistent,
/// and for the local daemon's Unavailable (its fallback shell lives on no daemon). A remote endpoint's result
/// always names it, Unavailable included (Phase 4 spec §7.5).
/// </param>
/// <param name="Detail">
/// Why the outcome is Unavailable or DaemonUnreachable: for the log, and for a remote endpoint the reason
/// its notice gives.
/// </param>
internal sealed record PersistentSessionResult(ITerminalSession? Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail)
{
    /// <summary>Unavailable/DaemonUnreachable because the running daemon speaks another protocol version (the pane adds a kill-server hint).</summary>
    public bool VersionMismatch { get; init; }

    /// <summary>Unavailable/DaemonUnreachable because a daemon holds the lock but cannot be reached (the pane raises its own notice).</summary>
    public bool OrphanedDaemon { get; init; }

    /// <summary>A share of a session whose shell had already exited when it was listed: its exit arrives with the attach.</summary>
    public bool AlreadyExited { get; init; }

    /// <summary>
    /// Unavailable/DaemonUnreachable for a remote endpoint because its connect failed (Phase 4 spec §7.5):
    /// the classified failure, whose kind decides whether the notice offers the install flow. Null for the
    /// local daemon, and for a remote failure that was not the connect's (a request that timed out, say).
    /// </summary>
    public RemoteMuxFailure? RemoteFailure { get; init; }

    /// <summary>
    /// The remote host the result's endpoint names, as the user knows it (<c>user@host</c>, its
    /// <see cref="MuxHostPolicy.DisplayName"/>), for the pane's banners and notices. Null for the local daemon.
    /// </summary>
    public string? HostDisplayName { get; init; }
}

/// <summary>A factory that can tell the pane whether the session it made will persist (spec §7).</summary>
internal interface IPersistentSessionFactory : ITerminalSessionFactory
{
    PersistentSessionResult CreatePersistent(TerminalSessionRequest request);
}
