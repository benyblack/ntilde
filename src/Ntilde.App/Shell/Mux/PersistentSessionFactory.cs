using Ntilde.Pty;

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
    /// </summary>
    DaemonUnreachable,

    /// <summary>
    /// A restore's session is held by another client (another window or instance): a fresh shell was
    /// started, and the pane says so. On v2 the pane produces this after the attach itself refused
    /// (session_attached); on v1 the factory's listSessions check does.
    /// </summary>
    AttachedElsewhere,
}

/// <param name="Session">The session to show; null only for <see cref="PersistentSessionOutcome.DaemonUnreachable"/>.</param>
/// <param name="Endpoint">The daemon endpoint the session lives on (persisted as PaneNode.MuxEndpoint); null when not persistent.</param>
/// <param name="Detail">Why the outcome is Unavailable or DaemonUnreachable, for the log.</param>
internal sealed record PersistentSessionResult(ITerminalSession? Session, PersistentSessionOutcome Outcome, string? Endpoint, string? Detail)
{
    /// <summary>Unavailable/DaemonUnreachable because the running daemon speaks another protocol version (the pane adds a kill-server hint).</summary>
    public bool VersionMismatch { get; init; }
}

/// <summary>A factory that can tell the pane whether the session it made will persist (spec §7).</summary>
internal interface IPersistentSessionFactory : ITerminalSessionFactory
{
    PersistentSessionResult CreatePersistent(TerminalSessionRequest request);
}
